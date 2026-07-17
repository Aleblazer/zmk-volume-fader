using ZmkVolumeFader;
using System.Text.Json;

static void Expect(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"Failed: {name}");
}

Expect(PickupLogic.HasReached(null, 49, 50), "pickup accepts tolerance");
Expect(PickupLogic.HasReached(40, 55, 50), "pickup crosses upward");
Expect(PickupLogic.HasReached(60, 45, 50), "pickup crosses downward");
Expect(!PickupLogic.HasReached(null, 40, 50), "pickup waits without prior sample");
Expect(!PickupLogic.HasReached(35, 40, 50), "pickup waits on same side");
Expect(AppIdentityLogic.KeyFor("Discord", null) == "discord", "app identity falls back to process name");
Expect(AppIdentityLogic.KeyFor("Discord", @"C:\Apps\Discord.exe")
    == @"exe:c:\apps\discord.exe", "app identity normalizes executable path");
var discordOld = AppIdentityLogic.Create("Discord", @"C:\Users\Test\AppData\Local\Discord\app-1.2.3\Discord.exe",
    "Discord Inc.", "Discord");
var discordNew = AppIdentityLogic.Create("Discord", @"C:\Users\Test\AppData\Local\Discord\app-1.2.4\Discord.exe",
    "Discord Inc.", "Discord");
Expect(discordOld.Key == discordNew.Key, "product identity survives versioned install directories");
Expect(discordOld.Aliases.Contains(@"exe:c:\users\test\appdata\local\discord\app-1.2.3\discord.exe"),
    "identity retains exact-path migration alias");
Expect(AppIdentityLogic.NormalizeVersionedDirectories(@"c:\apps\tool\app-2.4.1\tool.exe")
    == @"c:\apps\tool\{version}\tool.exe", "versioned directories normalize without metadata");

var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "speakers", "headset" };
Expect(OutputFallbackLogic.Resolve(new[] { "dock", "headset", "speakers" }, static id => id, outputs.Contains) == "headset",
    "output fallback selects first present preference");
Expect(OutputFallbackLogic.Resolve(new[] { "headset" }, static id => id, outputs.Contains, "speakers") == "speakers",
    "present manual output override wins");
Expect(OutputFallbackLogic.Resolve(new[] { "dock" }, static id => id, outputs.Contains, "missing") == null,
    "missing outputs resolve to no target");

var maps = DesiredVolumeLogic.Build(new[]
{
    new DesiredVolumeLogic.Assignment(false, "discord", 0.25f, 1),
    new DesiredVolumeLogic.Assignment(false, "discord", 0.70f, 4),
    new DesiredVolumeLogic.Assignment(true, "speakers", 1.2f, 2),
});
Expect(Math.Abs(maps.Apps["discord"] - 0.70f) < 0.001f, "most recent overlapping fader wins");
Expect(Math.Abs(maps.Endpoints["speakers"] - 1f) < 0.001f, "desired volume clamps to scalar range");

var goodCapture = Enumerable.Range(0, 40).Select(i => i * 82).ToArray();
var goodAnalysis = CalibrationCaptureLogic.Analyze(goodCapture, 3250);
Expect(goodAnalysis.Good, "full calibration sweep is accepted");
var limitedAnalysis = CalibrationCaptureLogic.Analyze(Enumerable.Range(0, 20).Select(i => i * 30).ToArray(), 3250);
Expect(!limitedAnalysis.Good, "limited calibration travel is rejected");

Expect(SettingsRecoveryLogic.CandidatePaths("settings.json").SequenceEqual(new[]
    { "settings.json", "settings.json.bak", "settings.json.pre-import.bak" }),
    "settings recovery checks backups in safe order");
Expect(SettingsRecoveryLogic.SupportsSchema(3, 3) && !SettingsRecoveryLogic.SupportsSchema(4, 3),
    "settings schema compatibility is enforced");

