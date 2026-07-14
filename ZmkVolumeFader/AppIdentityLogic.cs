namespace ZmkVolumeFader;

internal static class AppIdentityLogic
{
    public static string KeyFor(string legacyProcessName, string? executablePath)
    {
        string legacy = legacyProcessName.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(executablePath)) return legacy;
        try { return $"exe:{Path.GetFullPath(executablePath).ToLowerInvariant()}"; }
        catch { return legacy; }
    }
}
