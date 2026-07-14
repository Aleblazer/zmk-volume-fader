using ZmkVolumeFader;

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

Expect(AudioNotificationLogic.MatchesExpectedWrite(0.5f, 1_000, 900, 0.5f),
    "own session volume callback is suppressed");
Expect(!AudioNotificationLogic.MatchesExpectedWrite(0.5f, 1_000, 1_001, 0.5f),
    "expired session callback suppression is rejected");
Expect(AudioNotificationLogic.HasMeaningfulChange(float.NaN, 0.5f)
    && !AudioNotificationLogic.HasMeaningfulChange(0.5f, 0.501f),
    "external volume change deadband is stable");

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
        Expect(value >= previous, $"{taper} calibration is monotonic");
        previous = value;
    }
    Expect(Math.Abs(Calibration.Eval(curve, 0)) < 0.001, $"{taper} starts at zero");
    Expect(Math.Abs(Calibration.Eval(curve, 3300) - 100) < 0.001, $"{taper} ends at 100");
}

Console.WriteLine("All logic tests passed.");
