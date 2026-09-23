namespace TaskbarThermals;

internal readonly record struct Sample(DateTime Time, Reading Reading);

/// <summary>Rolling window of readings for the detail panel charts.</summary>
internal sealed class SampleHistory
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

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
