using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace TaskbarThermals;

/// <summary>Everything the panel draws, captured on the UI thread each tick.</summary>
internal sealed record PanelState(
    Reading Reading,
    IReadOnlyList<Sample> History,
    string CpuName,
    string GpuName,
    (float Value, DateTime At)? MaxCpu,
    (float Value, DateTime At)? MaxGpu,
    IReadOnlyList<StabilityEvent> Events,
    bool StabilityAvailable,
    DateTime? StabilityAckTime,
    bool SpikeLogExists,
    Settings Settings);

/// <summary>Flyout above the taskbar overlay: current values, 10-minute charts, board sensors and stability status.</summary>
internal sealed class DetailPanel : Form
{
    private const float WidthDip = 380;
    private const float PadDip = 16;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed record ChartSpec(string Title, bool Percent, Func<Reading, float?> Cpu, Func<Reading, float?> Gpu);

    private static readonly ChartSpec TempChart = new("Temperature", false, r => r.CpuTemp, r => r.GpuTemp);
    private static readonly ChartSpec LoadChart = new("Usage", true, r => r.CpuLoad, r => r.GpuLoad);

    private readonly record struct Tile(string Label, string Value, string? Prefix = null, string? Suffix = null, Color? Status = null);

    private static readonly StringFormat Near = MakeFormat(StringAlignment.Near);
    private static readonly StringFormat Far = MakeFormat(StringAlignment.Far);
    private static readonly StringFormat Center = MakeFormat(StringAlignment.Center);
    private static readonly StringFormat NearTrim = MakeFormat(StringAlignment.Near, trim: true);

    public event Action? TaskManagerClicked;
    public event Action? SpikeLogClicked;
    public event Action? SettingsClicked;
    public event Action? StabilityAckClicked;

    private PanelState? _state;
    private Theme _theme = Theme.Current;
    private int _fontDpi;
    private Font? _fHeader, _fTitle, _fText, _fSmall, _fAxis, _fValue;

    // Hit targets recorded during the last paint.
    private readonly List<(RectangleF Rect, Action Click, bool Enabled)> _buttons = new();
    private RectangleF _tempPlot;
    private Point? _mouse;

