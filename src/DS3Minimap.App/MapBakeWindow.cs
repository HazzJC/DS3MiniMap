using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DS3Minimap.App;

internal sealed class MapBakeWindow : Window
{
    private readonly string _baker;
    private readonly string _gameDirectory;
    private readonly TextBox _output;
    private readonly ProgressBar _progress;
    private readonly Button _close;
    private readonly bool _texturesOnly;
    private bool _running;

    internal MapBakeWindow(string baker, string gameDirectory, bool texturesOnly = false)
    {
        _baker = baker;
        _gameDirectory = gameDirectory;
        _texturesOnly = texturesOnly;
        Title = "DS3 Minimap â€” preparing local maps";
        Width = 640;
        Height = 420;
        MinWidth = 560;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#171411");
        Foreground = Brush("#E9DFC8");
        FontFamily = new FontFamily("Palatino Linotype");

        var panel = new DockPanel { Margin = new Thickness(26, 22, 26, 24) };
        var heading = new TextBlock
        {
            Text = texturesOnly ? "PREPARING GROUND TEXTURES" : "PREPARING YOUR LOCAL MAP PACKAGE", FontSize = 20, Foreground = Brush("#D7BE83"),
            Margin = new Thickness(0, 0, 0, 7)
        };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var note = new TextBlock
        {
            Text = texturesOnly
                ? "The minimap is painting real ground textures from your game, once. This takes a few minutes; DS3 files are read in place and never changed. You can close this window afterwards and play."
                : "This happens once per DS3 version. It reads your maps and then paints their real ground textures. DS3 files are read in place and are never changed.",
            Foreground = Brush("#BCAE91"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14)
        };
        DockPanel.SetDock(note, Dock.Top);
        panel.Children.Add(note);
        _progress = new ProgressBar { Height = 7, IsIndeterminate = true, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(_progress, Dock.Top);
        panel.Children.Add(_progress);
        _close = new Button { Content = "Preparingâ€¦", IsEnabled = false, MinWidth = 110, Padding = new Thickness(12, 5, 12, 5) };
        _close.Click += (_, _) => Close();
        DockPanel.SetDock(_close, Dock.Bottom);
        panel.Children.Add(_close);
        _output = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brush("#100F0D"), Foreground = Brush("#E9DFC8"), BorderBrush = Brush("#5E5039"),
            BorderThickness = new Thickness(1), Padding = new Thickness(10), FontFamily = new FontFamily("Consolas"), FontSize = 11
        };
        panel.Children.Add(_output);
        Content = panel;
        ContentRendered += async (_, _) => await BakeAsync();
        Closing += OnClosing;
    }

    private async Task BakeAsync()
    {
        _running = true;
        var succeeded = false;
        try
        {
            var start = new ProcessStartInfo(_baker)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            start.ArgumentList.Add("--game-dir");
            start.ArgumentList.Add(_gameDirectory);
            if (_texturesOnly) start.ArgumentList.Add("--materials-only");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The bundled map-preparation process could not start.");
            var stdout = PumpAsync(process.StandardOutput);
            var stderr = PumpAsync(process.StandardError);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0) throw new InvalidOperationException($"Map preparation stopped with exit code {process.ExitCode}.");
            _output.AppendText(Environment.NewLine + "Map package prepared successfully.\n");
            succeeded = true;
        }
        catch (Exception exception)
        {
            _output.AppendText(Environment.NewLine + "Map preparation could not finish: " + exception.Message + "\n");
        }
        finally
        {
            _running = false;
            _progress.IsIndeterminate = false;
            _close.Content = "Close";
            _close.IsEnabled = true;
        }
        if (succeeded) DialogResult = true;
    }

    private async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
            await Dispatcher.InvokeAsync(() =>
            {
                _output.AppendText(line + Environment.NewLine);
                _output.ScrollToEnd();
            });
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_running) e.Cancel = true;
    }

    private static SolidColorBrush Brush(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));
}
