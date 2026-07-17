using System.Text.Json.Serialization;

namespace ZmkVolumeFader;

internal enum TaperKind
{
    Linear,
    Audio,
    Straight,
    LinearReversed,
    AudioReversed,
}

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
    // Independent final direction toggle. Reversed taper variants mirror the
    // electrical compensation curve; Inverted flips the mapped percentage.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Inverted { get; set; }
    // Mute dead zone (raw mV): while the smoothed raw reading sits below this
    // the output is forced to 0%, like a mixer's mute detent at the bottom of
    // the throw. Covers a wiper that rests a few mV above the calibrated Min
    // and would otherwise hover at ~1%. The zone follows whichever raw endpoint
    // maps to 0% after taper direction and final inversion. 0 = off.
    public int MuteRaw { get; set; }

    // Presets: inverse of the Bourns datasheet output-vs-travel curves
    // (value-fraction% -> volume%), so physical travel maps ~linearly to volume.
    static readonly (double f, double v)[] LinearShape =
        { (0, 0), (1, 10), (4, 20), (10, 30), (30, 40), (50, 50), (63, 60), (78, 70), (92, 80), (98, 90), (100, 100) };
    static readonly (double f, double v)[] AudioShape =
        { (0, 0), (1, 10), (3, 20), (6, 30), (10, 40), (15, 50), (22, 60), (38, 70), (65, 80), (90, 90), (100, 100) };
    static readonly (double f, double v)[] StraightShape = { (0, 0), (100, 100) };

    public bool Reversed => Taper is TaperKind.LinearReversed or TaperKind.AudioReversed;
    bool ZeroAtHighRaw => Inverted;

    public Calibration Clone() => new()
    {
        Min = Min,
        Max = Max,
        Taper = Taper,
        Inverted = Inverted,
        MuteRaw = MuteRaw,
    };

    public (int v, int pct)[] BuildCurve()
    {
        int lo = Math.Min(Min, Max), hi = Math.Max(Min, Max);
        if (hi - lo < 2) { lo = 0; hi = 3250; }   // guard against a degenerate range
        var shape = Taper switch
        {
            TaperKind.Audio or TaperKind.AudioReversed => AudioShape,
            TaperKind.Straight => StraightShape,
            _ => LinearShape,   // Linear (also the fallback for any stale value)
        };
        var outp = new (int, int)[shape.Length];
        for (int i = 0; i < shape.Length; i++)
        {
            var point = Reversed ? shape[shape.Length - 1 - i] : shape[i];
            double rawFraction = Reversed ? 100.0 - point.f : point.f;
            outp[i] = ((int)Math.Round(lo + rawFraction / 100.0 * (hi - lo)),
                (int)Math.Round(Reversed ? 100.0 - point.v : point.v));
        }
        return Sanitize(outp);
    }

    // Guarantee strictly-increasing raw values so interpolation stays well-defined.
    static (int v, int pct)[] Sanitize((int v, int pct)[] pts)
    {
        for (int i = 1; i < pts.Length; i++)
            if (pts[i].v <= pts[i - 1].v) pts[i].v = pts[i - 1].v + 1;
        return pts;
    }

    // Inverse of Eval: the raw value whose mapped percentage is pct (clamps to
    // the curve ends). Used by the --diag-synth leak-isolation mode to inject
    // realistic raw values for a target percentage.
    public static double InvEval((int v, int pct)[] curve, double pct)
    {
        if (curve.Length == 0) return 0;
        bool ascending = curve[^1].pct >= curve[0].pct;
        if (ascending)
        {
            if (pct <= curve[0].pct) return curve[0].v;
            for (int i = 1; i < curve.Length; i++)
                if (pct <= curve[i].pct)
                {
                    var (v0, p0) = curve[i - 1];
                    var (v1, p1) = curve[i];
                    return p1 == p0 ? v1 : v0 + (pct - p0) / (p1 - p0) * (v1 - v0);
                }
        }
        else
        {
            if (pct >= curve[0].pct) return curve[0].v;
            for (int i = 1; i < curve.Length; i++)
                if (pct >= curve[i].pct)
                {
                    var (v0, p0) = curve[i - 1];
                    var (v1, p1) = curve[i];
                    return p1 == p0 ? v1 : v0 + (pct - p0) / (p1 - p0) * (v1 - v0);
                }
        }
        return curve[^1].v;
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

    public double MapRaw((int v, int pct)[] curve, int raw)
    {
        double mapped = Eval(curve, raw);
        return Inverted ? 100.0 - mapped : mapped;
    }

    public double RawAtPercentage((int v, int pct)[] curve, double percentage) =>
        InvEval(curve, Inverted ? 100.0 - percentage : percentage);

    int UpperMuteThreshold()
    {
        int lo = Math.Min(Min, Max), hi = Math.Max(Min, Max);
        return hi - Math.Max(0, MuteRaw - lo);
    }

    public bool IsInMuteZone(int raw)
    {
        if (MuteRaw <= 0) return false;
        return ZeroAtHighRaw ? raw > UpperMuteThreshold() : raw < MuteRaw;
    }

    public bool IsOutsideMuteZone(int raw, int exitBand)
    {
        if (MuteRaw <= 0) return true;
        return ZeroAtHighRaw
            ? raw < UpperMuteThreshold() - exitBand
            : raw > MuteRaw + exitBand;
    }
}
