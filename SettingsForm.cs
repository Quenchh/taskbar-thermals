namespace TaskbarThermals;

internal sealed class SettingsForm : Form
{
    private static readonly int[] Intervals = { 500, 1000, 2000 };
    private static readonly string[] Languages = { "auto", "en", "tr" };

    private readonly Settings _result;
    private readonly Theme _theme = Theme.Current;
    private readonly Dictionary<string, CheckBox> _metrics = new();
    private readonly CheckBox _alertCpu, _alertGpu, _alertStability, _checkUpdates, _startup;
    private readonly NumericUpDown _fontSize, _cpuWarn, _cpuHot, _gpuWarn, _gpuHot, _alertCpuTemp, _alertGpuTemp, _alertSeconds, _spikeJump;
    private readonly RadioButton[] _interval, _language;

    public SettingsForm(Settings current, bool startupEnabled)
    {
        _result = current.Clone();

        Text = L.SettingsTitle;
        Icon = AppIcon.Get(32);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18, 6, 18, 16);

        var selected = current.Metrics ?? Metrics.Defaults.ToList();
        foreach (var m in Metrics.All)
            _metrics[m.Id] = Check(L.MetricName(m.Id), selected.Contains(m.Id));

        _fontSize = Number(current.FontSize, 10, 14);
        // Radio buttons rather than a ComboBox: they follow the dark theme, a DropDownList ComboBox does not.
        _interval = Intervals.Select(ms => Radio(L.Seconds(L.Number(ms / 1000f, "0.#")), ms == current.IntervalMs)).ToArray();
        if (!_interval.Any(r => r.Checked)) _interval[1].Checked = true;

        _cpuWarn = Number(current.CpuWarn, 40, 105);
        _cpuHot = Number(current.CpuHot, 40, 105);
        _gpuWarn = Number(current.GpuWarn, 40, 105);
        _gpuHot = Number(current.GpuHot, 40, 105);

        _alertCpu = Check(L.NotifyWhen("CPU"), current.AlertCpu);
        _alertCpuTemp = Number(current.AlertCpuTemp, 50, 105);
        _alertGpu = Check(L.NotifyWhen("GPU"), current.AlertGpu);
        _alertGpuTemp = Number(current.AlertGpuTemp, 50, 105);
        _alertSeconds = Number(current.AlertSeconds, 1, 300);
        _alertStability = Check(L.StabilityAlerts, current.AlertStability);

        _spikeJump = Number(current.SpikeJump, 3, 40);

        _language = Languages.Select(code => Radio(code switch { "en" => "English", "tr" => "Türkçe", _ => L.Automatic }, code == current.Language)).ToArray();
        if (!_language.Any(r => r.Checked)) _language[0].Checked = true;
        _checkUpdates = Check(L.CheckUpdates, current.CheckUpdates);
        _startup = Check(L.StartWithWindowsLong, startupEnabled);

        var save = new Button { Text = L.Save, AutoSize = true, MinimumSize = new Size(88, 28), Margin = new Padding(0, 0, 8, 0) };
        var cancel = new Button { Text = L.Cancel, AutoSize = true, MinimumSize = new Size(88, 28), Margin = Padding.Empty };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        AcceptButton = save;
        CancelButton = cancel;

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        void Add(Control c) => layout.Controls.Add(c);

        Add(Header(L.Taskbar));
        Add(Label(L.ShowOnTaskbar, top: 2));
        Add(MetricGrid());
        Add(Row(Label(L.FontSize), _fontSize, Label("px")));
        Add(Row(new Control[] { Label(L.RefreshEvery) }.Concat(_interval).ToArray()));

        Add(Header(L.ColorThresholds));
        Add(Row(Label($"CPU   {L.Warning}"), _cpuWarn, Label(L.CriticalLower), _cpuHot));
        Add(Row(Label($"GPU   {L.Warning}"), _gpuWarn, Label(L.CriticalLower), _gpuHot));

        Add(Header(L.Notifications));
        Add(Row(_alertCpu, _alertCpuTemp, Label("°C")));
        Add(Row(_alertGpu, _alertGpuTemp, Label("°C")));
        Add(Row(Label(L.AboveFor), _alertSeconds, Label(L.SecondsWord)));
        Add(_alertStability);

        Add(Header(L.SpikeLog));
        Add(Row(Label(L.LogWhenRises), _spikeJump, Label(L.WithinFive)));

        Add(Header(L.General));
        Add(Row(new Control[] { Label(L.Language) }.Concat(_language).ToArray()));
        Add(_checkUpdates);
        Add(_startup);

        var buttons = Row(save, cancel);
        buttons.Anchor = AnchorStyles.Right;
        buttons.Margin = new Padding(0, 18, 0, 0);
        Add(buttons);

