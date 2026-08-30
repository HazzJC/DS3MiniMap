using System.Diagnostics;
using System.IO;
using System.Windows;
using DS3Minimap.Core;
using Microsoft.Win32;

namespace DS3Minimap.App;

/// <summary>
/// Makes a normal installed release self-sufficient: on first launch it invokes
/// the bundled, read-only map baker against the user's own DS3 installation.
/// </summary>
internal static class MapPackagePreparation
{
    internal static async Task<bool> EnsureCurrentAsync(Window owner)
    {
        if (await HasCurrentPackageAsync()) return true;

        if (Process.GetProcessesByName("DarkSoulsIII").Length > 0)
        {
            MessageBox.Show(owner,
                "DS3 Minimap needs to prepare its local map package once. Close Dark Souls III, then start DS3 Minimap again.",
                "Close DS3 to prepare maps", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var gameDirectory = FindGameDirectory() ?? AskForGameDirectory(owner);
        if (gameDirectory is null) return false;

        var baker = Path.Combine(AppContext.BaseDirectory, "baker", "DS3Minimap.MapBaker.exe");
        if (!File.Exists(baker))
        {
            MessageBox.Show(owner,
                "The bundled map-preparation component is missing. Run the installer again to repair DS3 Minimap.",
                "Map preparation unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        var dialog = new MapBakeWindow(baker, gameDirectory) { Owner = owner };
        return dialog.ShowDialog() == true && await HasCurrentPackageAsync();
    }

    private static async Task<bool> HasCurrentPackageAsync()
    {
        if (!File.Exists(AppPaths.MapPackage)) return false;
        try
        {
            var package = await MapPackageSerializer.ReadAsync(AppPaths.MapPackage);
            return package.SchemaVersion == MapPackage.CurrentSchemaVersion && package.GameVersion == "1.15.2";
        }
        catch (IOException) { return false; }
        catch (InvalidDataException) { return false; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static string? FindGameDirectory()
    {
        var candidates = new List<string>
        {
            "C:\\Program Files (x86)\\Steam\\steamapps\\common\\DARK SOULS III\\Game"
        };
        var libraryFile = "C:\\Program Files (x86)\\Steam\\steamapps\\libraryfolders.vdf";
        if (File.Exists(libraryFile))
        {
            foreach (var line in File.ReadLines(libraryFile))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"");
                if (!match.Success) continue;
                candidates.Add(Path.Combine(match.Groups[1].Value.Replace("\\\\", "\\"), "steamapps", "common", "DARK SOULS III", "Game"));
            }
        }
        return candidates.FirstOrDefault(IsGameDirectory);
    }

    private static string? AskForGameDirectory(Window owner)
    {
        var result = MessageBox.Show(owner,
            "Dark Souls III was not found in a Steam library. Choose DarkSoulsIII.exe so DS3 Minimap can prepare maps from your local installation.",
            "Locate Dark Souls III", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (result != MessageBoxResult.OK) return null;
        var dialog = new OpenFileDialog
        {
            Title = "Select DarkSoulsIII.exe",
            Filter = "Dark Souls III executable|DarkSoulsIII.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog(owner) == true && IsGameDirectory(Path.GetDirectoryName(dialog.FileName) ?? string.Empty)
            ? Path.GetDirectoryName(dialog.FileName)
            : null;
    }

    private static bool IsGameDirectory(string path) =>
        File.Exists(Path.Combine(path, "DarkSoulsIII.exe"))
        && File.Exists(Path.Combine(path, "Data0.bdt"))
        && File.Exists(Path.Combine(path, "Data5.bdt"));
}
