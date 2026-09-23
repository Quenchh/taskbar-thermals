namespace TaskbarThermals;

internal enum Device { Cpu, Gpu, Ram, Net, Disk }

internal enum HistoryRange { TenMinutes, Hour, Day }

/// <summary>A value that can be shown on the taskbar overlay. <see cref="Template"/> is the widest text it can produce, for layout.</summary>
internal sealed record Metric(string Id, Device Device, Func<string> Template, Func<Reading, string> Format);

internal static class Metrics
{
    public static readonly string[] Defaults = { "cpu.temp", "cpu.load", "gpu.temp", "gpu.load" };

    public static readonly Metric[] All =
    {
        new("cpu.temp", Device.Cpu, () => "100°C", r => Temp(r.CpuTemp)),
        new("cpu.load", Device.Cpu, () => L.Percent(100), r => Pct(r.CpuLoad)),
        new("cpu.power", Device.Cpu, () => "000W", r => r.CpuPower is float p ? $"{p:0}W" : "--W"),
        new("cpu.clock", Device.Cpu, () => $"{L.Number(8.8f, "0.0")}GHz", r => r.CpuClock is float c ? $"{L.Number(c / 1000, "0.0")}GHz" : "--GHz"),
        new("gpu.temp", Device.Gpu, () => "100°C", r => Temp(r.GpuTemp)),
        new("gpu.load", Device.Gpu, () => L.Percent(100), r => Pct(r.GpuLoad)),
        new("gpu.power", Device.Gpu, () => "000W", r => r.GpuPower is float p ? $"{p:0}W" : "--W"),
        new("gpu.vram", Device.Gpu, () => "100°C", r => Temp(r.GpuMemTemp)),
        new("ram.load", Device.Ram, () => L.Percent(100), r => Pct(r.RamLoad)),
        new("ram.used", Device.Ram, () => $"{L.Number(88.8f, "0.0")}G", r => r.RamUsedGb is float u ? $"{L.Number(u, "0.0")}G" : "--G"),
        new("net.down", Device.Net, () => "↓888M", r => "↓" + Rate(r.NetDown)),
        new("net.up", Device.Net, () => "↑888M", r => "↑" + Rate(r.NetUp)),
        new("disk.read", Device.Disk, () => "R 888M", r => "R " + Rate(r.DiskRead)),
        new("disk.write", Device.Disk, () => "W 888M", r => "W " + Rate(r.DiskWrite)),
    };

    public static string DeviceLabel(Device d) => d switch
    {
        Device.Net => "NET",
        Device.Disk => "DISK",
        _ => d.ToString().ToUpperInvariant(),
    };

    /// <summary>Threshold coloring applies to the CPU/GPU core temperatures only.</summary>
    public static (int Warn, int Hot)? Thresholds(Metric m, Settings s) => m.Id switch
    {
        "cpu.temp" => (s.CpuWarn, s.CpuHot),
        "gpu.temp" => (s.GpuWarn, s.GpuHot),
        _ => null,
    };

    public static float? RawValue(Metric m, Reading r) => m.Id switch
    {
        "cpu.temp" => r.CpuTemp,
        "gpu.temp" => r.GpuTemp,
        _ => null,
    };

    private static string Temp(float? t) => t is float v ? $"{v:0}°C" : "--°C";
    private static string Pct(float? v) => v is float p ? L.Percent(p) : L.Turkish ? "%--" : "--%";
    private static string Rate(float? v) => v is float b ? L.RateShort(b) : "--";
}
