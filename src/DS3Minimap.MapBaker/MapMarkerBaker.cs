using System.Numerics;
using System.Text.Json;
using DS3Minimap.Core;
using SoulsFormats;

namespace DS3Minimap.MapBaker;

/// <summary>
/// Extracts marker metadata without unpacking or changing the game. Treasure
/// placement comes from the map MSB, pickup flags from ItemLotParam_map, and
/// bonfire placement from BonfireWarpParam entity IDs matched back to MSB parts.
/// </summary>
internal sealed class MapMarkerBaker
{
    private readonly PARAM _itemLots;
    private readonly PARAM _bonfires;
    private readonly IReadOnlyDictionary<int, string> _itemNames;
    private readonly IReadOnlyDictionary<int, string> _bonfireNames;

    internal MapMarkerBaker(string gameDirectory, string assets)
    {
        var data0 = Path.Combine(gameDirectory, "Data0.bdt");
        if (!File.Exists(data0)) throw new FileNotFoundException("Data0.bdt was not found; the DS3 regulation cannot be read.", data0);

        var regulation = SFUtil.DecryptDS3Regulation(data0);
        _itemLots = ReadParam(regulation, "ItemLotParam.param", Path.Combine(assets, "Defs", "ItemLotParam.xml"));
        _bonfires = ReadParam(regulation, "BonfireWarpParam.param", Path.Combine(assets, "Defs", "BonfireWarpParam.xml"));
        _itemNames = ReadNames(Path.Combine(assets, "Param Row Names", "English", "ItemLotParam.json"));
        _bonfireNames = ReadNames(Path.Combine(assets, "Param Row Names", "English", "BonfireWarpParam.json"));
    }

    internal IReadOnlyList<MapMarker> Bake(string mapId, MSB3 msb, MapRegion region)
    {
        var parts = msb.Parts.GetEntries();
        var partsByName = parts
            .Where(part => !string.IsNullOrWhiteSpace(part.Name))
            .GroupBy(part => part.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var partsByEntity = parts
            .Where(part => part.EntityID > 0)
            .GroupBy(part => part.EntityID)
            .ToDictionary(group => group.Key, group => group.First());
        var result = new List<MapMarker>();

        foreach (var treasure in msb.Events.Treasures)
        {
            if (string.IsNullOrWhiteSpace(treasure.TreasurePartName)
                || !partsByName.TryGetValue(treasure.TreasurePartName, out var part)) continue;

            foreach (var lotId in new[] { treasure.ItemLot1, treasure.ItemLot2 }.Where(id => id > 0).Distinct())
            {
                var row = _itemLots.Rows.FirstOrDefault(candidate => candidate.ID == lotId);
                if (row is null) continue;
                var flagId = CellInt(row, "getItemFlagId");
                // A pickup with no row-level get flag cannot be classified as
                // collected by read-only telemetry, so do not make a false claim.
                if (flagId <= 0) continue;
                var label = _itemNames.TryGetValue(lotId, out var name) ? name : $"Item lot {lotId}";
                result.Add(CreateMarker(region, $"item-{mapId}-{treasure.Name}-{lotId}", MapMarkerKind.Item,
                    part.Position, label, flagId, lotId));
            }
        }

        foreach (var row in _bonfires.Rows)
        {
            var entityId = CellInt(row, "bonfireEntityId");
            if (entityId <= 0 || !partsByEntity.TryGetValue(entityId, out var part)) continue;
            var flagId = CellInt(row, "eventflagId");
            var label = _bonfireNames.TryGetValue(row.ID, out var name) ? name : $"Bonfire {row.ID}";
            result.Add(CreateMarker(region, $"bonfire-{mapId}-{row.ID}", MapMarkerKind.Bonfire,
                part.Position, label, flagId, -1));
        }

        return result
            .GroupBy(marker => (marker.Kind, marker.EventFlagId, X: MathF.Round(marker.X, 2), Y: MathF.Round(marker.Y, 2), Z: MathF.Round(marker.Z, 2)))
            .Select(group => group.First())
            .OrderBy(marker => marker.Kind)
            .ThenBy(marker => marker.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static MapMarker CreateMarker(MapRegion region, string id, MapMarkerKind kind, Vector3 position,
        string label, int eventFlagId, int itemLotId)
    {
        var layerId = LayerSelector.Select(region, position)?.Id ?? region.Layers.FirstOrDefault()?.Id ?? "default";
        return new MapMarker(id, kind, position.X, position.Y, position.Z, layerId, label, eventFlagId, itemLotId);
    }

    private static PARAM ReadParam(BND4 regulation, string fileName, string definitionPath)
    {
        if (!File.Exists(definitionPath))
            throw new FileNotFoundException("Pinned DS3 parameter definitions are missing. Run scripts/bootstrap.ps1.", definitionPath);
        var file = regulation.Files.FirstOrDefault(candidate =>
            candidate.Name?.Replace('\\', '/').EndsWith('/' + fileName, StringComparison.OrdinalIgnoreCase) == true
            || string.Equals(candidate.Name, fileName, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            var related = regulation.Files.Select(candidate => candidate.Name ?? $"#{candidate.ID}")
                .Where(name => name.Contains("ItemLot", StringComparison.OrdinalIgnoreCase)).ToArray();
            var available = string.Join(", ", related.Length > 0 ? related : regulation.Files.Select(candidate => candidate.Name ?? $"#{candidate.ID}").Take(20));
            throw new InvalidDataException($"{fileName} was not present in the DS3 regulation. First entries: {available}");
        }
        var param = PARAM.Read(file.Bytes);
        param.ApplyParamdef(PARAMDEF.XmlDeserialize(definitionPath));
        return param;
    }

    private static IReadOnlyDictionary<int, string> ReadNames(string path)
    {
        if (!File.Exists(path)) return new Dictionary<int, string>();
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var result = new Dictionary<int, string>();
        foreach (var entry in document.RootElement.GetProperty("Entries").EnumerateArray())
        {
            var id = entry.GetProperty("ID").GetInt32();
            var names = entry.GetProperty("Entries").EnumerateArray()
                .Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (names.Length > 0) result[id] = names[0]!;
        }
        return result;
    }

    private static int CellInt(PARAM.Row row, string name) =>
        row[name]?.Value is null ? -1 : Convert.ToInt32(row[name].Value);
}