var unsafeSettings = new Settings
{
    SchemaVersion = 3,
    ThemeMode = (ThemeMode)99,
    Categories = new() { new Category { Name = " #unassigned ", AppKeys = new() { "discord" } } },
    Devices = new()
    {
        [" keyboard "] = new LegacyDeviceProfile
        {
            Sliders = new()
            {
                new SliderConfig
                {
                    AxisIndex = 999,
                    Label = " ",
                    Max = -5,
                    Step = 0,
                    HkMute = new Hotkey { Vk = 999, Ctrl = true },
                    Cal = new Calibration { Min = -50, Max = 99_999, Taper = (TaperKind)99 },
                    Outputs = new()
                    {
                        new OutputPref { Id = " speakers ", Name = "" },
                        new OutputPref { Id = "SPEAKERS", Name = "duplicate" },
                    },
                },
            },
        },
    },
};
SettingsStore.Normalize(unsafeSettings);
var safeSlider = unsafeSettings.Layout.Single();
Expect(unsafeSettings.ThemeMode == ThemeMode.Auto && unsafeSettings.Categories.Count == 0,
    "invalid settings enums and reserved categories are sanitized");
Expect(safeSlider.AxisIndex == 7 && safeSlider.Max == 1 && safeSlider.Step == 1,
    "fader settings are clamped to supported bounds");
Expect(safeSlider.Cal.Min == 0 && safeSlider.Cal.Max == ushort.MaxValue
    && safeSlider.Cal.Taper == TaperKind.Linear, "calibration settings are sanitized");
Expect(safeSlider.HkMute.Vk == 0 && !safeSlider.HkMute.Ctrl && safeSlider.Outputs.Count == 1,
    "invalid hotkeys and duplicate outputs are normalized");

var oldImplicitProfile = new Settings
{
    SchemaVersion = 3,
    Devices = new()
    {
        ["steam-controller"] = new LegacyDeviceProfile
        {
            Sliders = new()
            {
                new SliderConfig { AxisIndex = 0, Label = "Left fader" },
                new SliderConfig { AxisIndex = 1, Label = "Right fader" },
            },
        },
    },
};
SettingsStore.Normalize(oldImplicitProfile);
Expect(oldImplicitProfile.Layout.Count == 0 && oldImplicitProfile.Devices == null,
    "untouched legacy left/right device template migrates to an empty setup");

var configuredPair = new Settings
{
    SchemaVersion = 3,
    Devices = new()
    {
        ["configured-keyboard"] = new LegacyDeviceProfile
        {
            Sliders = new()
            {
                new SliderConfig
                {
                    AxisIndex = 0,
                    Label = "Left fader",
                    Outputs = new() { new OutputPref { Id = "speakers", Name = "Speakers" } },
                },
                new SliderConfig { AxisIndex = 1, Label = "Right fader" },
            },
        },
    },
};
SettingsStore.Normalize(configuredPair);
Expect(configuredPair.Layout.Count == 2 && configuredPair.Devices == null,
    "configured legacy left/right pair is preserved");

var schemaThreeLayout = new Settings
{
    SchemaVersion = 3,
    Devices = new()
    {
        ["keyboard-a"] = new LegacyDeviceProfile
        {
            Name = "Keyboard A",
            Sliders = new() { new SliderConfig { AxisIndex = 3, Label = "Chat" } },
        },
        ["#virtual"] = new LegacyDeviceProfile
        {
            Sliders = new() { new SliderConfig { IsVirtual = true, AxisIndex = -1, Label = "Music" } },
        },
    },
};
SettingsStore.Normalize(schemaThreeLayout);
Expect(schemaThreeLayout.SchemaVersion == SettingsStore.CurrentSchema && schemaThreeLayout.Layout.Count == 2,
    "schema-three device profiles migrate into the global layout");
