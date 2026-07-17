namespace ZmkVolumeFader;

/// <summary>
/// Small, bounded history of recent Core Audio session writes. Session callbacks
/// are asynchronous and can arrive after a newer write, so one expected-value
/// slot is not sufficient to distinguish our own notifications from mixer input.
/// </summary>
internal sealed class ExpectedVolumeWrites
{
    // Enough history for more than a second at the controller's global write
    // limit, while remaining a tiny fixed allocation per live session.
    const int Capacity = 64;

    readonly object _gate = new();
    readonly List<(float Volume, long Until)> _writes = new(Capacity);

    public void Add(float volume, long until)
    {
        lock (_gate)
        {
            RemoveExpired(Environment.TickCount64);
            if (_writes.Count == Capacity) _writes.RemoveAt(0);
            _writes.Add((volume, until));
        }
    }

    public bool Consume(float actual, long now, float epsilon = 0.0049f)
    {
        lock (_gate)
        {
            RemoveExpired(now);
            int match = _writes.FindIndex(w => Math.Abs(w.Volume - actual) < epsilon);
            if (match < 0) return false;
            _writes.RemoveAt(match);
            return true;
        }
    }

    void RemoveExpired(long now) => _writes.RemoveAll(w => now > w.Until);
}
