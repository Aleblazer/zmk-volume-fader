namespace ZmkVolumeFader;

internal readonly record struct PhysicalBinding(string DeviceKey, int Axis);

internal static class FaderLayoutLogic
{
    public static string BindingKey(string deviceKey, int axis) => $"{deviceKey.Trim()}\n{axis}";

    public static IReadOnlyList<PhysicalBinding> FindDuplicates(IEnumerable<PhysicalBinding> bindings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<PhysicalBinding>();
        foreach (var binding in bindings)
        {
            string key = BindingKey(binding.DeviceKey, binding.Axis);
            if (seen.Add(key) || !duplicateKeys.Add(key)) continue;
            duplicates.Add(binding);
        }
        return duplicates;
    }

    public static void ApplyPhysicalSource(SliderConfig config, string deviceKey,
        string deviceName, int axis, int min, int max)
    {
        config.IsVirtual = false;
        config.SourceDeviceKey = deviceKey;
        config.SourceDeviceName = deviceName;
        config.AxisIndex = axis;
        config.Cal = config.Cal.Clone();
        config.Cal.Min = min;
        config.Cal.Max = max;
    }
}
