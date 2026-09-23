namespace TaskbarThermals;

internal sealed class SettingsForm : Form
{
    private static readonly (string Text, int Ms)[] Intervals = { ("0.5 s", 500), ("1 s", 1000), ("2 s", 2000) };

    private readonly Settings _result;
    private readonly Theme _theme = Theme.Current;
    private readonly CheckBox _showLoad, _showPower, _alertCpu, _alertGpu, _alertStability, _startup;
    private readonly NumericUpDown _fontSize, _cpuWarn, _cpuHot, _gpuWarn, _gpuHot, _alertCpuTemp, _alertGpuTemp, _alertSeconds, _spikeJump;
    private readonly RadioButton[] _interval;

    public SettingsForm(Settings current, bool startupEnabled)
    {
        _result = current.Clone();

        Text = "Taskbar Thermals Settings";
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

        _showLoad = Check("Show usage (%)", current.ShowLoad);
        _showPower = Check("Show power draw (W)", current.ShowPower);
        _fontSize = Number(current.FontSize, 10, 14);
        // Radio buttons rather than a ComboBox: they follow the dark theme, a DropDownList ComboBox does not.
        _interval = Intervals.Select(i => new RadioButton { Text = i.Text, AutoSize = true, Checked = i.Ms == current.IntervalMs, Margin = new Padding(0, 0, 10, 0) }).ToArray();
        if (!_interval.Any(r => r.Checked)) _interval[1].Checked = true;

        _cpuWarn = Number(current.CpuWarn, 40, 105);
        _cpuHot = Number(current.CpuHot, 40, 105);
        _gpuWarn = Number(current.GpuWarn, 40, 105);
        _gpuHot = Number(current.GpuHot, 40, 105);

        _alertCpu = Check("Notify when CPU exceeds", current.AlertCpu);
        _alertCpuTemp = Number(current.AlertCpuTemp, 50, 105);
        _alertGpu = Check("Notify when GPU exceeds", current.AlertGpu);
        _alertGpuTemp = Number(current.AlertGpuTemp, 50, 105);
        _alertSeconds = Number(current.AlertSeconds, 1, 300);
        _alertStability = Check("Hardware errors, blue screens and unexpected shutdowns", current.AlertStability);

        _spikeJump = Number(current.SpikeJump, 3, 40);
        _startup = Check("Start with Windows (as administrator, without a UAC prompt)", startupEnabled);

        var save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(88, 28), Margin = new Padding(0, 0, 8, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(88, 28), Margin = Padding.Empty };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        AcceptButton = save;
        CancelButton = cancel;

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        void Add(Control c) => layout.Controls.Add(c);

        Add(Header("Taskbar"));
        Add(_showLoad);
        Add(_showPower);
        Add(Row(Label("Font size"), _fontSize, Label("px")));
        Add(Row(new Control[] { Label("Refresh every") }.Concat(_interval).ToArray()));

        Add(Header("Color thresholds (°C)"));
        Add(Row(Label("CPU   warning"), _cpuWarn, Label("critical"), _cpuHot));
        Add(Row(Label("GPU   warning"), _gpuWarn, Label("critical"), _gpuHot));

        Add(Header("Notifications"));
        Add(Row(_alertCpu, _alertCpuTemp, Label("°C")));
        Add(Row(_alertGpu, _alertGpuTemp, Label("°C")));
        Add(Row(Label("when above that for"), _alertSeconds, Label("seconds")));
        Add(_alertStability);

        Add(Header("Spike log"));
        Add(Row(Label("Log when CPU rises"), _spikeJump, Label("°C within 5 seconds")));

        Add(Header("Startup"));
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

    private void Save()
    {
        if (_cpuWarn.Value >= _cpuHot.Value || _gpuWarn.Value >= _gpuHot.Value)
        {
            MessageBox.Show(this, "The warning threshold must be lower than the critical one.", "Taskbar Thermals", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _result.ShowLoad = _showLoad.Checked;
        _result.ShowPower = _showPower.Checked;
        _result.FontSize = (int)_fontSize.Value;
        _result.IntervalMs = Intervals[Array.FindIndex(_interval, r => r.Checked)].Ms;
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

    private static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 0, 6, 0) };

    private static CheckBox Check(string text, bool value) => new()
    {
        Text = text,
        Checked = value,
        AutoSize = true,
        Margin = new Padding(0, 3, 6, 3),
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
