using DS3Minimap.Core;
using SkiaSharp;

namespace DS3Minimap.Rendering;

/// <summary>
/// Holds the baked ground-texture atlas for the current map. The atlas file is
/// read off the UI thread; tiles are decoded to Skia images only when they come
/// into view and are kept in a least-recently-used cache with a memory budget.
/// When zoomed out, small preview versions of each tile are used instead, so a
/// wide view stays fast and light.
/// </summary>
internal sealed class GroundTextureCache : IDisposable
{
    internal const int PreviewSize = 128;
    private const long BudgetBytes = 96L * 1024 * 1024;
    private readonly Dictionary<(string Floor, int X, int Z), byte[]> _encoded = [];
    private readonly HashSet<string> _floors = [];
    private readonly Dictionary<Key, LinkedListNode<(Key Key, SKImage? Image, long Bytes)>> _decoded = [];
    private readonly LinkedList<(Key Key, SKImage? Image, long Bytes)> _recent = new();
    private readonly Func<string, string> _pathForMap;
    private long _bytes;
    private string? _mapId;
    private int _request;

    private readonly record struct Key(string Floor, int X, int Z, bool Preview);

    internal GroundTextureCache(Func<string, string>? pathForMap = null) =>
        _pathForMap = pathForMap ?? GroundAtlas.CachePath;

    /// <summary>Raised on the thread pool once a newly requested atlas has loaded.</summary>
    internal event Action? Loaded;

    internal bool HasFloor(string floor)
    {
        lock (_encoded) return _floors.Contains(floor);
    }

    internal void Request(string? mapId)
    {
        if (mapId == _mapId) return;
        _mapId = mapId;
        var request = ++_request;
        ClearTiles();
        if (mapId is null) return;
        var path = _pathForMap(mapId);
        _ = Task.Run(async () =>
        {
            var atlas = await GroundAtlas.ReadAsync(path).ConfigureAwait(false);
            if (atlas is null || atlas.Version != GroundAtlas.CurrentVersion || atlas.MapId != mapId) return;
            lock (_encoded)
            {
                if (request != _request) return;
                foreach (var tile in atlas.Tiles)
                {
                    _encoded[(tile.Floor, tile.X, tile.Z)] = tile.WebP;
                    _floors.Add(tile.Floor);
                }
            }
            Loaded?.Invoke();
        });
    }

    /// <summary>Returns the decoded tile, or null where no ground was textured.</summary>
    internal SKImage? Get(string floor, int x, int z, bool preview)
    {
        var key = new Key(floor, x, z, preview);
        if (_decoded.TryGetValue(key, out var node))
        {
            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value.Image;
        }
        byte[]? bytes;
        lock (_encoded) _encoded.TryGetValue((floor, x, z), out bytes);
        if (bytes is null) return null;
        var image = Decode(bytes, preview);
        long size = image is null ? 0 : (long)image.Width * image.Height * 4;
        _decoded[key] = _recent.AddFirst((key, image, size));
        _bytes += size;
        while (_bytes > BudgetBytes && _recent.Count > 1)
        {
            var last = _recent.Last!;
            _recent.RemoveLast();
            _decoded.Remove(last.Value.Key);
            _bytes -= last.Value.Bytes;
            last.Value.Image?.Dispose();
        }
        return image;
    }

    private static SKImage? Decode(byte[] bytes, bool preview)
    {
        using var data = SKData.CreateCopy(bytes);
        var full = SKImage.FromEncodedData(data);
        if (full is null || !preview || full.Width <= PreviewSize) return full;
        using (full)
        {
            using var small = new SKBitmap(new SKImageInfo(PreviewSize, PreviewSize, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (!full.ScalePixels(small.PeekPixels(), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))) return null;
            return SKImage.FromPixelCopy(small.PeekPixels());
        }
    }

    private void ClearTiles()
    {
        lock (_encoded)
        {
            _encoded.Clear();
            _floors.Clear();
        }
        foreach (var entry in _recent) entry.Image?.Dispose();
        _recent.Clear();
        _decoded.Clear();
        _bytes = 0;
    }

    public void Dispose()
    {
        _request++;
        _mapId = null;
        ClearTiles();
    }
}
