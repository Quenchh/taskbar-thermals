using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace TaskbarThermals;

/// <summary>Everything the panel draws, captured on the UI thread each tick.</summary>
internal sealed record PanelState(
    Reading Reading,
    IReadOnlyList<Sample> History,
    IReadOnlyList<MinuteSample> Minutes,
    string CpuName,
    string GpuName,
    (float Value, DateTime At)? MaxCpu,
    (float Value, DateTime At)? MaxGpu,
    IReadOnlyList<StabilityEvent> Events,
    bool StabilityAvailable,
    DateTime? StabilityAckTime,
    bool SpikeLogExists,
    Settings Settings);

/// <summary>Flyout above the taskbar overlay: current values, history charts, board sensors and stability status.</summary>
internal sealed class DetailPanel : Form
{
    private const float WidthDip = 380;
    private const float PadDip = 16;

    private sealed record ChartSpec(bool Percent, Func<Reading, float?> Cpu, Func<Reading, float?> Gpu, Func<MinuteSample, MinuteStat?> CpuMinute, Func<MinuteSample, MinuteStat?> GpuMinute);

    private static readonly ChartSpec TempChart = new(false, r => r.CpuTemp, r => r.GpuTemp, m => m.CpuTemp, m => m.GpuTemp);
    private static readonly ChartSpec LoadChart = new(true, r => r.CpuLoad, r => r.GpuLoad, m => m.CpuLoad, m => m.GpuLoad);

    /// <summary>One pixel column of a chart: the average line plus the min–max band.</summary>
    private readonly record struct Bucket(DateTime Time, float Avg, float Min, float Max, bool Has);

    private readonly record struct Tile(string Label, string Value, string? Prefix = null, string? Suffix = null, Color? Status = null);

    private static readonly StringFormat Near = MakeFormat(StringAlignment.Near);
    private static readonly StringFormat Far = MakeFormat(StringAlignment.Far);
    private static readonly StringFormat Center = MakeFormat(StringAlignment.Center);
    private static readonly StringFormat NearTrim = MakeFormat(StringAlignment.Near, trim: true);

    public event Action? TaskManagerClicked;
    public event Action? SpikeLogClicked;
    public event Action? SettingsClicked;
    public event Action? StabilityAckClicked;
    public event Action<HistoryRange>? RangeChanged;

    private PanelState? _state;
    private Theme _theme = Theme.Current;
    private int _fontDpi;
    private Font? _fHeader, _fTitle, _fText, _fSmall, _fAxis, _fValue;

    // Hit targets and chart areas recorded during the last paint.
    private readonly List<(RectangleF Rect, Action Click, bool Enabled)> _buttons = new();
    private readonly List<RectangleF> _plots = new();
    private readonly Dictionary<HistoryRange, RectangleF> _rangeButtons = new();
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

    /// <summary>Renders the current state off-screen, optionally with the mouse at <paramref name="mouse"/> (--preview / --demo).</summary>
    public Bitmap Snapshot(Point? mouse = null)
    {
        FitHeight();
        var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
        using var g = Graphics.FromImage(bmp);
        _mouse = mouse;
        PaintAll(g);
        _mouse = null;
        return bmp;
    }

    /// <summary>A point inside chart <paramref name="index"/> (0 = temperature, 1 = usage) as of the last render.</summary>
    public Point ChartPoint(int index, float fx, float fy)
    {
        var p = _plots[index];
        return new Point((int)(p.Left + p.Width * fx), (int)(p.Top + p.Height * fy));
    }

