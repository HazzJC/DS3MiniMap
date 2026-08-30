using System.Xml.Linq;
using System.IO;
using System.Windows;
using DS3Minimap.Core;

namespace DS3Minimap.App;

internal static class FirstRunSetup
{
    internal static async Task RunAsync(AppSettings settings)
    {
        var marker = Path.Combine(AppPaths.Root, ".first-run-complete");
        if (File.Exists(marker)) return;
        Directory.CreateDirectory(AppPaths.Root);
        MessageBox.Show(
            "DS3 Minimap is unofficial and reads the local game process. It never writes to DS3 memory, but third-party tools always carry risk. Use offline play.\n\nHotkeys:\nCtrl+Shift+M settings\nCtrl+Shift+H hide/show\nCtrl+Shift+C clear trail",
            "DS3 Minimap safety notice", MessageBoxButton.OK, MessageBoxImage.Information);
        if (settings.ManagedBorderless && !System.Diagnostics.Process.GetProcessesByName("DarkSoulsIII").Any())
        {
            var result = MessageBox.Show("Configure DS3 for managed borderless window mode? The current GraphicsConfig.xml will be backed up first.", "Managed borderless", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes) ConfigureWindowedMode();
        }
        await File.WriteAllTextAsync(marker, DateTimeOffset.UtcNow.ToString("O"));
    }

    private static void ConfigureWindowedMode()
    {
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DarkSoulsIII", "GraphicsConfig.xml");
        if (!File.Exists(config)) return;
        var backup = config + ".ds3minimap-backup";
        if (!File.Exists(backup)) File.Copy(config, backup);
        var document = XDocument.Load(config, LoadOptions.PreserveWhitespace);
        var element = document.Descendants("ScreenMode").FirstOrDefault();
        if (element is null) return;
        element.Value = "WINDOW";
        document.Save(config);
    }
}
