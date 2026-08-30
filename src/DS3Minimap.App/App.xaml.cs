using System.Windows;
using System.IO;
using DS3Minimap.Core;

namespace DS3Minimap.App;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "Local\\DS3Minimap.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("DS3 Minimap is already running.", "DS3 Minimap", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Logs);
                File.AppendAllText(Path.Combine(AppPaths.Logs, "unhandled.log"),
                    $"{DateTimeOffset.Now:O} {args.Exception}\n\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            args.Handled = true;
        };
        MainWindow = new OverlayWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