Expect(schemaThreeLayout.Layout[0].SourceDeviceKey == "keyboard-a"
    && schemaThreeLayout.Layout[0].SourceDeviceName == "Keyboard A"
    && schemaThreeLayout.Layout[1].IsVirtual,
    "global layout migration retains physical source identity and mixed ordering");

var intentionalEmptyLayout = new Settings
{
    SchemaVersion = SettingsStore.CurrentSchema,
    Devices = new()
    {
        ["stale-device"] = new LegacyDeviceProfile
        {
            Sliders = new() { new SliderConfig { AxisIndex = 0, Label = "Stale" } },
        },
    },
};
SettingsStore.Normalize(intentionalEmptyLayout);
Expect(intentionalEmptyLayout.Layout.Count == 0 && intentionalEmptyLayout.Devices == null,
    "current-schema empty layout does not resurrect legacy device profiles");

var ignoredLegacyDevice = new Settings
{
    SchemaVersion = 3,
    IgnoredDevices = new() { "unused-device" },
    Devices = new()
    {
        ["unused-device"] = new LegacyDeviceProfile
        {
            Name = "Unused",
            Sliders = new() { new SliderConfig { AxisIndex = 0, Label = "Custom but ignored" } },
        },
    },
};
SettingsStore.Normalize(ignoredLegacyDevice);
Expect(ignoredLegacyDevice.Layout.Count == 0,
    "ignored legacy devices do not reappear in the global layout");

var flatLegacySettings = new Settings
{
    LeftDeviceId = "speakers",
    LeftMax = 75,
    LeftCal = new Calibration { Min = 10, Max = 3200, Taper = TaperKind.Straight },
    RightOutputs = new() { new OutputPref { Id = "headset", Name = "Headset" } },
};
SettingsStore.Normalize(flatLegacySettings);
Expect(flatLegacySettings.Layout.Count == 2
    && flatLegacySettings.Layout.All(f => f.SourceDeviceKey == SettingsStore.LegacyFirstDeviceKey),
    "flat legacy settings are converted at the settings boundary");
Expect(flatLegacySettings.DeviceMax["speakers"] == 75
    && flatLegacySettings.Layout[0].Cal.Min == 10,
    "flat migration preserves endpoint cap and calibration");
string modernJson = JsonSerializer.Serialize(flatLegacySettings);
Expect(!modernJson.Contains("Devices", StringComparison.Ordinal)
    && !modernJson.Contains("LeftDeviceId", StringComparison.Ordinal)
    && !modernJson.Contains("IgnoredDevices", StringComparison.Ordinal),
    "normalized schema-four settings do not rewrite legacy payload fields");

var duplicatePhysicalBinding = new Settings
{
    Layout = new()
    {
        new SliderConfig { SourceDeviceKey = "keyboard", AxisIndex = 0, Label = "First" },
        new SliderConfig { SourceDeviceKey = "KEYBOARD", AxisIndex = 0, Label = "Duplicate" },
        new SliderConfig { SourceDeviceKey = "keyboard", AxisIndex = 1, Label = "Second axis" },
    },
};
SettingsStore.Normalize(duplicatePhysicalBinding);
Expect(duplicatePhysicalBinding.Layout.Select(f => (f.SourceDeviceKey, f.AxisIndex)).Count() == 2,
    "one physical device axis can appear only once in the layout");

var duplicateBindings = FaderLayoutLogic.FindDuplicates(new[]
{
    new PhysicalBinding("keyboard", 0),
    new PhysicalBinding("KEYBOARD", 0),
    new PhysicalBinding("keyboard", 1),
    new PhysicalBinding("keyboard", 0),
});
Expect(duplicateBindings.Count == 1 && duplicateBindings[0].Axis == 0,
    "layout validation reports each duplicate physical source once");

