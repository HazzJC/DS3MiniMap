using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DS3Minimap.Core;
using DS3Minimap.Rendering;
using System.Numerics;
using System.Windows.Automation;
using System.Windows.Documents;
using Microsoft.Win32;

namespace DS3Minimap.App;

public sealed class SettingsWindow : Window
{
    private readonly CheckBox _ground;
    private readonly CheckBox _floorLabel;
    private readonly ComboBox _trailStyle;
    private readonly Slider _edgeMargin;
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
    private readonly ComboBox _groundStyle;
    private readonly Slider _textureToning;
    private readonly CheckBox _unlitBonfires;
    private readonly CheckBox _bonfirePointer;
    private readonly CheckBox _fogOfWar;
    private readonly Slider _fogStrength;
    private readonly IReadOnlyList<TrailPoint> _trail;

    public SettingsWindow(AppSettings value, IReadOnlyList<TrailPoint> trail, string diagnostic, MapPackage? maps = null,
        PlayerTelemetry? player = null, EventFlagSnapshot? flags = null, EventFlagSnapshot? bonfireFlags = null,
        Func<Task>? resetExplored = null)
    {
        _trail = trail;
        Value = value;
        Title = "DS3 Minimap â€” Cartographer settings";
        Width = Math.Min(800, SystemParameters.WorkArea.Width);
        Height = Math.Min(620, SystemParameters.WorkArea.Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = true;
        FontFamily = new FontFamily("Segoe UI");
        Background = Brush("#171411");
        Foreground = Brush("#F2E9D6");
        AddControlStyles();
        var layout = new DockPanel { Margin = new Thickness(18) };
        var title = new TextBlock { Text = "Minimap settings", FontSize = 24, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(title, Dock.Top);
        layout.Children.Add(title);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        var save = new Button { Content = "Apply and return", IsDefault = true, Padding = new Thickness(16, 7, 16, 7) };
        save.Click += (_, _) => Save();
        DockPanel.SetDock(save, Dock.Right);
        footer.Children.Add(save);
        footer.Children.Add(new TextBlock { Text = diagnostic, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("#CCBEA4"), Margin = new Thickness(0, 0, 10, 0) });

        var previewPanel = new StackPanel { Width = 228, Margin = new Thickness(18, 16, 0, 0) };
        var preview = new MinimapView();
        var region = maps?.Regions.FirstOrDefault(r => r.MapId == player?.MapId)
            ?? maps?.Regions.FirstOrDefault(r => r.MapId == "m40_00") ?? maps?.Regions.FirstOrDefault();
        var landmark = region?.Markers?.FirstOrDefault(m => m.Kind == MapMarkerKind.Bonfire);
        if (player?.MapId != region?.MapId || player == null)
            player = landmark == null || region == null ? null : new PlayerTelemetry(DateTimeOffset.Now,
                new Vector3(landmark.X + 3, landmark.Y, landmark.Z + 3), 0, region.MapId, "Preview");
        var floor = player == null ? null : LayerSelector.Select(region, player.Position);
        previewPanel.Children.Add(new TextBlock { Text = "Map preview", Foreground = Brush("#DBBF81"), FontSize = 16 });
        previewPanel.Children.Add(new Viewbox { Width = 220, Height = 220, Child = preview });
        previewPanel.Children.Add(new TextBlock { Text = region?.DisplayName, TextWrapping = TextWrapping.Wrap });
        if (region != null)
            previewPanel.Children.Add(new TextBlock { Text = "Ground shows the real textures from your game. Pale paths show where you can walk.",
                Foreground = Brush("#CCBEA4"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        DockPanel.SetDock(previewPanel, Dock.Right);
        layout.Children.Add(previewPanel);
        SizeChanged += (_, _) => previewPanel.Visibility = ActualWidth >= 670 ? Visibility.Visible : Visibility.Collapsed;

        var tabs = new TabControl { Background = Background, BorderBrush = Brush("#66563E") };
        layout.Children.Add(tabs);
        StackPanel Tab(string name)
        {
            var controls = new StackPanel { Margin = new Thickness(14, 6, 14, 12) };
            TextElement.SetForeground(controls, Foreground);
            tabs.Items.Add(new TabItem { Header = name, Foreground = SystemColors.ControlTextBrush, Content = new ScrollViewer
                { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
            return controls;
        }
        var panel = Tab("Map");
        _size = AddSlider(panel, "Size", 160, 800, value.Size);
        _zoom = AddSlider(panel, "Zoom", .25, 10, value.Zoom);
        _opacity = AddSlider(panel, "Opacity", .15, 1, value.Opacity);
        _orientation = AddEnum(panel, "Orientation", value.Orientation);
        _mask = AddEnum(panel, "Mask", value.Mask);
        _anchor = AddEnum(panel, "Anchor", value.Anchor);
        _edgeMargin = AddSlider(panel, "Distance from game edge", 0, 120, value.EdgeMargin);
        _ground = new CheckBox { Content = "Show collision ground around paths", IsChecked = value.ShowCollisionGround };
        _floorLabel = new CheckBox { Content = "Show area and floor label", IsChecked = value.ShowFloorLabel };
        panel.Children.Add(_ground);
        panel.Children.Add(_floorLabel);
        _groundStyle = AddEnum(panel, "Ground style", value.GroundStyle);
        _textureToning = AddSlider(panel, "Texture toning (0 = exactly as in game, 1 = map colour)", 0, 1, value.TextureToning);
        _textureStrength = AddSlider(panel, "Parchment texture strength", 0, 1, value.MapTextureStrength);
        _transitionDuration = AddSlider(panel, "Floor transition (milliseconds)", 0, 1500, value.LayerTransitionMilliseconds);
        _fogOfWar = new CheckBox { Content = "Dim areas I haven't explored yet", IsChecked = value.FogOfWar, Margin = new Thickness(0, 10, 0, 4) };
        panel.Children.Add(_fogOfWar);
        _fogStrength = AddSlider(panel, "Unexplored dimming", 0, 0.9, value.FogOfWarStrength);
        var resetExploredButton = new Button { Content = "Reset explored area", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), IsEnabled = resetExplored is not null };
        resetExploredButton.Click += async (_, _) =>
        {
            if (resetExplored is null) return;
            if (MessageBox.Show(this, "Forget everywhere this character has explored? The trail is not affected.", "Reset explored area",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            await resetExplored();
            resetExploredButton.Content = "Explored area reset";
        };
        panel.Children.Add(resetExploredButton);
        panel = Tab("Landmarks");
        _showBonfires = new CheckBox { Content = "Show bonfires", IsChecked = value.ShowBonfires, Margin = new Thickness(0, 4, 0, 8) };
        _showItems = new CheckBox { Content = "Show uncollected items (read-only pickup tracking)", IsChecked = value.ShowUncollectedItems, Margin = new Thickness(0, 2, 0, 8) };
        panel.Children.Add(_showBonfires);
        panel.Children.Add(_showItems);
        _unlitBonfires = new CheckBox { Content = "Show bonfires I haven't lit yet as grey ash", IsChecked = value.ShowUnlitBonfires, Margin = new Thickness(0, 2, 0, 8) };
        _bonfirePointer = new CheckBox { Content = "Point to a bonfire off the map or on another floor (prefer lit bonfires)", IsChecked = value.ShowNearestBonfirePointer, Margin = new Thickness(0, 2, 0, 8) };
        panel.Children.Add(_unlitBonfires);
        panel.Children.Add(_bonfirePointer);
        _markerScale = AddSlider(panel, "Marker size", .6, 2.5, value.MarkerScale);
        panel = Tab("Trail");
        _trailStyle = AddEnum(panel, "Trail appearance", value.TrailStyle);
        _trailRadius = AddSlider(panel, "Trail dot size", 1, 10, value.TrailDotRadius);
        _trailLifetime = AddSlider(panel, "Trail duration in minutes (0 = entire session)", 0, 180, value.TrailLifetimeMinutes);
        _fadeTrail = new CheckBox { Content = "Fade trail gradually with age", IsChecked = value.FadeTrailWithAge, Margin = new Thickness(0, 8, 0, 4) };
        panel.Children.Add(_fadeTrail);
        _persistent = new CheckBox { Content = "Persist trail history locally", IsChecked = value.PersistentTrails, Margin = new Thickness(0, 10, 0, 10) };
        panel.Children.Add(_persistent);
        var trailPanel = panel;
        panel = Tab("Window");
        _hideUnfocused = new CheckBox { Content = "Hide while DS3 is unfocused", IsChecked = value.HideWhenGameUnfocused, Margin = new Thickness(0, 2, 0, 10) };
        panel.Children.Add(_hideUnfocused);
        _borderless = new CheckBox { Content = "Managed borderless game window", IsChecked = value.ManagedBorderless, Margin = new Thickness(0, 2, 0, 10) };
        panel.Children.Add(_borderless);
        panel = Tab("Colours");
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
        trailPanel.Children.Add(exports);
        void Refresh()
        {
            var settings = ReadControls();
            preview.Width = preview.Height = settings.Size;
            preview.Opacity = settings.Opacity;
            preview.Update(player, MapTransform.HeadingToScreenRadians(player?.Heading ?? 0),
                region, floor, trail, settings, flags, bonfireFlags);
        }
        layout.AddHandler(Slider.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => Refresh()));
        layout.AddHandler(ComboBox.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => Refresh()));
        layout.AddHandler(CheckBox.CheckedEvent, new RoutedEventHandler((_, _) => Refresh()));
        layout.AddHandler(CheckBox.UncheckedEvent, new RoutedEventHandler((_, _) => Refresh()));
        layout.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => Refresh()));
        Content = layout;
        Refresh();
    }

    public AppSettings Value { get; private set; }

    private void Save()
    {
        Value = ReadControls();
        DialogResult = true;
    }

    private AppSettings ReadControls() => Value with
        {
            ShowCollisionGround = _ground.IsChecked == true,
            ShowFloorLabel = _floorLabel.IsChecked == true,
            TrailStyle = (TrailStyle)_trailStyle.SelectedItem,
            EdgeMargin = _edgeMargin.Value,
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
            MarkerScale = _markerScale.Value,
            GroundStyle = (GroundStyle)_groundStyle.SelectedItem,
            TextureToning = _textureToning.Value,
            ShowUnlitBonfires = _unlitBonfires.IsChecked == true,
            ShowNearestBonfirePointer = _bonfirePointer.IsChecked == true,
            FogOfWar = _fogOfWar.IsChecked == true,
            FogOfWarStrength = _fogStrength.Value
        };

    private static Slider AddSlider(Panel panel, string label, double minimum, double maximum, double value)
    {
        var caption = new TextBlock { Text = $"{label}: {value:0.##}", Margin = new Thickness(0, 7, 0, 2) };
        panel.Children.Add(caption);
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = value, TickFrequency = (maximum - minimum) / 20 };
        slider.ValueChanged += (_, _) => caption.Text = $"{label}: {slider.Value:0.##}";
        AutomationProperties.SetName(slider, label);
        panel.Children.Add(slider);
        return slider;
    }

    private static ComboBox AddEnum<T>(Panel panel, string label, T value) where T : struct, Enum
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 2) });
        var combo = new ComboBox { ItemsSource = Enum.GetValues<T>(), SelectedItem = value };
        AutomationProperties.SetName(combo, label);
        panel.Children.Add(combo);
        return combo;
    }

