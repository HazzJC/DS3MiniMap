using System.Numerics;

namespace DS3Minimap.Core;

/// <summary>
/// Remembers which parts of each floor the player has walked near, as a set of
/// coarse square cells. The minimap keeps explored ground bright and gently
/// dims the rest, giving a visual record of "where I have already been".
/// </summary>
public sealed class ExploredArea
{
    public const float CellSize = 4f;
    public const float RevealRadius = 12f;
    private readonly Dictionary<(string MapId, string LayerId), HashSet<long>> _cells = [];

    /// <summary>Increments whenever any cell is added or the area is cleared.</summary>
    public int Version { get; private set; }

    public static long Key(int x, int z) => ((long)x << 32) | (uint)z;
    public static (int X, int Z) Unpack(long key) => ((int)(key >> 32), (int)(key & 0xffffffff));

    public IReadOnlySet<long>? Cells(string mapId, string layerId) =>
        _cells.TryGetValue((mapId, layerId), out var cells) ? cells : null;

    /// <summary>Reveals every cell whose centre lies within <see cref="RevealRadius"/> of the player.</summary>
    public IReadOnlyList<(int X, int Z)> Reveal(string mapId, string layerId, Vector3 position)
    {
        if (!_cells.TryGetValue((mapId, layerId), out var cells)) _cells[(mapId, layerId)] = cells = [];
        var added = new List<(int, int)>();
        int span = (int)MathF.Ceiling(RevealRadius / CellSize);
        int cx = (int)MathF.Floor(position.X / CellSize), cz = (int)MathF.Floor(position.Z / CellSize);
        const float radiusSquared = RevealRadius * RevealRadius;
        for (int z = cz - span; z <= cz + span; z++)
        for (int x = cx - span; x <= cx + span; x++)
        {
            var dx = (x + 0.5f) * CellSize - position.X;
            var dz = (z + 0.5f) * CellSize - position.Z;
            if (dx * dx + dz * dz > radiusSquared) continue;
            if (cells.Add(Key(x, z))) added.Add((x, z));
        }
        if (added.Count > 0) Version++;
        return added;
    }

    public void Add(string mapId, string layerId, int x, int z)
    {
        if (!_cells.TryGetValue((mapId, layerId), out var cells)) _cells[(mapId, layerId)] = cells = [];
        if (cells.Add(Key(x, z))) Version++;
    }

    public void Clear()
    {
        _cells.Clear();
        Version++;
    }
}
