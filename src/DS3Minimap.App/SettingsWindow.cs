using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DS3Minimap.Core;
using Microsoft.Win32;

namespace DS3Minimap.App;

public sealed class SettingsWindow : Window
{
    private readonly Slider _size;
    private readonly Slider _zoom;
    private readonly Slider _opacity;
    private readonly ComboBox _orientation;
    private readonly ComboBox _mask;
    private readonly ComboBox _anchor;
    private readonly CheckBox _persistent;
    private readonly CheckBox _hideUnfocused;
    private readonly CheckBox _borderless;
    private readonly TextBox _backgroundColor;
    private readonly TextBox _geometryColor;
    private readonly TextBox _playerColor;
    private readonly TextBox _trailColor;
    private readonly Slider _trailRadius;
    private readonly Slider _trailLifetime;
    private readonly CheckBox _fadeTrail;
    private readonly Slider _textureStrength;
    private readonly Slider _transitionDuration;
    private readonly TextBox _boundaryColor;
    private readonly TextBox _frameColor;
    private readonly TextBox _playerOutlineColor;
    private readonly CheckBox _showBonfires;
    private readonly CheckBox _showItems;
    private readonly TextBox _bonfireColor;
    private readonly TextBox _itemColor;
    private readonly Slider _markerScale;
    private readonly IReadOnlyList<TrailPoint> _trail;

