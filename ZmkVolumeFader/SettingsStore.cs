using System.Text.Json.Serialization;

namespace ZmkVolumeFader;

internal sealed class SliderConfig
{
    public string Id { get; set; } = "";
    public string? SourceDeviceKey { get; set; }
    public string? SourceDeviceName { get; set; }
    public int AxisIndex { get; set; }
    public string Label { get; set; } = "";
    public Calibration Cal { get; set; } = new();
    public List<OutputPref> Outputs { get; set; } = new();
    public string? OverrideId { get; set; }
    public TargetKind Target { get; set; }
    public string? AppKey { get; set; }
    public string? CategoryName { get; set; }
    public int Max { get; set; } = 100;
    public bool IsVirtual { get; set; }
    public int Value { get; set; } = 50;
    public Hotkey HkUp { get; set; } = new();
    public Hotkey HkDown { get; set; } = new();
    public Hotkey HkMute { get; set; } = new();
    public int Step { get; set; } = 5;
    public bool Muted { get; set; }
    public int PreMute { get; set; } = 50;
}

// Schema 3 and earlier only. Active schema-4 settings use Layout exclusively.
internal sealed class LegacyDeviceProfile
{
    public string Name { get; set; } = "";
    public List<SliderConfig> Sliders { get; set; } = new();
}

internal sealed class Settings
{
    // Missing in the original flat format; zero lets the loader preserve a
    // migration backup before normalization.
    public int SchemaVersion { get; set; }
    public List<SliderConfig> Layout { get; set; } = new();
    public Dictionary<string, int> DeviceMax { get; set; } = new();
    public Dictionary<string, string> KnownApps { get; set; } = new();
    public List<Category> Categories { get; set; } = new();
    public ThemeMode ThemeMode { get; set; } = ThemeMode.Auto;
    public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.Ask;
    public bool SoftTakeover { get; set; } = true;
    public Dictionary<string, long> AppSeen { get; set; } = new();
    // Legacy-only payload. These members deserialize old files, are consumed by
    // SettingsStore.Normalize, then cleared so they never enter active runtime
    // state or get written back into a schema-4 document.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, LegacyDeviceProfile>? Devices { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? IgnoredDevices { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? MonitoredDevices { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LeftDeviceId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RightDeviceId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LeftMax { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RightMax { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OutputPref>? LeftOutputs { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OutputPref>? RightOutputs { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LeftOverrideId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RightOverrideId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Calibration? LeftCal { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Calibration? RightCal { get; set; }
}

/// <summary>Size limits and normalization for settings loaded from disk or import.</summary>
internal static class SettingsStore
{
    internal const int CurrentSchema = 4;
    internal const int MaxKnownApps = 4096;
    internal const string LegacyFirstDeviceKey = "#legacy-first-device";
    const int MaxSettingsBytes = 8 * 1024 * 1024;
    const int MaxProfiles = 64, MaxSlidersPerProfile = 64, MaxLayoutFaders = 128, MaxCategories = 128;
    const int MaxCategoryApps = 4096, MaxOutputPrefs = 64, MaxAxes = 8;

    public static string Read(string path)
    {
        var file = new FileInfo(path);
        if (file.Length > MaxSettingsBytes)
            throw new InvalidDataException($"Settings file is larger than the {MaxSettingsBytes / 1024 / 1024} MB safety limit.");
        return File.ReadAllText(path);
    }

