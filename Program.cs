using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace TaskbarThermals;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        switch (args.FirstOrDefault())
        {
            case "--dump":
                return Dump(args.Length > 1 ? args[1] : Path.Combine(AppPaths.Dir, "sensors.txt"));
            case "--preview":
                return Preview(args.Length > 1 ? args[1] : AppPaths.Dir);
            case "--enable-startup":
                return Startup.Enable() ? 0 : 1;
            case "--disable-startup":
                return Startup.Disable() ? 0 : 1;
        }

        // CPU temperature needs admin. If the user declines UAC we still run, just without CPU temp.
        if (!IsElevated() && !args.Contains("--no-elevate") && TryRelaunchElevated())
            return 0;

        Mutex mutex;
        try
        {
            mutex = new Mutex(true, @"Local\TaskbarThermals", out bool created);
            if (!created) return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0; // an elevated instance already owns it
        }

        Application.ThreadException += (_, e) => AppPaths.LogError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) AppPaths.LogError(ex); };
        Application.Run(new OverlayForm());
        GC.KeepAlive(mutex);
        return 0;
    }

    private static bool IsElevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static bool TryRelaunchElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception)
        {
            return false; // UAC cancelled
        }
    }

    /// <summary>Diagnostics: writes every sensor LHM sees plus the values the overlay would pick.</summary>
    private static int Dump(string path)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.WriteLine($"Elevated: {IsElevated()}");
        try
        {
            using var sensors = new SensorReader();
            sensors.Read();
            Thread.Sleep(1000);
            w.WriteLine($"CPU: {sensors.CpuName}  GPU: {sensors.GpuName}");
            w.WriteLine($"Picked: {sensors.Read()}");
            sensors.DumpSensors(w);
            w.WriteLine();
            w.WriteLine("--- 20 s series");
            for (int i = 0; i < 20; i++)
            {
                w.WriteLine($"{DateTime.Now:HH:mm:ss}  {sensors.DiagnosticLine()}");
                w.Flush();
                Thread.Sleep(1000);
            }
        }
        catch (Exception ex)
        {
            w.WriteLine($"Sensor error: {ex}");
        }

        IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
        Native.GetWindowRect(tray, out var tr);
        IntPtr notify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        Native.GetWindowRect(notify, out var nr);
        w.WriteLine($"Taskbar: {tr.ToRectangle()} dpi={Native.GetDpiForWindow(tray)}  TrayNotifyWnd: {nr.ToRectangle()}");
        w.WriteLine($"Screen: {Screen.PrimaryScreen?.Bounds}");

        using var procs = new ProcessSampler();
        procs.Snapshot();
        Thread.Sleep(1000);
        procs.Snapshot();
        foreach (var (name, pct) in procs.Top(5)) w.WriteLine($"Top: {name} {pct:0.0}%");
        return 0;
    }

    /// <summary>
    /// Diagnostics: renders the detail panel (dark + light, with and without hover) and the settings window to PNGs.
    /// The live reading is real; the 10-minute history is synthesized around it, only to check the chart layout.
    /// </summary>
    private static int Preview(string dir)
    {
        Directory.CreateDirectory(dir);
        using var sensors = new SensorReader();
        sensors.Read();
        Thread.Sleep(1000);
        var live = sensors.Read();

        var history = new SampleHistory();
        var start = DateTime.Now - SampleHistory.Window;
        var rnd = new Random(7);
        for (int i = 0; i <= 600; i++)
        {
            float wave = (float)(Math.Sin(i / 600.0 * 9) * 4) + (i is > 380 and < 420 ? 12 : 0);
            history.Add(start.AddSeconds(i), live with
            {
                CpuTemp = (live.CpuTemp ?? 62) + wave + (float)rnd.NextDouble() * 2,
                CpuLoad = Math.Clamp((live.CpuLoad ?? 20) + wave * 3 + (float)rnd.NextDouble() * 6, 0f, 100f),
                GpuTemp = (live.GpuTemp ?? 45) + wave / 3,
                GpuLoad = Math.Clamp((live.GpuLoad ?? 5) + (i is > 200 and < 300 ? 60 : 0) + (float)rnd.NextDouble() * 3, 0f, 100f),
            });
        }

        using var stability = new StabilityMonitor();
        stability.Start(TimeSpan.FromDays(7));
        var settings = Settings.Load();

        foreach (var theme in new[] { Theme.Dark, Theme.LightMode })
        {
            Theme.Override = theme;
            string name = theme.Light ? "light" : "dark";
            using var panel = new DetailPanel();
            panel.UpdateState(new PanelState(
                history.Samples[^1].Reading, history.Samples, sensors.CpuName, sensors.GpuName,
                (81f, DateTime.Now.AddMinutes(-4)), (64f, DateTime.Now.AddMinutes(-7)),
                stability.Since(settings.StabilityAckTime ?? DateTime.MinValue), stability.Available,
                settings.StabilityAckTime, true, settings));
            using (var bmp = panel.Snapshot()) bmp.Save(Path.Combine(dir, $"panel-{name}.png"));
            using (var bmp = panel.Snapshot(hover: 0.6f)) bmp.Save(Path.Combine(dir, $"panel-{name}-hover.png"));

            // Shown on screen and captured from it, so the real DWM title bar and theme are in the picture.
            using var form = new SettingsForm(settings, startupEnabled: true) { StartPosition = FormStartPosition.CenterScreen, TopMost = true };
            form.Show();
            for (int i = 0; i < 10; i++)
            {
                Application.DoEvents();
                Thread.Sleep(50);
            }
            var bounds = Native.VisibleBounds(form.Handle);
            if (bounds.IsEmpty) bounds = form.Bounds;
            using (var bmp = new Bitmap(bounds.Width, bounds.Height))
            {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                bmp.Save(Path.Combine(dir, $"settings-{name}.png"));
            }
            form.Close();
        }
        Theme.Override = null;
        return 0;
    }
}
