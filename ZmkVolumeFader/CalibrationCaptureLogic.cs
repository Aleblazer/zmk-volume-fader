namespace ZmkVolumeFader;

internal static class CalibrationCaptureLogic
{
    internal sealed record Analysis(string Message, bool Good, int? SuggestedMuteRaw,
        int Span, int CoveragePercent, int PriorTravelPercent);

    public static Analysis Analyze(IReadOnlyList<int> samples, int priorSpan)
    {
        if (samples.Count < 8)
            return new Analysis("Too few samples — sweep the full travel more slowly", false, null, 0, 0, 0);

        int min = samples.Min(), max = samples.Max(), span = max - min;
        if (span < 100)
            return new Analysis("Travel is too narrow — make sure both endpoints are reached", false, null, span, 0, 0);

        const int binCount = 12;
        var bins = new bool[binCount];
        foreach (int sample in samples)
        {
            int bin = Math.Clamp((int)((long)(sample - min) * binCount / (span + 1L)), 0, binCount - 1);
            bins[bin] = true;
        }
        int coverage = (int)Math.Round(bins.Count(v => v) * 100d / binCount);
        int travel = priorSpan > 0 ? Math.Clamp((int)Math.Round(span * 100d / priorSpan), 0, 999) : 100;

        int? suggestedMute = null;
        int tailCount = Math.Min(8, samples.Count);
        var tail = samples.Skip(samples.Count - tailCount).ToArray();
        int tailSpread = tail.Max() - tail.Min();
        double tailAverage = tail.Average();
        bool endedLow = tailAverage <= min + span * 0.10;
        bool stableEndpoint = tailSpread <= Math.Max(4, span * 0.01);
        if (endedLow && stableEndpoint && min < 300)
            suggestedMute = Math.Min(300, min + Math.Max(10, tailSpread * 3));

        bool good = travel >= 85 && coverage >= 67;
        string message = travel < 85
            ? $"Limited capture · {travel}% of prior travel — sweep both endpoints again"
            : coverage < 67
                ? $"Sparse capture · {coverage}% coverage — move more slowly through the middle"
                : $"Good capture · {travel}% travel · {coverage}% coverage";
        if (suggestedMute is int mute) message += $" · suggested mute below {mute}";
        return new Analysis(message, good, suggestedMute, span, coverage, travel);
    }
}
