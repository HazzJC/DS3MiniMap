using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DS3Minimap.Core;
using DS3Minimap.Memory;
using DS3Minimap.Rendering;

namespace DS3Minimap.App;

public partial class OverlayWindow : Window
{
    private const int HotkeySettings = 1;
    private const int HotkeyToggle = 2;
    private const int HotkeyClear = 3;
    private const int HotkeyFloorUp = 4;
    private const int HotkeyFloorDown = 5;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private readonly MinimapView _view = new();
    private readonly Ds3TelemetrySource _telemetry = new();
    private readonly TrailRecorder _trail = new();
    private readonly HeadingTracker _heading = new();
    private readonly SettingsStore _settingsStore = new(AppPaths.Settings);
    private readonly TrailStore _trailStore = new(AppPaths.TrailDatabase);
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private MapPackage? _maps;
    private AppSettings _settings = new();
    private bool _manuallyHidden;
    private bool _reading;
    private bool _settingsOpen;
    private HwndSource? _source;
    private int _layerOffset;
    private string? _lastMapId;
    private MapLayer? _automaticLayer;
    private System.Numerics.Vector3? _layerSelectionPosition;
    private string _diagnostic = "Starting";
    private bool _trailStoreReady;
    private EventFlagSnapshot? _itemFlags;
    private DateTimeOffset _nextFlagReadAt;

    public OverlayWindow()
    {
        InitializeComponent();
        Root.Children.Insert(0, _view);
        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialized;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = await _settingsStore.LoadAsync();
        await FirstRunSetup.RunAsync(_settings);
        await MapPackagePreparation.EnsureCurrentAsync(this);
        if (_settings.PersistentTrails) { await _trailStore.InitializeAsync(); _trailStoreReady = true; }
        Width = Height = _settings.Size;
        Opacity = _settings.Opacity;
        PositionOverlay();
        if (File.Exists(AppPaths.MapPackage))
        {
            try { _maps = await MapPackageSerializer.ReadAsync(AppPaths.MapPackage); }
            catch (InvalidDataException) { _maps = null; }
        }
        _timer.Tick += Tick;
        _timer.Start();
    }

