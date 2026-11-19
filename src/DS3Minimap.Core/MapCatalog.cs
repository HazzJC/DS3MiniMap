namespace DS3Minimap.Core;

public sealed record GameMap(string Id, string Name, string? Expansion = null);

public static class MapCatalog
{
    public static readonly GameMap[] Maps =
    [
        new("m30_00", "High Wall of Lothric"),
        new("m30_01", "Lothric Castle / Consumed King’s Garden"),
        new("m31_00", "Undead Settlement"),
        new("m32_00", "Archdragon Peak"),
        new("m33_00", "Road of Sacrifices / Farron Keep"),
        new("m34_01", "Grand Archives"),
        new("m35_00", "Cathedral of the Deep"),
        new("m37_00", "Irithyll / Anor Londo"),
        new("m38_00", "Catacombs of Carthus / Smouldering Lake"),
        new("m39_00", "Irithyll Dungeon / Profaned Capital"),
        new("m40_00", "Cemetery of Ash / Firelink / Untended Graves"),
        new("m41_00", "Kiln of the First Flame"),
        new("m45_00", "Painted World of Ariandel", "DLC1"),
        new("m46_00", "Hollow Arena · Grand Roof"),
        new("m47_00", "Hollow Arena · Kiln of Flame"),
        new("m50_00", "The Dreg Heap", "DLC2"),
        new("m51_00", "The Ringed City", "DLC2"),
        new("m51_01", "Filianore’s Rest", "DLC2"),
        new("m53_00", "Hollow Arena · Dragon Ruins"),
        new("m54_00", "Hollow Arena · Round Plaza")
    ];

    public static string[] InstalledArchives(string directory)
    {
        var names = Enumerable.Range(0, 6).Select(index => $"Data{index}").ToList();
        foreach (var dlc in new[] { "DLC1", "DLC2" })
        {
            if (File.Exists(Path.Combine(directory, dlc + ".bhd")) || File.Exists(Path.Combine(directory, dlc + ".bdt")))
                names.Add(dlc);
        }
        return names.ToArray();
    }

    public static bool MatchesInstallation(MapPackage package, string directory, DateTime packageTime)
    {
        var names = InstalledArchives(directory);
        return package.Archives?.SequenceEqual(names) == true && names.All(name =>
            new[] { ".bhd", ".bdt" }.All(extension =>
            {
                var file = new FileInfo(Path.Combine(directory, name + extension));
                return file.Exists && file.LastWriteTimeUtc <= packageTime;
            }));
    }
}
