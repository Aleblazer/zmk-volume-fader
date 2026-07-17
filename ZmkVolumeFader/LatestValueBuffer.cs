namespace ZmkVolumeFader;

/// <summary>
/// Thread-safe replace-only buffer. Publishers overwrite the latest value for a
/// logical key and only the first publisher asks the consumer to schedule a drain.
/// </summary>
internal sealed class LatestValueBuffer<T>(IEqualityComparer<string>? comparer = null)
{
    readonly object _gate = new();
    readonly Dictionary<string, T> _latest = new(comparer ?? StringComparer.OrdinalIgnoreCase);
    bool _drainQueued;

    /// <returns>true only when the caller must schedule a drain.</returns>
    public bool Publish(string key, T value)
    {
        lock (_gate)
        {
            _latest[key] = value;
            if (_drainQueued) return false;
            _drainQueued = true;
            return true;
        }
    }

    public IReadOnlyList<T> Drain()
    {
        lock (_gate)
        {
            var values = _latest.Values.ToArray();
            _latest.Clear();
            _drainQueued = false;
            return values;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _latest.Clear();
            _drainQueued = false;
        }
    }
}
