namespace ZmkVolumeFader;

internal static class OutputFallbackLogic
{
    public static string? Resolve<T>(IReadOnlyList<T> ranked, Func<T, string> idFor,
        Func<string, bool> isPresent,
        string? overrideId = null)
    {
        if (overrideId != null && isPresent(overrideId)) return overrideId;
        for (int i = 0; i < ranked.Count; i++)
        {
            string id = idFor(ranked[i]);
            if (isPresent(id)) return id;
        }
        return null;
    }
}
