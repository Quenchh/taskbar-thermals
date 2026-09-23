using System.Text;

namespace TaskbarThermals;

/// <summary>Writes a CSV line whenever the CPU temperature jumps quickly or runs hot, with the busiest processes at that moment.</summary>
internal sealed class SpikeLogger
{
    private readonly Func<Settings> _settings;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private readonly Queue<(DateTime Time, float Temp)> _history = new();
    private DateTime _lastLogged = DateTime.MinValue;

    public SpikeLogger(Func<Settings> settings) => _settings = settings;

    public string LogPath { get; } = Path.Combine(AppPaths.Dir, "spikes.csv");

    /// <summary>Short description of the most recent spike, for the context menu.</summary>
    public string? Last { get; private set; }

    public void Check(DateTime now, Reading r, Func<List<(string Name, double Percent)>> topProcesses)
    {
        if (r.CpuTemp is not float temp) return;

        while (_history.Count > 0 && now - _history.Peek().Time > Window) _history.Dequeue();
        float min = _history.Count > 0 ? _history.Min(h => h.Temp) : temp;
        _history.Enqueue((now, temp));

        float rise = temp - min;
        var settings = _settings();
        bool jump = rise >= settings.SpikeJump;
        if ((!jump && temp < settings.CpuHot) || now - _lastLogged < Cooldown) return;
        _lastLogged = now;

        var top = topProcesses();
        string procs = string.Join("; ", top.Select(p => $"{p.Name} {p.Percent:0}%"));
        string reason = jump ? $"jump +{rise:0}°C" : "high temperature";

        string culprit = top.Count > 0 ? Path.GetFileNameWithoutExtension(top[0].Name) : "?";
        Last = $"{now:HH:mm:ss}  {min:0}→{temp:0}°C  ({culprit})";

        try
        {
            bool isNew = !File.Exists(LogPath);
            using var w = new StreamWriter(LogPath, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            if (isNew) w.WriteLine("Time,Reason,CPU °C before,CPU °C,CPU %,GPU °C,GPU %,Top CPU processes");
            w.WriteLine(string.Join(',',
                now.ToString("yyyy-MM-dd HH:mm:ss"),
                reason,
                min.ToString("0"),
                temp.ToString("0"),
                r.CpuLoad?.ToString("0") ?? "",
                r.GpuTemp?.ToString("0") ?? "",
                r.GpuLoad?.ToString("0") ?? "",
                $"\"{procs}\""));
        }
        catch (IOException)
        {
            // File is probably open in Excel; skip this entry rather than crash.
        }
    }
}