    public SettingsWindow(AppSettings value, IReadOnlyList<TrailPoint> trail, string diagnostic)
    {
        _trail = trail;
        Value = value;
        Title = "DS3 Minimap — Cartographer settings";
        Width = 500;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = true;
        FontFamily = new FontFamily("Palatino Linotype");
        Background = Brush("#171411");
        Foreground = Brush("#E9DFC8");
        AddControlStyles();
        var panel = new StackPanel { Margin = new Thickness(28, 22, 28, 28) };
        panel.Children.Add(new TextBlock { Text = "DS3 MINIMAP", FontSize = 27, FontWeight = FontWeights.Light, Foreground = Brush("#D7BE83") });
        panel.Children.Add(new TextBlock { Text = "CARTOGRAPHER SETTINGS", FontSize = 10, Foreground = Brush("#8F7A55"), Margin = new Thickness(2, 0, 0, 14) });
        panel.Children.Add(new Border
        {
            Background = Brush("#C913110F"), BorderBrush = Brush("#584A34"), BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 13),
            Child = new TextBlock { Text = diagnostic, TextWrapping = TextWrapping.Wrap, Opacity = .72, FontSize = 11 }
        });
        AddSection(panel, "MAP PRESENTATION");
        _size = AddSlider(panel, "Size", 160, 800, value.Size);
        _zoom = AddSlider(panel, "Zoom", .25, 10, value.Zoom);
        _opacity = AddSlider(panel, "Opacity", .15, 1, value.Opacity);
        _orientation = AddEnum(panel, "Orientation", value.Orientation);
        _mask = AddEnum(panel, "Mask", value.Mask);
        _anchor = AddEnum(panel, "Anchor", value.Anchor);
        _textureStrength = AddSlider(panel, "Ground texture", 0, 1, value.MapTextureStrength);
        _transitionDuration = AddSlider(panel, "Floor transition (milliseconds)", 0, 1500, value.LayerTransitionMilliseconds);
        AddSection(panel, "MAP MARKERS");
        _showBonfires = new CheckBox { Content = "Show bonfires", IsChecked = value.ShowBonfires, Margin = new Thickness(0, 4, 0, 8) };
        _showItems = new CheckBox { Content = "Show uncollected items (read-only pickup tracking)", IsChecked = value.ShowUncollectedItems, Margin = new Thickness(0, 2, 0, 8) };
        panel.Children.Add(_showBonfires);
        panel.Children.Add(_showItems);
        _markerScale = AddSlider(panel, "Marker size", .6, 2.5, value.MarkerScale);
        AddSection(panel, "BREADCRUMBS");
        _trailRadius = AddSlider(panel, "Trail dot size", 1, 10, value.TrailDotRadius);
        _trailLifetime = AddSlider(panel, "Trail duration in minutes (0 = entire session)", 0, 180, value.TrailLifetimeMinutes);
        _fadeTrail = new CheckBox { Content = "Fade trail gradually with age", IsChecked = value.FadeTrailWithAge, Margin = new Thickness(0, 8, 0, 4) };
        panel.Children.Add(_fadeTrail);
        _persistent = new CheckBox { Content = "Persist trail history locally", IsChecked = value.PersistentTrails, Margin = new Thickness(0, 10, 0, 10) };
        panel.Children.Add(_persistent);
        AddSection(panel, "WINDOW BEHAVIOUR");
        _hideUnfocused = new CheckBox { Content = "Hide while DS3 is unfocused", IsChecked = value.HideWhenGameUnfocused, Margin = new Thickness(0, 2, 0, 10) };
        panel.Children.Add(_hideUnfocused);
        _borderless = new CheckBox { Content = "Managed borderless game window", IsChecked = value.ManagedBorderless, Margin = new Thickness(0, 2, 0, 10) };
        panel.Children.Add(_borderless);
        AddSection(panel, "PALETTE");
        _backgroundColor = AddText(panel, "Background color (ARGB hex)", value.BackgroundColor);
        _geometryColor = AddText(panel, "Map color", value.GeometryColor);
        _boundaryColor = AddText(panel, "Map-edge color", value.BoundaryColor);
        _frameColor = AddText(panel, "Frame color", value.FrameColor);
        _playerColor = AddText(panel, "Player color", value.PlayerColor);
        _playerOutlineColor = AddText(panel, "Player-outline color", value.PlayerOutlineColor);
        _trailColor = AddText(panel, "Trail color", value.TrailColor);
        _bonfireColor = AddText(panel, "Bonfire color", value.BonfireColor);
        _itemColor = AddText(panel, "Uncollected-item color", value.ItemColor);
        var exports = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var exportJson = new Button { Content = "Export trail JSON", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
        var exportCsv = new Button { Content = "Export trail CSV", Padding = new Thickness(10, 5, 10, 5) };
        exportJson.Click += async (_, _) => await ExportAsync("JSON files|*.json", "json");
        exportCsv.Click += async (_, _) => await ExportAsync("CSV files|*.csv", "csv");
        exports.Children.Add(exportJson);
        exports.Children.Add(exportCsv);
        panel.Children.Add(exports);
        var save = new Button { Content = "APPLY AND RETURN", Height = 42, Margin = new Thickness(0, 18, 0, 0), FontWeight = FontWeights.SemiBold };
        save.Click += (_, _) => Save();
        panel.Children.Add(save);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public AppSettings Value { get; private set; }

    private void Save()
    {
        Value = Value with
        {
            Size = _size.Value,
            Zoom = _zoom.Value,
            Opacity = _opacity.Value,
            Orientation = (MapOrientation)_orientation.SelectedItem,
            Mask = (MapMask)_mask.SelectedItem,
            Anchor = (OverlayAnchor)_anchor.SelectedItem,
            PersistentTrails = _persistent.IsChecked == true,
            HideWhenGameUnfocused = _hideUnfocused.IsChecked == true,
            ManagedBorderless = _borderless.IsChecked == true,
            BackgroundColor = _backgroundColor.Text.Trim(),
            GeometryColor = _geometryColor.Text.Trim(),
            PlayerColor = _playerColor.Text.Trim(),
            TrailColor = _trailColor.Text.Trim(),
            TrailDotRadius = (float)_trailRadius.Value,
            TrailLifetimeMinutes = _trailLifetime.Value,
            FadeTrailWithAge = _fadeTrail.IsChecked == true,
            MapTextureStrength = _textureStrength.Value,
            LayerTransitionMilliseconds = _transitionDuration.Value,
            BoundaryColor = _boundaryColor.Text.Trim(),
            FrameColor = _frameColor.Text.Trim(),
            PlayerOutlineColor = _playerOutlineColor.Text.Trim(),
            ShowBonfires = _showBonfires.IsChecked == true,
            ShowUncollectedItems = _showItems.IsChecked == true,
            BonfireColor = _bonfireColor.Text.Trim(),
            ItemColor = _itemColor.Text.Trim(),
            MarkerScale = _markerScale.Value
        };
        DialogResult = true;
    }

    private static Slider AddSlider(Panel panel, string label, double minimum, double maximum, double value)
    {
        var caption = new TextBlock { Text = $"{label}: {value:0.##}", Margin = new Thickness(0, 7, 0, 2) };
        panel.Children.Add(caption);
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = value, TickFrequency = (maximum - minimum) / 20 };
        slider.ValueChanged += (_, _) => caption.Text = $"{label}: {slider.Value:0.##}";
        panel.Children.Add(slider);
        return slider;
    }