    static string CleanText(string? value, int maxLength)
    {
        string cleaned = (value ?? "").Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    static int ClampLimit(int value) => Math.Clamp(value, 1, 100);

    static Hotkey NormalizeHotkey(Hotkey? hotkey)
    {
        hotkey ??= new Hotkey();
        if (hotkey.Vk is < 0 or > 255) hotkey.Vk = 0;
        if (hotkey.Vk == 0)
            hotkey.Ctrl = hotkey.Alt = hotkey.Shift = hotkey.Win = false;
        return hotkey;
    }

    static Calibration NormalizeCalibration(Calibration? calibration)
    {
        calibration ??= new Calibration();
        calibration.Min = Math.Clamp(calibration.Min, 0, ushort.MaxValue);
        calibration.Max = Math.Clamp(calibration.Max, 0, ushort.MaxValue);
        calibration.MuteRaw = Math.Clamp(calibration.MuteRaw, 0, ushort.MaxValue);
        // A short-lived preview exposed Straight (reversed) as numeric value 5.
        // Mirroring a straight line changes nothing, so retain its intent as Straight.
        if ((int)calibration.Taper == 5) calibration.Taper = TaperKind.Straight;
        else if (!Enum.IsDefined(calibration.Taper)) calibration.Taper = TaperKind.Linear;
        return calibration;
    }

    static List<OutputPref> NormalizeOutputs(IEnumerable<OutputPref>? outputs)
    {
        var normalized = new List<OutputPref>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in outputs ?? Enumerable.Empty<OutputPref>())
        {
            if (normalized.Count >= MaxOutputPrefs) break;
            if (output == null) continue;
            string id = CleanText(output.Id, 1024);
            if (id.Length == 0 || !seen.Add(id)) continue;
            string name = CleanText(output.Name, 256);
            normalized.Add(new OutputPref { Id = id, Name = name.Length == 0 ? id : name });
        }
        return normalized;
    }

    static SliderConfig NormalizeSlider(SliderConfig slider)
    {
        slider.Id = CleanText(slider.Id, 64);
        slider.SourceDeviceKey = CleanText(slider.SourceDeviceKey, 512) is { Length: > 0 } source ? source : null;
        slider.SourceDeviceName = CleanText(slider.SourceDeviceName, 160) is { Length: > 0 } sourceName ? sourceName : null;
        slider.Label = CleanText(slider.Label, 80);
        slider.Cal = NormalizeCalibration(slider.Cal);
        slider.Outputs = NormalizeOutputs(slider.Outputs);
        slider.OverrideId = CleanText(slider.OverrideId, 1024) is { Length: > 0 } over ? over : null;
        slider.AppKey = CleanText(slider.AppKey, 1024) is { Length: > 0 } app ? app : null;
        slider.CategoryName = CleanText(slider.CategoryName, 80) is { Length: > 0 } category ? category : null;
        slider.Max = ClampLimit(slider.Max);
        slider.Value = Math.Clamp(slider.Value, 0, 100);
        slider.PreMute = Math.Clamp(slider.PreMute, 0, 100);
        slider.Step = Math.Clamp(slider.Step, 1, 100);
        slider.HkUp = NormalizeHotkey(slider.HkUp);
        slider.HkDown = NormalizeHotkey(slider.HkDown);
        slider.HkMute = NormalizeHotkey(slider.HkMute);
        if (!Enum.IsDefined(slider.Target)) slider.Target = TargetKind.Output;
        if (slider.Target == TargetKind.App && slider.AppKey == null) slider.Target = TargetKind.Output;
        if (slider.Target == TargetKind.Category && slider.CategoryName == null) slider.Target = TargetKind.Output;
        if (slider.IsVirtual)
        {
            slider.SourceDeviceKey = null;
            slider.SourceDeviceName = null;
            slider.AxisIndex = -1;
        }
        else slider.AxisIndex = Math.Clamp(slider.AxisIndex, 0, MaxAxes - 1);
        if (slider.Label.Length == 0)
            slider.Label = slider.IsVirtual ? "Virtual fader" : $"Fader {slider.AxisIndex + 1}";
        return slider;
    }

