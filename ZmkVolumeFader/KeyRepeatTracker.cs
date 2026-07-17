namespace ZmkVolumeFader;

/// <summary>Tracks key transitions on the keyboard-hook thread.</summary>
internal sealed class KeyRepeatTracker
{
    readonly HashSet<int> _down = new();

    /// <returns>true when this key was already down (an OS auto-repeat).</returns>
    public bool Press(int vk) => !_down.Add(vk);

    public void Release(int vk) => _down.Remove(vk);

    public void Clear() => _down.Clear();
}