    private async void Tick(object? sender, EventArgs e)
    {
        if (_reading) return;
        _reading = true;
        try
        {
            var snapshot = await _telemetry.ReadAsync();
            _diagnostic = $"{snapshot.State}: {snapshot.Diagnostic}";
            Title = $"DS3 Minimap — {snapshot.State}: {snapshot.Diagnostic}";
            var player = snapshot.Player;
            MapRegion? region = player is null ? null : _maps?.Regions.FirstOrDefault(item => item.MapId == player.MapId);
            if (player?.MapId != _lastMapId)
            {
                _layerOffset = 0;
                _lastMapId = player?.MapId;
                _automaticLayer = null;
                _layerSelectionPosition = null;
                _itemFlags = null;
                _nextFlagReadAt = DateTimeOffset.MinValue;
            }
            var layer = SelectLayerWithOffset(region, player?.Position ?? default);
            if (player is not null)
            {
                var point = _trail.Observe(player, layer?.Id ?? "default");
                if (_settings.PersistentTrails && !_trailStoreReady) { await _trailStore.InitializeAsync(); _trailStoreReady = true; }
                if (point is not null && _settings.PersistentTrails) await _trailStore.AppendAsync(_sessionId, player.CharacterName, point);
            }
            var screenHeading = player is null ? 0 : _heading.Update(player);
            if (_settings.ShowUncollectedItems && player is not null && region is not null
                && DateTimeOffset.UtcNow >= _nextFlagReadAt)
            {
                var pickupFlags = (region.Markers ?? []).Where(marker => marker.Kind == MapMarkerKind.Item && marker.EventFlagId > 0)
                    .Select(marker => marker.EventFlagId).Distinct().ToArray();
                _itemFlags = await _telemetry.ReadEventFlagsAsync(pickupFlags);
                _nextFlagReadAt = DateTimeOffset.UtcNow.AddMilliseconds(500);
            }
            else if (!_settings.ShowUncollectedItems) _itemFlags = null;
            StatusText.Text = BuildStatus(snapshot, region, layer);
            _view.Update(player, screenHeading, region, layer, _trail.Points, _settings, _itemFlags);
            var gameWindow = GameWindowTracker.TryGet();
            if (gameWindow is not null)
            {
                if (_settings.ManagedBorderless) GameWindowTracker.ApplyBorderless(gameWindow.Handle);
                PositionOverlay(gameWindow);
            }
            var autoHidden = !_settingsOpen && (gameWindow is null || gameWindow.Minimized || (_settings.HideWhenGameUnfocused && !gameWindow.Focused));
            Visibility = _manuallyHidden || autoHidden ? Visibility.Hidden : Visibility.Visible;
        }
        catch (Exception exception)
        {
            // A game process can close between any two telemetry/window calls.
            // Keep the dispatcher timer alive and surface the issue non-modally.
            _diagnostic = $"Paused: {exception.Message}";
            StatusText.Text = "DS3 closed - waiting for the game";
            if (!_settingsOpen) Visibility = Visibility.Hidden;
        }
        finally { _reading = false; }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WindowProcedure);
        NativeHotkeys.RegisterHotKey(helper.Handle, HotkeySettings, ModControl | ModShift, 0x4D);
        NativeHotkeys.RegisterHotKey(helper.Handle, HotkeyToggle, ModControl | ModShift, 0x48);
        NativeHotkeys.RegisterHotKey(helper.Handle, HotkeyClear, ModControl | ModShift, 0x43);
        NativeHotkeys.RegisterHotKey(helper.Handle, HotkeyFloorUp, ModControl | ModShift, 0x21);
        NativeHotkeys.RegisterHotKey(helper.Handle, HotkeyFloorDown, ModControl | ModShift, 0x22);
        var style = NativeHotkeys.GetWindowLongPtr(helper.Handle, -20).ToInt64();
        NativeHotkeys.SetWindowLongPtr(helper.Handle, -20, new nint(style | 0x20 | 0x80000));
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0312) return 0;
        handled = true;
        switch (wParam.ToInt32())
        {
            case HotkeySettings:
                _settingsOpen = true;
                _timer.Stop();
                Topmost = false;
                var editor = new SettingsWindow(_settings, _trail.Points, _diagnostic)
                {
                    Topmost = true,
                    ShowActivated = true,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                editor.Loaded += (_, _) => { editor.Activate(); editor.Focus(); };
                var saved = editor.ShowDialog() == true;
                Topmost = true;
                _settingsOpen = false;
                _timer.Start();
                if (saved)
                {
                    _settings = editor.Value;
                    Width = Height = _settings.Size;
                    Opacity = _settings.Opacity;
                    PositionOverlay();
                    _nextFlagReadAt = DateTimeOffset.MinValue;
                    _ = _settingsStore.SaveAsync(_settings);
                }
                break;
            case HotkeyToggle: _manuallyHidden = !_manuallyHidden; break;
            case HotkeyClear: _trail.Clear(); break;
            case HotkeyFloorUp: _layerOffset++; break;
            case HotkeyFloorDown: _layerOffset--; break;
        }
        return 0;
    }

    private void PositionOverlay()
    {
        const double margin = 24;
        var area = SystemParameters.WorkArea;
        Left = _settings.Anchor is OverlayAnchor.TopLeft or OverlayAnchor.BottomLeft ? area.Left + margin : area.Right - Width - margin;
        Top = _settings.Anchor is OverlayAnchor.TopLeft or OverlayAnchor.TopRight ? area.Top + margin : area.Bottom - Height - margin;
    }

    private void PositionOverlay(GameWindowState game)
    {
        const double margin = 24;
        Left = _settings.Anchor is OverlayAnchor.TopLeft or OverlayAnchor.BottomLeft ? game.X + margin : game.X + game.Width - Width - margin;
        Top = _settings.Anchor is OverlayAnchor.TopLeft or OverlayAnchor.TopRight ? game.Y + margin : game.Y + game.Height - Height - margin;
    }

    private MapLayer? SelectLayerWithOffset(MapRegion? region, System.Numerics.Vector3 position)
    {
        if (region is null) return null;
        var moved = _layerSelectionPosition is null || System.Numerics.Vector3.DistanceSquared(_layerSelectionPosition.Value, position) >= 1f;
        if (_automaticLayer is null || moved)
        {
            _automaticLayer = LayerSelector.SelectStable(region, position, _automaticLayer);
            _layerSelectionPosition = position;
        }
        var automatic = _automaticLayer;
        if (region is null || automatic is null || _layerOffset == 0) return automatic;
        var index = -1;
        for (var candidate = 0; candidate < region.Layers.Count; candidate++)
            if (ReferenceEquals(region.Layers[candidate], automatic)) { index = candidate; break; }
        if (index < 0) return automatic;
        return region.Layers[Math.Clamp(index + _layerOffset, 0, region.Layers.Count - 1)];
    }

    private string BuildStatus(TelemetrySnapshot snapshot, MapRegion? region, MapLayer? layer)
    {
        if (_maps is null) return "Map package missing - run scripts\\generate-maps.ps1";
        if (snapshot.Player is null) return snapshot.Diagnostic;
        if (region is null) return $"No baked region for telemetry ID {snapshot.Player.MapId}";
        var markerStatus = string.Empty;
        if (_settings.ShowUncollectedItems)
        {
            var itemMarkers = (region.Markers ?? []).Where(marker => marker.Kind == MapMarkerKind.Item
                && (layer is null || marker.LayerId == layer.Id)).ToArray();
            markerStatus = _itemFlags?.Available == true
                ? $"  •  {itemMarkers.Count(marker => !_itemFlags.SetFlags.Contains(marker.EventFlagId))} items"
                : "  •  item tracking unavailable";
        }
        return $"{region.DisplayName}  •  {layer?.DisplayName ?? "No floor"}{markerStatus}";
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        _timer.Stop();
        await _telemetry.DisposeAsync();
        var handle = new WindowInteropHelper(this).Handle;
        NativeHotkeys.UnregisterHotKey(handle, HotkeySettings);
        NativeHotkeys.UnregisterHotKey(handle, HotkeyToggle);
        NativeHotkeys.UnregisterHotKey(handle, HotkeyClear);
        NativeHotkeys.UnregisterHotKey(handle, HotkeyFloorUp);
        NativeHotkeys.UnregisterHotKey(handle, HotkeyFloorDown);
        _source?.RemoveHook(WindowProcedure);
    }
}

internal static partial class NativeHotkeys
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial nint GetWindowLongPtr(nint hWnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static partial nint SetWindowLongPtr(nint hWnd, int index, nint newStyle);
}
