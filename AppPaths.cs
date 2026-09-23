using System.Text.Json;

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

    // Taskbar overlay
    public bool ShowLoad { get; set; } = true;
    public bool ShowPower { get; set; }
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

    /// <summary>Stability events up to this moment were acknowledged by the user ("Mark as seen").</summary>
    public DateTime? StabilityAckTime { get; set; }

    /// <summary>Events up to this moment were already announced, so a restart doesn't repeat the notification.</summary>
    public DateTime? StabilityNotifiedUntil { get; set; }

    private static string FilePath => Path.Combine(AppPaths.Dir, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { AppPaths.LogError(ex); }
    }

    public Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this))!;
}
