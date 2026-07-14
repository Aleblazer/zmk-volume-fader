namespace ZmkVolumeFader;

internal static class SettingsRecoveryLogic
{
    public static IReadOnlyList<string> CandidatePaths(string primaryPath) =>
        new[] { primaryPath, primaryPath + ".bak", primaryPath + ".pre-import.bak" };

    public static bool SupportsSchema(int schemaVersion, int currentSchema) =>
        schemaVersion >= 0 && schemaVersion <= currentSchema;
}