var rebound = new SliderConfig
{
    Id = "stable-id", SourceDeviceKey = "old", AxisIndex = 0,
    Target = TargetKind.App, AppKey = "discord", Max = 83,
    Cal = new Calibration { Min = 0, Max = 3000, Taper = TaperKind.AudioReversed, Inverted = true },
    HkMute = new Hotkey { Vk = 0x7B },
};
FaderLayoutLogic.ApplyPhysicalSource(rebound, "new-device", "New Device", 3, 20, 3250);
Expect(rebound.Id == "stable-id" && rebound.SourceDeviceKey == "new-device" && rebound.AxisIndex == 3
    && rebound.Cal.Min == 20 && rebound.Cal.Max == 3250,
    "source reassignment changes identity, axis, and captured range");
Expect(rebound.Target == TargetKind.App && rebound.AppKey == "discord" && rebound.Max == 83
    && rebound.Cal.Taper == TaperKind.AudioReversed && rebound.Cal.Inverted && rebound.HkMute.Vk == 0x7B,
    "source reassignment preserves target, cap, reversed taper, inversion, and hotkeys");


Expect(AudioNotificationLogic.MatchesExpectedWrite(0.5f, 1_000, 900, 0.5f),
    "own session volume callback is suppressed");
Expect(!AudioNotificationLogic.MatchesExpectedWrite(0.5f, 1_000, 1_001, 0.5f),
    "expired session callback suppression is rejected");
Expect(AudioNotificationLogic.HasMeaningfulChange(float.NaN, 0.5f)
    && !AudioNotificationLogic.HasMeaningfulChange(0.5f, 0.501f),
    "external volume change deadband is stable");

var expectedWrites = new ExpectedVolumeWrites();
long expectedNow = Environment.TickCount64;
expectedWrites.Add(0.20f, expectedNow + 1_000);
expectedWrites.Add(0.30f, expectedNow + 1_000);
Expect(expectedWrites.Consume(0.20f, expectedNow + 900), "delayed earlier session callback is still recognized");
Expect(expectedWrites.Consume(0.30f, expectedNow + 900), "newer session callback is independently recognized");
expectedWrites.Add(0.40f, expectedNow + 1_000);
Expect(!expectedWrites.Consume(0.40f, expectedNow + 1_001), "expired session write is rejected");

var latest = new LatestValueBuffer<int>();
Expect(latest.Publish("discord", 10), "first coalesced value schedules a drain");
Expect(!latest.Publish("DISCORD", 20), "replacement does not schedule another drain");
Expect(latest.Drain().SequenceEqual(new[] { 20 }), "coalesced buffer keeps only the newest logical value");
Expect(latest.Publish("discord", 30), "publishing after a drain schedules again");

var repeats = new KeyRepeatTracker();
Expect(!repeats.Press(0x7C), "first key-down is not a repeat");
Expect(repeats.Press(0x7C), "held key-down is marked as repeat");
repeats.Release(0x7C);
Expect(!repeats.Press(0x7C), "key-up resets repeat tracking");

var muted = FaderMuteLogic.Toggle(false, 64, 10);
var unmuted = FaderMuteLogic.Toggle(muted.Muted, muted.Target, muted.PreMute);
Expect(muted.Muted && muted.Target == 0 && muted.PreMute == 64, "mute remembers prior volume");
Expect(!unmuted.Muted && unmuted.Target == 64, "unmute restores prior volume");

foreach (TaperKind taper in Enum.GetValues<TaperKind>())
{
    var calibration = new Calibration { Min = 0, Max = 3300, Taper = taper };
    var curve = calibration.BuildCurve();
    double previous = -1;
    for (int raw = 0; raw <= 3300; raw += 25)
    {
        double value = Calibration.Eval(curve, raw);
        Expect(value >= previous, $"{taper} preserves low-to-high travel");
        previous = value;
    }
    Expect(Math.Abs(Calibration.Eval(curve, 0)) < 0.001, $"{taper} starts at zero");
    Expect(Math.Abs(Calibration.Eval(curve, 3300) - 100) < 0.001, $"{taper} ends at 100");
}