    static bool IsObsoleteImplicitPair(IReadOnlyList<SliderConfig> sliders)
    {
        if (sliders.Count != 2) return false;
        return IsUntouched(sliders[0], 0, "Left fader")
            && IsUntouched(sliders[1], 1, "Right fader");

        static bool IsUntouched(SliderConfig slider, int axis, string label)
        {
            var cal = slider.Cal;
            return !slider.IsVirtual && slider.AxisIndex == axis
                && slider.Label.Equals(label, StringComparison.OrdinalIgnoreCase)
                && slider.Target == TargetKind.Output && slider.Outputs.Count == 0
                && slider.OverrideId == null && slider.AppKey == null && slider.CategoryName == null
                && slider.Max == 100 && slider.Value == 50 && slider.PreMute == 50
                && slider.Step == 5 && !slider.Muted
                && !slider.HkUp.IsBound && !slider.HkDown.IsBound && !slider.HkMute.IsBound
                && cal.Min == 4 && cal.Max == 3215 && cal.MuteRaw == 0
                && !cal.Inverted && cal.Taper == TaperKind.Linear;
        }
    }

    public static Settings Normalize(Settings settings)
    {
        settings.DeviceMax ??= new();
        settings.Layout ??= new();
        settings.KnownApps ??= new();
        settings.AppSeen ??= new();
        settings.Categories ??= new();

        var ignoredDevices = (settings.IgnoredDevices ?? new()).Select(key => CleanText(key, 512))
            .Where(key => key.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxProfiles).ToList();

        var deviceMax = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.DeviceMax)
        {
            if (deviceMax.Count >= MaxOutputPrefs * 4) break;
            string key = CleanText(pair.Key, 1024);
            if (key.Length > 0) deviceMax[key] = ClampLimit(pair.Value);
        }
        settings.DeviceMax = deviceMax;

