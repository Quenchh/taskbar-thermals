using System.Globalization;

namespace TaskbarThermals;

internal readonly record struct Sample(DateTime Time, Reading Reading);

/// <summary>Per-second readings for the last hour (10-minute and 1-hour charts).</summary>
internal sealed class SampleHistory
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly List<Sample> _samples = new();

    public IReadOnlyList<Sample> Samples => _samples;

    public void Add(DateTime time, Reading reading)
    {
        _samples.Add(new Sample(time, reading));
        int stale = 0;
        while (stale < _samples.Count && time - _samples[stale].Time > Window) stale++;
        if (stale > 0) _samples.RemoveRange(0, stale);
    }
}

/// <summary>Average, minimum and maximum of one series over one minute.</summary>
internal readonly record struct MinuteStat(float Avg, float Min, float Max);

internal sealed record MinuteSample(DateTime Time, MinuteStat? CpuTemp, MinuteStat? GpuTemp, MinuteStat? CpuLoad, MinuteStat? GpuLoad);

/// <summary>
/// One aggregate per minute for the last 24 hours, persisted to history.csv so the 24-hour chart
/// survives restarts. The CSV is also a plain export for anyone who wants to graph it elsewhere.
/// </summary>
internal sealed class MinuteHistory
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    private const string Header = "time,cpu_temp_avg,cpu_temp_min,cpu_temp_max,gpu_temp_avg,gpu_temp_min,gpu_temp_max," +
                                  "cpu_load_avg,cpu_load_min,cpu_load_max,gpu_load_avg,gpu_load_min,gpu_load_max";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly List<MinuteSample> _minutes = new();
    private readonly string? _path;
    private readonly Accumulator _cpuTemp = new(), _gpuTemp = new(), _cpuLoad = new(), _gpuLoad = new();
    private DateTime _minute;

    /// <param name="path">CSV file to load from and append to; null keeps everything in memory.</param>
    public MinuteHistory(string? path) => _path = path;

    public IReadOnlyList<MinuteSample> Minutes => _minutes;

    public void Load()
    {
        if (_path == null || !File.Exists(_path)) return;
        try
        {
            var lines = File.ReadAllLines(_path);
            var cutoff = DateTime.Now - Window;
            foreach (var line in lines.Skip(1))
                if (Parse(line) is { } m && m.Time >= cutoff) _minutes.Add(m);

            // Keep the file from growing forever: rewrite it once it holds more than two days.
            if (lines.Length > 2 * 24 * 60 + 1)
                File.WriteAllLines(_path, new[] { Header }.Concat(_minutes.Select(Format)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppPaths.LogError(ex);
        }
    }

    public void Add(DateTime time, Reading r)
    {
        var minute = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, time.Kind);
        if (minute != _minute)
        {
            Flush();
            _minute = minute;
        }
        _cpuTemp.Add(r.CpuTemp);
        _gpuTemp.Add(r.GpuTemp);
        _cpuLoad.Add(r.CpuLoad);
        _gpuLoad.Add(r.GpuLoad);
    }

    /// <summary>Closes the current minute (also called on exit so the last partial minute isn't lost).</summary>
    public void Flush()
    {
        if (_minute == default || (_cpuTemp.Empty && _gpuTemp.Empty && _cpuLoad.Empty && _gpuLoad.Empty)) return;

        var sample = new MinuteSample(_minute, _cpuTemp.Take(), _gpuTemp.Take(), _cpuLoad.Take(), _gpuLoad.Take());
        _minutes.Add(sample);
        int stale = 0;
        while (stale < _minutes.Count && sample.Time - _minutes[stale].Time > Window) stale++;
        if (stale > 0) _minutes.RemoveRange(0, stale);

        if (_path == null) return;
        try
        {
            if (!File.Exists(_path)) File.WriteAllText(_path, Header + Environment.NewLine);
            File.AppendAllText(_path, Format(sample) + Environment.NewLine);
        }
        catch (IOException)
        {
            // Open in another program; the in-memory history still has it.
        }
    }

    private static string Format(MinuteSample m)
    {
        static string Stat(MinuteStat? s) => s is { } v
            ? $"{v.Avg.ToString("0.0", Inv)},{v.Min.ToString("0.0", Inv)},{v.Max.ToString("0.0", Inv)}"
            : ",,";
        return $"{m.Time.ToString("yyyy-MM-ddTHH:mm", Inv)},{Stat(m.CpuTemp)},{Stat(m.GpuTemp)},{Stat(m.CpuLoad)},{Stat(m.GpuLoad)}";
    }

    private static MinuteSample? Parse(string line)
    {
        var f = line.Split(',');
        if (f.Length < 13 || !DateTime.TryParseExact(f[0], "yyyy-MM-ddTHH:mm", Inv, DateTimeStyles.None, out var time)) return null;

        MinuteStat? Stat(int i) =>
            float.TryParse(f[i], NumberStyles.Float, Inv, out var avg)
            && float.TryParse(f[i + 1], NumberStyles.Float, Inv, out var min)
            && float.TryParse(f[i + 2], NumberStyles.Float, Inv, out var max)
                ? new MinuteStat(avg, min, max)
                : null;

        return new MinuteSample(time, Stat(1), Stat(4), Stat(7), Stat(10));
    }

    private sealed class Accumulator
    {
        private double _sum;
        private int _count;
        private float _min = float.MaxValue, _max = float.MinValue;

        public bool Empty => _count == 0;

        public void Add(float? value)
        {
            if (value is not float v) return;
            _sum += v;
            _count++;
            _min = Math.Min(_min, v);
            _max = Math.Max(_max, v);
        }

        public MinuteStat? Take()
        {
            MinuteStat? result = _count == 0 ? null : new MinuteStat((float)(_sum / _count), _min, _max);
            _sum = 0;
            _count = 0;
            _min = float.MaxValue;
            _max = float.MinValue;
            return result;
        }
    }
}

/// <summary>Fires once when a value stays at or above a threshold for a given duration, then stays quiet for a cooldown.</summary>
internal sealed class SustainedAlert
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    private DateTime? _aboveSince;
    private DateTime _lastFired = DateTime.MinValue;

    public bool Check(DateTime now, float? value, int threshold, int seconds)
    {
        if (value is not float v || v < threshold)
        {
            _aboveSince = null;
            return false;
        }

        _aboveSince ??= now;
        if (now - _aboveSince.Value < TimeSpan.FromSeconds(seconds) || now - _lastFired < Cooldown) return false;
        _lastFired = now;
        return true;
    }
}
