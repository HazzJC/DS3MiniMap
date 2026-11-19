using System.Numerics;
using DS3Minimap.Core;

namespace DS3Minimap.MapBaker;

// Seed the collision surface from known routes, then follow neighbouring ground at
// compatible heights. A ceiling, distant hill or lower storey at the same X/Z
// cannot enter the map just because it overlaps a route in a top-down view.
internal sealed class GroundGrid
{
    private readonly record struct Cell(int X, int Z, int Y);
    private readonly HashSet<Cell> _surface = [];
    private readonly int _minX, _maxX, _minZ, _maxZ, _minY, _maxY;
    private const float Unit = CollisionRun.CellSize;

    internal GroundGrid(MapRegion region)
    {
        var points = region.Layers.SelectMany(l => l.Polygons).SelectMany(p => p.Points).ToArray();
        const int margin = 64;
        _minX = (int)MathF.Floor(points.Min(p => p.X) / Unit) - margin;
        _maxX = (int)MathF.Ceiling(points.Max(p => p.X) / Unit) + margin;
        _minZ = (int)MathF.Floor(points.Min(p => p.Z) / Unit) - margin;
        _maxZ = (int)MathF.Ceiling(points.Max(p => p.Z) / Unit) + margin;
        _minY = (int)MathF.Floor(region.Layers.Min(l => l.MinimumY) / Unit) - margin;
        _maxY = (int)MathF.Ceiling(region.Layers.Max(l => l.MaximumY) / Unit) + margin;
    }

    internal void AddSurface(Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Cross(b - a, c - a);
        if (normal.Y <= 0 || normal.Y < 0.62f * normal.Length()) return;
        foreach (var cell in Sample(a, b, c)) _surface.Add(cell);
    }

    private IEnumerable<Cell> Sample(Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        float determinant = ab.X * ac.Z - ab.Z * ac.X;
        if (!float.IsFinite(determinant) || MathF.Abs(determinant) < 0.00001f) yield break;
        var first = Vector3.Min(a, Vector3.Min(b, c)) / Unit;
        var last = Vector3.Max(a, Vector3.Max(b, c)) / Unit;
        int startX = Math.Max(_minX, (int)MathF.Ceiling(first.X - 0.5f));
        int endX = Math.Min(_maxX, (int)MathF.Floor(last.X - 0.5f));
        int startZ = Math.Max(_minZ, (int)MathF.Ceiling(first.Z - 0.5f));
        int endZ = Math.Min(_maxZ, (int)MathF.Floor(last.Z - 0.5f));
        for (var z = startZ; z <= endZ; z++)
        for (var x = startX; x <= endX; x++)
        {
            float dx = (x + 0.5f) * Unit - a.X, dz = (z + 0.5f) * Unit - a.Z;
            float u = (dx * ac.Z - dz * ac.X) / determinant;
            float v = (ab.X * dz - ab.Z * dx) / determinant;
            if (u < 0 || v < 0 || u + v > 1) continue;
            int y = (int)MathF.Round((a.Y + u * ab.Y + v * ac.Y) / Unit);
            if (y >= _minY && y <= _maxY) yield return new Cell(x, z, y);
        }
    }

    internal MapRegion AssignFloors(MapRegion region)
    {
        var owners = new Dictionary<Cell, int>();
        var queue = new Queue<Cell>();
        for (var floor = 0; floor < region.Layers.Count; floor++)
        foreach (var polygon in region.Layers[floor].Polygons)
        {
            var points = polygon.Points;
            static Vector3 World(MapPoint p) => new(p.X, p.Y, p.Z);
            for (var i = 2; i < points.Count; i++)
            foreach (var sample in Sample(World(points[0]), World(points[i - 1]), World(points[i])))
            {
                foreach (var delta in SeedHeights)
                {
                    var cell = sample with { Y = sample.Y + delta };
                    if (!_surface.Contains(cell)) continue;
                    if (owners.TryAdd(cell, floor)) queue.Enqueue(cell);
                    break;
                }
            }
        }

        var seeds = queue.Count;
        while (queue.TryDequeue(out var cell))
        {
            foreach (var (dx, dz) in Neighbours)
            for (var dy = -1; dy <= 1; dy++)
            {
                var next = new Cell(cell.X + dx, cell.Z + dz, cell.Y + dy);
                if (_surface.Contains(next) && owners.TryAdd(next, owners[cell])) queue.Enqueue(next);
            }
        }
        var floors = region.Layers.ToArray();
        foreach (var group in owners.GroupBy(pair => pair.Value))
        {
            var runs = new List<CollisionRun>();
            foreach (var cell in group.Select(pair => pair.Key).OrderBy(p => p.Z).ThenBy(p => p.Y).ThenBy(p => p.X))
            {
                if (runs.Count > 0 && runs[^1] is var last && last.Z == cell.Z && last.Y == cell.Y && last.EndX == cell.X)
                    runs[^1] = last with { EndX = cell.X + 1 };
                else runs.Add(new CollisionRun(cell.X, cell.X + 1, cell.Z, cell.Y));
            }
            floors[group.Key] = floors[group.Key] with { Ground = runs };
        }
        Console.WriteLine($"  collision: {seeds:N0} cells aligned with navigation, {owners.Count - seeds:N0} adjoining ground cells");
        return region with { Layers = floors };
    }

    private static readonly int[] SeedHeights = [0, -1, 1, -2, 2];
    private static readonly (int X, int Z)[] Neighbours = [(1, 0), (-1, 0), (0, 1), (0, -1)];
}
