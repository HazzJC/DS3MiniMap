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
    private const int HotkeyAutoFloor = 6;
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
    private PlayerTelemetry? _lastPlayer;
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
    private EventFlagSnapshot? _bonfireFlags;
    private DateTimeOffset _nextFlagReadAt;
    private readonly ExploredArea _explored = new();
    private string? _exploredCharacter;

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
        _maps = await MapPackagePreparation.EnsureCurrentAsync(this);
        if (_maps is null) { Close(); return; }
        if (_settings.PersistentTrails) { await _trailStore.InitializeAsync(); _trailStoreReady = true; }
        Width = Height = _settings.Size;
        Opacity = _settings.Opacity;
        PositionOverlay();
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
            var player = _lastPlayer = snapshot.Player;
            MapRegion? region = player is null ? null : _maps?.Regions.FirstOrDefault(item => item.MapId == player.MapId);
            if (player?.MapId != _lastMapId)
            {
                _trail.BreakSegment();
                _layerOffset = 0;
                _lastMapId = player?.MapId;
                _automaticLayer = null;
                _layerSelectionPosition = null;
                _itemFlags = null;
                _bonfireFlags = null;
                _nextFlagReadAt = DateTimeOffset.MinValue;
            }
            var layer = SelectLayerWithOffset(region, player?.Position ?? default);
            if (player is not null)
            {
                var point = _trail.Observe(player, _automaticLayer?.Id ?? "default");
                if (_settings.PersistentTrails && !_trailStoreReady) { await _trailStore.InitializeAsync(); _trailStoreReady = true; }
                if (point is not null && _settings.PersistentTrails) await _trailStore.AppendAsync(_sessionId, player.CharacterName, point);
                await RevealExploredAsync(player);
            }
            var screenHeading = player is null ? 0 : _heading.Update(player);
            if (player is not null && region is not null && DateTimeOffset.UtcNow >= _nextFlagReadAt)
            {
                // Both reads are read-only and polled twice a second. They are kept
                // separate so one unresolvable flag cannot hide the other markers.
                if (_settings.ShowUncollectedItems)
                    _itemFlags = await _telemetry.ReadEventFlagsAsync(FlagsFor(region, MapMarkerKind.Item));
                if (_settings.ShowBonfires)
                    _bonfireFlags = await _telemetry.ReadEventFlagsAsync(FlagsFor(region, MapMarkerKind.Bonfire));
                _nextFlagReadAt = DateTimeOffset.UtcNow.AddMilliseconds(500);
            }
            if (!_settings.ShowUncollectedItems) _itemFlags = null;
            if (!_settings.ShowBonfires) _bonfireFlags = null;
            StatusText.Text = BuildStatus(snapshot, region, layer);
            LabelPanel.Visibility = _settings.ShowFloorLabel || region is null ? Visibility.Visible : Visibility.Collapsed;
            _view.Update(player, screenHeading, region, layer, _trail.Points, _settings, _itemFlags, _bonfireFlags, _explored);
            var gameWindow = GameWindowTracker.TryGet();
            if (gameWindow is not null)
            {
                if (_settings.ManagedBorderless) GameWindowTracker.ApplyBorderless(gameWindow.Handle);
                if (_source is not null) GameWindowTracker.PlaceOverlay(_source.Handle, gameWindow, _settings);
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

    private static int[] FlagsFor(MapRegion region, MapMarkerKind kind) =>
        (region.Markers ?? []).Where(marker => marker.Kind == kind && marker.EventFlagId > 0)
            .Select(marker => marker.EventFlagId).Distinct().ToArray();

    /// <summary>
    /// Marks the ground around the player as explored. Each character has its
    /// own explored area, which is saved with trail history when that is on.
    /// </summary>
    private async Task RevealExploredAsync(PlayerTelemetry player)
    {
        if (_exploredCharacter != player.CharacterName)
        {
            _explored.Clear();
            _exploredCharacter = player.CharacterName;
            if (_settings.PersistentTrails && _trailStoreReady)
                await _trailStore.LoadExploredAsync(player.CharacterName, _explored);
        }
        var added = _explored.Reveal(player.MapId, _automaticLayer?.Id ?? "default", player.Position);
        if (added.Count > 0 && _settings.PersistentTrails && _trailStoreReady)
            await _trailStore.AppendExploredAsync(player.CharacterName, player.MapId, _automaticLayer?.Id ?? "default", added);
    }

    private async Task ResetExploredAsync()
    {
        _explored.Clear();
        if (_exploredCharacter is not null && _trailStoreReady)
            await _trailStore.ClearExploredAsync(_exploredCharacter);
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
        NativeHotkeys.RegisterHotKey(helper.Handle, HotkeyAutoFloor, ModControl | ModShift, 0x24);
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
                if (_settingsOpen) break;
                _settingsOpen = true;
                _timer.Stop();
                Topmost = false;
                var editor = new SettingsWindow(_settings, _trail.Points, _diagnostic, _maps, _lastPlayer, _itemFlags,
                    _bonfireFlags, ResetExploredAsync)
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
            case HotkeyAutoFloor: _layerOffset = 0; break;
        }
        return 0;
    }

    private void PositionOverlay()
    {
        double margin = _settings.EdgeMargin;
        var area = SystemParameters.WorkArea;
        Left = _settings.Anchor is OverlayAnchor.TopLeft or OverlayAnchor.BottomLeft ? area.Left + margin : area.Right - Width - margin;
        Top = _settings.Anchor is OverlayAnchor.TopLeft or OverlayAnchor.TopRight ? area.Top + margin : area.Bottom - Height - margin;
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
        _layerOffset = Math.Clamp(_layerOffset, -index, region.Layers.Count - 1 - index);
        return region.Layers[index + _layerOffset];
    }

    private string BuildStatus(TelemetrySnapshot snapshot, MapRegion? region, MapLayer? layer)
    {
        if (_maps is null) return "Restart DS3 Minimap to prepare maps";
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
        var floor = (_layerOffset == 0 ? "" : "Manual · ") + (layer?.DisplayName ?? "No floor");
        return $"{floor}{markerStatus}  •  {region.DisplayName}";
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
        NativeHotkeys.UnregisterHotKey(handle, HotkeyAutoFloor);
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
