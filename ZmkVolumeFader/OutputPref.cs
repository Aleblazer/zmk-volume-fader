namespace ZmkVolumeFader;

/// <summary>
/// One entry in a fader's ranked output list: a Windows audio endpoint id plus a
/// friendly name kept so the device still reads sensibly in the editor when it's
/// unplugged (and thus not enumerable). The app drives the highest-ranked entry
/// whose device is currently present.
/// </summary>
internal sealed class OutputPref
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public override string ToString() => Name;
}
