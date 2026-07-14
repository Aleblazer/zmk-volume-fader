using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ZmkVolumeFader;

internal static partial class AppIdentityLogic
{
    internal sealed record Identity(string Key, IReadOnlyList<string> Aliases);

    public static Identity Create(string legacyProcessName, string? executablePath,
        string? companyName = null, string? productName = null)
    {
        string legacy = legacyProcessName.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(executablePath)) return new Identity(legacy, new[] { legacy });
        try
        {
            string exactPath = Path.GetFullPath(executablePath).ToLowerInvariant();
            string exactKey = $"exe:{exactPath}"; // alias for builds made before durable identities
            string fileName = Path.GetFileName(exactPath);
            string company = NormalizeMetadata(companyName);
            string product = NormalizeMetadata(productName);
            string key;
            if (company.Length > 0 || product.Length > 0)
                key = $"app:{Hash($"{company}|{product}|{fileName}")}";
            else
                key = $"exe:{NormalizeVersionedDirectories(exactPath)}";
            return new Identity(key, new[] { legacy, exactKey }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch { return new Identity(legacy, new[] { legacy }); }
    }

    public static string KeyFor(string legacyProcessName, string? executablePath) =>
        Create(legacyProcessName, executablePath).Key;

    internal static string NormalizeVersionedDirectories(string path)
    {
        string[] parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < parts.Length - 1; i++)
            if (LooksVersionedDirectory(parts[i])) parts[i] = "{version}";
        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    static bool LooksVersionedDirectory(string value) =>
        VersionDirectoryPattern().IsMatch(value);

    static string NormalizeMetadata(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ""
            : string.Join(' ', value.Trim().ToLowerInvariant().Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));

    static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    [GeneratedRegex(@"^(?:app[-_.]?|v(?:ersion)?[-_.]?)?\d+(?:[-_.]\d+){1,}(?:[-_.][a-z0-9]+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionDirectoryPattern();
}