    public DetailPanel()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        KeyPreview = true;
        Text = "Taskbar Thermals";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    /// <summary>When the panel was last hidden, so the click that closed it doesn't immediately reopen it.</summary>
    public long HiddenAt { get; private set; }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleWindow(Handle, !_theme.Light, _theme.Border, rounded: true);
    }

    public void UpdateState(PanelState state)
    {
        _state = state;
        var theme = Theme.Current;
        if (theme != _theme)
        {
            _theme = theme;
            if (IsHandleCreated) Native.StyleWindow(Handle, !_theme.Light, _theme.Border, rounded: true);
        }
        FitHeight();
        Invalidate();
    }

    public void ShowAt(Rectangle anchor)
    {
        FitHeight();
        var work = Screen.FromRectangle(anchor).WorkingArea;
        int margin = Px(12);
        Location = new Point(
            Math.Clamp(anchor.Right - Width, work.Left + margin, Math.Max(work.Left + margin, work.Right - Width - margin)),
            work.Bottom - Height - margin);
        Show();
        Activate();
        Native.SetForegroundWindow(Handle);
    }

    public void HidePanel()
    {
        if (!Visible) return;
        HiddenAt = Environment.TickCount64;
        Hide();
    }

    /// <summary>
    /// Renders the current state off-screen (used by the --preview diagnostics mode).
    /// <paramref name="hover"/> simulates the mouse over the temperature chart at that fraction of its width.
    /// </summary>
    public Bitmap Snapshot(float? hover = null)
    {
        FitHeight();
        var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
        using var g = Graphics.FromImage(bmp);
        if (hover is float f)
        {
            _mouse = null;
            PaintAll(g); // records the chart rectangles
            _mouse = new Point((int)(_tempPlot.Left + _tempPlot.Width * f), (int)(_tempPlot.Top + _tempPlot.Height / 2));
        }
        PaintAll(g);
        _mouse = null;
        return bmp;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        HidePanel();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) HidePanel();
        base.OnKeyDown(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitHeight();
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.Location;
        Cursor = _buttons.Any(b => b.Enabled && b.Rect.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        foreach (var b in _buttons)
        {
            if (!b.Enabled || !b.Rect.Contains(e.Location)) continue;
            b.Click();
            return;
        }
    }

    protected override void OnPaint(PaintEventArgs e) => PaintAll(e.Graphics);

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeFonts();
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- layout helpers

    private int Px(float dip) => (int)Math.Round(dip * DeviceDpi / 96f);
    private float Pf(float dip) => dip * DeviceDpi / 96f;

    private void EnsureFonts()
    {
        if (_fontDpi == DeviceDpi && _fHeader != null) return;
        DisposeFonts();
        _fontDpi = DeviceDpi;
        _fHeader = new Font("Segoe UI Semibold", Pf(14), GraphicsUnit.Pixel);
        _fTitle = new Font("Segoe UI Semibold", Pf(12), GraphicsUnit.Pixel);
        _fText = new Font("Segoe UI", Pf(12), GraphicsUnit.Pixel);
        _fSmall = new Font("Segoe UI", Pf(11), GraphicsUnit.Pixel);
        _fAxis = new Font("Segoe UI", Pf(10), GraphicsUnit.Pixel);
        _fValue = new Font("Segoe UI Semibold", Pf(18), GraphicsUnit.Pixel);
    }

    private void DisposeFonts()
    {
        foreach (var f in new[] { _fHeader, _fTitle, _fText, _fSmall, _fAxis, _fValue }) f?.Dispose();
    }

    private void FitHeight()
    {
        EnsureFonts();
        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        var size = new Size(Px(WidthDip), (int)Math.Ceiling(DrawContent(g)));
        if (ClientSize == size) return;
        int bottom = Bottom;
        ClientSize = size;
        if (Visible) Top = bottom - Height; // grow upwards, stay glued above the taskbar
    }

    private void PaintAll(Graphics g)
    {
        EnsureFonts();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(_theme.Surface);
        DrawContent(g);
    }

    private static StringFormat MakeFormat(StringAlignment alignment, bool trim = false)
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.Alignment = alignment;
        f.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
        if (trim) f.Trimming = StringTrimming.EllipsisCharacter;
        return f;
    }

    private static float Ascent(Font f) => f.Size * f.FontFamily.GetCellAscent(f.Style) / f.FontFamily.GetEmHeight(f.Style);

    private static float TextWidth(Graphics g, string s, Font f) => g.MeasureString(s, f, PointF.Empty, Near).Width;

    /// <summary>Draws text so its baseline sits on <paramref name="baseline"/>; alignment is relative to <paramref name="x"/>.</summary>
    private static void TextAt(Graphics g, string s, Font f, Color c, float x, float baseline, StringFormat? format = null)
    {
        using var brush = new SolidBrush(c);
        g.DrawString(s, f, brush, x, baseline - Ascent(f), format ?? Near);
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
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

    private static void Fill(Graphics g, RectangleF r, float radius, Color c)
    {
        using var path = Rounded(r, radius);
        using var brush = new SolidBrush(c);
        g.FillPath(brush, path);
    }

    // ---------------------------------------------------------------- content

    private float DrawContent(Graphics g)
    {
        _buttons.Clear();
        float pad = Pf(PadDip);
        if (_state is not { } s) return pad * 2;
        var r = s.Reading;
        var set = s.Settings;

        float y = pad;
        y = DrawDevice(g, y, "CPU", s.CpuName, new[]
        {
            TempTile(r.CpuTemp, set.CpuWarn, set.CpuHot),
            new Tile("Usage", Num(r.CpuLoad, "0"), Suffix: "%"),
            new Tile("Power", Num(r.CpuPower, "0"), Suffix: " W"),
            new Tile("Clock", Num(r.CpuClock / 1000, "0.0"), Suffix: " GHz"),
        });
        y += Pf(14);
        y = DrawDevice(g, y, "GPU", s.GpuName, new[]
        {
            TempTile(r.GpuTemp, set.GpuWarn, set.GpuHot),
            new Tile("Usage", Num(r.GpuLoad, "0"), Suffix: "%"),
            new Tile("Power", Num(r.GpuPower, "0"), Suffix: " W"),
            new Tile("Mem clock", Num(r.GpuMemClock / 1000, "0.0"), Suffix: " GHz"),
        });

        y += Pf(22);
        y = DrawChart(g, y, TempChart, s.History);
        y += Pf(18);
        y = DrawChart(g, y, LoadChart, s.History);
        y = Divider(g, y + Pf(14));
        y = DrawSystem(g, y, r);
        y = Divider(g, y + Pf(10));
        y = DrawStability(g, y, s);
        y = Divider(g, y + Pf(10));
        y = DrawFooter(g, y, s);
        return y + pad;
    }

    private static string Num(float? v, string format) => v is float f ? f.ToString(format, Inv) : "--";

    private static Tile TempTile(float? temp, int warn, int hot) => temp switch
    {
        float t when t >= hot => new Tile("Critical", Num(t, "0"), Suffix: "°C", Status: Theme.Critical),
        float t when t >= warn => new Tile("High", Num(t, "0"), Suffix: "°C", Status: Theme.Warning),
        _ => new Tile("Temp", Num(temp, "0"), Suffix: "°C"),
    };

    private float Divider(Graphics g, float y)
    {
        using var pen = new Pen(_theme.Grid, 1f);
        float ly = MathF.Round(y) + 0.5f;
        g.DrawLine(pen, Pf(PadDip), ly, Pf(WidthDip - PadDip), ly);
        return y + Pf(12);
    }

    private float DrawDevice(Graphics g, float y, string title, string name, Tile[] tiles)
    {
        float pad = Pf(PadDip), inner = Pf(WidthDip - 2 * PadDip);
        float baseline = y + Ascent(_fHeader!);
        TextAt(g, title, _fHeader!, _theme.Text, pad, baseline);
        float nameX = pad + TextWidth(g, title, _fHeader!) + Pf(8);
        using (var brush = new SolidBrush(_theme.TextSecondary))
            g.DrawString(name, _fText!, brush, new RectangleF(nameX, baseline - Ascent(_fText!), pad + inner - nameX, Pf(18)), NearTrim);
        y += Pf(26);

        float gap = Pf(8), tileW = (inner - 3 * gap) / 4, tileH = Pf(56);
        for (int i = 0; i < tiles.Length; i++)
            DrawTile(g, new RectangleF(pad + i * (tileW + gap), y, tileW, tileH), tiles[i]);
        return y + tileH;
    }

    private void DrawTile(Graphics g, RectangleF r, Tile tile)
    {
        Fill(g, r, Pf(6), _theme.Raised);
        float x = r.Left + Pf(10);
        float labelBase = r.Top + Pf(8) + Ascent(_fSmall!);

        if (tile.Status is Color status)
        {
            // Status = icon + label, never color alone.
            float s = Pf(8), top = labelBase - Pf(8);
            using var path = new GraphicsPath();
            path.AddPolygon(new[] { new PointF(x + s / 2, top), new PointF(x + s, top + s), new PointF(x, top + s) });
            using var brush = new SolidBrush(status);
            g.FillPath(brush, path);
            TextAt(g, tile.Label, _fSmall!, _theme.TextSecondary, x + s + Pf(4), labelBase);
        }
        else
        {
            TextAt(g, tile.Label, _fSmall!, _theme.TextSecondary, x, labelBase);
        }

        float valueBase = r.Top + Pf(24) + Ascent(_fValue!);
        if (tile.Prefix != null && tile.Value != "--")
        {
            TextAt(g, tile.Prefix, _fSmall!, _theme.TextSecondary, x, valueBase);
            x += TextWidth(g, tile.Prefix, _fSmall!) + Pf(1);
        }
        TextAt(g, tile.Value, _fValue!, _theme.Text, x, valueBase);
        if (tile.Suffix != null && tile.Value != "--")
            TextAt(g, tile.Suffix, _fSmall!, _theme.TextSecondary, x + TextWidth(g, tile.Value, _fValue!) + Pf(1), valueBase);
    }

    // ---------------------------------------------------------------- charts

    private float DrawChart(Graphics g, float y, ChartSpec spec, IReadOnlyList<Sample> samples)
    {
        float pad = Pf(PadDip), width = Pf(WidthDip);

        // Title row with the legend on the right (two series → legend always present).
        float baseline = y + Ascent(_fTitle!);
        TextAt(g, spec.Title, _fTitle!, _theme.Text, pad, baseline);
        TextAt(g, "last 10 min", _fSmall!, _theme.TextMuted, pad + TextWidth(g, spec.Title, _fTitle!) + Pf(6), baseline);
        float lx = LegendItem(g, width - pad, baseline, "GPU", _theme.Gpu);
        LegendItem(g, lx - Pf(14), baseline, "CPU", _theme.Cpu);
        y += Pf(24);

        float axisW = Pf(30), endW = Pf(34);
        var plot = new RectangleF(pad + axisW, y + Pf(4), width - 2 * pad - axisW - endW, Pf(84));
        if (spec == TempChart) _tempPlot = plot;

        DateTime end = samples.Count > 0 ? samples[^1].Time : DateTime.Now;
        DateTime start = end - SampleHistory.Window;
        var (min, max, step) = spec.Percent
            ? (0f, 100f, 50f)
            : NiceRange(samples.SelectMany(p => new[] { spec.Cpu(p.Reading), spec.Gpu(p.Reading) }));
        float Y(float v) => plot.Bottom - (Math.Clamp(v, min, max) - min) / (max - min) * plot.Height;
        float X(DateTime t) => plot.Left + (float)((t - start).TotalMilliseconds / SampleHistory.Window.TotalMilliseconds) * plot.Width;

        // Recessive hairline grid + y ticks.
        using (var grid = new Pen(_theme.Grid, 1f))
        {
            for (float v = min; v <= max + 0.01f; v += step)
            {
                float gy = MathF.Round(Y(v)) + 0.5f;
                g.DrawLine(grid, plot.Left, gy, plot.Right, gy);
                string tick = spec.Percent ? $"{v:0}%" : $"{v:0}°";
                TextAt(g, tick, _fAxis!, _theme.TextMuted, plot.Left - Pf(6), gy + Ascent(_fAxis!) / 2 - Pf(1), Far);
            }
        }

        float xBase = plot.Bottom + Pf(6) + Ascent(_fAxis!);
        TextAt(g, "10 min ago", _fAxis!, _theme.TextMuted, plot.Left, xBase);
        TextAt(g, "5 min", _fAxis!, _theme.TextMuted, plot.Left + plot.Width / 2, xBase, Center);
        TextAt(g, "now", _fAxis!, _theme.TextMuted, plot.Right, xBase, Far);

        var state = g.Save();
        g.SetClip(RectangleF.Inflate(plot, Pf(2), Pf(2)));
        DrawSeries(g, samples, spec.Cpu, _theme.Cpu, X, Y);
        DrawSeries(g, samples, spec.Gpu, _theme.Gpu, X, Y);
        g.Restore(state);

        if (samples.Count > 0)
        {
            var last = samples[^1];
            float? cpu = spec.Cpu(last.Reading), gpu = spec.Gpu(last.Reading);
            if (cpu is float c) Dot(g, X(last.Time), Y(c), _theme.Cpu);
            if (gpu is float gv) Dot(g, X(last.Time), Y(gv), _theme.Gpu);

            // End labels, unless they would collide - then legend + tooltip carry it.
            if (cpu is float c2 && gpu is float g2 && Math.Abs(Y(c2) - Y(g2)) >= _fAxis!.Size + Pf(3))
            {
                EndLabel(g, plot, Y(c2), Format(spec, c2));
                EndLabel(g, plot, Y(g2), Format(spec, g2));
            }
        }

        if (_mouse is Point m && plot.Contains(m) && samples.Count > 0)
            DrawHover(g, plot, spec, samples, X, Y, m);

        return xBase + Pf(4);
    }

    private float LegendItem(Graphics g, float right, float baseline, string label, Color color)
    {
        float textW = TextWidth(g, label, _fSmall!);
        TextAt(g, label, _fSmall!, _theme.TextSecondary, right - textW, baseline);
        float keyRight = right - textW - Pf(5), keyY = baseline - Ascent(_fSmall!) / 2 + Pf(1);
        using var pen = new Pen(color, Pf(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, keyRight - Pf(12), keyY, keyRight, keyY);
        return keyRight - Pf(12);
    }

    private void EndLabel(Graphics g, RectangleF plot, float y, string text) =>
        TextAt(g, text, _fAxis!, _theme.TextSecondary, plot.Right + Pf(9), y + Ascent(_fAxis!) / 2 - Pf(1));

    private static string Format(ChartSpec spec, float? v) =>
        v is not float f ? "--" : spec.Percent ? $"{f:0}%" : $"{f:0}°C";

    private void DrawSeries(Graphics g, IReadOnlyList<Sample> samples, Func<Reading, float?> value, Color color,
        Func<DateTime, float> x, Func<float, float> y)
    {
        using var pen = new Pen(color, Pf(2)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        var points = new List<PointF>(samples.Count);
        DateTime? previous = null;

        void Flush()
        {
            if (points.Count >= 2) g.DrawLines(pen, points.ToArray());
            points.Clear();
        }

        foreach (var sample in samples)
        {
            var v = value(sample.Reading);
            // Break the line on missing values or pauses (sleep, sensor restart) instead of bridging them.
            if (v is null || (previous is DateTime p && sample.Time - p > TimeSpan.FromSeconds(10))) Flush();
            if (v is float f) points.Add(new PointF(x(sample.Time), y(f)));
            previous = sample.Time;
        }
        Flush();
    }

    private void Dot(Graphics g, float x, float y, Color color)
    {
        float ring = Pf(6), dot = Pf(4);
        using (var surface = new SolidBrush(_theme.Surface)) g.FillEllipse(surface, x - ring, y - ring, 2 * ring, 2 * ring);
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, x - dot, y - dot, 2 * dot, 2 * dot);
    }

    private void DrawHover(Graphics g, RectangleF plot, ChartSpec spec, IReadOnlyList<Sample> samples,
        Func<DateTime, float> X, Func<float, float> Y, Point mouse)
    {
        var best = samples[0];
        float bestDistance = float.MaxValue;
        foreach (var sample in samples)
        {
            float d = Math.Abs(X(sample.Time) - mouse.X);
            if (d < bestDistance) { bestDistance = d; best = sample; }
        }

        float x = X(best.Time);
        using (var cross = new Pen(_theme.TextMuted, 1f))
            g.DrawLine(cross, x, plot.Top, x, plot.Bottom);
        float? cpu = spec.Cpu(best.Reading), gpu = spec.Gpu(best.Reading);
        if (cpu is float c) Dot(g, x, Y(c), _theme.Cpu);
        if (gpu is float gv) Dot(g, x, Y(gv), _theme.Gpu);

        string time = best.Time.ToString("HH:mm:ss", Inv);
        string cpuText = Format(spec, cpu), gpuText = Format(spec, gpu);
        float lineH = Pf(17), boxPad = Pf(8), key = Pf(10);
        float labelW = Math.Max(TextWidth(g, "CPU", _fSmall!), TextWidth(g, "GPU", _fSmall!));
        float valueW = Math.Max(TextWidth(g, cpuText, _fTitle!), TextWidth(g, gpuText, _fTitle!));
        float boxW = Math.Max(TextWidth(g, time, _fSmall!), key + Pf(6) + labelW + Pf(12) + valueW) + 2 * boxPad;
        float boxH = 3 * lineH + 2 * boxPad - Pf(4);

        float bx = x + Pf(12);
        if (bx + boxW > Pf(WidthDip) - Pf(4)) bx = x - Pf(12) - boxW;
        var box = new RectangleF(bx, plot.Top, boxW, boxH);
        Fill(g, box, Pf(5), _theme.Raised);
        using (var border = new Pen(_theme.Border, 1f))
        using (var path = Rounded(box, Pf(5)))
            g.DrawPath(border, path);

        float row = box.Top + boxPad + Ascent(_fSmall!);
        TextAt(g, time, _fSmall!, _theme.TextSecondary, box.Left + boxPad, row);
        foreach (var (label, text, color) in new[] { ("CPU", cpuText, _theme.Cpu), ("GPU", gpuText, _theme.Gpu) })
        {
            row += lineH;
            using (var pen = new Pen(color, Pf(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, box.Left + boxPad, row - Pf(4), box.Left + boxPad + key, row - Pf(4));
            TextAt(g, label, _fSmall!, _theme.TextSecondary, box.Left + boxPad + key + Pf(6), row);
            TextAt(g, text, _fTitle!, _theme.Text, box.Right - boxPad, row, Far);
        }
    }

    private static (float Min, float Max, float Step) NiceRange(IEnumerable<float?> values)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var v in values)
        {
            if (v is not float f) continue;
            lo = Math.Min(lo, f);
            hi = Math.Max(hi, f);
        }
        if (lo > hi) (lo, hi) = (30, 70);

        float min = MathF.Floor((lo - 2) / 10) * 10;
        float max = MathF.Ceiling((hi + 2) / 10) * 10;
        if (max - min < 20) max = min + 20;
        float step = max - min > 40 ? 20 : 10;
        return (MathF.Floor(min / step) * step, MathF.Ceiling(max / step) * step, step);
    }

    // ---------------------------------------------------------------- system / stability / footer

    private float SectionTitle(Graphics g, float y, string title)
    {
        TextAt(g, title, _fTitle!, _theme.Text, Pf(PadDip), y + Ascent(_fTitle!));
        return y + Pf(24);
    }

    private float DrawSystem(Graphics g, float y, Reading r)
    {
        y = SectionTitle(g, y, "System");
        var items = new List<(string Label, string Value)>();
        if (r.SocVoltage is float soc) items.Add(("SoC voltage", $"{soc.ToString("0.00", Inv)} V"));
        if (r.CoreVoltage is float vcore) items.Add(("Vcore", $"{vcore.ToString("0.00", Inv)} V"));
        if (r.RamUsedGb is float used && r.RamTotalGb is float total)
            items.Add(("RAM", $"{used.ToString("0.0", Inv)} / {total.ToString("0", Inv)} GB"));
        if (r.GpuMemTemp is float vram) items.Add(("GPU memory", $"{vram:0}°C"));
        foreach (var (name, rpm) in r.Fans) items.Add((FanName(name), $"{rpm:0} rpm"));
        if (items.Count == 0) items.Add(("Sensors", "need admin rights"));

        float pad = Pf(PadDip), colGap = Pf(24), colW = (Pf(WidthDip) - 2 * pad - colGap) / 2, rowH = Pf(20);
        for (int i = 0; i < items.Count; i++)
        {
            float x = pad + (i % 2) * (colW + colGap);
            float baseline = y + (i / 2) * rowH + Ascent(_fText!);
            TextAt(g, items[i].Label, _fText!, _theme.TextSecondary, x, baseline);
            TextAt(g, items[i].Value, _fText!, _theme.Text, x + colW, baseline, Far);
        }
        return y + (items.Count + 1) / 2 * rowH;
    }

    private static string FanName(string name) => name switch
    {
        "CPU Fan" => "CPU fan",
        "Chipset Fan" => "Chipset fan",
        _ when name.StartsWith("System Fan") => "Case fan" + name["System Fan".Length..].Replace("#", ""),
        _ when name.StartsWith("Pump Fan") => "Pump" + name["Pump Fan".Length..].Replace("#", ""),
        _ => name,
    };

    private float DrawStability(Graphics g, float y, PanelState s)
    {
        float pad = Pf(PadDip), right = Pf(WidthDip - PadDip);
        float titleBase = y + Ascent(_fTitle!);
        TextAt(g, "Stability", _fTitle!, _theme.Text, pad, titleBase);
        if (s.Events.Count > 0)
            TextButton(g, right, titleBase, "Mark as seen", () => StabilityAckClicked?.Invoke());
        y += Pf(26);

        Color color;
        string headline;
        if (!s.StabilityAvailable) (color, headline) = (Theme.Warning, "Event log unavailable");
        else if (s.Events.Count == 0) (color, headline) = (Theme.Good, "No hardware errors");
        else (color, headline) = (s.Events.Any(e => e.Serious) ? Theme.Critical : Theme.Warning, $"{s.Events.Count} event{(s.Events.Count == 1 ? "" : "s")} logged");

        float icon = Pf(16), baseline = y + Ascent(_fText!);
        var iconRect = new RectangleF(pad, baseline - Ascent(_fText!) / 2 - icon / 2 - Pf(1), icon, icon);
        StatusIcon(g, iconRect, color, ok: s.StabilityAvailable && s.Events.Count == 0);
        float tx = pad + icon + Pf(8);
        TextAt(g, headline, _fText!, _theme.Text, tx, baseline);
        string scope = s.StabilityAckTime is DateTime ack ? $"· since {ack.ToString("MMM d, HH:mm", Inv)}" : "· last 7 days";
        TextAt(g, scope, _fSmall!, _theme.TextMuted, tx + TextWidth(g, headline, _fText!) + Pf(6), baseline);
        y += Pf(24);

        foreach (var ev in s.Events.Take(3))
        {
            float rowBase = y + Ascent(_fSmall!);
            string when = ev.Time.ToString("MMM d, HH:mm", Inv);
            TextAt(g, when, _fSmall!, _theme.TextMuted, tx, rowBase);
            float titleX = tx + TextWidth(g, "Sep 00, 00:00", _fSmall!) + Pf(8);
            using (var brush = new SolidBrush(_theme.TextSecondary))
                g.DrawString(ev.Title, _fSmall!, brush, new RectangleF(titleX, y, right - titleX, Pf(16)), NearTrim);
            y += Pf(18);
        }
        if (s.Events.Count > 3)
        {
            TextAt(g, $"+{s.Events.Count - 3} more", _fSmall!, _theme.TextMuted, tx, y + Ascent(_fSmall!));
            y += Pf(18);
        }
        return y;
    }

    private void StatusIcon(Graphics g, RectangleF r, Color color, bool ok)
    {
        using (var brush = new SolidBrush(color)) g.FillEllipse(brush, r);
        // Dark glyph on the yellow warning disc, white elsewhere, for contrast.
        var glyph = color == Theme.Warning ? Color.FromArgb(31, 31, 31) : Color.White;
        using var pen = new Pen(glyph, Pf(1.8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (ok)
        {
            g.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.28f, r.Top + r.Height * 0.52f),
                new PointF(r.Left + r.Width * 0.44f, r.Top + r.Height * 0.68f),
                new PointF(r.Left + r.Width * 0.72f, r.Top + r.Height * 0.34f),
            });
        }
        else
        {
            float cx = r.Left + r.Width / 2;
            g.DrawLine(pen, cx, r.Top + r.Height * 0.25f, cx, r.Top + r.Height * 0.56f);
            using var dot = new SolidBrush(glyph);
            float d = Pf(2.2f);
            g.FillEllipse(dot, cx - d / 2, r.Top + r.Height * 0.70f, d, d);
        }
    }

    private void TextButton(Graphics g, float right, float baseline, string text, Action click)
    {
        float w = TextWidth(g, text, _fSmall!) + Pf(12), h = Pf(22);
        var rect = new RectangleF(right - w, baseline - Ascent(_fSmall!) / 2 - h / 2 - Pf(1), w, h);
        bool hover = _mouse is Point m && rect.Contains(m);
        Fill(g, rect, Pf(4), hover ? _theme.RaisedHover : _theme.Raised);
        TextAt(g, text, _fSmall!, _theme.Text, rect.Left + rect.Width / 2, baseline, Center);
        _buttons.Add((rect, click, true));
    }

    private float DrawFooter(Graphics g, float y, PanelState s)
    {
        static string Max((float Value, DateTime At)? m) => m is { } v ? $"{v.Value:0}°C ({v.At:HH:mm})" : "--";

        float pad = Pf(PadDip), inner = Pf(WidthDip - 2 * PadDip);
        TextAt(g, $"Max CPU {Max(s.MaxCpu)}   ·   GPU {Max(s.MaxGpu)}", _fSmall!, _theme.TextSecondary, pad, y + Ascent(_fSmall!));
        y += Pf(24);

        float gap = Pf(8), w = (inner - 2 * gap) / 3, h = Pf(32);
        Button(g, new RectangleF(pad, y, w, h), "Task Manager", () => TaskManagerClicked?.Invoke(), true);
        Button(g, new RectangleF(pad + w + gap, y, w, h), "Spike log", () => SpikeLogClicked?.Invoke(), s.SpikeLogExists);
        Button(g, new RectangleF(pad + 2 * (w + gap), y, w, h), "Settings", () => SettingsClicked?.Invoke(), true);
        return y + h;
    }

    private void Button(Graphics g, RectangleF rect, string text, Action click, bool enabled)
    {
        bool hover = enabled && _mouse is Point m && rect.Contains(m);
        Fill(g, rect, Pf(5), hover ? _theme.RaisedHover : _theme.Raised);
        float baseline = rect.Top + rect.Height / 2 + Ascent(_fText!) / 2 - Pf(1);
        TextAt(g, text, _fText!, enabled ? _theme.Text : _theme.TextMuted, rect.Left + rect.Width / 2, baseline, Center);
        _buttons.Add((rect, click, enabled));
    }
}
