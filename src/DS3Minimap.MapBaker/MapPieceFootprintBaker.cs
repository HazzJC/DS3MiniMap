using System.Numerics;
using DS3Minimap.Core;
using SoulsFormats;

namespace DS3Minimap.MapBaker;

/// <summary>
/// Builds a lightweight top-down silhouette from DS3's placed visual map pieces.
/// The AI navigation mesh remains the authoritative walkable/floor layer, while
/// these footprints provide caves, water, architecture and other player space
/// that is intentionally absent from enemy navigation data.
/// </summary>
internal sealed class MapPieceFootprintBaker(Ds3ArchiveReader archives)
{
    internal IReadOnlyList<MapPolygon> Bake(string mapId)
    {
        var resource = mapId + "_00_00";
        var msb = MSB3.Read(archives.Read($"/map/mapstudio/{resource}.msb.dcx"));
        var modelFootprints = new Dictionary<string, IReadOnlyList<LocalFootprint>>(StringComparer.Ordinal);
        var polygons = new List<MapPolygon>();
        var uniqueParts = new HashSet<PartKey>();

        foreach (var part in msb.Parts.MapPieces)
        {
            var key = new PartKey(part.ModelName, part.Position, part.Rotation, part.Scale);
            if (!uniqueParts.Add(key)) continue;
            if (!modelFootprints.TryGetValue(part.ModelName, out var footprints))
            {
                footprints = ReadModelFootprints(resource, part.ModelName);
                modelFootprints.Add(part.ModelName, footprints);
            }

            var transform = CreateTransform(part.Scale, part.Rotation, part.Position);
            foreach (var footprint in footprints)
            {
                var transformed = footprint.Points.Select(point => Vector3.Transform(point, transform)).ToArray();
                if (transformed.Length < 3 || transformed.Any(point => !IsFinite(point))) continue;
                var area = Math.Abs(SignedArea(transformed));
                if (area < 0.15f) continue;
                polygons.Add(new MapPolygon(
                    $"context-{polygons.Count}",
                    transformed.Min(point => point.Y),
                    transformed.Max(point => point.Y),
                    transformed.Select(point => new MapPoint(point.X, point.Z)).ToArray()));
            }
        }

        Console.WriteLine($"  visual context: {polygons.Count:N0} placed map-piece footprints");
        return polygons;
    }

    private IReadOnlyList<LocalFootprint> ReadModelFootprints(string resource, string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || modelName.Length < 2) return [];
        try
        {
            var path = $"/map/{resource}/{resource}_{modelName[1..]}.mapbnd.dcx";
            var binder = BND4.Read(DCX.Decompress(archives.Read(path)));
            var file = binder.Files.FirstOrDefault(item => item.Name?.EndsWith(".flver", StringComparison.OrdinalIgnoreCase) == true);
            if (file is null) return [];
            var flver = FLVER2.Read(file.Bytes);
            var result = new List<LocalFootprint>();
            foreach (var mesh in flver.Meshes)
            {
                var hull = ConvexHull(mesh.Vertices.Select(vertex => vertex.Position));
                if (hull.Count >= 3) result.Add(new LocalFootprint(hull));
            }
            return result;
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException or NotSupportedException or KeyNotFoundException)
        {
            return [];
        }
    }

    private static Matrix4x4 CreateTransform(Vector3 scale, Vector3 rotationDegrees, Vector3 position)
    {
        const float degreesToRadians = MathF.PI / 180f;
        return Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateRotationX(rotationDegrees.X * degreesToRadians)
            * Matrix4x4.CreateRotationZ(rotationDegrees.Z * degreesToRadians)
            * Matrix4x4.CreateRotationY(rotationDegrees.Y * degreesToRadians)
            * Matrix4x4.CreateTranslation(position);
    }

    private static IReadOnlyList<Vector3> ConvexHull(IEnumerable<Vector3> source)
    {
        var points = source
            .Where(IsFinite)
            .GroupBy(point => (MathF.Round(point.X, 3), MathF.Round(point.Z, 3)))
            .Select(group => group.First())
            .OrderBy(point => point.X)
            .ThenBy(point => point.Z)
            .ToArray();
        if (points.Length < 3) return [];

        var lower = new List<Vector3>();
        foreach (var point in points)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], point) <= 0) lower.RemoveAt(lower.Count - 1);
            lower.Add(point);
        }
        var upper = new List<Vector3>();
        for (var index = points.Length - 1; index >= 0; index--)
        {
            var point = points[index];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], point) <= 0) upper.RemoveAt(upper.Count - 1);
            upper.Add(point);
        }
        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static float Cross(Vector3 origin, Vector3 a, Vector3 b) =>
        (a.X - origin.X) * (b.Z - origin.Z) - (a.Z - origin.Z) * (b.X - origin.X);

    private static float SignedArea(IReadOnlyList<Vector3> points)
    {
        var area = 0f;
        for (var index = 0; index < points.Count; index++)
        {
            var next = points[(index + 1) % points.Count];
            area += points[index].X * next.Z - next.X * points[index].Z;
        }
        return area * 0.5f;
    }

    private static bool IsFinite(Vector3 point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z)
        && Math.Abs(point.X) < 1_000_000 && Math.Abs(point.Y) < 1_000_000 && Math.Abs(point.Z) < 1_000_000;

    private sealed record LocalFootprint(IReadOnlyList<Vector3> Points);
    private readonly record struct PartKey(string ModelName, Vector3 Position, Vector3 Rotation, Vector3 Scale);
}
