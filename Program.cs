using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
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
            case "--demo":
                return Demo(args.Length > 1 ? args[1] : AppPaths.Dir);
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

    // ---------------------------------------------------------------- screenshots for the README

    /// <summary>
    /// Live sensor reading plus generated history (an hour of seconds, a day of minutes) so the charts have
    /// something to show. Only used by --preview and --demo; the real app never shows made-up data.
    /// </summary>
    private sealed record DemoData(SampleHistory Seconds, MinuteHistory Minutes, string CpuName, string GpuName);

    private static DemoData CreateDemoData()
    {
        using var sensors = new SensorReader();
        sensors.Read();
        Thread.Sleep(1000);
        var live = sensors.Read();
        var now = DateTime.Now;
        var rnd = new Random(7);

        Reading At(DateTime t)
        {
            double hour = t.TimeOfDay.TotalHours;
            double ago = (now - t).TotalMinutes;
            bool gaming = (hour >= 20 && hour < 23.5) || ago is > 12 and < 25;
            bool working = hour >= 9 && hour < 18;
            double m = t.TimeOfDay.TotalMinutes;
            float wave = (float)(1.6 * Math.Sin(m / 7) + 1.1 * Math.Sin(m / 23 + 1) + 0.8 * Math.Sin(m / 61 + 2));
            float spike = ago is > 5.5 and < 6.2 || rnd.NextDouble() < 0.003 ? 11 : 0;
            float noise = (float)rnd.NextDouble();
            return live with
            {
                CpuTemp = (gaming ? 76 : working ? 62 : 51) + wave + spike + noise * 2,
                CpuLoad = Math.Clamp((gaming ? 42 : working ? 18 : 5) + wave * 3 + spike * 2 + noise * 8, 0f, 100f),
                GpuTemp = (gaming ? 69 : working ? 47 : 42) + wave / 2 + noise,
                GpuLoad = Math.Clamp((gaming ? 91 : working ? 6 : 1) + noise * 5, 0f, 100f),
            };
        }

        var minutes = new MinuteHistory(path: null);
        for (var t = now - MinuteHistory.Window; t < now; t = t.AddSeconds(10)) minutes.Add(t, At(t));
        minutes.Flush();
        var seconds = new SampleHistory();
        for (var t = now - SampleHistory.Window; t <= now; t = t.AddSeconds(1)) seconds.Add(t, At(t));
        return new DemoData(seconds, minutes, sensors.CpuName, sensors.GpuName);
    }

    private static PanelState DemoState(DemoData data, Settings settings, StabilityMonitor stability) => new(
        data.Seconds.Samples[^1].Reading, data.Seconds.Samples, data.Minutes.Minutes, data.CpuName, data.GpuName,
        (81f, DateTime.Now.AddMinutes(-18)), (72f, DateTime.Now.AddMinutes(-15)),
        stability.Since(DateTime.MinValue), stability.Available, null, true, settings);

    /// <summary>Renders the detail panel and the settings window, in English and Turkish, dark and light, to PNGs.</summary>
    private static int Preview(string dir)
    {
        Directory.CreateDirectory(dir);
        var data = CreateDemoData();
        using var stability = new StabilityMonitor();
        stability.Start(TimeSpan.FromDays(7));
        var settings = new Settings { Metrics = new() { "cpu.temp", "cpu.load", "gpu.temp", "gpu.load", "ram.load", "net.down", "net.up" } };

        foreach (var lang in new[] { "en", "tr" })
        foreach (var theme in new[] { Theme.Dark, Theme.LightMode })
        {
            L.Apply(lang);
            Theme.Override = theme;
            string name = $"{lang}-{(theme.Light ? "light" : "dark")}";

            using var panel = new DetailPanel();
            foreach (var range in Enum.GetValues<HistoryRange>())
            {
                settings.HistoryRange = range;
                panel.UpdateState(DemoState(data, settings, stability));
                using (panel.Snapshot()) { } // lay out once so chart positions are known
                using var bmp = panel.Snapshot(panel.ChartPoint(0, 0.62f, 0.5f));
                bmp.Save(Path.Combine(dir, $"panel-{name}-{range}.png"));
            }

            // Rendered by the window itself (PrintWindow), never read back from the screen: the real DWM title bar
            // and theme end up in the picture, whatever else happens to be on the desktop does not.
            using var form = new SettingsForm(settings, startupEnabled: true) { StartPosition = FormStartPosition.Manual, Location = new Point(-8000, -8000) };
            form.Show();
            for (int i = 0; i < 10; i++)
            {
                Application.DoEvents();
                Thread.Sleep(50);
            }
            using (var bmp = Native.CaptureWindow(form.Handle, form.DeviceDpi)) bmp.Save(Path.Combine(dir, $"settings-{name}.png"));
            form.Close();
        }
        Theme.Override = null;
        return 0;
    }

    /// <summary>Writes numbered frames of a scripted walk through the panel (hover, switch ranges) for the README GIF.</summary>
    private static int Demo(string dir)
    {
        Directory.CreateDirectory(dir);
        var data = CreateDemoData();
        using var stability = new StabilityMonitor();
        stability.Start(TimeSpan.FromDays(7));
        var settings = new Settings();
        L.Apply("en");
        Theme.Override = Theme.Dark;

        using var panel = new DetailPanel();
        int frame = 0;
        Point cursor = default;

        void Render(Point? mouse, int repeat = 1)
        {
            panel.UpdateState(DemoState(data, settings, stability));
            using var bmp = panel.Snapshot(mouse);
            if (mouse is Point m) DrawCursor(bmp, m);
            for (int i = 0; i < repeat; i++) bmp.Save(Path.Combine(dir, $"frame{frame++:000}.png"));
            if (mouse is Point p) cursor = p;
        }

        void Glide(Point to, int steps)
        {
            var from = cursor;
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                t = t * t * (3 - 2 * t); // ease in-out
                Render(new Point((int)(from.X + (to.X - from.X) * t), (int)(from.Y + (to.Y - from.Y) * t)));
            }
        }

        void Sweep(int chart, int steps)
        {
            for (int i = 0; i <= steps; i++) Render(panel.ChartPoint(chart, 0.08f + 0.84f * i / steps, 0.45f));
        }

        void Switch(HistoryRange range)
        {
            Glide(panel.RangeButton(range), 8);
            settings.HistoryRange = range;
            Render(cursor, repeat: 6);
        }

        panel.UpdateState(DemoState(data, settings, stability));
        using (panel.Snapshot()) { }
        cursor = panel.ChartPoint(0, 0.08f, 0.9f);
        Render(null, repeat: 6);
        Sweep(0, 24);
        Switch(HistoryRange.Hour);
        Glide(panel.ChartPoint(1, 0.08f, 0.45f), 8);
        Sweep(1, 24);
        Switch(HistoryRange.Day);
        Glide(panel.ChartPoint(0, 0.08f, 0.45f), 8);
        Sweep(0, 24);
        Render(cursor, repeat: 10);

        Theme.Override = null;
        return 0;
    }

    private static void DrawCursor(Bitmap bmp, Point p)
    {
        float s = bmp.Width / 380f; // panel is 380 DIP wide
        var arrow = new[] { (0f, 0f), (0f, 16f), (4.2f, 12.2f), (7f, 18.5f), (9.6f, 17.4f), (6.9f, 11.2f), (12f, 11.2f) }
            .Select(v => new PointF(p.X + v.Item1 * s, p.Y + v.Item2 * s)).ToArray();
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPolygon(Brushes.White, arrow);
        using var outline = new Pen(Color.Black, 1.2f * s) { LineJoin = LineJoin.Round };
        g.DrawPolygon(outline, arrow);
    }
}
