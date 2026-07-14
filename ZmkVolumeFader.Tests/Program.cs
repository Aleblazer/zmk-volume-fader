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
