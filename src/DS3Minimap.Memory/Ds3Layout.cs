namespace DS3Minimap.Memory;

public sealed record Ds3Layout(
    string SupportedFileVersion,
    string WorldChrManPattern,
    int WorldChrManDisplacementOffset,
    int WorldChrManInstructionLength,
    string GameDataManPattern,
    int GameDataManDisplacementOffset,
    int GameDataManInstructionLength,
    string EventFlagManPattern,
    int EventFlagManDisplacementOffset,
    int EventFlagManInstructionLength,
    string FieldAreaPattern,
    int FieldAreaDisplacementOffset,
    int FieldAreaInstructionLength)
{
    public static Ds3Layout Version1152 { get; } = new(
        "1.15.2.0",
        "48 8B 1D ?? ?? ?? 04 48 8B F9 48 85 DB ?? ?? 8B 11 85 D2 ?? ?? 8D",
        3,
        7,
        "48 8B 05 ?? ?? ?? ?? 48 85 C0 ?? ?? 48 8B 40 ?? C3",
        3,
        7,
        "48 C7 05 ?? ?? ?? ?? 00 00 00 00 48 8B 7C 24 38 C7 46 54 FF FF FF FF 48 83 C4 20 5E C3",
        3,
        11,
        "4C 8B 3D ?? ?? ?? ?? 8B 45 87 83 F8 FF 74 69 48 8D 4D 8F 48 89 4D 9F 89 45 8F 48 8D 55 8F 49 8B 4F 10",
        3,
        7);
}