    /// <summary>Center of a history range button as of the last render.</summary>
    public Point RangeButton(HistoryRange range)
    {
        var r = _rangeButtons[range];
        return new Point((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
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

    private bool Hovered(RectangleF r) => _mouse is Point m && r.Contains(m);

    // ---------------------------------------------------------------- content

    private float DrawContent(Graphics g)
    {
        _buttons.Clear();
        _plots.Clear();
        float pad = Pf(PadDip);
        if (_state is not { } s) return pad * 2;
        var r = s.Reading;
        var set = s.Settings;

        float y = pad;
        y = DrawDevice(g, y, "CPU", s.CpuName, new[]
        {
            TempTile(r.CpuTemp, set.CpuWarn, set.CpuHot),
            PercentTile(L.Usage, r.CpuLoad),
            new Tile(L.Power, Num(r.CpuPower, "0"), Suffix: " W"),
            new Tile(L.Clock, Num(r.CpuClock / 1000, "0.0"), Suffix: " GHz"),
        });
        y += Pf(14);
        y = DrawDevice(g, y, "GPU", s.GpuName, new[]
        {
            TempTile(r.GpuTemp, set.GpuWarn, set.GpuHot),
            PercentTile(L.Usage, r.GpuLoad),
            new Tile(L.Power, Num(r.GpuPower, "0"), Suffix: " W"),
            new Tile(L.MemClock, Num(r.GpuMemClock / 1000, "0.0"), Suffix: " GHz"),
        });

        y = Divider(g, y + Pf(16));
        y = DrawRangeSelector(g, y, set.HistoryRange);
        y = DrawChart(g, y, L.Temperature, TempChart, s);
        y += Pf(18);
        y = DrawChart(g, y, L.Usage, LoadChart, s);
        y = Divider(g, y + Pf(14));
        y = DrawSystem(g, y, r);
        y = Divider(g, y + Pf(10));
        y = DrawStability(g, y, s);
        y = Divider(g, y + Pf(10));
        y = DrawFooter(g, y, s);
        return y + pad;
    }

    private static string Num(float? v, string format) => v is float f ? L.Number(f, format) : "--";

    private static Tile PercentTile(string label, float? v) =>
        L.Turkish ? new Tile(label, Num(v, "0"), Prefix: "%") : new Tile(label, Num(v, "0"), Suffix: "%");

    private static Tile TempTile(float? temp, int warn, int hot) => temp switch
    {
        float t when t >= hot => new Tile(L.Critical, Num(t, "0"), Suffix: "°C", Status: Theme.Critical),
        float t when t >= warn => new Tile(L.High, Num(t, "0"), Suffix: "°C", Status: Theme.Warning),
        _ => new Tile(L.Temp, Num(temp, "0"), Suffix: "°C"),
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

    // ---------------------------------------------------------------- history charts

    private float DrawRangeSelector(Graphics g, float y, HistoryRange current)
    {
        float pad = Pf(PadDip), right = Pf(WidthDip - PadDip), h = Pf(26);
        TextAt(g, L.History, _fTitle!, _theme.Text, pad, y + h / 2 + Ascent(_fTitle!) / 2 - Pf(1));

        var ranges = Enum.GetValues<HistoryRange>();
        float segW = Pf(56), total = segW * ranges.Length;
        var box = new RectangleF(right - total, y, total, h);
        Fill(g, box, Pf(6), _theme.Raised);
        for (int i = 0; i < ranges.Length; i++)
        {
            var range = ranges[i];
            var seg = new RectangleF(box.Left + i * segW, box.Top, segW, h);
            _rangeButtons[range] = seg;
            bool selected = range == current;
            if (selected || Hovered(seg))
                Fill(g, RectangleF.Inflate(seg, -Pf(2), -Pf(2)), Pf(5), selected ? _theme.RaisedHover : Color.FromArgb(_theme.Light ? 10 : 14, _theme.Text));
            TextAt(g, L.RangeName(range), selected ? _fTitle! : _fSmall!, selected ? _theme.Text : _theme.TextSecondary,
                seg.Left + segW / 2, seg.Top + h / 2 + Ascent(_fSmall!) / 2 - Pf(1), Center);
            if (!selected) _buttons.Add((seg, () => RangeChanged?.Invoke(range), true));
        }
        return y + h + Pf(14);
    }

    private float DrawChart(Graphics g, float y, string title, ChartSpec spec, PanelState s)
    {
        float pad = Pf(PadDip), width = Pf(WidthDip);
        var range = s.Settings.HistoryRange;

        // Title row with the legend on the right (two series → legend always present).
        float baseline = y + Ascent(_fTitle!);
        TextAt(g, title, _fTitle!, _theme.Text, pad, baseline);
        float lx = LegendItem(g, width - pad, baseline, "GPU", _theme.Gpu);
        LegendItem(g, lx - Pf(14), baseline, "CPU", _theme.Cpu);
        y += Pf(24);

        float axisW = Pf(30), endW = Pf(34);
        var plot = new RectangleF(pad + axisW, y + Pf(4), width - 2 * pad - axisW - endW, Pf(84));
        _plots.Add(plot);

        TimeSpan span = range switch { HistoryRange.Hour => TimeSpan.FromHours(1), HistoryRange.Day => TimeSpan.FromHours(24), _ => TimeSpan.FromMinutes(10) };
        DateTime end = s.History.Count > 0 ? s.History[^1].Time : DateTime.Now;
        DateTime start = end - span;
        var (cpu, gpu) = Buckets(spec, s, range, start, span, (int)plot.Width);
        TimeSpan bucketSpan = span / cpu.Length;

        var (min, max, step) = spec.Percent
            ? (0f, 100f, 50f)
            : NiceRange(cpu.Concat(gpu).Where(b => b.Has).SelectMany(b => new[] { b.Min, b.Max }));
        float Y(float v) => plot.Bottom - (Math.Clamp(v, min, max) - min) / (max - min) * plot.Height;
        float X(int i) => plot.Left + (i + 0.5f) * plot.Width / cpu.Length;

        // Recessive hairline grid + y ticks.
        using (var grid = new Pen(_theme.Grid, 1f))
        {
            for (float v = min; v <= max + 0.01f; v += step)
            {
                float gy = MathF.Round(Y(v)) + 0.5f;
                g.DrawLine(grid, plot.Left, gy, plot.Right, gy);
                string tick = spec.Percent ? L.Percent(v) : $"{v:0}°";
                TextAt(g, tick, _fAxis!, _theme.TextMuted, plot.Left - Pf(6), gy + Ascent(_fAxis!) / 2 - Pf(1), Far);
            }
        }

        float xBase = plot.Bottom + Pf(6) + Ascent(_fAxis!);
        TextAt(g, L.RangeStart(range), _fAxis!, _theme.TextMuted, plot.Left, xBase);
        TextAt(g, L.RangeMiddle(range), _fAxis!, _theme.TextMuted, plot.Left + plot.Width / 2, xBase, Center);
        TextAt(g, L.Now, _fAxis!, _theme.TextMuted, plot.Right, xBase, Far);

        var state = g.Save();
        g.SetClip(RectangleF.Inflate(plot, Pf(2), Pf(2)));
        DrawSeries(g, cpu, _theme.Cpu, X, Y, bucketSpan);
        DrawSeries(g, gpu, _theme.Gpu, X, Y, bucketSpan);
        g.Restore(state);

        int lastCpu = Array.FindLastIndex(cpu, b => b.Has), lastGpu = Array.FindLastIndex(gpu, b => b.Has);
        if (lastCpu >= 0) Dot(g, X(lastCpu), Y(cpu[lastCpu].Avg), _theme.Cpu);
        if (lastGpu >= 0) Dot(g, X(lastGpu), Y(gpu[lastGpu].Avg), _theme.Gpu);

        // End labels, unless they would collide - then legend + tooltip carry it.
        if (lastCpu >= 0 && lastGpu >= 0 && Math.Abs(Y(cpu[lastCpu].Avg) - Y(gpu[lastGpu].Avg)) >= _fAxis!.Size + Pf(3))
        {
            EndLabel(g, plot, Y(cpu[lastCpu].Avg), Format(spec, cpu[lastCpu].Avg));
            EndLabel(g, plot, Y(gpu[lastGpu].Avg), Format(spec, gpu[lastGpu].Avg));
        }

        if (_mouse is Point m && plot.Contains(m))
            DrawHover(g, plot, spec, range, cpu, gpu, X, Y, m);

        return xBase + Pf(4);
    }

    /// <summary>
    /// Folds the raw data into one bucket per pixel column (never more buckets than samples), so an hour of
    /// per-second readings or a day of per-minute aggregates both draw as a clean line with a min–max band.
    /// </summary>
    private static (Bucket[] Cpu, Bucket[] Gpu) Buckets(ChartSpec spec, PanelState s, HistoryRange range, DateTime start, TimeSpan span, int pixels)
    {
        double sampleSeconds = range == HistoryRange.Day ? 60 : s.Settings.IntervalMs / 1000.0;
        int count = Math.Clamp((int)(span.TotalSeconds / sampleSeconds), 1, Math.Max(1, pixels));

        IEnumerable<(DateTime T, float Avg, float Min, float Max)> Points(bool gpu) => range == HistoryRange.Day
            ? s.Minutes
                .Select(m => (m.Time.AddSeconds(30), gpu ? spec.GpuMinute(m) : spec.CpuMinute(m)))
                .Where(p => p.Item2.HasValue)
                .Select(p => (p.Item1, p.Item2!.Value.Avg, p.Item2.Value.Min, p.Item2.Value.Max))
            : s.History
                .Select(h => (h.Time, gpu ? spec.Gpu(h.Reading) : spec.Cpu(h.Reading)))
                .Where(p => p.Item2.HasValue)
                .Select(p => (p.Time, p.Item2!.Value, p.Item2.Value, p.Item2.Value));

        return (Fold(Points(false), start, span, count), Fold(Points(true), start, span, count));
    }

    private static Bucket[] Fold(IEnumerable<(DateTime T, float Avg, float Min, float Max)> points, DateTime start, TimeSpan span, int count)
    {
        var sum = new double[count];
        var n = new int[count];
        var min = new float[count];
        var max = new float[count];
        Array.Fill(min, float.MaxValue);
        Array.Fill(max, float.MinValue);

        foreach (var p in points)
        {
            if (p.T < start) continue;
            int i = Math.Min(count - 1, (int)((p.T - start).Ticks * count / span.Ticks));
            sum[i] += p.Avg;
            n[i]++;
            min[i] = Math.Min(min[i], p.Min);
            max[i] = Math.Max(max[i], p.Max);
        }

        var buckets = new Bucket[count];
        for (int i = 0; i < count; i++)
        {
            var time = start + span * ((i + 0.5) / count);
            buckets[i] = n[i] > 0 ? new Bucket(time, (float)(sum[i] / n[i]), min[i], max[i], true) : new Bucket(time, 0, 0, 0, false);
        }
        return buckets;
    }

    private void DrawSeries(Graphics g, Bucket[] buckets, Color color, Func<int, float> x, Func<float, float> y, TimeSpan bucketSpan)
    {
        // Bridge a few empty columns (sampling jitter) but break the line on real pauses (sleep, app restart).
        int maxGap = Math.Max(3, (int)Math.Ceiling(TimeSpan.FromSeconds(30) / bucketSpan));
        using var band = new SolidBrush(Color.FromArgb(26, color));
        using var pen = new Pen(color, Pf(2)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

        var run = new List<int>();
        void Flush()
        {
            if (run.Count >= 2)
            {
                var outline = run.Select(i => new PointF(x(i), y(buckets[i].Max)))
                    .Concat(run.AsEnumerable().Reverse().Select(i => new PointF(x(i), y(buckets[i].Min))))
                    .ToArray();
                g.FillPolygon(band, outline);
                g.DrawLines(pen, run.Select(i => new PointF(x(i), y(buckets[i].Avg))).ToArray());
            }
            run.Clear();
        }

        for (int i = 0; i < buckets.Length; i++)
        {
            if (!buckets[i].Has) continue;
            if (run.Count > 0 && i - run[^1] > maxGap) Flush();
            run.Add(i);
        }
        Flush();
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

    private static string Format(ChartSpec spec, float v) => spec.Percent ? L.Percent(v) : $"{v:0}°C";

    private void Dot(Graphics g, float x, float y, Color color)
    {
        float ring = Pf(6), dot = Pf(4);
        using (var surface = new SolidBrush(_theme.Surface)) g.FillEllipse(surface, x - ring, y - ring, 2 * ring, 2 * ring);
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, x - dot, y - dot, 2 * dot, 2 * dot);
    }

    private void DrawHover(Graphics g, RectangleF plot, ChartSpec spec, HistoryRange range, Bucket[] cpu, Bucket[] gpu,
        Func<int, float> X, Func<float, float> Y, Point mouse)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < cpu.Length; i++)
        {
            if (!cpu[i].Has && !gpu[i].Has) continue;
            float d = Math.Abs(X(i) - mouse.X);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        if (best < 0) return;

        float x = X(best);
        using (var cross = new Pen(_theme.TextMuted, 1f))
            g.DrawLine(cross, x, plot.Top, x, plot.Bottom);
        if (cpu[best].Has) Dot(g, x, Y(cpu[best].Avg), _theme.Cpu);
        if (gpu[best].Has) Dot(g, x, Y(gpu[best].Avg), _theme.Gpu);

        // Longer ranges average many samples per column, so also show the peak that the average hides.
        bool showMax = range != HistoryRange.TenMinutes;
        string time = cpu[best].Time.ToString(range == HistoryRange.Day ? "HH:mm" : "HH:mm:ss", L.Culture);
        var rows = new[] { ("CPU", cpu[best], _theme.Cpu), ("GPU", gpu[best], _theme.Gpu) };
        string Value(Bucket b) => b.Has ? Format(spec, b.Avg) : "--";
        string Peak(Bucket b) => showMax && b.Has ? $"{L.MaxShort} {Format(spec, b.Max)}" : "";

        float lineH = Pf(17), boxPad = Pf(8), key = Pf(10);
        float labelW = rows.Max(r => TextWidth(g, r.Item1, _fSmall!));
        float valueW = rows.Max(r => TextWidth(g, Value(r.Item2), _fTitle!));
        float peakW = rows.Max(r => TextWidth(g, Peak(r.Item2), _fSmall!));
        float boxW = Math.Max(TextWidth(g, time, _fSmall!), key + Pf(6) + labelW + Pf(12) + valueW + (peakW > 0 ? Pf(8) + peakW : 0)) + 2 * boxPad;
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
        float valueRight = box.Left + boxPad + key + Pf(6) + labelW + Pf(12) + valueW;
        foreach (var (label, bucket, color) in rows)
        {
            row += lineH;
            using (var pen = new Pen(color, Pf(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, box.Left + boxPad, row - Pf(4), box.Left + boxPad + key, row - Pf(4));
            TextAt(g, label, _fSmall!, _theme.TextSecondary, box.Left + boxPad + key + Pf(6), row);
            TextAt(g, Value(bucket), _fTitle!, _theme.Text, valueRight, row, Far);
            if (peakW > 0) TextAt(g, Peak(bucket), _fSmall!, _theme.TextMuted, valueRight + Pf(8), row);
        }
    }

    private static (float Min, float Max, float Step) NiceRange(IEnumerable<float> values)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var v in values)
        {
            lo = Math.Min(lo, v);
            hi = Math.Max(hi, v);
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
        y = SectionTitle(g, y, L.System);
        var items = new List<(string Label, string Value)>();
        if (r.SocVoltage is float soc) items.Add((L.SocVoltage, $"{L.Number(soc, "0.00")} V"));
        if (r.CoreVoltage is float vcore) items.Add(("Vcore", $"{L.Number(vcore, "0.00")} V"));
        if (r.RamUsedGb is float used && r.RamTotalGb is float total)
            items.Add(("RAM", $"{L.Number(used, "0.0")} / {L.Number(total, "0")} GB"));
        if (r.GpuMemTemp is float vram) items.Add((L.GpuMemory, $"{vram:0}°C"));
        if (r.NetDown is float down) items.Add((L.Download, L.Rate(down)));
        if (r.NetUp is float up) items.Add((L.Upload, L.Rate(up)));
        if (r.DiskRead is float read) items.Add((L.DiskRead, L.Rate(read)));
        if (r.DiskWrite is float write) items.Add((L.DiskWrite, L.Rate(write)));
        foreach (var (name, rpm) in r.Fans) items.Add((L.FanName(name), $"{rpm:0} rpm"));
        if (r.SocVoltage is null && r.Fans.Count == 0) items.Add((L.Sensors, L.NeedAdmin));

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

    private float DrawStability(Graphics g, float y, PanelState s)
    {
        float pad = Pf(PadDip), right = Pf(WidthDip - PadDip);
        float titleBase = y + Ascent(_fTitle!);
        TextAt(g, L.Stability, _fTitle!, _theme.Text, pad, titleBase);
        if (s.Events.Count > 0)
            TextButton(g, right, titleBase, L.MarkSeen, () => StabilityAckClicked?.Invoke());
        y += Pf(26);

        Color color;
        string headline;
        if (!s.StabilityAvailable) (color, headline) = (Theme.Warning, L.EventLogUnavailable);
        else if (s.Events.Count == 0) (color, headline) = (Theme.Good, L.NoHardwareErrors);
        else (color, headline) = (s.Events.Any(e => e.Serious) ? Theme.Critical : Theme.Warning, L.EventsLogged(s.Events.Count));

        float icon = Pf(16), baseline = y + Ascent(_fText!);
        var iconRect = new RectangleF(pad, baseline - Ascent(_fText!) / 2 - icon / 2 - Pf(1), icon, icon);
        StatusIcon(g, iconRect, color, ok: s.StabilityAvailable && s.Events.Count == 0);
        float tx = pad + icon + Pf(8);
        TextAt(g, headline, _fText!, _theme.Text, tx, baseline);
        string scope = s.StabilityAckTime is DateTime ack ? L.Since(ack) : L.LastSevenDays;
        TextAt(g, scope, _fSmall!, _theme.TextMuted, tx + TextWidth(g, headline, _fText!) + Pf(6), baseline);
        y += Pf(24);

        float whenW = TextWidth(g, L.When(new DateTime(2026, 12, 28, 20, 58, 0)), _fSmall!);
        foreach (var ev in s.Events.Take(3))
        {
            TextAt(g, L.When(ev.Time), _fSmall!, _theme.TextMuted, tx, y + Ascent(_fSmall!));
            float titleX = tx + whenW + Pf(8);
            using (var brush = new SolidBrush(_theme.TextSecondary))
                g.DrawString(ev.Title, _fSmall!, brush, new RectangleF(titleX, y, right - titleX, Pf(16)), NearTrim);
            y += Pf(18);
        }
        if (s.Events.Count > 3)
        {
            TextAt(g, L.More(s.Events.Count - 3), _fSmall!, _theme.TextMuted, tx, y + Ascent(_fSmall!));
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
        Fill(g, rect, Pf(4), Hovered(rect) ? _theme.RaisedHover : _theme.Raised);
        TextAt(g, text, _fSmall!, _theme.Text, rect.Left + rect.Width / 2, baseline, Center);
        _buttons.Add((rect, click, true));
    }

    private float DrawFooter(Graphics g, float y, PanelState s)
    {
        static string Max((float Value, DateTime At)? m) => m is { } v ? $"{v.Value:0}°C ({v.At:HH:mm})" : "--";

        float pad = Pf(PadDip), inner = Pf(WidthDip - 2 * PadDip);
        TextAt(g, L.FooterMax(Max(s.MaxCpu), Max(s.MaxGpu)), _fSmall!, _theme.TextSecondary, pad, y + Ascent(_fSmall!));
        y += Pf(24);

        float gap = Pf(8), w = (inner - 2 * gap) / 3, h = Pf(32);
        Button(g, new RectangleF(pad, y, w, h), L.TaskManager, () => TaskManagerClicked?.Invoke(), true);
        Button(g, new RectangleF(pad + w + gap, y, w, h), L.SpikeLog, () => SpikeLogClicked?.Invoke(), s.SpikeLogExists);
        Button(g, new RectangleF(pad + 2 * (w + gap), y, w, h), L.Settings, () => SettingsClicked?.Invoke(), true);
        return y + h;
    }

    private void Button(Graphics g, RectangleF rect, string text, Action click, bool enabled)
    {
        bool hover = enabled && Hovered(rect);
        Fill(g, rect, Pf(5), hover ? _theme.RaisedHover : _theme.Raised);
        float baseline = rect.Top + rect.Height / 2 + Ascent(_fText!) / 2 - Pf(1);
        TextAt(g, text, _fText!, enabled ? _theme.Text : _theme.TextMuted, rect.Left + rect.Width / 2, baseline, Center);
        _buttons.Add((rect, click, enabled));
    }
}
