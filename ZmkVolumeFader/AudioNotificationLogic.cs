namespace ZmkVolumeFader;

internal static class AudioNotificationLogic
{
    public static bool MatchesExpectedWrite(float expected, long expectedUntil, long now, float actual,
        float epsilon = 0.0049f) =>
        now <= expectedUntil && !float.IsNaN(expected) && Math.Abs(expected - actual) < epsilon;

    public static bool HasMeaningfulChange(float previous, float current, float epsilon = 0.0049f) =>
        float.IsNaN(previous) || Math.Abs(previous - current) >= epsilon;
}
