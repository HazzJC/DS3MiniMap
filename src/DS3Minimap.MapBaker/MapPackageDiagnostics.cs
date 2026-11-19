using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DS3Minimap.Core;

namespace DS3Minimap.MapBaker;

internal static class MapPackageDiagnostics
{
    internal static async Task<MapValidationReport> ValidateAndWritePreviewsAsync(MapPackage package, string directory)
    {
        Directory.CreateDirectory(directory);
        var combinedDirectory = Path.Combine(directory, "combined");
        var layerDirectory = Path.Combine(directory, "layers");
        var obsoleteContextDirectory = Path.Combine(directory, "context");
        if (Directory.Exists(combinedDirectory)) Directory.Delete(combinedDirectory, true);
        if (Directory.Exists(layerDirectory)) Directory.Delete(layerDirectory, true);
        if (Directory.Exists(obsoleteContextDirectory)) Directory.Delete(obsoleteContextDirectory, true);
        Directory.CreateDirectory(combinedDirectory);
        Directory.CreateDirectory(layerDirectory);
        var issues = new List<string>();
        var regions = new List<RegionValidation>();

        if (package.SchemaVersion != MapPackage.CurrentSchemaVersion)
            issues.Add($"Schema version {package.SchemaVersion} is unsupported.");
        if (package.GameVersion != "1.15.2") issues.Add($"Unexpected game version {package.GameVersion}.");

        var expectedIds = MapCatalog.Maps.Where(map => map.Expansion is null || package.Archives?.Contains(map.Expansion) == true)
            .Select(map => map.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in expectedIds)
            if (package.Regions.All(region => region.MapId != expected)) issues.Add($"Missing required map {expected}.");
        foreach (var extra in package.Regions.Where(region => !expectedIds.Contains(region.MapId)))
            issues.Add($"Unexpected map {extra.MapId}.");

        foreach (var region in package.Regions.OrderBy(region => region.MapId, StringComparer.Ordinal))
        {
            var points = region.Layers.SelectMany(layer => layer.Polygons).SelectMany(polygon => polygon.Points).ToArray();
            var polygons = region.Layers.Sum(layer => layer.Polygons.Count);
            var invalidPolygons = region.Layers.SelectMany(layer => layer.Polygons).Count(polygon => polygon.Points.Count < 3);
            var invalidCoordinates = points.Count(point => !float.IsFinite(point.X) || !float.IsFinite(point.Z) || Math.Abs(point.X) > 1_000_000 || Math.Abs(point.Z) > 1_000_000);
            var markers = region.Markers ?? [];
            var bonfireCount = markers.Count(marker => marker.Kind == MapMarkerKind.Bonfire);
            var itemCount = markers.Count(marker => marker.Kind == MapMarkerKind.Item);
            var invalidMarkers = markers.Count(marker => !float.IsFinite(marker.X) || !float.IsFinite(marker.Y) || !float.IsFinite(marker.Z)
                || region.Layers.All(layer => layer.Id != marker.LayerId)
                || (marker.Kind == MapMarkerKind.Item && marker.EventFlagId <= 0));
            var minX = points.Length == 0 ? 0 : points.Min(point => point.X);
            var maxX = points.Length == 0 ? 0 : points.Max(point => point.X);
            var minZ = points.Length == 0 ? 0 : points.Min(point => point.Z);
            var maxZ = points.Length == 0 ? 0 : points.Max(point => point.Z);
            var minY = region.Layers.Count == 0 ? 0 : region.Layers.Min(layer => layer.MinimumY);
            var maxY = region.Layers.Count == 0 ? 0 : region.Layers.Max(layer => layer.MaximumY);

            if (region.Layers.Count == 0) issues.Add($"{region.MapId} has no elevation layers.");
            var groundCells = region.Layers.SelectMany(l => l.Ground ?? []).Sum(run => run.EndX - run.X);
            if (groundCells <= 0) issues.Add($"{region.MapId} has no navigation-aligned collision ground.");
            if (polygons < (MapCatalog.Maps.Any(m => m.Id == region.MapId && m.Expansion == null) ? 100 : 1)) issues.Add($"{region.MapId} has suspiciously few polygons ({polygons}).");
            if (invalidPolygons > 0) issues.Add($"{region.MapId} has {invalidPolygons} polygons with fewer than three points.");
            if (invalidCoordinates > 0) issues.Add($"{region.MapId} has {invalidCoordinates} invalid coordinates.");
            if (maxX - minX < 10 || maxZ - minZ < 10) issues.Add($"{region.MapId} has a suspiciously small X/Z extent.");
            if (invalidMarkers > 0) issues.Add($"{region.MapId} has {invalidMarkers} invalid markers.");

            var unaligned = markers.Where(marker =>
            {
                var layer = region.Layers.FirstOrDefault(l => l.Id == marker.LayerId);
                if (layer?.Ground is not { } ground) return true;
                const float unit = CollisionRun.CellSize;
                const float tolerance = 2;
                for (int i = CollisionRun.FirstRow(ground, (int)MathF.Floor((marker.Z - tolerance) / unit));
                     i < ground.Count && ground[i].Z * unit <= marker.Z + tolerance; i++)
                {
                    var run = ground[i];
                    if (MathF.Abs(run.Y * unit - marker.Y) <= tolerance && run.X * unit <= marker.X + tolerance
                        && run.EndX * unit >= marker.X - tolerance) return false;
                }
                return true;
            }).Select(marker => marker.Id).ToArray();

            foreach (var marker in markers.Where(m => m.Kind == MapMarkerKind.Bonfire && unaligned.Contains(m.Id)))
                issues.Add($"{region.MapId} bonfire {marker.Id} ({marker.Label}) needs review: no collision ground within 2 units on {marker.LayerId}.");

            var result = new RegionValidation(region.MapId, region.DisplayName, region.Layers.Count, polygons, points.Length,
                minX, maxX, minZ, maxZ, minY, maxY, invalidPolygons, invalidCoordinates, bonfireCount, itemCount, invalidMarkers, groundCells, unaligned);
            regions.Add(result);
            await WriteSvgAsync(region, result, Path.Combine(combinedDirectory, region.MapId + ".svg"), region.Layers);
            foreach (var layer in region.Layers)
                await WriteSvgAsync(region, result, Path.Combine(layerDirectory, region.MapId + "-" + layer.Id + ".svg"), [layer]);
        }

        var report = new MapValidationReport(package.SchemaVersion, package.GameVersion, package.Regions.Count,
            regions.Sum(region => region.PolygonCount), regions.Sum(region => region.PointCount),
            regions.Sum(region => region.BonfireCount), regions.Sum(region => region.ItemCount), issues.Count == 0, issues, regions);
        if (report.TotalBonfires < 20) issues.Add($"Only {report.TotalBonfires} base-game bonfires were resolved.");
        if (report.TotalItems < 200) issues.Add($"Only {report.TotalItems} tracked base-game pickups were resolved.");
        report = report with { IsValid = issues.Count == 0 };
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        await File.WriteAllTextAsync(Path.Combine(directory, "validation-report.json"), JsonSerializer.Serialize(report, jsonOptions));
        await WriteReadmeAsync(package, directory);
        await WriteIndexAsync(report, directory);
        return report;
    }

