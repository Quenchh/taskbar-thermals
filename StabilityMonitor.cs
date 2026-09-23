using System.Diagnostics.Eventing.Reader;

namespace TaskbarThermals;

internal enum StabilityKind { Whea, UnexpectedShutdown, BugCheck }

/// <summary>A stability-relevant event; the display text comes from <see cref="L.EventTitle"/> so it follows the UI language.</summary>
internal sealed record StabilityEvent(long Id, DateTime Time, StabilityKind Kind, int WheaId, bool Serious)
{
    public string Title => L.EventTitle(this);
}

/// <summary>
/// Watches the System event log for the usual signs of an unstable undervolt / memory setting:
/// WHEA hardware errors, unexpected shutdowns (Kernel-Power 41) and bugchecks (blue screens).
/// </summary>
internal sealed class StabilityMonitor : IDisposable
{
    private const string Filter =
        "(Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2 or Level=3)) or " +
        "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or " +
        "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001)";

    private readonly object _lock = new();
    private readonly List<StabilityEvent> _events = new();
    private readonly HashSet<long> _seen = new();
    private EventLogWatcher? _watcher;

    public bool Available { get; private set; } = true;

    /// <summary>Raised on a thread-pool thread for events written after <see cref="Start"/>.</summary>
    public event Action<StabilityEvent>? EventLogged;

    public void Start(TimeSpan lookback)
    {
        try
        {
            long ms = (long)lookback.TotalMilliseconds;
            var history = new EventLogQuery("System", PathType.LogName,
                $"*[System[({Filter}) and TimeCreated[timediff(@SystemTime) <= {ms}]]]");
            using (var reader = new EventLogReader(history))
            {
                for (var record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                    using (record) Add(Describe(record));
            }

            _watcher = new EventLogWatcher(new EventLogQuery("System", PathType.LogName, $"*[System[{Filter}]]"));
            _watcher.EventRecordWritten += OnRecordWritten;
            _watcher.Enabled = true;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            Available = false;
            AppPaths.LogError(ex);
        }
    }

    /// <summary>Events newer than <paramref name="since"/>, newest first.</summary>
    public List<StabilityEvent> Since(DateTime since)
    {
        lock (_lock) return _events.Where(e => e.Time > since).OrderByDescending(e => e.Time).ToList();
    }

    private void OnRecordWritten(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventRecord is not { } record) return;
        StabilityEvent ev;
        using (record) ev = Describe(record);
        if (Add(ev)) EventLogged?.Invoke(ev);
    }

    private bool Add(StabilityEvent ev)
    {
        lock (_lock)
        {
            if (!_seen.Add(ev.Id)) return false;
            _events.Add(ev);
            return true;
        }
    }

    private static StabilityEvent Describe(EventRecord r)
    {
        long id = r.RecordId ?? r.GetHashCode();
        var time = r.TimeCreated ?? DateTime.Now;
        return r.ProviderName switch
        {
            // Corrected PCIe errors (17) are often link-level noise; list them but don't raise an alarm.
            "Microsoft-Windows-WHEA-Logger" => new(id, time, StabilityKind.Whea, r.Id, Serious: r.Id != 17),
            "Microsoft-Windows-Kernel-Power" => new(id, time, StabilityKind.UnexpectedShutdown, 0, true),
            _ => new(id, time, StabilityKind.BugCheck, 0, true),
        };
    }

    public void Dispose()
    {
        if (_watcher == null) return;
        _watcher.Enabled = false;
        _watcher.Dispose();
    }
}
