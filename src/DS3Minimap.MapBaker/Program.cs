using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using DS3Minimap.Core;
using SoulsFormats;

namespace DS3Minimap.MapBaker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var output = ValueAfter(args, "--output") ?? AppPaths.MapPackage;
            if (ValueAfter(args, "--audit-trail") is { } trailDatabase)
            {
                TrailCoverage.Report(await MapPackageSerializer.ReadAsync(output), trailDatabase);
                return 0;
            }
            var gameDir = ValueAfter(args, "--game-dir") ?? throw new ArgumentException("Usage: --game-dir <DS3 Game directory> [--output <file>]");
            var previewDirectory = ValueAfter(args, "--preview-dir") ?? Path.Combine(Path.GetDirectoryName(output)!, "diagnostics");
            var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
            ValidateGame(gameDir);
            if (ValueAfter(args, "--bake-materials") is { } materialMap)
            {
                var maps = await MapPackageSerializer.ReadAsync(output);
                var region = maps.Regions.Single(r => r.MapId == materialMap);
                using var materialsArchive = new Ds3ArchiveReader(gameDir);
                var atlas = new GroundMaterialBaker(region).Bake(materialsArchive, region.MapId, maps.GeneratedAt);
                if (atlas.Tiles.Count == 0) throw new InvalidDataException($"No ground materials resolved for {materialMap}.");
                await atlas.SaveAsync(ValueAfter(args, "--material-output") ?? GroundAtlas.CachePath(region.MapId, output));
                if (ValueAfter(args, "--dump-material-tiles") is { } dumpDirectory) DumpMaterialTiles(atlas, dumpDirectory);
                return 0;
            }
            if (args.Contains("--materials-only", StringComparer.OrdinalIgnoreCase))
            {
                // Existing installs: the map package is current, only textures are missing.
                await BakeMaterialsAsync(await MapPackageSerializer.ReadAsync(output), gameDir, output, force);
                return 0;
            }
            var dumpMap = ValueAfter(args, "--dump-transforms");
            if (dumpMap is not null)
            {
                using var transformArchives = new Ds3ArchiveReader(gameDir);
                new NavmeshBaker(transformArchives).DumpInstanceTransforms(dumpMap);
                return 0;
            }
            var dumpPieces = ValueAfter(args, "--dump-pieces");
            if (dumpPieces is not null)
            {
                using var pieceArchives = new Ds3ArchiveReader(gameDir);
                var resource = dumpPieces + "_00_00";
                var msb = MSB3.Read(pieceArchives.Read($"/map/mapstudio/{resource}.msb.dcx"));
                Console.WriteLine($"Models: {msb.Models.MapPieces.Count}; parts: {msb.Parts.MapPieces.Count}");
                foreach (var model in msb.Models.MapPieces.Take(20)) Console.WriteLine($"MODEL {model.Name} sib={model.SibPath}");
                foreach (var part in msb.Parts.MapPieces.Take(30)) Console.WriteLine($"PART {part.Name} model={part.ModelName} pos={part.Position} rot={part.Rotation} scale={part.Scale}");
                var sampleModel = msb.Models.MapPieces.First();
                var samplePath = $"/map/{resource}/{resource}_{sampleModel.Name[1..]}.mapbnd.dcx";
                var sampleBinder = BND4.Read(DCX.Decompress(pieceArchives.Read(samplePath)));
                foreach (var file in sampleBinder.Files) Console.WriteLine($"FILE {file.Name} bytes={file.Bytes.Length}");
                return 0;
            }
            var installed = MapCatalog.InstalledArchives(gameDir);
            var rebuild = force || !IsCurrent(output, gameDir);
            MapPackage package;
            if (!rebuild)
            {
                Console.WriteLine($"Map package is current: {output}");
                package = await MapPackageSerializer.ReadAsync(output);
            }
            else
            {
                using var archives = new Ds3ArchiveReader(gameDir);
                var baker = new NavmeshBaker(archives);
                var markerBaker = new MapMarkerBaker(gameDir, LocateMarkerAssets());
                var regions = new List<MapRegion>();
                var ground = new CollisionGroundBaker(archives);
                foreach (var (id, name, expansion) in MapCatalog.Maps)
                {
                    if (expansion is not null && !installed.Contains(expansion)) continue;
                    Console.WriteLine($"Baking {id}: {name}");
                    var region = baker.Bake(id, name);
                    var msb = MSB3.Read(archives.Read($"/map/mapstudio/{id}_00_00.msb.dcx"));
                    region = ground.Bake(region, msb);
                    var markers = markerBaker.Bake(id, msb, region);
                    Console.WriteLine($"  markers: {markers.Count(marker => marker.Kind == MapMarkerKind.Bonfire)} bonfires, {markers.Count(marker => marker.Kind == MapMarkerKind.Item)} tracked pickups");
                    regions.Add(region with { Markers = markers });
                }
                package = new MapPackage(MapPackage.CurrentSchemaVersion, "1.15.2", DateTimeOffset.UtcNow, regions, installed);
            }
            var inspectPoint = ValueAfter(args, "--inspect-point");
            if (inspectPoint is not null)
            {
                var mapId = ValueAfter(args, "--inspect-map") ?? throw new ArgumentException("--inspect-point also requires --inspect-map <map ID>.");
                var components = inspectPoint.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (components.Length != 3) throw new ArgumentException("--inspect-point must be X,Y,Z.");
                var position = new Vector3(
                    float.Parse(components[0], CultureInfo.InvariantCulture),
                    float.Parse(components[1], CultureInfo.InvariantCulture),
                    float.Parse(components[2], CultureInfo.InvariantCulture));
                var region = package.Regions.FirstOrDefault(item => item.MapId == mapId)
                    ?? throw new ArgumentException($"Map {mapId} is not present in the package.");
                Console.WriteLine($"Layer matches for {mapId} at X={position.X:F3}, Y={position.Y:F3}, Z={position.Z:F3}:");
                foreach (var match in LayerSelector.Rank(region, position))
                    Console.WriteLine($"  {match.Layer.DisplayName,-10} horizontal={match.HorizontalDistance,8:F3} vertical={match.VerticalDistance,8:F3} score={match.Score,8:F3}");
            }
            Console.WriteLine("Validating geometry and writing top-down previews...");
            var report = await MapPackageDiagnostics.ValidateAndWritePreviewsAsync(package, previewDirectory);
            Console.WriteLine($"Validation {(report.IsValid ? "passed" : "failed")}: {report.TotalPolygons:N0} polygons, {report.TotalPoints:N0} points.");
            Console.WriteLine($"Preview index: {Path.Combine(previewDirectory, "index.html")}");
            foreach (var issue in report.Issues) Console.Error.WriteLine("  " + issue);
            if (report.IsValid && rebuild)
            {
                await MapPackageSerializer.WriteAtomicAsync(package, output);
                Console.WriteLine($"Wrote {package.Regions.Count} regions to {output}");
            }
            if (report.IsValid && !args.Contains("--skip-materials", StringComparer.OrdinalIgnoreCase))
                await BakeMaterialsAsync(package, gameDir, output, force);
            return report.IsValid ? 0 : 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Map bake failed: " + exception);
            return 1;
        }
    }

    /// <summary>
    /// Bakes the top-down ground textures for every map whose atlas is missing or
    /// stale. A map that cannot be textured keeps the flat ground fill; it never
    /// fails map preparation as a whole.
    /// </summary>
    private static async Task BakeMaterialsAsync(MapPackage package, string gameDir, string output, bool force)
    {
        var pending = new List<MapRegion>();
        foreach (var region in package.Regions)
            if (force || !await GroundAtlas.IsCurrentAsync(GroundAtlas.CachePath(region.MapId, output), region.MapId, package.GeneratedAt))
                pending.Add(region);
        if (pending.Count == 0)
        {
            Console.WriteLine("Ground textures are current.");
            return;
        }
        Console.WriteLine($"Baking ground textures for {pending.Count} maps...");
        using var archives = new Ds3ArchiveReader(gameDir);
        int done = 0, failed = 0;
        foreach (var region in pending)
        {
            Console.WriteLine($"Texturing {region.MapId}: {region.DisplayName} ({done + failed + 1}/{pending.Count})");
            try
            {
                var atlas = new GroundMaterialBaker(region).Bake(archives, region.MapId, package.GeneratedAt);
                if (atlas.Tiles.Count == 0) { Console.WriteLine("  no textured ground; flat shading will be used"); failed++; continue; }
                await atlas.SaveAsync(GroundAtlas.CachePath(region.MapId, output));
                done++;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Textures are optional; a problem in one map must not stop the rest.
                var reason = exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0] : exception;
                Console.WriteLine($"  ground textures skipped: {reason.GetType().Name}: {reason.Message}");
                failed++;
            }
            finally { GC.Collect(); }
        }
        Console.WriteLine($"Ground textures: {done}/{pending.Count} maps baked" + (failed > 0 ? $", {failed} use flat shading." : "."));
    }

    private static void DumpMaterialTiles(GroundAtlas atlas, string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var tile in atlas.Tiles)
            File.WriteAllBytes(Path.Combine(directory, $"{tile.Floor}_{tile.X}_{tile.Z}.webp"), tile.WebP);
        // One stitched PNG per floor, at half resolution, for visual review.
        const int half = GroundTile.Resolution / 2;
        foreach (var floor in atlas.Tiles.GroupBy(tile => tile.Floor))
        {
            int minX = floor.Min(t => t.X), minZ = floor.Min(t => t.Z);
            int width = (floor.Max(t => t.X) - minX + 1) * half, height = (floor.Max(t => t.Z) - minZ + 1) * half;
            using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(width, height));
            surface.Canvas.Clear(new SkiaSharp.SKColor(18, 21, 24));
            foreach (var tile in floor)
            {
                using var image = SkiaSharp.SKImage.FromEncodedData(tile.WebP);
                surface.Canvas.DrawImage(image, SkiaSharp.SKRect.Create((tile.X - minX) * half, (tile.Z - minZ) * half, half, half),
                    new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.Linear));
            }
            using var snapshot = surface.Snapshot();
            using var png = snapshot.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(directory, $"mosaic-{floor.Key}.png"), png.ToArray());
        }
        Console.WriteLine($"Dumped {atlas.Tiles.Count} tiles to {directory}");
    }

    private static void ValidateGame(string gameDir)
    {
        var executable = Path.Combine(gameDir, "DarkSoulsIII.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("DarkSoulsIII.exe was not found.", executable);
        var version = FileVersionInfo.GetVersionInfo(executable).FileVersion;
        if (version is null || !version.StartsWith("1.15.2", StringComparison.Ordinal))
            throw new NotSupportedException($"DS3 version {version ?? "unknown"} is unsupported; expected 1.15.2.");
    }

    private static string? ValueAfter(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++) if (args[index] == name) return args[index + 1];
        return null;
    }

    private static string LocateMarkerAssets()
    {
        var installedAssets = Path.Combine(AppContext.BaseDirectory, "Assets", "PARAM", "DS3");
        if (File.Exists(Path.Combine(installedAssets, "Defs", "ItemLotParam.xml"))) return installedAssets;
        var developmentAssets = Path.Combine(Environment.CurrentDirectory, ".deps", "smithbox", "src", "Smithbox.Data", "Assets", "PARAM", "DS3");
        if (File.Exists(Path.Combine(developmentAssets, "Defs", "ItemLotParam.xml"))) return developmentAssets;
        throw new FileNotFoundException("The map baker's DS3 parameter definitions are missing. Reinstall DS3 Minimap or run scripts/bootstrap.ps1 for development.");
    }

    private static bool IsCurrent(string output, string gameDir)
    {
        if (!File.Exists(output)) return false;
        try
        {
            var package = MapPackageSerializer.ReadAsync(output).GetAwaiter().GetResult();
            return package.SchemaVersion == MapPackage.CurrentSchemaVersion && MapCatalog.MatchesInstallation(package, gameDir, File.GetLastWriteTimeUtc(output));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
