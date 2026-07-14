namespace ZmkVolumeFader;

/// <summary>Pure soft-takeover crossing logic, kept separate for regression tests.</summary>
internal static class PickupLogic
{
    public static bool HasReached(int? previous, int current, int target, int tolerance = 2)
    {
        if (Math.Abs(current - target) <= tolerance) return true;
        return previous is int last && (last - target) * (current - target) <= 0;
    }
}
