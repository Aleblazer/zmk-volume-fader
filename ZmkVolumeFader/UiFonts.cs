namespace ZmkVolumeFader;

/// <summary>
/// Process-lifetime UI font cache. Dynamic fader/dialog rebuilds otherwise create
/// fresh native font handles that Controls do not own or dispose consistently.
/// </summary>
static class UiFonts
{
    static readonly object Gate = new();
    static readonly Dictionary<(float Size, FontStyle Style), Font> Cache = new();

    public static Font Get(float size, FontStyle style = FontStyle.Regular)
    {
        lock (Gate)
        {
            var key = (size, style);
            if (!Cache.TryGetValue(key, out var font))
                Cache[key] = font = new Font("Segoe UI", size, style);
            return font;
        }
    }
}