var reversedCalibration = new Calibration
{
    Min = 0, Max = 3300, Taper = TaperKind.LinearReversed, MuteRaw = 10,
};
var reversedCurve = reversedCalibration.BuildCurve();
Expect(Math.Abs(reversedCalibration.MapRaw(reversedCurve, 0)) < 0.001,
    "reversed compensation starts at zero without changing travel");
Expect(Math.Abs(reversedCalibration.MapRaw(reversedCurve, 3300) - 100) < 0.001,
    "reversed compensation ends at 100 without changing travel");
double reversedRaw = reversedCalibration.RawAtPercentage(reversedCurve, 25);
Expect(Math.Abs(reversedCalibration.MapRaw(reversedCurve, (int)Math.Round(reversedRaw)) - 25) < 0.1,
    "reversed calibration generates raw diagnostics in the correct direction");
Expect(reversedCalibration.IsInMuteZone(5) && !reversedCalibration.IsInMuteZone(100),
    "reversed compensation does not move the zero-percent mute endpoint");
Expect(reversedCalibration.IsOutsideMuteZone(30, 15),
    "reversed compensation keeps normal mute hysteresis direction");
Expect(reversedCalibration.Clone().Taper == TaperKind.LinearReversed,
    "calibration clone retains its reversed taper");

var reversedAudio = new Calibration { Min = 0, Max = 3300, Taper = TaperKind.AudioReversed };
Expect(Math.Abs(reversedAudio.MapRaw(reversedAudio.BuildCurve(), 330) - 10) < 0.001,
    "reversed audio mirrors compensation while preserving travel direction");
var independentTransforms = new Calibration
{
    Min = 0, Max = 3300, Taper = TaperKind.AudioReversed, Inverted = true,
};
var independentCurve = independentTransforms.BuildCurve();
Expect(Math.Abs(independentTransforms.MapRaw(independentCurve, 330) - 90) < 0.001,
    "final inversion remains independent from reversed electrical compensation");
double independentRaw = independentTransforms.RawAtPercentage(independentCurve, 25);
Expect(Math.Abs(independentTransforms.MapRaw(independentCurve, (int)Math.Round(independentRaw)) - 25) < 0.1,
    "diagnostic inverse honors the combination of reversed taper and inversion");
var combinedMute = new Calibration
{
    Min = 0, Max = 3300, Taper = TaperKind.LinearReversed, Inverted = true, MuteRaw = 10,
};
Expect(combinedMute.IsInMuteZone(3295) && !combinedMute.IsInMuteZone(5),
    "mute endpoint follows the combined taper and final inversion direction");

var invertSettings = new Settings
{
    SchemaVersion = SettingsStore.CurrentSchema,
    Layout = new()
    {
        new SliderConfig
        {
            SourceDeviceKey = "keyboard", AxisIndex = 0,
            Cal = new Calibration { Taper = TaperKind.Audio, Inverted = true },
        },
    },
};
SettingsStore.Normalize(invertSettings);
Expect(invertSettings.Layout[0].Cal.Taper == TaperKind.Audio
    && invertSettings.Layout[0].Cal.Inverted,
    "final inversion remains independent during settings normalization");
Expect(JsonSerializer.Serialize(invertSettings).Contains("Inverted", StringComparison.Ordinal),
    "enabled final inversion is persisted");

var redundantStraightPreview = new Settings
{
    SchemaVersion = SettingsStore.CurrentSchema,
    Layout = new()
    {
        new SliderConfig
        {
            SourceDeviceKey = "keyboard", AxisIndex = 0,
            Cal = new Calibration { Taper = (TaperKind)5 },
        },
    },
};
SettingsStore.Normalize(redundantStraightPreview);
Expect(redundantStraightPreview.Layout[0].Cal.Taper == TaperKind.Straight,
    "redundant reversed-straight preview settings migrate to uncompensated Straight");

Console.WriteLine("All logic tests passed.");
