using System.Text.Json;

namespace DS3Minimap.Core;

public sealed record GroundTile(string Floor, int X, int Z, byte[] WebP)
{
    public const int WorldSize = 32;
    public const int PixelsPerUnit = 16;
    public const int Resolution = WorldSize * PixelsPerUnit;
}

/// <summary>
/// Top-down ground textures for one map, baked from the player's own DS3
/// installation: one transparent WebP tile per 32x32 world units per floor.
/// </summary>
public sealed record GroundAtlas(int Version, string MapId, DateTimeOffset MapGeneration, IReadOnlyList<GroundTile> Tiles)
{
    // 2: two-texture terrain blending, hole filling and baked hillshade.
    // 3: 16 pixels per unit for sharper close zoom.
    // 4: deterministic surface selection for overlapping map pieces.
    public const int CurrentVersion = 4;

    public bool Matches(string mapId, DateTimeOffset generation) =>
        Version == CurrentVersion && MapId == mapId && MapGeneration == generation;

    public static string CachePath(string mapId) => CachePath(mapId, AppPaths.MapPackage);

    public static string CachePath(string mapId, string packagePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(packagePath))!, "materials", mapId + ".atlas");

    public static async Task<GroundAtlas?> ReadAsync(string file, CancellationToken token = default)
    {
        if (!File.Exists(file)) return null;
        try
        {
            await using var stream = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<GroundAtlas>(stream, cancellationToken: token).ConfigureAwait(false);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>Reads only the header fields, so staleness checks skip the tile payload.</summary>
    public static async Task<bool> IsCurrentAsync(string file, string mapId, DateTimeOffset generation, CancellationToken token = default)
    {
        if (!File.Exists(file)) return false;
        try
        {
            await using var stream = File.OpenRead(file);
            var header = await JsonSerializer.DeserializeAsync<Header>(stream, cancellationToken: token).ConfigureAwait(false);
            return header is not null && header.Version == CurrentVersion && header.MapId == mapId && header.MapGeneration == generation;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
    }

    private sealed record Header(int Version, string MapId, DateTimeOffset MapGeneration);

    public async Task SaveAsync(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, this);
            File.Move(temp, file, true);
        }
        finally { File.Delete(temp); }
    }
}
