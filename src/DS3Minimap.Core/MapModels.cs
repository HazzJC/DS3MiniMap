using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DS3Minimap.Core;

public sealed record MapPoint(float X, float Z);

public sealed record MapPolygon(
    string Id,
    float MinimumY,
    float MaximumY,
    IReadOnlyList<MapPoint> Points);

public sealed record MapLayer(
    string Id,
    string DisplayName,
    float MinimumY,
    float MaximumY,
    IReadOnlyList<MapPolygon> Polygons);

public enum MapMarkerKind
{
    Bonfire,
    Item
}

/// <summary>
/// A point of interest extracted from the installed game's MSB and regulation.
/// EventFlagId is the read-only game flag used to determine activation/pickup
/// state; -1 means that no reliable live state exists for this marker.
/// </summary>
public sealed record MapMarker(
    string Id,
    MapMarkerKind Kind,
    float X,
    float Y,
    float Z,
    string LayerId,
    string Label,
    int EventFlagId = -1,
    int ItemLotId = -1);

public sealed record MapRegion(
    string MapId,
    string DisplayName,
    IReadOnlyList<MapLayer> Layers,
    IReadOnlyList<MapPolygon>? ContextPolygons = null,
    IReadOnlyList<MapMarker>? Markers = null);

public sealed record MapPackage(
    int SchemaVersion,
    string GameVersion,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<MapRegion> Regions)
{
    public const int CurrentSchemaVersion = 4;
}

public static class MapPackageSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static async Task WriteAtomicAsync(MapPackage package, string destination, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
        var temporary = destination + ".tmp";
        await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
        await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize, false))
        {
            await JsonSerializer.SerializeAsync(gzip, package, Options, cancellationToken);
        }
        File.Move(temporary, destination, true);
    }

    public static async Task<MapPackage> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var package = await JsonSerializer.DeserializeAsync<MapPackage>(gzip, Options, cancellationToken);
        return package ?? throw new InvalidDataException("The map package is empty.");
    }
}

public static class LayerSelector
{
    public sealed record Match(MapLayer Layer, float HorizontalDistance, float VerticalDistance, float Score);

    /// <summary>
    /// Selects a floor using the player's complete world position. DS3 maps contain
    /// floors whose global Y ranges overlap, so elevation alone cannot reliably tell
    /// which piece of navigation geometry is beneath the player.
    /// </summary>
    public static MapLayer? Select(MapRegion? region, Vector3 playerPosition, string? overrideLayerId = null)
    {
        if (region is null) return null;
        if (!string.IsNullOrWhiteSpace(overrideLayerId))
        {
            var requested = region.Layers.FirstOrDefault(layer => layer.Id == overrideLayerId);
            if (requested is not null) return requested;
        }

        return Rank(region, playerPosition).FirstOrDefault()?.Layer;
    }

    public static MapLayer? SelectStable(MapRegion? region, Vector3 playerPosition, MapLayer? current, float hysteresis = 2.5f)
    {
        if (region is null) return null;
        var ranked = Rank(region, playerPosition);
        var best = ranked.FirstOrDefault();
        if (best is null || current is null) return best?.Layer;
        var currentMatch = ranked.FirstOrDefault(match => match.Layer.Id == current.Id);
        return currentMatch is not null && currentMatch.Score <= best.Score + Math.Max(hysteresis, 0)
            ? current
            : best.Layer;
    }

    public static IReadOnlyList<Match> Rank(MapRegion region, Vector3 playerPosition)
    {
        var point = new MapPoint(playerPosition.X, playerPosition.Z);
        var nearbyElevations = region.Layers
            .Where(layer => DistanceToRange(playerPosition.Y, layer.MinimumY, layer.MaximumY) <= 16f)
            .ToArray();
        var candidates = nearbyElevations.Length > 0 ? nearbyElevations : region.Layers;
        return candidates
            .Select(layer => Measure(layer, point, playerPosition.Y))
            .OrderBy(match => match.Score)
            .ThenBy(match => match.HorizontalDistance)
            .ThenBy(match => match.VerticalDistance)
            .ToArray();
    }

    public static MapLayer? Select(MapRegion? region, float playerY, string? overrideLayerId = null)
    {
        if (region is null) return null;
        if (!string.IsNullOrWhiteSpace(overrideLayerId))
        {
            var requested = region.Layers.FirstOrDefault(layer => layer.Id == overrideLayerId);
            if (requested is not null) return requested;
        }

        return region.Layers
            .Where(layer => playerY >= layer.MinimumY - 2f && playerY <= layer.MaximumY + 2f)
            .OrderBy(layer => Math.Abs(((layer.MinimumY + layer.MaximumY) / 2f) - playerY))
            .FirstOrDefault()
            ?? region.Layers.OrderBy(layer => DistanceToRange(playerY, layer.MinimumY, layer.MaximumY)).FirstOrDefault();
    }