    private static ComboBox AddEnum<T>(Panel panel, string label, T value) where T : struct, Enum
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 2) });
        var combo = new ComboBox { ItemsSource = Enum.GetValues<T>(), SelectedItem = value };
        panel.Children.Add(combo);
        return combo;
    }

    private static TextBox AddText(Panel panel, string label, string value)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 2) });
        var text = new TextBox { Text = value };
        panel.Children.Add(text);
        return text;
    }

    private static void AddSection(Panel panel, string title)
    {
        panel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#B79A62"),
            Margin = new Thickness(0, 16, 0, 4)
        });
        panel.Children.Add(new Border { Height = 1, Background = Brush("#4D402C"), Margin = new Thickness(0, 0, 0, 3) });
    }

    private void AddControlStyles()
    {
        Resources[typeof(Button)] = StyleFor<Button>(
            (Control.ForegroundProperty, Brush("#E9DFC8")), (Control.BackgroundProperty, Brush("#302820")),
            (Control.BorderBrushProperty, Brush("#806A43")), (Control.BorderThicknessProperty, new Thickness(1)),
            (Control.PaddingProperty, new Thickness(10, 5, 10, 5)));
        Resources[typeof(TextBox)] = StyleFor<TextBox>(
            (Control.ForegroundProperty, Brush("#E9DFC8")), (Control.BackgroundProperty, Brush("#100F0D")),
            (Control.BorderBrushProperty, Brush("#5E5039")), (Control.BorderThicknessProperty, new Thickness(1)),
            (Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
        Resources[typeof(ComboBox)] = StyleFor<ComboBox>(
            (Control.ForegroundProperty, Brush("#E9DFC8")), (Control.BackgroundProperty, Brush("#242019")),
            (Control.BorderBrushProperty, Brush("#5E5039")), (Control.PaddingProperty, new Thickness(4, 2, 4, 2)));
        Resources[typeof(Slider)] = StyleFor<Slider>((Control.ForegroundProperty, Brush("#B79A62")));
    }

    private static Style StyleFor<T>(params (DependencyProperty Property, object Value)[] setters)
    {
        var style = new Style(typeof(T));
        foreach (var (property, value) in setters) style.Setters.Add(new Setter(property, value));
        return style;
    }

    private static SolidColorBrush Brush(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));

    private async Task ExportAsync(string filter, string extension)
    {
        var dialog = new SaveFileDialog { Filter = filter, DefaultExt = extension, FileName = $"ds3-trail-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}" };
        if (dialog.ShowDialog(this) != true) return;
        if (extension == "json") await TrailExporter.ExportJsonAsync(_trail, dialog.FileName);
        else await TrailExporter.ExportCsvAsync(_trail, dialog.FileName);
    }
}