        var devices = new Dictionary<string, LegacyDeviceProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.Devices ?? new())
        {
            if (devices.Count >= MaxProfiles) break;
            if (pair.Value == null) continue;
            string key = CleanText(pair.Key, 512);
            if (key.Length == 0 || devices.ContainsKey(key)) continue;
            var profile = pair.Value;
            profile.Name = CleanText(profile.Name, 160);
            profile.Sliders = (profile.Sliders ?? new()).Where(slider => slider != null)
                .Take(MaxSlidersPerProfile).Select(NormalizeSlider).ToList();
            // Older builds silently seeded every newly detected HID unit with a
            // LiberArk68-specific left/right pair. Remove only the exact untouched
            // template; any renamed, calibrated, targeted, or otherwise edited
            // pair remains intact.
            if (IsObsoleteImplicitPair(profile.Sliders)) profile.Sliders.Clear();
            devices[key] = profile;
        }
        settings.Devices = devices;

        // Schema 3 and earlier stored one ordered list per device. Flatten those
        // profiles once into the global layout; schema 4 can intentionally persist
        // an empty layout without the legacy dictionaries resurrecting it.
        bool migrateLegacyLayout = settings.SchemaVersion < CurrentSchema && settings.Layout.Count == 0;
        if (migrateLegacyLayout)
        {
            foreach (var pair in devices
                         .Where(pair => pair.Key.Equals("#virtual", StringComparison.OrdinalIgnoreCase)
                             || !ignoredDevices.Any(key => key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)))
                         .OrderBy(pair => pair.Key.Equals("#virtual", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                         .ThenBy(pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase))
                foreach (var slider in pair.Value.Sliders)
                {
                    slider.IsVirtual |= pair.Key.Equals("#virtual", StringComparison.OrdinalIgnoreCase);
                    slider.SourceDeviceKey = slider.IsVirtual ? null : pair.Key;
                    slider.SourceDeviceName = slider.IsVirtual ? null : pair.Value.Name;
                    settings.Layout.Add(slider);
                }
        }

        // The original two-fader settings format did not store HID identity.
        // Convert it here and mark the source for one-time binding to the first
        // compatible device, rather than carrying a DeviceProfile into MainForm.
        bool hasFlatSettings = settings.LeftOutputs != null || settings.RightOutputs != null
            || settings.LeftDeviceId != null || settings.RightDeviceId != null
            || settings.LeftCal != null || settings.RightCal != null
            || settings.LeftOverrideId != null || settings.RightOverrideId != null;
        if (settings.Layout.Count == 0 && hasFlatSettings)
        {
            SliderConfig MakeFlat(int axis, string label, string? endpointId,
                List<OutputPref>? outputs, string? overrideId, Calibration? calibration, int max)
            {
                endpointId = CleanText(endpointId, 1024) is { Length: > 0 } cleanedEndpoint
                    ? cleanedEndpoint : null;
                var normalizedOutputs = NormalizeOutputs(outputs);
                if (normalizedOutputs.Count == 0 && endpointId != null)
                    normalizedOutputs.Add(new OutputPref { Id = endpointId, Name = endpointId });
                if (endpointId != null && !settings.DeviceMax.ContainsKey(endpointId))
                    settings.DeviceMax[endpointId] = ClampLimit(max);
                return new SliderConfig
                {
                    SourceDeviceKey = LegacyFirstDeviceKey,
                    SourceDeviceName = "First compatible device",
                    AxisIndex = axis,
                    Label = label,
                    Cal = calibration ?? new Calibration(),
                    Outputs = normalizedOutputs,
                    OverrideId = overrideId,
                };
            }

            settings.Layout.Add(MakeFlat(0, "Left fader", settings.LeftDeviceId,
                settings.LeftOutputs, settings.LeftOverrideId, settings.LeftCal, settings.LeftMax ?? 100));
            settings.Layout.Add(MakeFlat(1, "Right fader", settings.RightDeviceId,
                settings.RightOutputs, settings.RightOverrideId, settings.RightCal, settings.RightMax ?? 100));
        }

        var layout = new List<SliderConfig>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var physicalSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in settings.Layout)
        {
            if (layout.Count >= MaxLayoutFaders || source == null) break;
            var slider = NormalizeSlider(source);
            if (!slider.IsVirtual)
            {
                if (slider.SourceDeviceKey == null) continue;
                string binding = FaderLayoutLogic.BindingKey(slider.SourceDeviceKey, slider.AxisIndex);
                if (!physicalSources.Add(binding)) continue;
            }
            if (slider.Id.Length == 0 || !ids.Add(slider.Id))
            {
                do slider.Id = Guid.NewGuid().ToString("N"); while (!ids.Add(slider.Id));
            }
            layout.Add(slider);
        }
        settings.Layout = layout;
        settings.SchemaVersion = CurrentSchema;

        var knownApps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.KnownApps)
        {
            if (knownApps.Count >= MaxKnownApps) break;
            string key = CleanText(pair.Key, 1024);
            if (key.Length == 0) continue;
            string name = CleanText(pair.Value, 256);
            knownApps[key] = name.Length == 0 ? key : name;
        }
        settings.KnownApps = knownApps;

        var seenApps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.AppSeen)
            if (seenApps.Count < MaxKnownApps && knownApps.ContainsKey(pair.Key) && pair.Value >= 0)
                seenApps[pair.Key] = pair.Value;
        settings.AppSeen = seenApps;

        var categories = new List<Category>();
        var categoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in settings.Categories)
        {
            if (categories.Count >= MaxCategories) break;
            if (category == null) continue;
            string name = CleanText(category.Name, 80);
            if (name.Length == 0 || name.Equals("#unassigned", StringComparison.OrdinalIgnoreCase)
                || !categoryNames.Add(name)) continue;
            category.Name = name;
            category.AppKeys = (category.AppKeys ?? new()).Select(key => CleanText(key, 1024))
                .Where(key => key.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxCategoryApps).ToList();
            categories.Add(category);
        }
        settings.Categories = categories;

        if (!Enum.IsDefined(settings.ThemeMode)) settings.ThemeMode = ThemeMode.Auto;
        if (!Enum.IsDefined(settings.CloseBehavior)) settings.CloseBehavior = CloseBehavior.Ask;

        settings.Devices = null;
        settings.IgnoredDevices = null;
        settings.MonitoredDevices = null;
        settings.LeftDeviceId = settings.RightDeviceId = null;
        settings.LeftMax = settings.RightMax = null;
        settings.LeftOutputs = settings.RightOutputs = null;
        settings.LeftOverrideId = settings.RightOverrideId = null;
        settings.LeftCal = settings.RightCal = null;
        return settings;
    }
}