    private static float DistanceToRange(float value, float minimum, float maximum) =>
        value < minimum ? minimum - value : value > maximum ? value - maximum : 0;

    private static Match Measure(MapLayer layer, MapPoint point, float playerY)
    {
        var bestHorizontal = float.PositiveInfinity;
        var bestVertical = float.PositiveInfinity;
        var bestScore = float.PositiveInfinity;

        foreach (var polygon in layer.Polygons)
        {
            if (polygon.Points.Count < 3) continue;
            var horizontal = Contains(point, polygon.Points) ? 0f : DistanceToEdges(point, polygon.Points);
            var vertical = DistanceToRange(playerY, polygon.MinimumY, polygon.MaximumY);

            // A unit of elevation error is more suspicious than a unit of lateral
            // navmesh clearance. This still permits small gaps around props, map-piece
            // seams and deliberate holes in the navigation mesh.
            var score = horizontal + vertical * 2f;
            if (score >= bestScore) continue;
            bestScore = score;
            bestHorizontal = horizontal;
            bestVertical = vertical;
        }

        if (!float.IsFinite(bestScore))
        {
            bestVertical = DistanceToRange(playerY, layer.MinimumY, layer.MaximumY);
            bestHorizontal = float.PositiveInfinity;
            bestScore = float.PositiveInfinity;
        }
        return new Match(layer, bestHorizontal, bestVertical, bestScore);
    }

    private static bool Contains(MapPoint point, IReadOnlyList<MapPoint> polygon)
    {
        var inside = false;
        for (var index = 0; index < polygon.Count; index++)
        {
            var a = polygon[index];
            var b = polygon[(index + polygon.Count - 1) % polygon.Count];
            if ((a.Z > point.Z) != (b.Z > point.Z)
                && point.X < (b.X - a.X) * (point.Z - a.Z) / (b.Z - a.Z) + a.X) inside = !inside;
        }
        return inside;
    }

    private static float DistanceToEdges(MapPoint point, IReadOnlyList<MapPoint> polygon)
    {
        var nearest = float.PositiveInfinity;
        for (var index = 0; index < polygon.Count; index++)
        {
            var a = polygon[index];
            var b = polygon[(index + 1) % polygon.Count];
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            var lengthSquared = dx * dx + dz * dz;
            float distance;
            if (lengthSquared <= float.Epsilon)
            {
                distance = MathF.Sqrt((point.X - a.X) * (point.X - a.X) + (point.Z - a.Z) * (point.Z - a.Z));
            }
            else
            {
                var t = Math.Clamp(((point.X - a.X) * dx + (point.Z - a.Z) * dz) / lengthSquared, 0f, 1f);
                var x = a.X + t * dx;
                var z = a.Z + t * dz;
                distance = MathF.Sqrt((point.X - x) * (point.X - x) + (point.Z - z) * (point.Z - z));
            }
            nearest = Math.Min(nearest, distance);
        }
        return nearest;
    }
}

public static class MapTransform
{
    public static Vector2 WorldToMap(Vector3 world, Vector3 origin, float scale) =>
        new((world.X - origin.X) * scale, (world.Z - origin.Z) * scale);

    // DS3 yaw zero faces +Z. Screen-space +Z is down, while the marker artwork
    // initially points up, so a half turn and a sign conversion are required.
    public static float HeadingToScreenRadians(float ds3Heading) => MathF.PI - ds3Heading;

    public static float PlayerUpMapRotation(float ds3Heading) => -HeadingToScreenRadians(ds3Heading);
}

/// <summary>
/// Resolves the on-screen direction from observed travel. This avoids relying
/// solely on DS3's model-yaw convention and makes the marker agree with its
/// breadcrumb while moving. At rest it falls back to the character yaw.
/// </summary>
public sealed class HeadingTracker
{
    private Vector3? _anchor;
    private string? _mapId;
    private float _screenHeading;

    public float Update(PlayerTelemetry player)
    {
        if (_mapId != player.MapId || _anchor is null)
        {
            _mapId = player.MapId;
            _anchor = player.Position;
            _screenHeading = MapTransform.HeadingToScreenRadians(player.Heading);
            return _screenHeading;
        }

        var dx = player.Position.X - _anchor.Value.X;
        var dz = player.Position.Z - _anchor.Value.Z;
        var distance = MathF.Sqrt(dx * dx + dz * dz);
        if (distance is >= 0.25f and < 25f)
        {
            _screenHeading = MathF.Atan2(dx, -dz);
            _anchor = player.Position;
        }
        else if (distance >= 25f)
        {
            _screenHeading = MapTransform.HeadingToScreenRadians(player.Heading);
            _anchor = player.Position;
        }
        return _screenHeading;
    }
}