        Controls.Add(layout);
        if (!_theme.Light) ApplyDark(this);
    }

    public bool Saved { get; private set; }
    public Settings Result => _result;
    public bool StartupChecked => _startup.Checked;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleWindow(Handle, !_theme.Light);
    }

    /// <summary>One row per device, one aligned column per metric slot.</summary>
    private TableLayoutPanel MetricGrid()
    {
        var devices = Enum.GetValues<Device>();
        int columns = 1 + devices.Max(d => Metrics.All.Count(m => m.Device == d));
        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = columns,
            RowCount = devices.Length,
            Margin = new Padding(12, 2, 0, 6),
        };
        for (int c = 0; c < columns; c++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        for (int row = 0; row < devices.Length; row++)
        {
            var label = Label(L.DeviceName(devices[row]));
            label.Anchor = AnchorStyles.Left;
            grid.Controls.Add(label, 0, row);
            int col = 1;
            foreach (var m in Metrics.All.Where(m => m.Device == devices[row]))
            {
                var box = _metrics[m.Id];
                box.Anchor = AnchorStyles.Left;
                grid.Controls.Add(box, col++, row);
            }
        }
        return grid;
    }

    private void Save()
    {
        if (_cpuWarn.Value >= _cpuHot.Value || _gpuWarn.Value >= _gpuHot.Value)
        {
            MessageBox.Show(this, L.ThresholdOrder, "Taskbar Thermals", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var metrics = Metrics.All.Where(m => _metrics[m.Id].Checked).Select(m => m.Id).ToList();
        if (metrics.Count == 0)
        {
            MessageBox.Show(this, L.PickOneMetric, "Taskbar Thermals", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _result.Metrics = metrics;
        _result.FontSize = (int)_fontSize.Value;
        _result.IntervalMs = Intervals[Array.FindIndex(_interval, r => r.Checked)];
        _result.CpuWarn = (int)_cpuWarn.Value;
        _result.CpuHot = (int)_cpuHot.Value;
        _result.GpuWarn = (int)_gpuWarn.Value;
        _result.GpuHot = (int)_gpuHot.Value;
        _result.AlertCpu = _alertCpu.Checked;
        _result.AlertCpuTemp = (int)_alertCpuTemp.Value;
        _result.AlertGpu = _alertGpu.Checked;
        _result.AlertGpuTemp = (int)_alertGpuTemp.Value;
        _result.AlertSeconds = (int)_alertSeconds.Value;
        _result.AlertStability = _alertStability.Checked;
        _result.SpikeJump = (int)_spikeJump.Value;
        _result.Language = Languages[Array.FindIndex(_language, r => r.Checked)];
        _result.CheckUpdates = _checkUpdates.Checked;

        Saved = true;
        Close();
    }

    private static Label Header(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 10.5f),
        Margin = new Padding(0, 14, 0, 4),
    };

    private static Label Label(string text, int top = 0) => new() { Text = text, AutoSize = true, Margin = new Padding(0, top, 6, 0) };

    private static CheckBox Check(string text, bool value) => new()
    {
        Text = text,
        Checked = value,
        AutoSize = true,
        Margin = new Padding(0, 3, 10, 3),
    };

    private static RadioButton Radio(string text, bool value) => new()
    {
        Text = text,
        Checked = value,
        AutoSize = true,
        Margin = new Padding(0, 0, 10, 0),
    };

    private static NumericUpDown Number(int value, int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = Math.Clamp(value, min, max),
        Width = 58,
        TextAlign = HorizontalAlignment.Right,
        Margin = new Padding(0, 2, 6, 2),
    };

    /// <summary>One line of controls, vertically centered against each other.</summary>
    private static TableLayoutPanel Row(params Control[] controls)
    {
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = controls.Length,
            RowCount = 1,
            Margin = new Padding(0, 1, 0, 1),
        };
        for (int i = 0; i < controls.Length; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            controls[i].Anchor = AnchorStyles.Left;
            row.Controls.Add(controls[i], i, 0);
        }
        return row;
    }

    private void ApplyDark(Control root)
    {
        var surface = Color.FromArgb(32, 32, 32);
        var field = Color.FromArgb(45, 45, 45);
        root.BackColor = surface;
        root.ForeColor = Color.White;

        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case NumericUpDown n:
                    n.BackColor = field;
                    n.ForeColor = Color.White;
                    n.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case Button b:
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = field;
                    b.ForeColor = Color.White;
                    b.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
                    break;
                case Panel:
                    // Only recurse into layout containers; NumericUpDown has child controls of its own.
                    ApplyDark(c);
                    break;
            }
        }
    }
}
