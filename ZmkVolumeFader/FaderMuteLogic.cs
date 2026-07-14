namespace ZmkVolumeFader;

internal static class FaderMuteLogic
{
    internal readonly record struct State(bool Muted, int Target, int PreMute);

    public static State Toggle(bool muted, int target, int preMute) => muted
        ? new State(false, Math.Clamp(preMute, 0, 100), Math.Clamp(preMute, 0, 100))
        : new State(true, 0, Math.Clamp(target, 0, 100));
}