    private static async Task WriteSvgAsync(MapRegion region, RegionValidation stats, string path, IReadOnlyList<MapLayer> layers)
    {
        var width = Math.Max(stats.MaximumX - stats.MinimumX, 1);
        var height = Math.Max(stats.MaximumZ - stats.MinimumZ, 1);
        var margin = Math.Max(width, height) * 0.02f;
        var stroke = Math.Max(width, height) / 3500f;
        var palette = new[] { "#6da9d2", "#88b86d", "#d1aa62", "#bf7f93", "#8e80c5", "#63b7ad", "#c8895c", "#7b9fc9" };

        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20);
        await writer.WriteLineAsync("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        await writer.WriteLineAsync(FormattableString.Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{stats.MinimumX - margin} {stats.MinimumZ - margin} {width + margin * 2} {height + margin * 2}\">"));
        await writer.WriteLineAsync("<rect width=\"100%\" height=\"100%\" fill=\"#121212\"/>");
        await writer.WriteLineAsync($"<title>{Escape(region.MapId + " - " + region.DisplayName)}</title>");
        await writer.WriteLineAsync("""
            <defs>
              <filter id="ground-clouds" x="-12%" y="-12%" width="124%" height="124%" color-interpolation-filters="sRGB">
                <feTurbulence type="fractalNoise" baseFrequency="0.012 0.016" numOctaves="4" seed="47" result="clouds"/>
                <feColorMatrix in="clouds" type="saturate" values="0" result="grey-clouds"/>
                <feComponentTransfer in="grey-clouds" result="soft-clouds"><feFuncA type="linear" slope="0.38"/></feComponentTransfer>
                <feComposite in="soft-clouds" in2="SourceGraphic" operator="in" result="clipped-clouds"/>
                <feBlend in="SourceGraphic" in2="clipped-clouds" mode="soft-light"/>
              </filter>
              <radialGradient id="bonfire-aura"><stop offset="0" stop-color="#ffd995" stop-opacity=".82"/><stop offset=".42" stop-color="#ff8a35" stop-opacity=".34"/><stop offset="1" stop-color="#ff6325" stop-opacity="0"/></radialGradient>
              <radialGradient id="wisp-aura"><stop offset="0" stop-color="#fff" stop-opacity=".9"/><stop offset=".35" stop-color="#d9f2ff" stop-opacity=".36"/><stop offset="1" stop-color="#9edcff" stop-opacity="0"/></radialGradient>
              <filter id="marker-glow" x="-80%" y="-80%" width="260%" height="260%"><feGaussianBlur stdDeviation=".75" result="blur"/><feMerge><feMergeNode in="blur"/><feMergeNode in="SourceGraphic"/></feMerge></filter>
              <g id="bonfire-marker">
                <circle r="11" fill="url(#bonfire-aura)"/>
                <path d="M-7 6 Q0 10 7 6 M-6 5 L3 1 M6 5 L-2 0" fill="none" stroke="#130c07" stroke-width="3.5" stroke-linecap="round"/>
                <path d="M-7 6 Q0 10 7 6 M-6 5 L3 1 M6 5 L-2 0" fill="none" stroke="#ff9b45" stroke-width="1.5" stroke-linecap="round"/>
                <path d="M-1 5 C-7 1-3-3-1-5 C-2-1 2-3 2-8 C7-2 5 3 1 6 Z" fill="#f88938" stroke="#160b04" stroke-width="2" stroke-linejoin="round"/>
                <path d="M0 4 C-2 1 1-1 .5-4.5 C4-.5 3 3 0 4 Z" fill="#ffe7a2"/>
                <path d="M2.7-9 L-.3 5.5 M-2.1-.2 L3.1.9" fill="none" stroke="#090706" stroke-width="3.4" stroke-linecap="round"/>
                <path d="M2.7-9 L-.3 5.5 M-2.1-.2 L3.1.9" fill="none" stroke="#ded1ad" stroke-width="1.2" stroke-linecap="round"/>
              </g>
              <g id="item-wisp">
                <ellipse cx="0" cy="0" rx="8" ry="10" fill="url(#wisp-aura)"/>
                <path d="M-1.8 4.5 C4.5 1.5-3.5-1 1.4-4.1 C4.2-5.9 1.8-7.2 .2-8.6" fill="none" stroke="#071017" stroke-width="3.5" stroke-linecap="round"/>
                <path d="M-1.8 4.5 C4.5 1.5-3.5-1 1.4-4.1 C4.2-5.9 1.8-7.2 .2-8.6" fill="none" stroke="#d9f2ff" stroke-width="1.35" stroke-linecap="round"/>
                <circle cx="-1.8" cy="4.8" r="2.3" fill="#071017"/><circle cx="-1.8" cy="4.8" r="1.45" fill="#fff"/>
                <circle cx="3.8" cy="-2.8" r=".7" fill="#d9f2ff"/><circle cx="-3.4" cy="-.8" r=".5" fill="#d9f2ff"/>
              </g>
            </defs>
            """);
        for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
        {
            var layer = layers[layerIndex];
            var color = palette[region.Layers.ToList().IndexOf(layer) % palette.Length];
            await writer.WriteAsync("<path fill=\"#77716b\" d=\"");
            foreach (var run in layer.Ground ?? [])
                await writer.WriteAsync(FormattableString.Invariant($"M{run.X * CollisionRun.CellSize} {run.Z * CollisionRun.CellSize}h{(run.EndX - run.X) * CollisionRun.CellSize}v0.5H{run.X * CollisionRun.CellSize}Z"));
            await writer.WriteLineAsync("\"/>");
            await writer.WriteLineAsync(FormattableString.Invariant($"<g id=\"{Escape(layer.Id)}\" fill=\"{color}\" fill-opacity=\"0.62\" stroke=\"#17130f\" stroke-width=\"{stroke}\" stroke-linejoin=\"round\" filter=\"url(#ground-clouds)\">"));
            foreach (var polygon in layer.Polygons)
            {
                if (polygon.Points.Count < 3) continue;
                await writer.WriteAsync("<polygon points=\"");
                foreach (var point in polygon.Points)
                    await writer.WriteAsync(string.Create(CultureInfo.InvariantCulture, $"{point.X},{point.Z} "));
                await writer.WriteLineAsync("\"/>");
            }
            await writer.WriteLineAsync("</g>");
        }
        var layerIds = layers.Select(layer => layer.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var marker in (region.Markers ?? []).Where(marker => layerIds.Contains(marker.LayerId)))
        {
            var size = Math.Max(width, height) / 180f;
            if (marker.Kind == MapMarkerKind.Bonfire)
                await writer.WriteLineAsync(FormattableString.Invariant($"<g transform=\"translate({marker.X} {marker.Z}) scale({size / 10f})\" filter=\"url(#marker-glow)\"><title>{Escape(marker.Label)}</title><use href=\"#bonfire-marker\"/></g>"));
            else
                await writer.WriteLineAsync(FormattableString.Invariant($"<g transform=\"translate({marker.X} {marker.Z}) scale({size / 10f})\" filter=\"url(#marker-glow)\"><title>{Escape(marker.Label)} (flag {marker.EventFlagId})</title><use href=\"#item-wisp\"/></g>"));
        }
        await writer.WriteLineAsync("</svg>");
    }

    private static async Task WriteIndexAsync(MapValidationReport report, string directory)
    {
        var html = new StringBuilder("""
            <!doctype html><html><head><meta charset="utf-8"><title>DS3 Minimap map validation</title>
            <style>body{font-family:Segoe UI,sans-serif;background:#171717;color:#eee;margin:2rem}a{color:#9dd5ff}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(360px,1fr));gap:1rem}.card{background:#242424;padding:1rem;border-radius:10px}img{width:100%;height:320px;object-fit:contain;background:#121212}code{color:#f0c674}.links{display:flex;flex-wrap:wrap;gap:.7rem}</style></head><body>
            """);
        html.Append("<h1>DS3 Minimap map validation</h1><p>Status: <strong>")
            .Append(report.IsValid ? "PASS" : "FAIL").Append("</strong> - ")
            .Append(report.TotalPolygons.ToString("N0", CultureInfo.InvariantCulture)).Append(" polygons across ")
            .Append(report.RegionCount).Append(" regions; ").Append(report.TotalBonfires).Append(" bonfires and ")
            .Append(report.TotalItems).Append(" tracked pickups.</p><p>Grey shows connected collision ground. Different colours represent cloud-textured elevation layers; flame-and-sword symbols are bonfires and pale wisps are pickup locations.</p><p><a href=\"validation-report.json\">Full JSON report</a> | <a href=\"README.md\">Review guide</a></p><div class=\"grid\">");
        foreach (var region in report.Regions)
        {
            html.Append("<section class=\"card\"><h2>").Append(Escape(region.MapId)).Append("</h2><p>")
                .Append(Escape(region.DisplayName)).Append("<br>")
                .Append(region.LayerCount).Append(" layers; ").Append(region.PolygonCount.ToString("N0", CultureInfo.InvariantCulture)).Append(" polygons; ")
                .Append(region.BonfireCount).Append(" bonfires; ").Append(region.ItemCount).Append(" pickups</p>")
                .Append("<a href=\"combined/").Append(region.MapId).Append(".svg\"><img src=\"combined/").Append(region.MapId).Append(".svg\" alt=\"")
                .Append(region.MapId).Append(" preview\"></a><p class=\"links\">");
            for (var layer = 0; layer < region.LayerCount; layer++)
                html.Append("<a href=\"layers/").Append(region.MapId).Append("-floor-").Append(layer).Append(".svg\">floor ").Append(layer + 1).Append("</a>");
            html.Append("</p></section>");
        }
        html.Append("</div></body></html>");
        await File.WriteAllTextAsync(Path.Combine(directory, "index.html"), html.ToString(), new UTF8Encoding(false));
    }

    private static async Task WriteReadmeAsync(MapPackage package, string directory)
    {
        var text = $"""
            # DS3 Minimap generated-map review

            Generated from the locally installed Dark Souls III {package.GameVersion} files.

            - `combined/` overlays every extracted AI-navigation floor in a different colour.
            - `layers/` contains one identically framed SVG for each generated elevation layer.
            - `validation-report.json` contains dimensions and geometry counts for automated comparison.

            Grey ground is sampled from upward-facing static collision connected to navigation geometry. Elevation colours show the underlying navigation layer. Dynamic platforms and isolated collision can be absent.

            Open `index.html` in a browser. SVG files can be opened in browsers, Inkscape, Illustrator, or image-comparison tools without losing detail.
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, "README.md"), text, new UTF8Encoding(false));
    }

    private static string Escape(string value) => System.Net.WebUtility.HtmlEncode(value);

}

internal sealed record MapValidationReport(int SchemaVersion, string GameVersion, int RegionCount, int TotalPolygons,
    int TotalPoints, int TotalBonfires, int TotalItems, bool IsValid, IReadOnlyList<string> Issues, IReadOnlyList<RegionValidation> Regions);

internal sealed record RegionValidation(string MapId, string DisplayName, int LayerCount, int PolygonCount, int PointCount,
    float MinimumX, float MaximumX, float MinimumZ, float MaximumZ, float MinimumY, float MaximumY,
    int InvalidPolygonCount, int InvalidCoordinateCount, int BonfireCount, int ItemCount, int InvalidMarkerCount, int CollisionCells, IReadOnlyList<string> MarkersOutsideGroundTolerance);
