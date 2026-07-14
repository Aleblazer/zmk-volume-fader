namespace ZmkVolumeFader;

internal static class DesiredVolumeLogic
{
    internal readonly record struct Assignment(bool IsEndpoint, string Key, float Scalar, long Sequence);
    internal sealed record Maps(Dictionary<string, float> Endpoints, Dictionary<string, float> Apps);

    public static Maps Build(IEnumerable<Assignment> assignments)
    {
        var endpoints = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var apps = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in assignments.OrderBy(a => a.Sequence))
            Apply(endpoints, apps, assignment.IsEndpoint, assignment.Key, assignment.Scalar);
        return new Maps(endpoints, apps);
    }

    public static void Apply(Dictionary<string, float> endpoints, Dictionary<string, float> apps,
        bool isEndpoint, string key, float scalar)
    {
        var destination = isEndpoint ? endpoints : apps;
        destination[key] = Math.Clamp(scalar, 0f, 1f);
    }
}
