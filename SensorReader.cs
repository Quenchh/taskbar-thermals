using System.Diagnostics;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace TaskbarThermals;

internal sealed record Reading
{
    public static readonly Reading Empty = new();

    public float? CpuTemp { get; init; }
    public float? CpuLoad { get; init; }
    public float? CpuPower { get; init; }
    public float? CpuClock { get; init; }     // MHz, fastest core
    public float? SocVoltage { get; init; }
    public float? CoreVoltage { get; init; }
    public float? GpuTemp { get; init; }
    public float? GpuLoad { get; init; }
    public float? GpuPower { get; init; }
    public float? GpuMemClock { get; init; }  // MHz
    public float? GpuMemTemp { get; init; }
    public float? RamUsedGb { get; init; }
    public float? RamTotalGb { get; init; }
    public float? RamLoad => RamUsedGb / RamTotalGb * 100;
    public float? NetDown { get; init; }      // bytes/s
    public float? NetUp { get; init; }
    public float? DiskRead { get; init; }     // bytes/s
    public float? DiskWrite { get; init; }
    public IReadOnlyList<(string Name, float Rpm)> Fans { get; init; } = Array.Empty<(string, float)>();
}

/// <summary>Reads CPU/GPU/motherboard sensors. CPU temperature and board sensors need admin rights (PawnIO driver).</summary>
internal sealed class SensorReader : IDisposable
{
    private readonly Computer _computer = new() { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true };
    private readonly IHardware? _cpu;
    private readonly IHardware? _gpu;
    private readonly IHardware[] _boardChips;
    private readonly PerformanceCounter? _cpuUtility;
    private readonly PerformanceCounter? _diskRead, _diskWrite;
    private Dictionary<string, (long Rx, long Tx)> _netCounters = new();
    private long _netStamp;

