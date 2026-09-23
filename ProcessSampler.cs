using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskbarThermals;

/// <summary>
/// Cheap per-process CPU sampling via one NtQuerySystemInformation call per tick,
/// so a temperature spike can be attributed to whatever was busy during the last interval.
/// </summary>
internal sealed class ProcessSampler : IDisposable
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    // SYSTEM_PROCESS_INFORMATION field offsets (x64).
    private const int OffUserTime = 0x28;
    private const int OffKernelTime = 0x30;
    private const int OffNameLength = 0x38;
    private const int OffNameBuffer = 0x40;
    private const int OffPid = 0x50;

    private IntPtr _buffer;
    private int _bufferSize = 512 * 1024;
    private Dictionary<long, (string Name, long Cpu)> _prev = new();
    private Dictionary<long, (string Name, long Cpu)> _cur = new();
    private long _prevStamp, _curStamp;

    public void Snapshot()
    {
        if (!Environment.Is64BitProcess) return;
        if (_buffer == IntPtr.Zero) _buffer = Marshal.AllocHGlobal(_bufferSize);

        int status;
        while ((status = Native.NtQuerySystemInformation(SystemProcessInformation, _buffer, _bufferSize, out int needed)) == StatusInfoLengthMismatch)
        {
            Marshal.FreeHGlobal(_buffer);
            _bufferSize = Math.Max(needed, _bufferSize) + 64 * 1024;
            _buffer = Marshal.AllocHGlobal(_bufferSize);
        }
        if (status != 0) return;

        (_prev, _cur) = (_cur, _prev);
        _cur.Clear();
        _prevStamp = _curStamp;
        _curStamp = Stopwatch.GetTimestamp();

        IntPtr p = _buffer;
        while (true)
        {
            long pid = (long)Marshal.ReadIntPtr(p, OffPid);
            if (pid != 0)
            {
                long cpu = Marshal.ReadInt64(p, OffUserTime) + Marshal.ReadInt64(p, OffKernelTime);
                string name = _prev.TryGetValue(pid, out var known) ? known.Name : ReadName(p, pid);
                _cur[pid] = (name, cpu);
            }

            int next = Marshal.ReadInt32(p, 0);
            if (next == 0) break;
            p += next;
        }
    }

    private static string ReadName(IntPtr entry, long pid)
    {
        int bytes = (ushort)Marshal.ReadInt16(entry, OffNameLength);
        IntPtr str = Marshal.ReadIntPtr(entry, OffNameBuffer);
        if (str == IntPtr.Zero || bytes == 0) return pid == 4 ? "System" : $"pid {pid}";
        return Marshal.PtrToStringUni(str, bytes / 2);
    }

    /// <summary>Busiest processes (grouped by exe name) between the last two snapshots, in % of total CPU.</summary>
    public List<(string Name, double Percent)> Top(int count)
    {
        if (_prevStamp == 0) return new();
        double capacity = (_curStamp - _prevStamp) * 1e7 / Stopwatch.Frequency * Environment.ProcessorCount;

        var byName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, now) in _cur)
        {
            if (!_prev.TryGetValue(pid, out var before) || now.Cpu <= before.Cpu) continue;
            byName[now.Name] = byName.GetValueOrDefault(now.Name) + (now.Cpu - before.Cpu);
        }

        return byName
            .OrderByDescending(kv => kv.Value)
            .Take(count)
            .Select(kv => (kv.Key, kv.Value / capacity * 100))
            .ToList();
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
    }
}
