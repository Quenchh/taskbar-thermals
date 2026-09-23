using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarThermals;

internal static class AppPaths
{
    public static string Dir { get; } = Directory.CreateDirectory(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarThermals")).FullName;

    public static void LogError(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(Dir, "errors.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n"); }
        catch { /* nowhere left to report it */ }
    }
}

internal sealed class Settings
{
    /// <summary>Distance (in 96-DPI units) between the overlay's right edge and the taskbar's right edge. Null = automatic.</summary>
    public int? OffsetFromRight { get; set; }

    // Taskbar overlay: ids from <see cref="Metrics.All"/>. Null only when read from a pre-1.1 settings file.
    public List<string>? Metrics { get; set; }

    // Pre-1.1 settings, only read to migrate into <see cref="Metrics"/>.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ShowLoad { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ShowPower { get; set; }

    public int FontSize { get; set; } = 12;
    public int IntervalMs { get; set; } = 1000;

    // Color thresholds (°C)
    public int CpuWarn { get; set; } = 75;
    public int CpuHot { get; set; } = 85;
    public int GpuWarn { get; set; } = 75;
    public int GpuHot { get; set; } = 83;

    // Notifications
    public bool AlertCpu { get; set; } = true;
    public int AlertCpuTemp { get; set; } = 88;
    public bool AlertGpu { get; set; } = true;
    public int AlertGpuTemp { get; set; } = 85;
    public int AlertSeconds { get; set; } = 10;
    public bool AlertStability { get; set; } = true;

    // Spike log: rise within 5 s that counts as a spike.
    public int SpikeJump { get; set; } = 10;

    // General
    public string Language { get; set; } = "auto";   // auto | en | tr
    public bool CheckUpdates { get; set; } = true;
    public HistoryRange HistoryRange { get; set; } = HistoryRange.TenMinutes;

    /// <summary>Newest version the user was already told about, so each update is announced once.</summary>
    public string? UpdateNotified { get; set; }

    /// <summary>Stability events up to this moment were acknowledged by the user ("Mark as seen").</summary>
    public DateTime? StabilityAckTime { get; set; }

    /// <summary>Events up to this moment were already announced, so a restart doesn't repeat the notification.</summary>
    public DateTime? StabilityNotifiedUntil { get; set; }

    private static string FilePath => Path.Combine(AppPaths.Dir, "settings.json");

    public static Settings Load()
    {
        Settings s;
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { s = new(); }

        if (s.Metrics == null)
        {
            s.Metrics = new() { "cpu.temp", "gpu.temp" };
            if (s.ShowLoad ?? true) s.Metrics.AddRange(new[] { "cpu.load", "gpu.load" });
            if (s.ShowPower == true) s.Metrics.AddRange(new[] { "cpu.power", "gpu.power" });
            s.ShowLoad = s.ShowPower = null;
        }
        return s;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { AppPaths.LogError(ex); }
    }

    public Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this))!;
}