    private static TextBox AddText(Panel panel, string label, string value)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 2) });
        var text = new TextBox { Text = value };
        AutomationProperties.SetName(text, label);
        panel.Children.Add(text);
        return text;
    }

    private void AddControlStyles()
    {
        Resources[typeof(CheckBox)] = StyleFor<CheckBox>((Control.ForegroundProperty, Foreground),
            (MarginProperty, new Thickness(0, 6, 0, 6)));
        Resources[typeof(Button)] = StyleFor<Button>(
            (Control.ForegroundProperty, Brush("#201B15")), (Control.BackgroundProperty, Brush("#DDC99B")),
            (Control.BorderBrushProperty, Brush("#806A43")), (Control.BorderThicknessProperty, new Thickness(1)),
            (Control.PaddingProperty, new Thickness(10, 5, 10, 5)));
        Resources[typeof(TextBox)] = StyleFor<TextBox>(
            (Control.ForegroundProperty, Brush("#E9DFC8")), (Control.BackgroundProperty, Brush("#100F0D")),
            (Control.BorderBrushProperty, Brush("#5E5039")), (Control.BorderThicknessProperty, new Thickness(1)),
            (Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
        Resources[typeof(ComboBox)] = StyleFor<ComboBox>(
            (Control.ForegroundProperty, Brush("#201B15")), (Control.BackgroundProperty, Brush("#E8DDC7")),
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
