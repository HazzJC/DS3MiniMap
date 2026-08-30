using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using DS3Minimap.Core;
using SoulsFormats;

namespace DS3Minimap.MapBaker;

internal static class Program
{
    private static readonly (string Id, string Name)[] BaseMaps =
    [
        ("m30_00", "High Wall of Lothric / Lothric Castle / Grand Archives"),
        ("m30_01", "Consumed King's Garden / Untended Graves"),
        ("m31_00", "Undead Settlement"),
        ("m32_00", "Archdragon Peak"),
        ("m33_00", "Road of Sacrifices / Farron Keep"),
        ("m34_01", "Grand Archives interior"),
        ("m35_00", "Cathedral of the Deep"),
        ("m37_00", "Irithyll of the Boreal Valley / Anor Londo"),
        ("m38_00", "Catacombs of Carthus / Smouldering Lake"),
        ("m39_00", "Irithyll Dungeon / Profaned Capital"),
        ("m40_00", "Cemetery of Ash / Firelink Shrine"),
        ("m41_00", "Kiln of the First Flame")
    ];

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var gameDir = ValueAfter(args, "--game-dir") ?? throw new ArgumentException("Usage: --game-dir <DS3 Game directory> [--output <file>]");
            var output = ValueAfter(args, "--output") ?? AppPaths.MapPackage;
            var previewDirectory = ValueAfter(args, "--preview-dir") ?? Path.Combine(Path.GetDirectoryName(output)!, "diagnostics");
            var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
            ValidateGame(gameDir);
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
            MapPackage package;
            if (!force && IsCurrent(output, gameDir))
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
                foreach (var (id, name) in BaseMaps)
                {
                    Console.WriteLine($"Baking {id}: {name}");
                    var region = baker.Bake(id, name);
                    var msb = MSB3.Read(archives.Read($"/map/mapstudio/{id}_00_00.msb.dcx"));
                    var markers = markerBaker.Bake(id, msb, region);
                    Console.WriteLine($"  markers: {markers.Count(marker => marker.Kind == MapMarkerKind.Bonfire)} bonfires, {markers.Count(marker => marker.Kind == MapMarkerKind.Item)} tracked pickups");
                    regions.Add(region with { Markers = markers });
                }
                package = new MapPackage(MapPackage.CurrentSchemaVersion, "1.15.2", DateTimeOffset.UnixEpoch, regions);
                await MapPackageSerializer.WriteAtomicAsync(package, output);
                Console.WriteLine($"Wrote {regions.Count} base-game regions to {output}");
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
            return report.IsValid ? 0 : 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Map bake failed: " + exception.Message);
            return 1;
        }
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
            if (package.SchemaVersion != MapPackage.CurrentSchemaVersion) return false;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return false;
        }
        var packageTime = File.GetLastWriteTimeUtc(output);
        return Enumerable.Range(0, 6)
            .SelectMany(index => new[] { Path.Combine(gameDir, $"Data{index}.bhd"), Path.Combine(gameDir, $"Data{index}.bdt") })
            .All(path => File.Exists(path) && File.GetLastWriteTimeUtc(path) <= packageTime);
    }
}
