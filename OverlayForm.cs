using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace TaskbarThermals;

/// <summary>
/// Borderless, per-pixel-alpha window that sits on top of the Windows 11 taskbar.
/// Win11 dropped deskband support, so overlaying the taskbar is the only way to "embed" into it.
/// Also owns the tray icon, notifications, the detail panel and the settings window.
/// </summary>
internal sealed class OverlayForm : Form
{
    // Shell windows that cover the whole screen without being a real full-screen app.
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "ForegroundStaging", "MultitaskingViewFrame",
    };

    private Settings _settings = Settings.Load();
    private readonly SpikeLogger _spikes;
    private readonly ProcessSampler _processes = new();
    private readonly SampleHistory _history = new();
    private readonly StabilityMonitor _stability = new();
    private readonly SustainedAlert _cpuAlert = new();
    private readonly SustainedAlert _gpuAlert = new();
    private readonly ManualResetEvent _stop = new(false);
    private readonly System.Windows.Forms.Timer _placementTimer = new() { Interval = 250 };
    private readonly ContextMenuStrip _menu = new() { ShowImageMargin = false, ShowCheckMargin = true };
    private readonly NotifyIcon _tray = new();
    private readonly Native.WinEventDelegate _winEventProc;
    private IntPtr _winEventHook;
    private Thread? _worker;
    private SensorReader? _sensors;
    private volatile bool _sensorsFailed;
    private bool? _startupEnabled;
    private DetailPanel? _panel;
    private SettingsForm? _settingsForm;
    private bool _stabilityLoaded;
    private DateTime _lastStabilityNotice = DateTime.MinValue;

    private Reading _reading = Reading.Empty;
    private (float Value, DateTime At)? _maxCpu, _maxGpu;

    // Layout, in physical pixels.
    private Rectangle _taskbar;
    private int _dpi;
    private bool _needsLayout = true;
    private Size _size;
    private Point _pos;
    private Font? _labelFont, _valueFont;
    private float _colLabel, _colTempRight, _colLoadRight, _colPowerRight, _wLabel, _wTemp, _wLoad, _wPower;

    private bool _light = Theme.SystemUsesLightTheme();
    private bool _hover;
    private bool _dragging, _dragMoved;
    private int _dragStartCursorX, _dragStartLeft;

    private static readonly StringFormat LeftFormat = MakeFormat(StringAlignment.Near);
    private static readonly StringFormat RightFormat = MakeFormat(StringAlignment.Far);

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Text = "Taskbar Thermals";

        _spikes = new SpikeLogger(() => _settings);
        _winEventProc = (_, _, _, _, _, _, _) => UpdatePlacement();
        _placementTimer.Tick += (_, _) => UpdatePlacement();

        _menu.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false; // WinForms pre-cancels menus that were empty when opening started
        };
        _tray.Icon = AppIcon.Get(SystemInformation.SmallIconSize.Width);
        _tray.Text = "Taskbar Thermals";
        _tray.ContextMenuStrip = _menu;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };

        _stability.EventLogged += ev => Post(() => OnStabilityEvent(ev));
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        UpdatePlacement();
        _tray.Visible = true;

        // Clicking the taskbar raises it above us; re-assert topmost immediately instead of waiting for the timer.
        _winEventHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        _placementTimer.Start();

        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Sensors" };
        _worker.Start();

        Task.Run(() => _stability.Start(TimeSpan.FromDays(7))).ContinueWith(_ => Post(OnStabilityLoaded));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _placementTimer.Stop();
        if (_winEventHook != IntPtr.Zero) Native.UnhookWinEvent(_winEventHook);
        _tray.Visible = false;
        _tray.Dispose();
        _stop.Set();
        _worker?.Join(3000);
        _sensors?.Dispose();
        _processes.Dispose();
        _stability.Dispose();
        _panel?.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>Marshals work from background threads onto the UI thread; silently drops it once the window is gone.</summary>
    private void Post(Action action)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    // ---------------------------------------------------------------- sampling

    private void WorkerLoop()
    {
        try
        {
            _sensors = new SensorReader();
        }
        catch (Exception ex)
        {
            _sensorsFailed = true;
            AppPaths.LogError(ex);
        }

        do
        {
            try
            {
                var now = DateTime.Now;
                var reading = _sensors?.Read() ?? Reading.Empty;
                _processes.Snapshot();
                _spikes.Check(now, reading, () => _processes.Top(3));
                Post(() => OnReading(reading, now));
            }
            catch (Exception ex)
            {
                AppPaths.LogError(ex);
            }
        }
        while (!_stop.WaitOne(_settings.IntervalMs));
    }

    private void OnReading(Reading r, DateTime at)
    {
        if (IsDisposed) return;
        _reading = r;
        _history.Add(at, r);
        if (r.CpuTemp is float c && (_maxCpu is null || c > _maxCpu.Value.Value)) _maxCpu = (c, at);
        if (r.GpuTemp is float g && (_maxGpu is null || g > _maxGpu.Value.Value)) _maxGpu = (g, at);
        _light = Theme.SystemUsesLightTheme();

        if (_settings.AlertCpu && _cpuAlert.Check(at, r.CpuTemp, _settings.AlertCpuTemp, _settings.AlertSeconds))
            Notify("CPU running hot", $"CPU at {r.CpuTemp:0}°C, above {_settings.AlertCpuTemp}°C for {_settings.AlertSeconds} seconds.", ToolTipIcon.Warning);
        if (_settings.AlertGpu && _gpuAlert.Check(at, r.GpuTemp, _settings.AlertGpuTemp, _settings.AlertSeconds))
            Notify("GPU running hot", $"GPU at {r.GpuTemp:0}°C, above {_settings.AlertGpuTemp}°C for {_settings.AlertSeconds} seconds.", ToolTipIcon.Warning);

        _tray.Text = $"Taskbar Thermals\nCPU {TempText(r.CpuTemp)}  {LoadText(r.CpuLoad)}%\nGPU {TempText(r.GpuTemp)}  {LoadText(r.GpuLoad)}%";
        Render();
        RefreshPanel();
    }

    private static string TempText(float? t) => t is float v ? $"{v:0}°C" : "--";
    private static string LoadText(float? l) => l is float v ? $"{v:0}" : "--";
    private static string When(DateTime t) => t.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);

    private void Notify(string title, string text, ToolTipIcon icon) => _tray.ShowBalloonTip(10_000, title, text, icon);

    // ---------------------------------------------------------------- stability

    private List<StabilityEvent> OpenStabilityEvents() =>
        _stabilityLoaded ? _stability.Since(_settings.StabilityAckTime ?? DateTime.MinValue) : new();

    private void OnStabilityLoaded()
    {
        _stabilityLoaded = true;
        DateTime since = Later(_settings.StabilityAckTime, _settings.StabilityNotifiedUntil);
        var fresh = _stability.Since(since).Where(e => e.Serious).ToList();
        if (_settings.AlertStability && fresh.Count > 0)
        {
            string text = fresh.Count == 1
                ? $"{fresh[0].Title} ({When(fresh[0].Time)})."
                : $"{fresh.Count} events. Latest: {fresh[0].Title} ({When(fresh[0].Time)}).";
            Notify("Stability warning", text, ToolTipIcon.Error);
        }
        MarkStabilityNotified();
    }

    private void OnStabilityEvent(StabilityEvent ev)
    {
        if (IsDisposed) return;
        if (_settings.AlertStability && ev.Serious && DateTime.Now - _lastStabilityNotice > TimeSpan.FromMinutes(10))
        {
            _lastStabilityNotice = DateTime.Now;
            Notify("Stability warning",
                $"{ev.Title} at {ev.Time:HH:mm}. A recent BIOS change (undervolt, Curve Optimizer, SoC voltage) may be unstable.",
                ToolTipIcon.Error);
        }
        MarkStabilityNotified();
    }

    private void MarkStabilityNotified()
    {
        _settings.StabilityNotifiedUntil = DateTime.Now;
        _settings.Save();
        Render();
        RefreshPanel();
    }

    private void AcknowledgeStability()
    {
        _settings.StabilityAckTime = DateTime.Now;
        _settings.Save();
        Render();
        RefreshPanel();
    }

    private static DateTime Later(DateTime? a, DateTime? b) =>
        new DateTime(Math.Max((a ?? DateTime.MinValue).Ticks, (b ?? DateTime.MinValue).Ticks));

    // ---------------------------------------------------------------- panel & settings

    private void TogglePanel()
    {
        _panel ??= CreatePanel();
        if (_panel.Visible)
        {
            _panel.HidePanel();
            return;
        }
        // The click that deactivated (and so hid) the panel also lands here; don't reopen it right away.
        if (Environment.TickCount64 - _panel.HiddenAt < 300) return;

        _panel.UpdateState(BuildPanelState());
        _panel.ShowAt(new Rectangle(_pos, _size));
    }

    private DetailPanel CreatePanel()
    {
        var panel = new DetailPanel();
        panel.TaskManagerClicked += OpenTaskManager;
        panel.SpikeLogClicked += ShowSpikeLog;
        panel.SettingsClicked += OpenSettings;
        panel.StabilityAckClicked += AcknowledgeStability;
        return panel;
    }

    private void RefreshPanel()
    {
        if (_panel is { Visible: true }) _panel.UpdateState(BuildPanelState());
    }

    private PanelState BuildPanelState() => new(
        _reading,
        _history.Samples,
        _sensors?.CpuName ?? "CPU",
        _sensors?.GpuName ?? "GPU",
        _maxCpu,
        _maxGpu,
        OpenStabilityEvents(),
        _stability.Available,
        _settings.StabilityAckTime,
        File.Exists(_spikes.LogPath),
        _settings);

    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _startupEnabled ??= Startup.IsEnabled();
        var form = new SettingsForm(_settings, _startupEnabled.Value);
        form.FormClosed += (_, _) =>
        {
            _settingsForm = null;
            if (!form.Saved) return;

            // Fields the dialog doesn't own may have changed while it was open (dragging, stability acks).
            var s = form.Result;
            s.OffsetFromRight = _settings.OffsetFromRight;
            s.StabilityAckTime = _settings.StabilityAckTime;
            s.StabilityNotifiedUntil = _settings.StabilityNotifiedUntil;
            _settings = s;
            _settings.Save();

            _needsLayout = true;
            UpdatePlacement();
            if (form.StartupChecked != _startupEnabled) ToggleStartup();
        };
        _settingsForm = form;
        form.Show();
        Native.SetForegroundWindow(form.Handle);
    }

    // ---------------------------------------------------------------- placement

    private void UpdatePlacement()
    {
        if (IsDisposed || !IsHandleCreated) return;

        IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !Native.GetWindowRect(tray, out var rc))
        {
            Visible = false;
            return;
        }

        var taskbar = rc.ToRectangle();
        var monitor = Screen.FromRectangle(taskbar).Bounds;
        int dpi = (int)Native.GetDpiForWindow(tray);
        if (dpi == 0) dpi = 96;

        bool relayout = _needsLayout || dpi != _dpi || taskbar.Height != _taskbar.Height;
        _taskbar = taskbar;
        if (relayout)
        {
            _dpi = dpi;
            _needsLayout = false;
            Relayout();
        }

        // Auto-hidden taskbar or a full-screen game/video: get out of the way like the taskbar does.
        var visibleBar = Rectangle.Intersect(taskbar, monitor);
        if (visibleBar.Height < Px(20) || IsFullscreenAppActive(monitor))
        {
            Visible = false;
            return;
        }

        var pos = new Point(_dragging ? _pos.X : ComputeX(tray), taskbar.Top + (taskbar.Height - _size.Height) / 2);
        if (relayout || pos != _pos)
        {
            _pos = pos;
            Render();
        }

        // Our own context menu overlaps us; don't jump above it.
        if (!_menu.Visible)
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        Visible = true;
    }

    private int ComputeX(IntPtr tray)
    {
        int x;
        if (_settings.OffsetFromRight is int offset)
        {
            x = _taskbar.Right - Px(offset) - _size.Width;
        }
        else
        {
            // Default: just left of the notification area (the ^ / ENG / clock block).
            IntPtr notify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            x = notify != IntPtr.Zero && Native.GetWindowRect(notify, out var nr) && nr.Right > nr.Left && nr.Left > _taskbar.Left + _taskbar.Width / 2
                ? nr.Left - _size.Width - Px(8)
                : _taskbar.Right - _size.Width - Px(260);
        }
        return ClampX(x);
    }

    private int ClampX(int x) => Math.Clamp(x, _taskbar.Left, Math.Max(_taskbar.Left, _taskbar.Right - _size.Width));

    private bool IsFullscreenAppActive(Rectangle monitor)
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Handle) return false;
        if (ShellClasses.Contains(Native.GetClassName(fg))) return false;
        if (!Native.GetWindowRect(fg, out var r)) return false;
        return r.Left <= monitor.Left && r.Top <= monitor.Top && r.Right >= monitor.Right && r.Bottom >= monitor.Bottom;
    }

    // ---------------------------------------------------------------- drawing

    private int Px(int v) => (int)Math.Round(v * _dpi / 96.0);
    private float Px(float v) => v * _dpi / 96f;

    private void Relayout()
    {
        _labelFont?.Dispose();
        _valueFont?.Dispose();
        _labelFont = new Font("Segoe UI", Px(_settings.FontSize - 1f), FontStyle.Regular, GraphicsUnit.Pixel);
        _valueFont = new Font("Segoe UI Semibold", Px((float)_settings.FontSize), FontStyle.Regular, GraphicsUnit.Pixel);

        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _wLabel = Measure(g, "GPU", _labelFont);
        _wTemp = Measure(g, "100°C", _valueFont);
        _wLoad = Measure(g, "100%", _valueFont);
        _wPower = Measure(g, "000W", _valueFont);

        float pad = Px(8f), gap = Px(7f);
        _colLabel = pad;
        float x = _colTempRight = pad + _wLabel + gap + _wTemp;
        if (_settings.ShowLoad) x = _colLoadRight = x + gap + _wLoad;
        if (_settings.ShowPower) x = _colPowerRight = x + gap + _wPower;
        _size = new Size((int)Math.Ceiling(x + pad), Math.Max(Px(24), _taskbar.Height));
    }

    private static float Measure(Graphics g, string text, Font font) =>
        g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;

    private void Render()
    {
        if (!IsHandleCreated || _size.Width <= 0 || _size.Height <= 0 || _labelFont is null || _valueFont is null) return;

        using var bmp = new Bitmap(_size.Width, _size.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // Alpha 1 is invisible but keeps the whole rectangle clickable (alpha 0 would be click-through).
            g.Clear(Color.FromArgb(1, 0, 0, 0));

            if (_hover || _dragging)
            {
                float inset = Math.Max(Px(2f), (_size.Height - Px(40f)) / 2);
                using var path = RoundedRect(new RectangleF(Px(1f), inset, _size.Width - Px(2f), _size.Height - 2 * inset), Px(5f));
                using var hoverBrush = new SolidBrush(_light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(24, 255, 255, 255));
                g.FillPath(hoverBrush, path);
            }

            float rowOffset = Px(_settings.FontSize * 0.667f);
            DrawRow(g, _size.Height / 2f - rowOffset, "CPU", _reading.CpuTemp, _reading.CpuLoad, _reading.CpuPower, _settings.CpuWarn, _settings.CpuHot);
            DrawRow(g, _size.Height / 2f + rowOffset, "GPU", _reading.GpuTemp, _reading.GpuLoad, _reading.GpuPower, _settings.GpuWarn, _settings.GpuHot);

            // Unacknowledged hardware errors: a small red dot; the panel carries the icon + label explanation.
            if (OpenStabilityEvents().Any(e => e.Serious))
            {
                float d = Px(6f);
                using var dot = new SolidBrush(Theme.Critical);
                g.FillEllipse(dot, _size.Width - d - Px(3f), Px(6f), d, d);
            }
        }

        Native.UpdateLayered(Handle, bmp, _pos);
    }

    private void DrawRow(Graphics g, float centerY, string label, float? temp, float? load, float? power, float warn, float hot)
    {
        float rowH = Px(_settings.FontSize + 4f);
        float top = centerY - rowH / 2;

        Color text = _light ? Color.FromArgb(255, 26, 26, 26) : Color.White;
        Color dim = _light ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(165, 255, 255, 255);
        Color tempColor = temp switch
        {
            float t when t >= hot => _light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 107, 107),
            float t when t >= warn => _light ? Color.FromArgb(157, 93, 0) : Color.FromArgb(255, 200, 61),
            _ => text,
        };

        using var dimBrush = new SolidBrush(dim);
        using var tempBrush = new SolidBrush(tempColor);
        using var textBrush = new SolidBrush(text);

        RectangleF Column(float right, float width) => new(right - width - Px(4f), top, width + Px(4f), rowH);

        g.DrawString(label, _labelFont!, dimBrush, new RectangleF(_colLabel, top, _wLabel + Px(4f), rowH), LeftFormat);
        g.DrawString(temp is float tv ? $"{tv:0}°C" : "--°C", _valueFont!, tempBrush, Column(_colTempRight, _wTemp), RightFormat);
        if (_settings.ShowLoad)
            g.DrawString(load is float lv ? $"{lv:0}%" : "--%", _valueFont!, textBrush, Column(_colLoadRight, _wLoad), RightFormat);
        if (_settings.ShowPower)
            g.DrawString(power is float pv ? $"{pv:0}W" : "--W", _valueFont!, textBrush, Column(_colPowerRight, _wPower), RightFormat);
    }

    private static StringFormat MakeFormat(StringAlignment alignment)
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.Alignment = alignment;
        f.LineAlignment = StringAlignment.Center;
        f.FormatFlags |= StringFormatFlags.NoWrap;
        return f;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Render();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Render();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _dragMoved = false;
        _dragStartCursorX = Cursor.Position.X;
        _dragStartLeft = _pos.X;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        int x = ClampX(_dragStartLeft + Cursor.Position.X - _dragStartCursorX);
        // A few pixels of jitter during a click shouldn't count as a drag.
        if (!_dragMoved && Math.Abs(x - _dragStartLeft) < Px(4)) return;
        if (x == _pos.X) return;
        _dragMoved = true;
        _pos.X = x;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, _pos.X, _pos.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _dragging)
        {
            _dragging = false;
            if (_dragMoved)
            {
                _settings.OffsetFromRight = (int)Math.Round((_taskbar.Right - _pos.X - _size.Width) * 96.0 / _dpi);
                _settings.Save();
                Render();
            }
            else
            {
                Render();
                TogglePanel();
            }
        }
        else if (e.Button == MouseButtons.Right)
        {
            // Without foreground the menu would not close when clicking elsewhere.
            Native.SetForegroundWindow(Handle);
            _menu.Show(Cursor.Position);
        }
    }

    // ---------------------------------------------------------------- menu

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Renderer = _light ? new ToolStripProfessionalRenderer() : new DarkMenuRenderer();

        AddInfo($"Max CPU: {FormatMax(_maxCpu)}     Max GPU: {FormatMax(_maxGpu)}");
        AddInfo(_spikes.Last is { } last ? $"Last spike: {last}" : "No spikes logged yet");
        var open = OpenStabilityEvents();
        if (open.Count > 0) AddInfo($"Stability: {open.Count} event(s), latest: {open[0].Title}");
        if (_sensorsFailed || (_sensors != null && _reading.CpuTemp is null && _reading.CpuLoad is not null))
            AddInfo("Can't read CPU temperature — run as administrator");
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add("Details", null, (_, _) => TogglePanel());
        _menu.Items.Add("Task Manager", null, (_, _) => OpenTaskManager());
        var log = _menu.Items.Add("Show spike log", null, (_, _) => ShowSpikeLog());
        log.Enabled = File.Exists(_spikes.LogPath);
        _menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add("Reset max values", null, (_, _) => { _maxCpu = null; _maxGpu = null; });
        var reset = _menu.Items.Add("Reset position", null, (_, _) =>
        {
            _settings.OffsetFromRight = null;
            _settings.Save();
            UpdatePlacement();
        });
        reset.Enabled = _settings.OffsetFromRight is not null;

        _startupEnabled ??= Startup.IsEnabled();
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = _startupEnabled.Value };
        startup.Click += (_, _) => ToggleStartup();
        _menu.Items.Add(startup);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => Close());

        if (!_light)
            foreach (ToolStripItem item in _menu.Items) item.ForeColor = Color.White;
    }

    private void AddInfo(string text) => _menu.Items.Add(new ToolStripMenuItem(text) { Enabled = false });

    private static string FormatMax((float Value, DateTime At)? max) =>
        max is { } m ? $"{m.Value:0}°C ({m.At:HH:mm:ss})" : "--";

    private void ToggleStartup()
    {
        bool ok = _startupEnabled == true ? Startup.Disable() : Startup.Enable();
        _startupEnabled = Startup.IsEnabled();
        if (!ok)
            MessageBox.Show("Couldn't update the Task Scheduler entry. Make sure the app is running as administrator.",
                "Taskbar Thermals", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void ShowSpikeLog()
    {
        if (File.Exists(_spikes.LogPath))
            Process.Start("explorer.exe", $"/select,\"{_spikes.LogPath}\"");
    }

    private static void OpenTaskManager()
    {
        try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); }
        catch (Exception ex) { AppPaths.LogError(ex); }
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) => RoundedEdges = false;

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.White : Color.FromArgb(170, 170, 170);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            using var pen = new Pen(Color.White, Math.Max(1.5f, r.Height / 9f));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.18f, r.Top + r.Height * 0.52f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.76f),
                new PointF(r.Left + r.Width * 0.84f, r.Top + r.Height * 0.26f),
            });
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        private static readonly Color Back = Color.FromArgb(43, 43, 43);
        private static readonly Color Hot = Color.FromArgb(65, 65, 65);
        private static readonly Color Line = Color.FromArgb(75, 75, 75);

        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hot;
        public override Color MenuItemSelected => Hot;
        public override Color MenuItemSelectedGradientBegin => Hot;
        public override Color MenuItemSelectedGradientEnd => Hot;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Back;
        public override Color CheckBackground => Back;
        public override Color CheckSelectedBackground => Hot;
        public override Color CheckPressedBackground => Hot;
    }
}
