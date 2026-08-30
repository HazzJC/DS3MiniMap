namespace DS3Minimap.Core;

public static class AppPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DS3Minimap");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string TrailDatabase => Path.Combine(Root, "data", "trails.sqlite3");
    public static string MapPackage => Path.Combine(Root, "maps", "1.15.2", "base.dsmaps");
    public static string Logs => Path.Combine(Root, "logs");
}
