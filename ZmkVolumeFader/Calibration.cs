namespace ZmkVolumeFader;

internal enum TaperKind { Linear, Audio, Straight }

/// <summary>
/// Per-fader calibration: the captured raw range plus which taper preset maps the
/// value to a volume percentage between the ends. <see cref="BuildCurve"/> turns it
/// into the piecewise (raw -> %) table the app interpolates at runtime.
/// </summary>
internal sealed class Calibration
{
    public int Min { get; set; } = 4;
    public int Max { get; set; } = 3215;
    public TaperKind Taper { get; set; } = TaperKind.Linear;
    // Mute dead zone (raw mV): while the smoothed raw reading sits below this
    // the output is forced to 0%, like a mixer's mute detent at the bottom of
    // the throw. Covers a wiper that rests a few mV above the calibrated Min
    // and would otherwise hover at ~1%. 0 = off.
    public int MuteRaw { get; set; }

    // Presets: inverse of the Bourns datasheet output-vs-travel curves
    // (value-fraction% -> volume%), so physical travel maps ~linearly to volume.
    static readonly (double f, double v)[] LinearShape =
        { (0, 0), (1, 10), (4, 20), (10, 30), (30, 40), (50, 50), (63, 60), (78, 70), (92, 80), (98, 90), (100, 100) };
    static readonly (double f, double v)[] AudioShape =
        { (0, 0), (1, 10), (3, 20), (6, 30), (10, 40), (15, 50), (22, 60), (38, 70), (65, 80), (90, 90), (100, 100) };
    static readonly (double f, double v)[] StraightShape = { (0, 0), (100, 100) };

    public Calibration Clone() => new()
    {
        Min = Min,
        Max = Max,
        Taper = Taper,
        MuteRaw = MuteRaw,
    };

    public (int v, int pct)[] BuildCurve()
    {
        int lo = Math.Min(Min, Max), hi = Math.Max(Min, Max);
        if (hi - lo < 2) { lo = 0; hi = 3250; }   // guard against a degenerate range
        var shape = Taper switch
        {
            TaperKind.Audio => AudioShape,
            TaperKind.Straight => StraightShape,
            _ => LinearShape,   // Linear (also the fallback for any stale value)
        };
        var outp = new (int, int)[shape.Length];
        for (int i = 0; i < shape.Length; i++)
            outp[i] = ((int)Math.Round(lo + shape[i].f / 100.0 * (hi - lo)), (int)Math.Round(shape[i].v));
        return Sanitize(outp);
    }

    // Guarantee strictly-increasing raw values so interpolation stays well-defined.
    static (int v, int pct)[] Sanitize((int v, int pct)[] pts)
    {
        for (int i = 1; i < pts.Length; i++)
            if (pts[i].v <= pts[i - 1].v) pts[i].v = pts[i - 1].v + 1;
        return pts;
    }

    // Piecewise-linear lookup; clamps to the end points (continuous dead bands).
    public static double Eval((int v, int pct)[] curve, int v)
    {
        if (curve.Length == 0) return 0;
        if (v <= curve[0].v) return curve[0].pct;
        for (int i = 1; i < curve.Length; i++)
            if (v <= curve[i].v)
            {
                var (v0, p0) = curve[i - 1];
                var (v1, p1) = curve[i];
                return p0 + (double)(v - v0) / (v1 - v0) * (p1 - p0);
            }
        return curve[^1].pct;
    }
}
