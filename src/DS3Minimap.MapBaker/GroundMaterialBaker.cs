using System.Numerics;
using System.Runtime.InteropServices;
using DS3Minimap.Core;
using SkiaSharp;
using SoulsFormats;

namespace DS3Minimap.MapBaker;

/// <summary>
/// Projects the diffuse textures of DS3's placed map pieces straight down onto
/// each floor's collision ground, then bakes gentle hillshading and ledge/wall
/// rims from the same height grid. The result is a per-floor tile atlas the
/// overlay draws beneath the navigation paths.
/// </summary>
internal sealed class GroundMaterialBaker
{
    private const int Size = GroundTile.Resolution;
    private const int Scale = GroundTile.PixelsPerUnit;
    private const int Cells = GroundTile.WorldSize * 2;
    private const int PixelsPerCell = Size / Cells;
    private const float HillshadeStrength = 0.25f;
    // Visual terrain is sculpted more finely than collision; allow some slack.
    private const float MaxHeightError = 3f;
    // Heights are stored in half units; 3 half units = a 1.5 unit ledge or wall.
    private const int LedgeHalfUnits = 3;
    private readonly Dictionary<(int X, int Z), List<Tile>> _columns = [];
    private readonly Dictionary<(string Floor, int X, int Z), Tile> _tiles = [];
    private readonly int _minX, _minZ, _maxX, _maxZ;

    private sealed class Tile(string floor, int x, int z)
    {
        internal readonly string Floor = floor;
        internal readonly int X = x, Z = z;
        internal readonly short[] Heights = Enumerable.Repeat(short.MinValue, Cells * Cells).ToArray();
        internal int[]? Pixels;
        internal uint[]? Scores;
        internal int MinY = int.MaxValue, MaxY = int.MinValue;
    }

    internal GroundMaterialBaker(MapRegion region)
    {
        foreach (var layer in region.Layers)
        {
            foreach (var run in layer.Ground ?? [])
            {
                int tz = (int)Math.Floor((double)run.Z / Cells);
                for (int x = run.X; x < run.EndX;)
                {
                    int tx = (int)Math.Floor((double)x / Cells);
                    if (!_tiles.TryGetValue((layer.Id, tx, tz), out var tile))
                    {
                        _tiles[(layer.Id, tx, tz)] = tile = new(layer.Id, tx, tz);
                        if (!_columns.TryGetValue((tx, tz), out var column)) _columns[(tx, tz)] = column = [];
                        column.Add(tile);
                    }
                    var end = Math.Min(run.EndX, (tx + 1) * Cells);
                    var start = (run.Z - tz * Cells) * Cells + x - tx * Cells;
                    tile.Heights.AsSpan(start, end - x).Fill(checked((short)run.Y));
                    tile.MinY = Math.Min(tile.MinY, run.Y);
                    tile.MaxY = Math.Max(tile.MaxY, run.Y);
                    x = end;
                }
            }
        }
        if (_columns.Count > 0)
        {
            _minX = _columns.Keys.Min(k => k.X); _maxX = _columns.Keys.Max(k => k.X);
            _minZ = _columns.Keys.Min(k => k.Z); _maxZ = _columns.Keys.Max(k => k.Z);
        }
    }