    public SensorReader()
    {
        _computer.Open();
        _cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        _gpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
            ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd)
            ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);
        _boardChips = _computer.Hardware
            .Where(h => h.HardwareType == HardwareType.Motherboard)
            .SelectMany(h => h.SubHardware)
            .ToArray();

        // "% Processor Utility" is the counter Task Manager shows, so the numbers match what the user sees there.
        try
        {
            _cpuUtility = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
            _cpuUtility.NextValue();
        }
        catch
        {
            _cpuUtility = null;
        }

        try
        {
            _diskRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
            _diskWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
            _diskRead.NextValue();
            _diskWrite.NextValue();
        }
        catch
        {
            _diskRead = _diskWrite = null;
        }
    }

    /// <summary>
    /// Download/upload rate summed over physical adapters. Counters are tracked per adapter, so an adapter
    /// appearing or disappearing doesn't show up as a huge spike. Virtual adapters (Hyper-V switches, VPNs)
    /// are skipped because their traffic also passes through a physical one.
    /// </summary>
    private (float? Down, float? Up) ReadNetwork()
    {
        var current = new Dictionary<string, (long Rx, long Tx)>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                if (nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                    || nic.Description.Contains("Pseudo", StringComparison.OrdinalIgnoreCase)) continue;
                var stats = nic.GetIPStatistics();
                current[nic.Id] = (stats.BytesReceived, stats.BytesSent);
            }
        }
        catch
        {
            return (null, null);
        }

        long now = Stopwatch.GetTimestamp();
        (float?, float?) rate = (null, null);
        if (_netStamp != 0)
        {
            double seconds = (now - _netStamp) / (double)Stopwatch.Frequency;
            long rx = 0, tx = 0;
            foreach (var (id, c) in current)
            {
                if (!_netCounters.TryGetValue(id, out var p)) continue;
                rx += Math.Max(0, c.Rx - p.Rx);
                tx += Math.Max(0, c.Tx - p.Tx);
            }
            if (seconds > 0) rate = ((float)(rx / seconds), (float)(tx / seconds));
        }
        _netCounters = current;
        _netStamp = now;
        return rate;
    }

    private static float? Next(PerformanceCounter? counter)
    {
        try { return counter?.NextValue(); }
        catch { return null; }
    }

    public string CpuName => _cpu?.Name ?? "?";
    public string GpuName => _gpu?.Name ?? "?";

    public Reading Read()
    {
        _cpu?.Update();
        _gpu?.Update();
        foreach (var chip in _boardChips) chip.Update();

        float? cpuLoad = null;
        if (_cpuUtility != null)
        {
            try { cpuLoad = Math.Clamp(_cpuUtility.NextValue(), 0f, 100f); }
            catch { /* fall back to LHM below */ }
        }
        cpuLoad ??= Find(_cpu, SensorType.Load, "CPU Total")?.Value;

        float? ramUsed = null, ramTotal = null;
        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref mem))
        {
            ramTotal = mem.ullTotalPhys / 1073741824f;
            ramUsed = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824f;
        }
        var (netDown, netUp) = ReadNetwork();

        return new Reading
        {
            CpuTemp = PickCpuTemp()?.Value,
            CpuLoad = cpuLoad,
            // Like the temperature, package power reads 0 (not null) when the driver isn't available.
            CpuPower = (Find(_cpu, SensorType.Power, "Package") ?? Find(_cpu, SensorType.Power, "CPU Package"))?.Value is > 0 and var p ? p : null,
            CpuClock = _cpu?.Sensors
                .Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("Core #") && !s.Name.Contains('(') && s.Value > 0)
                .Max(s => s.Value),
            SocVoltage = Board(SensorType.Voltage, n => n.Contains("SoC")),
            CoreVoltage = Board(SensorType.Voltage, n => n is "Vcore" or "CPU Core" or "CPU VCore"),
            GpuTemp = (Find(_gpu, SensorType.Temperature, "GPU Core") ?? First(_gpu, SensorType.Temperature))?.Value,
            GpuLoad = (Find(_gpu, SensorType.Load, "GPU Core") ?? First(_gpu, SensorType.Load))?.Value,
            GpuPower = (Find(_gpu, SensorType.Power, "GPU Package") ?? First(_gpu, SensorType.Power))?.Value,
            GpuMemClock = Find(_gpu, SensorType.Clock, "GPU Memory")?.Value,
            GpuMemTemp = Find(_gpu, SensorType.Temperature, "GPU Memory Junction")?.Value,
            RamUsedGb = ramUsed,
            RamTotalGb = ramTotal,
            NetDown = netDown,
            NetUp = netUp,
            DiskRead = Next(_diskRead),
            DiskWrite = Next(_diskWrite),
            Fans = _boardChips
                .SelectMany(c => c.Sensors)
                .Where(s => s.SensorType == SensorType.Fan && s.Value > 0)
                .Select(s => (s.Name, s.Value!.Value))
                .ToArray(),
        };
    }

    private ISensor? PickCpuTemp()
    {
        if (_cpu == null) return null;
        // Without admin rights the PawnIO driver can't be opened and LHM reports 0 instead of null.
        var temps = _cpu.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value > 0).ToList();
        return temps.FirstOrDefault(s => s.Name == "Core (Tctl/Tdie)")
            ?? temps.FirstOrDefault(s => s.Name == "CPU Package")
            ?? temps.FirstOrDefault(s => s.Name.Contains("Tctl"))
            ?? temps.FirstOrDefault(s => s.Name.Contains("Package"))
            ?? temps.FirstOrDefault(s => s.Name == "Core Max")
            ?? temps.FirstOrDefault();
    }

    private float? Board(SensorType type, Func<string, bool> match) =>
        _boardChips.SelectMany(c => c.Sensors).FirstOrDefault(s => s.SensorType == type && s.Value.HasValue && match(s.Name))?.Value;

    private static ISensor? Find(IHardware? hw, SensorType type, string name) =>
        hw?.Sensors.FirstOrDefault(s => s.SensorType == type && s.Name == name && s.Value.HasValue);

    private static ISensor? First(IHardware? hw, SensorType type) =>
        hw?.Sensors.FirstOrDefault(s => s.SensorType == type && s.Value.HasValue);

    public void DumpSensors(TextWriter w)
    {
        foreach (var hw in _computer.Hardware) DumpHardware(w, hw, "");
    }

    private static void DumpHardware(TextWriter w, IHardware hw, string indent)
    {
        hw.Update();
        w.WriteLine($"{indent}[{hw.HardwareType}] {hw.Name}");
        foreach (var s in hw.Sensors)
            w.WriteLine($"{indent}    {s.SensorType,-12} {s.Name,-28} {s.Value?.ToString("0.00") ?? "null"}");
        foreach (var sub in hw.SubHardware) DumpHardware(w, sub, indent + "  ");
    }

    /// <summary>One line of CPU temp/power/voltage/max clock plus motherboard fan RPM and duty, for watching them move together.</summary>
    public string DiagnosticLine()
    {
        var parts = new List<string>();
        if (_cpu != null)
        {
            _cpu.Update();
            foreach (var s in _cpu.Sensors.Where(s => s.Value.HasValue && s.SensorType is SensorType.Temperature or SensorType.Power or SensorType.Voltage))
                parts.Add($"{s.Name}={s.Value:0.00}");
            var clocks = _cpu.Sensors.Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("Core #") && s.Value.HasValue).ToList();
            if (clocks.Count > 0) parts.Add($"MaxCoreClock={clocks.Max(s => s.Value):0}");
            parts.Add($"CpuTotal={Find(_cpu, SensorType.Load, "CPU Total")?.Value:0}%");
        }

        foreach (var chip in _boardChips)
        {
            chip.Update();
            foreach (var s in chip.Sensors.Where(s => s.Value.HasValue && s.SensorType is SensorType.Fan or SensorType.Control))
                parts.Add($"{s.Name}{(s.SensorType == SensorType.Fan ? "_rpm" : "_%")}={s.Value:0}");
        }
        return string.Join("  ", parts);
    }

    public void Dispose()
    {
        _cpuUtility?.Dispose();
        _diskRead?.Dispose();
        _diskWrite?.Dispose();
        _computer.Close();
    }
}
