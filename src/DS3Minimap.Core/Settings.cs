using System.Text.Json;

namespace DS3Minimap.Core;

public enum MapOrientation { PlayerUp, NorthUp }
// New values go at the end: settings files store the number.
public enum TrailStyle { Dots, Connected, Hidden, Soul, Inked }
public enum MapMask { Circle, RoundedSquare, Square }
public enum OverlayAnchor { TopLeft, TopRight, BottomLeft, BottomRight }
public enum GroundStyle { RealTextures, Parchment }

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 7;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public double Size { get; init; } = 320;
    public double Zoom { get; init; } = 2.2;
    public double Opacity { get; init; } = 0.90;
    public OverlayAnchor Anchor { get; init; } = OverlayAnchor.TopRight;
    public MapOrientation Orientation { get; init; } = MapOrientation.PlayerUp;
    public MapMask Mask { get; init; } = MapMask.Circle;
    public bool ShowCollisionGround { get; init; } = true;
    public TrailStyle TrailStyle { get; init; } = TrailStyle.Soul;
    public double EdgeMargin { get; init; } = 24;
    public bool ShowFloorLabel { get; init; } = true;
    public bool PersistentTrails { get; init; }
    public bool HideWhenGameUnfocused { get; init; } = true;
    public bool ManagedBorderless { get; init; } = true;
    public string BackgroundColor { get; init; } = "#E0121518";
    public string GeometryColor { get; init; } = "#FFE6D7B7";
    public string PlayerColor { get; init; } = "#FFFFC857";
    public string TrailColor { get; init; } = "#FF7FC8F8";
    public float TrailDotRadius { get; init; } = 2.5f;
    public double TrailLifetimeMinutes { get; init; }
    public bool FadeTrailWithAge { get; init; } = true;
    public double MapTextureStrength { get; init; } = 0.28;
    public double LayerTransitionMilliseconds { get; init; } = 480;
    public string BoundaryColor { get; init; } = "#E60E0D0B";
    public string FrameColor { get; init; } = "#FF9A7C48";
    public string PlayerOutlineColor { get; init; } = "#F20A0907";
    public bool ShowBonfires { get; init; } = true;
    public bool ShowUncollectedItems { get; init; }
    public string BonfireColor { get; init; } = "#FFFF9B45";
    public string ItemColor { get; init; } = "#FFD9F2FF";
    public double MarkerScale { get; init; } = 1;
    // Real DS3 ground textures, or the procedural parchment fill.
    public GroundStyle GroundStyle { get; init; } = GroundStyle.RealTextures;
    // 0 = textures exactly as in the game; 1 = fully tinted towards the map colour.
    public double TextureToning { get; init; } = 0.15;
    public bool ShowUnlitBonfires { get; init; } = true;
    public bool ShowNearestBonfirePointer { get; init; } = true;
    public bool FogOfWar { get; init; } = true;
    public double FogOfWarStrength { get; init; } = 0.55;
}

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            await using var stream = File.OpenRead(path);
            return Validate(await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken) ?? new AppSettings());
        }
        catch (JsonException)
        {
            File.Move(path, path + ".invalid-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true);
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temporary = path + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, Validate(settings), Options, cancellationToken);
        File.Move(temporary, path, true);
    }

    private static AppSettings Validate(AppSettings value)
    {
        var migrateDefaults = value.SchemaVersion < 2;
        return value with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            // Version 7 replaced the plain-dot default with the glowing soul trail.
            TrailStyle = value.SchemaVersion < 7 && value.TrailStyle == TrailStyle.Dots ? TrailStyle.Soul : value.TrailStyle,
            TextureToning = Math.Clamp(value.TextureToning, 0, 1),
            FogOfWarStrength = Math.Clamp(value.FogOfWarStrength, 0, 0.9),
            EdgeMargin = Math.Clamp(value.EdgeMargin, 0, 120),
            Size = Math.Clamp(value.Size, 160, 800),
            Zoom = Math.Clamp(value.Zoom, 0.25, 10),
            Opacity = Math.Clamp(value.Opacity, 0.15, 1),
            TrailDotRadius = Math.Clamp(value.TrailDotRadius, 1, 10),
            TrailLifetimeMinutes = Math.Clamp(value.TrailLifetimeMinutes, 0, 180),
            MapTextureStrength = Math.Clamp(value.MapTextureStrength, 0, 1),
            LayerTransitionMilliseconds = Math.Clamp(value.LayerTransitionMilliseconds, 0, 1500),
            MarkerScale = Math.Clamp(value.MarkerScale, 0.6, 2.5),
            BackgroundColor = migrateDefaults && value.BackgroundColor == "#B0121212" ? "#E0121518" : value.BackgroundColor,
            GeometryColor = migrateDefaults && value.GeometryColor == "#FFD9D1C1" ? "#FFE6D7B7" : value.GeometryColor
        };
    }
}