    internal GroundAtlas Bake(Ds3ArchiveReader archives, string mapId, DateTimeOffset generation)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var resource = mapId + "_00_00";
        var msb = MSB3.Read(archives.Read($"/map/mapstudio/{resource}.msb.dcx"));
        var diffuse = DiffuseTextures.Load(archives, mapId[..3]);
        var textureSeconds = clock.Elapsed.TotalSeconds;
        var models = msb.Parts.MapPieces.GroupBy(p => p.ModelName).ToArray();
        int loaded = 0, blended = 0;
        if (_tiles.Count > 0)
        {
            // Each worker owns one model. Within a model the FLVER face order is
            // stable. Across models the MSB order breaks ties on coplanar surfaces.
            Parallel.For(0, models.Length, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, modelIndex =>
            {
                var batches = LoadModel(archives, resource, models[modelIndex], diffuse);
                if (batches is null) return;
                Interlocked.Increment(ref loaded);
                ushort priority = checked((ushort)(models.Length - modelIndex));
                foreach (var batch in batches)
                {
                    if (batch.Surface.Layers.Length > 1) Interlocked.Increment(ref blended);
                    var vertices = batch.Vertices;
                    foreach (var indices in batch.FaceSets)
                        for (var i = 0; i + 2 < indices.Count; i += 3)
                            Project(vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]], batch.Surface, priority);
                }
            });
        }
        var floorAverage = FloorAverages();
        var output = new List<GroundTile>();
        foreach (var tile in _tiles.Values)
        {
            if (tile.Pixels == null) continue;
            FillHoles(tile, floorAverage.GetValueOrDefault(tile.Floor, 0xff6e665a));
            ApplyHillshade(tile);
            using var bitmap = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul));
            Marshal.Copy(tile.Pixels, 0, bitmap.GetPixels(), tile.Pixels.Length);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Webp, 82);
            output.Add(new(tile.Floor, tile.X, tile.Z, encoded.ToArray()));
        }
        Console.WriteLine($"  materials {mapId}: {loaded}/{models.Length} visual models, {diffuse.Count} textures, {blended} blended meshes, "
            + $"{output.Count} floor tiles ({textureSeconds:0}s textures, {clock.Elapsed.TotalSeconds:0}s total)");
        return new(GroundAtlas.CurrentVersion, mapId, generation, output);
    }

    private sealed record MeshBatch(Vertex[] Vertices, List<int>[] FaceSets, Surface Surface);

    private static MeshBatch[]? LoadModel(Ds3ArchiveReader archives, string resource, IGrouping<string, MSB3.Part.MapPiece> model,
        Dictionary<string, DiffuseTexture> diffuse)
    {
        byte[] bytes;
        try { bytes = archives.Read($"/map/{resource}/{resource}_{model.Key[1..]}.mapbnd.dcx"); }
        catch (FileNotFoundException) { return null; }
        var binder = BND4.Read(DCX.Decompress(bytes));
        var file = binder.Files.FirstOrDefault(f => f.Name?.EndsWith(".flver", StringComparison.OrdinalIgnoreCase) == true);
        if (file == null) return null;
        var flver = FLVER2.Read(file.Bytes);
        var placements = model.Select(p => (p.Scale, R: p.Rotation * (MathF.PI / 180), p.Position)).Distinct()
            .Select(p => Matrix4x4.CreateScale(p.Scale) * Matrix4x4.CreateRotationX(p.R.X)
                * Matrix4x4.CreateRotationZ(p.R.Z) * Matrix4x4.CreateRotationY(p.R.Y) * Matrix4x4.CreateTranslation(p.Position)).ToArray();
        var batches = new List<MeshBatch>();
        foreach (var mesh in flver.Meshes)
        {
            var surface = ResolveSurface(flver.Materials[mesh.MaterialIndex], diffuse);
            if (surface is null) continue;
            var faceSets = mesh.FaceSets.Where(f => f.Flags == FLVER2.FaceSet.FSFlags.None)
                .Select(f => f.Triangulate(mesh.Vertices.Count < ushort.MaxValue)).ToArray();
            if (faceSets.Length == 0) continue;
            foreach (var transform in placements)
            {
                var vertices = new Vertex[mesh.Vertices.Count];
                for (int i = 0; i < vertices.Length; i++)
                {
                    var v = mesh.Vertices[i];
                    var uv = v.UVs.FirstOrDefault();
                    var color = v.Colors.Count > 0
                        ? Vector4.Clamp(new Vector4(v.Colors[0].R, v.Colors[0].G, v.Colors[0].B, v.Colors[0].A), Vector4.Zero, Vector4.One)
                        : new Vector4(0, 0, 0, 1);
                    vertices[i] = new(Vector3.Transform(v.Position, transform), new Vector2(uv.X, uv.Y),
                        Vector3.TransformNormal(v.Normal, transform), color);
                }
                batches.Add(new MeshBatch(vertices, faceSets, surface));
            }
        }
        return [.. batches];
    }

    private readonly record struct Material(DiffuseTexture Texture, Vector2 Tiling)
    {
        internal Vector4 Sample(Vector2 uv) => Texture.Sample(uv.X * Tiling.X, uv.Y * Tiling.Y);
    }

    private enum BlendKind { Single, VertexAlpha, VertexChannels }

    private sealed record Surface(Material[] Layers, BlendKind Blend, bool Water = false);

    private static Material? Resolve(FLVER2.Texture? slot, Dictionary<string, DiffuseTexture> diffuse)
    {
        if (slot == null || string.IsNullOrEmpty(slot.Path)
            || !diffuse.TryGetValue(Path.GetFileNameWithoutExtension(slot.Path), out var texture)) return null;
        var tiling = slot.TilingScale;
        return new Material(texture, tiling == Vector2.Zero ? Vector2.One : tiling);
    }

    /// <summary>
    /// Ordinary map materials have one diffuse texture, optionally painted over
    /// with a second one by vertex-colour alpha (dirt under grass, moss on
    /// stone). Large terrain pieces use DS3's MultiBlend materials instead: up to
    /// four albedo maps layered by the vertex colour channels.
    /// </summary>
    private static Surface? ResolveSurface(FLVER2.Material material, Dictionary<string, DiffuseTexture> diffuse)
    {
        FLVER2.Texture? Slot(string name) =>
            material.Textures.FirstOrDefault(t => t.ParamName.Equals(name, StringComparison.OrdinalIgnoreCase));
        // Water albedo is a vivid placeholder; the shader supplies its real look.
        var water = material.MTD?.Contains("water", StringComparison.OrdinalIgnoreCase) == true;
        if (Resolve(Slot("g_DiffuseTexture"), diffuse) is { } primary)
        {
            var secondary = Resolve(Slot("g_DiffuseTexture2"), diffuse);
            return secondary is { } second && !ReferenceEquals(second.Texture, primary.Texture)
                ? new Surface([primary, second], BlendKind.VertexAlpha)
                : new Surface([primary], BlendKind.Single, water);
        }
        var albedo = material.Textures
            .Select(t => (Slot: t, Index: AlbedoIndex(t.ParamName)))
            .Where(t => t.Index >= 0)
            .OrderBy(t => t.Index)
            .Select(t => Resolve(t.Slot, diffuse))
            .ToArray();
        if (albedo.Length == 0 || albedo[0] is not { } base0) return null;
        // A missing upper layer falls back to the base so weights stay meaningful.
        var layers = albedo.Take(4).Select(layer => layer ?? base0).ToArray();
        return new Surface(layers, layers.Length > 1 ? BlendKind.VertexChannels : BlendKind.Single);
    }

    private static int AlbedoIndex(string parameter)
    {
        const string marker = "AlbedoMap_";
        var at = parameter.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at >= 0 && int.TryParse(parameter.AsSpan(at + marker.Length), out var index) ? index : -1;
    }

    private static readonly Vector4 WaterColor = new(46, 66, 76, 255);

    private static Vector4 Shade(Surface surface, Vector2 uv, Vector4 vertexColor)
    {
        var color = surface.Layers[0].Sample(uv);
        if (surface.Water)
        {
            // Keep a hint of the ripple pattern as brightness only.
            var luminance = (color.X * 0.3f + color.Y * 0.59f + color.Z * 0.11f) / 255f;
            return (WaterColor * (0.85f + 0.3f * luminance)) with { W = 255 };
        }
        switch (surface.Blend)
        {
            case BlendKind.VertexAlpha:
                return Vector4.Lerp(surface.Layers[1].Sample(uv), color, vertexColor.W);
            case BlendKind.VertexChannels:
                for (int layer = 1; layer < surface.Layers.Length; layer++)
                {
                    var weight = layer switch { 1 => vertexColor.X, 2 => vertexColor.Y, _ => vertexColor.Z };
                    if (weight > 0.004f) color = Vector4.Lerp(color, surface.Layers[layer].Sample(uv), weight);
                }
                return color;
            default:
                return color;
        }
    }

    private readonly record struct Vertex(Vector3 Position, Vector2 UV, Vector3 Normal, Vector4 Color);

    private void Project(Vertex a, Vertex b, Vertex c, Surface surface, ushort modelPriority)
    {
        var ab = b.Position - a.Position;
        var ac = c.Position - a.Position;
        var det = ab.X * ac.Z - ab.Z * ac.X;
        if (MathF.Abs(det) < 0.0001f || a.Normal.Y + b.Normal.Y + c.Normal.Y < 0.2f) return;
        // Vertex normals of foliage cards often point up for lighting; the true
        // face normal must also be reasonably upward, or a steep card would win.
        var face = Vector3.Cross(ab, ac);
        if (MathF.Abs(face.Y) < 0.35f * face.Length()) return;
        var lo = Vector3.Min(a.Position, Vector3.Min(b.Position, c.Position));
        var hi = Vector3.Max(a.Position, Vector3.Max(b.Position, c.Position));
        int TileIndex(float p) => (int)MathF.Floor(p / GroundTile.WorldSize);
        int x0 = Math.Max(_minX, TileIndex(lo.X)), x1 = Math.Min(_maxX, TileIndex(hi.X));
        int z0 = Math.Max(_minZ, TileIndex(lo.Z)), z1 = Math.Min(_maxZ, TileIndex(hi.Z));
        var uAB = b.UV - a.UV;
        var uAC = c.UV - a.UV;
        var normal = Vector3.Normalize(a.Normal + b.Normal + c.Normal);
        var light = Math.Clamp(1.08f + 0.25f * normal.Y - 0.18f * normal.X - 0.15f * normal.Z, 0.8f, 1.5f);
        for (int tz = z0; tz <= z1; tz++)
        for (int tx = x0; tx <= x1; tx++)
        {
            if (!_columns.TryGetValue((tx, tz), out var tiles)) continue;
            int originX = tx * Size, originZ = tz * Size;
            int left = Math.Max(0, (int)MathF.Ceiling(lo.X * Scale - 0.5f) - originX);
            int right = Math.Min(Size - 1, (int)MathF.Floor(hi.X * Scale - 0.5f) - originX);
            int top = Math.Max(0, (int)MathF.Ceiling(lo.Z * Scale - 0.5f) - originZ);
            int bottom = Math.Min(Size - 1, (int)MathF.Floor(hi.Z * Scale - 0.5f) - originZ);
            if (left > right || top > bottom) continue;
            lock (tiles)
            foreach (var tile in tiles)
            {
                if (hi.Y < tile.MinY * 0.5f - 1 || lo.Y > tile.MaxY * 0.5f + 1) continue;
                for (int z = top; z <= bottom; z++)
                for (int x = left; x <= right; x++)
                {
                    int height = tile.Heights[(z / PixelsPerCell) * Cells + x / PixelsPerCell];
                    if (height == short.MinValue) continue;
                    var dx = (originX + x + 0.5f) / Scale - a.Position.X;
                    var dz = (originZ + z + 0.5f) / Scale - a.Position.Z;
                    float u = (dx * ac.Z - dz * ac.X) / det;
                    float v = (ab.X * dz - ab.Z * dx) / det;
                    if (u < 0 || v < 0 || u + v > 1) continue;
                    float surfaceY = a.Position.Y + ab.Y * u + ac.Y * v;
                    float offset = surfaceY - height * 0.5f;
                    // Visual terrain is sculpted more finely than collision, so allow
                    // some vertical slack around this floor's ground.
                    if (MathF.Abs(offset) >= MaxHeightError) continue;
                    // Like a view from above, the highest surface near the floor wins:
                    // grass and decals over the ground beneath them, not alternating cells.
                    int index = z * Size + x;
                    uint elevation = (uint)((offset + MaxHeightError) / (2 * MaxHeightError) * ushort.MaxValue);
                    uint score = (elevation << 16) | modelPriority;
                    if (tile.Scores != null && tile.Scores[index] >= score) continue;
                    var uv = a.UV + u * uAB + v * uAC;
                    var vertexColor = surface.Blend == BlendKind.Single ? Vector4.One
                        : Vector4.Clamp(a.Color + (b.Color - a.Color) * u + (c.Color - a.Color) * v, Vector4.Zero, Vector4.One);
                    var color = Shade(surface, uv, vertexColor);
                    // Terrain albedo alpha holds a blend height, not transparency.
                    if (surface.Blend != BlendKind.VertexChannels && color.W < 128) continue;
                    tile.Scores ??= new uint[Size * Size];
                    tile.Pixels ??= new int[Size * Size];
                    tile.Scores[index] = score;
                    int Lit(float channel) => Math.Clamp((int)(channel * light + 12), 0, 255);
                    tile.Pixels[index] = unchecked((int)0xff000000) | Lit(color.X) << 16 | Lit(color.Y) << 8 | Lit(color.Z);
                }
            }
        }
    }

    private Dictionary<string, uint> FloorAverages()
    {
        var sums = new Dictionary<string, (long R, long G, long B, long N)>();
        foreach (var tile in _tiles.Values)
        {
            if (tile.Pixels is null) continue;
            var (r, g, b, n) = sums.GetValueOrDefault(tile.Floor);
            for (int i = 0; i < tile.Pixels.Length; i += 7)
            {
                var p = tile.Pixels[i];
                if (p == 0) continue;
                r += (p >> 16) & 255; g += (p >> 8) & 255; b += p & 255; n++;
            }
            sums[tile.Floor] = (r, g, b, n);
        }
        return sums.Where(s => s.Value.N > 0).ToDictionary(s => s.Key,
            s => 0xff000000u | (uint)(s.Value.R / s.Value.N) << 16 | (uint)(s.Value.G / s.Value.N) << 8 | (uint)(s.Value.B / s.Value.N));
    }

    /// <summary>
    /// Collision ground that no visual triangle covered (props, decals, alpha
    /// cut-outs) would otherwise be a transparent speck. Grow neighbouring
    /// colour into it for a few pixels, then use the floor's average colour.
    /// </summary>
    private static void FillHoles(Tile tile, uint fallback)
    {
        var pixels = tile.Pixels!;
        bool HasGround(int x, int z) => tile.Heights[(z / PixelsPerCell) * Cells + x / PixelsPerCell] != short.MinValue;
        var next = (int[])pixels.Clone();
        // Grow about a third of a unit, whatever the bake resolution.
        for (int pass = 0; pass < Math.Max(3, Scale * 3 / 8); pass++)
        {
            bool changed = false;
            for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                int index = z * Size + x;
                if (pixels[index] != 0 || !HasGround(x, z)) continue;
                int r = 0, g = 0, b = 0, n = 0;
                void Add(int nx, int nz)
                {
                    if ((uint)nx >= Size || (uint)nz >= Size) return;
                    var p = pixels[nz * Size + nx];
                    if (p == 0) return;
                    r += (p >> 16) & 255; g += (p >> 8) & 255; b += p & 255; n++;
                }
                Add(x - 1, z); Add(x + 1, z); Add(x, z - 1); Add(x, z + 1);
                if (n == 0) continue;
                next[index] = unchecked((int)0xff000000) | (r / n) << 16 | (g / n) << 8 | (b / n);
                changed = true;
            }
            Array.Copy(next, pixels, pixels.Length);
            if (!changed) break;
        }
        // Larger gaps are ground whose visual model lies outside this map's
        // pieces (distant scenery). Give it the floor's average colour with two
        // octaves of world-anchored noise so it reads as earth, not a flat patch.
        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
        {
            if (pixels[z * Size + x] != 0 || !HasGround(x, z)) continue;
            int wx = tile.X * Size + x, wz = tile.Z * Size + z;
            var grain = 0.86f + 0.16f * ValueNoise(wx / (2.9f * Scale), wz / (2.9f * Scale)) + 0.1f * ValueNoise(wx / (0.6f * Scale) + 91, wz / (0.6f * Scale) + 37);
            int Channel(int shift) => Math.Clamp((int)(((fallback >> shift) & 255) * grain), 0, 255);
            pixels[z * Size + x] = unchecked((int)0xff000000) | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
        }
    }

    private static float ValueNoise(float x, float z)
    {
        static float Lattice(int x, int z)
        {
            unchecked
            {
                var h = (uint)x * 0x9E3779B1u ^ (uint)z * 0x85EBCA77u;
                h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
                return (h & 0xffff) / 65535f;
            }
        }
        int x0 = (int)MathF.Floor(x), z0 = (int)MathF.Floor(z);
        float tx = x - x0, tz = z - z0;
        tx = tx * tx * (3 - 2 * tx); tz = tz * tz * (3 - 2 * tz);
        var top = Lattice(x0, z0) + (Lattice(x0 + 1, z0) - Lattice(x0, z0)) * tx;
        var bottom = Lattice(x0, z0 + 1) + (Lattice(x0 + 1, z0 + 1) - Lattice(x0, z0 + 1)) * tx;
        return top + (bottom - top) * tz;
    }

    private int HeightAt(string floor, int cellX, int cellZ)
    {
        int tx = (int)Math.Floor((double)cellX / Cells), tz = (int)Math.Floor((double)cellZ / Cells);
        if (!_tiles.TryGetValue((floor, tx, tz), out var tile)) return short.MinValue;
        return tile.Heights[(cellZ - tz * Cells) * Cells + cellX - tx * Cells];
    }

    /// <summary>
    /// Light from the north-west, plus a darker rim where the ground meets a
    /// wall or void and a pale lip along the top of a drop. Stairs, ramps,
    /// cliffs and ledges then read as light and shadow at a glance. Heights are
    /// smoothed and the shade is interpolated between cells, so the half-unit
    /// collision grid does not show through as blocks.
    /// </summary>
    private void ApplyHillshade(Tile tile)
    {
        const int Pad = 3, Span = Cells + Pad * 2;
        var pixels = tile.Pixels!;
        int baseX = tile.X * Cells, baseZ = tile.Z * Cells;
        var raw = new int[Span * Span];
        for (int z = 0; z < Span; z++)
        for (int x = 0; x < Span; x++)
            raw[z * Span + x] = HeightAt(tile.Floor, baseX + x - Pad, baseZ + z - Pad);
        int Raw(int cx, int cz) => raw[(cz + Pad) * Span + cx + Pad];

        // Smoothed height in units: mean of the continuous neighbourhood only.
        var smooth = new float[Span * Span];
        for (int z = 1; z < Span - 1; z++)
        for (int x = 1; x < Span - 1; x++)
        {
            int h = raw[z * Span + x];
            if (h == short.MinValue) { smooth[z * Span + x] = float.NaN; continue; }
            float sum = 0; int n = 0;
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int o = raw[(z + dz) * Span + x + dx];
                if (o == short.MinValue || Math.Abs(o - h) > LedgeHalfUnits) continue;
                sum += o; n++;
            }
            smooth[z * Span + x] = sum / n * 0.5f;
        }
        float Smooth(int cx, int cz) => smooth[(cz + Pad) * Span + cx + Pad];

        var cellShade = new float[(Cells + 2) * (Cells + 2)];
        var edges = new byte[Cells * Cells]; // bit 0-3 W,E,N,S dark rim; bit 4-7 W,E,N,S pale lip
        for (int cz = -1; cz <= Cells; cz++)
        for (int cx = -1; cx <= Cells; cx++)
        {
            var centre = Smooth(cx, cz);
            if (float.IsNaN(centre)) continue;
            float Gradient(int ax, int az, int bx, int bz)
            {
                // Two cells either side spans two world units.
                float a = Smooth(ax, az), b = Smooth(bx, bz);
                bool av = !float.IsNaN(a) && MathF.Abs(a - centre) <= 1.5f, bv = !float.IsNaN(b) && MathF.Abs(b - centre) <= 1.5f;
                if (av && bv) return (b - a) / 2f;
                if (bv) return b - centre;
                if (av) return centre - a;
                return 0;
            }
            // Ground rising towards the south-east faces the north-west light.
            var facing = Gradient(cx - 2, cz, cx + 2, cz) + Gradient(cx, cz - 2, cx, cz + 2);
            cellShade[(cz + 1) * (Cells + 2) + cx + 1] = 1f + HillshadeStrength * Math.Clamp(facing, -2f, 2f) * 0.5f;
            if ((uint)cx >= Cells || (uint)cz >= Cells) continue;
            int h = Raw(cx, cz);
            byte flags = 0;
            void Edge(int neighbour, int bit)
            {
                if (neighbour == short.MinValue || neighbour - h > LedgeHalfUnits) flags |= (byte)(1 << bit);
                else if (h - neighbour > LedgeHalfUnits) flags |= (byte)(1 << (bit + 4));
            }
            Edge(Raw(cx - 1, cz), 0); Edge(Raw(cx + 1, cz), 1); Edge(Raw(cx, cz - 1), 2); Edge(Raw(cx, cz + 1), 3);
            edges[cz * Cells + cx] = flags;
        }
        float ShadeAt(int cx, int cz, float fallback)
        {
            var value = cellShade[(cz + 1) * (Cells + 2) + cx + 1];
            return value == 0 ? fallback : value;
        }
        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
        {
            int index = z * Size + x;
            var p = pixels[index];
            if (p == 0) continue;
            int cx = x / PixelsPerCell, cz = z / PixelsPerCell;
            var own = cellShade[(cz + 1) * (Cells + 2) + cx + 1];
            if (own == 0) continue;
            // Bilinear between neighbouring cell centres.
            float fx = (x + 0.5f) / PixelsPerCell - 0.5f, fz = (z + 0.5f) / PixelsPerCell - 0.5f;
            int x0 = (int)MathF.Floor(fx), z0 = (int)MathF.Floor(fz);
            float tx = fx - x0, tz = fz - z0;
            var top = ShadeAt(x0, z0, own) + (ShadeAt(x0 + 1, z0, own) - ShadeAt(x0, z0, own)) * tx;
            var bottom = ShadeAt(x0, z0 + 1, own) + (ShadeAt(x0 + 1, z0 + 1, own) - ShadeAt(x0, z0 + 1, own)) * tx;
            var shade = top + (bottom - top) * tz;
            // Rims fade over two pixels from the cell side that meets a wall or drop.
            int sx = x % PixelsPerCell, sz = z % PixelsPerCell;
            var flags = edges[cz * Cells + cx];
            for (int bit = 0; bit < 4; bit++)
            {
                var distance = bit switch { 0 => sx, 1 => PixelsPerCell - 1 - sx, 2 => sz, _ => PixelsPerCell - 1 - sz };
                // Rims are a quarter of a unit wide, fading outward.
                var rim = Math.Max(2, Scale / 4);
                if (distance >= rim) continue;
                var weight = 1f - (float)distance / rim;
                if ((flags & (1 << bit)) != 0) shade *= 1f - 0.32f * weight;
                else if ((flags & (1 << (bit + 4))) != 0) shade *= 1f + 0.14f * weight;
            }
            int Channel(int value) => Math.Clamp((int)(value * shade), 0, 255);
            pixels[index] = unchecked((int)0xff000000) | Channel((p >> 16) & 255) << 16 | Channel((p >> 8) & 255) << 8 | Channel(p & 255);
        }
    }
}
