using System.Numerics;
using DS3Minimap.Core;
using DS3Minimap.Memory;
using Xunit;

namespace DS3Minimap.Tests;

public sealed class CoreTests
{
    [Fact]
    public void SignatureScannerSupportsWildcards()
    {
        var bytes = new byte[] { 0x90, 0x48, 0x8B, 0x05, 0x12, 0x34, 0xC3 };
        Assert.Equal(1, SignatureScanner.Find(bytes, new BytePattern("48 8B 05 ?? ?? C3")));
        Assert.Equal(-1, SignatureScanner.Find(bytes, new BytePattern("48 8B 06")));
    }

    [Theory]
    [InlineData(0, 0, 1, 51, "m51_01")]
    [InlineData(45, 0, 0, 0, "m45_00")]
    [InlineData(54, 0, 0, 0, "m54_00")]
    [InlineData(30, 0, 0, 0, "m30_00")]
    [InlineData(0, 30, 0, 0, "m30_00")]
    [InlineData(0, 0, 34, 1, "m34_01")]
    [InlineData(0, 0, 0, 41, "m41_00")]
    public void MapIdDecoderHandlesObservedByteOrders(byte a, byte b, byte c, byte d, string expected)
    {
        Assert.Equal(expected, Ds3TelemetrySource.DecodeMapId([a, b, c, d]));
    }

    [Fact]
    public void TrailRecorderSamplesAndSegmentsMovement()
    {
        var recorder = new TrailRecorder();
        var start = DateTimeOffset.UnixEpoch;
        Assert.NotNull(recorder.Observe(Telemetry(start, Vector3.Zero), "floor-0"));
        Assert.Null(recorder.Observe(Telemetry(start.AddMilliseconds(100), new Vector3(2, 0, 0)), "floor-0"));
        var moved = recorder.Observe(Telemetry(start.AddMilliseconds(300), new Vector3(2, 0, 0)), "floor-0");
        var teleported = recorder.Observe(Telemetry(start.AddMilliseconds(600), new Vector3(200, 0, 0)), "floor-0");
        Assert.NotNull(moved);
        Assert.NotNull(teleported);
        Assert.NotEqual(moved!.Segment, teleported!.Segment);
    }

    [Fact]
    public void LayerSelectorChoosesClosestElevation()
    {
        var region = new MapRegion("m30_00", "test",
        [
            new MapLayer("low", "Low", -10, 10, []),
            new MapLayer("high", "High", 40, 60, [])
        ]);
        Assert.Equal("low", LayerSelector.Select(region, 5)?.Id);
        Assert.Equal("high", LayerSelector.Select(region, 55)?.Id);
        Assert.Equal("high", LayerSelector.Select(region, 27)?.Id);
    }

    [Fact]
    public void LayerSelectorUsesHorizontalGeometryWhenElevationRangesOverlap()
    {
        static MapPolygon Square(string id, float x, float y) => new(id, y, y,
        [
            new MapPoint(x - 2, -2), new MapPoint(x + 2, -2),
            new MapPoint(x + 2, 2), new MapPoint(x - 2, 2)
        ]);
        var region = new MapRegion("m40_00", "test",
        [
            new MapLayer("near", "Near", -10, 10, [Square("near", 0, 5)]),
            new MapLayer("far", "Far", -10, 10, [Square("far", 50, 0)])
        ]);

        Assert.Equal("near", LayerSelector.Select(region, new Vector3(0, 0, 0))?.Id);
    }

    [Fact]
    public void LayerSelectorHysteresisPreventsFloorFlickerAtSeams()
    {
        static MapPolygon Square(string id, float x) => new(id, 0, 0,
        [new MapPoint(x - 1, -1), new MapPoint(x + 1, -1), new MapPoint(x + 1, 1), new MapPoint(x - 1, 1)]);
        var left = new MapLayer("left", "Left", -2, 2, [Square("left", -2)]);
        var right = new MapLayer("right", "Right", -2, 2, [Square("right", 2)]);
        var region = new MapRegion("m40_00", "test", [left, right]);

        Assert.Same(left, LayerSelector.SelectStable(region, new Vector3(0.25f, 0, 0), left));
        Assert.Same(right, LayerSelector.SelectStable(region, new Vector3(4, 0, 0), left));
    }

    [Fact]
    public void Ds3HeadingIsConvertedToTopDownScreenCoordinates()
    {
        Assert.Equal(MathF.PI, MapTransform.HeadingToScreenRadians(0), 5);
        Assert.Equal(MathF.PI / 2, MapTransform.HeadingToScreenRadians(MathF.PI / 2), 5);
        Assert.Equal(-MathF.PI, MapTransform.PlayerUpMapRotation(0), 5);
    }

    [Fact]
    public void HeadingTrackerFollowsObservedTravelDirection()
    {
        var tracker = new HeadingTracker();
        var start = Telemetry(DateTimeOffset.UnixEpoch, Vector3.Zero);
        tracker.Update(start);
        Assert.Equal(MathF.PI / 2, tracker.Update(Telemetry(DateTimeOffset.UnixEpoch.AddSeconds(1), new Vector3(1, 0, 0))), 5);
        Assert.Equal(MathF.PI, tracker.Update(Telemetry(DateTimeOffset.UnixEpoch.AddSeconds(2), new Vector3(1, 0, 1))), 5);
    }

    [Theory]
    [InlineData(40000123, 4, 0, 0, 0, 12, 0x00000010u)]
    [InlineData(14000001, 1, 40, 0, 0, 0, 0x40000000u)]
    [InlineData(54000010, 5, 40, 0, 0, 0, 0x00200000u)]
    public void EventFlagIdsDecodeToDs3BitStorage(int flagId, int major, int area, int areaBlock,
        int thousands, int wordOffset, uint mask)
    {
        var decoded = Ds3EventFlagBit.Decode(flagId);
        Assert.Equal(major, decoded.MajorGroup);
        Assert.Equal(area, decoded.Area);
        Assert.Equal(areaBlock, decoded.AreaBlock);
        Assert.Equal(thousands, decoded.ThousandsGroup);
        Assert.Equal(wordOffset, decoded.WordOffset);
        Assert.Equal(mask, decoded.Mask);
    }

    [Fact]
    public async Task MapPackageRoundTrips()
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"ds3-minimap-{Guid.NewGuid():N}.dsmaps");
        try
        {
            var package = new MapPackage(1, "1.15.2", DateTimeOffset.UnixEpoch,
            [new MapRegion("m40_00", "Firelink", [new MapLayer("main", "Main", -1, 1, [])], Markers:
                [new MapMarker("bonfire", MapMarkerKind.Bonfire, 1, 2, 3, "main", "Firelink Shrine", 14000000)])]);
            await MapPackageSerializer.WriteAtomicAsync(package, temporary, TestContext.Current.CancellationToken);
            var loaded = await MapPackageSerializer.ReadAsync(temporary, TestContext.Current.CancellationToken);
            Assert.Equal("m40_00", Assert.Single(loaded.Regions).MapId);
            Assert.Equal(MapMarkerKind.Bonfire, Assert.Single(Assert.Single(loaded.Regions).Markers!).Kind);
        }
        finally { File.Delete(temporary); }
    }

    [Fact]
    public async Task SettingsMigrationClampsTrailLifetime()
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"ds3-minimap-settings-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(temporary, """{"schemaVersion":2,"trailLifetimeMinutes":999,"fadeTrailWithAge":true,"mapTextureStrength":9,"layerTransitionMilliseconds":9000}""",
                TestContext.Current.CancellationToken);
            var settings = await new SettingsStore(temporary).LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal(180, settings.TrailLifetimeMinutes);
            Assert.True(settings.FadeTrailWithAge);
            Assert.Equal(1, settings.MapTextureStrength);
            Assert.Equal(1500, settings.LayerTransitionMilliseconds);
            Assert.True(settings.ShowBonfires);
            Assert.False(settings.ShowUncollectedItems);
        }
        finally { File.Delete(temporary); }
    }

    [Fact]
    public async Task SettingsFromVersionFiveGainTextureAndMemoryAidDefaults()
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"ds3-minimap-settings-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(temporary, """{"schemaVersion":5,"showBonfires":true,"textureToning":4,"fogOfWarStrength":-1,"trailStyle":0}""",
                TestContext.Current.CancellationToken);
            var settings = await new SettingsStore(temporary).LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal(TrailStyle.Soul, settings.TrailStyle);
            Assert.Equal(GroundStyle.RealTextures, settings.GroundStyle);
            Assert.Equal(1, settings.TextureToning);
            Assert.Equal(0, settings.FogOfWarStrength);
            Assert.True(settings.FogOfWar);
            Assert.True(settings.ShowUnlitBonfires);
            Assert.True(settings.ShowNearestBonfirePointer);
        }
        finally { File.Delete(temporary); }
    }

    [Fact]
    public void ExploredAreaRevealsCellsAroundThePlayerOnItsOwnFloor()
    {
        var area = new ExploredArea();
        var added = area.Reveal("m40_00", "floor-1", new Vector3(1, 0, 1));
        Assert.NotEmpty(added);
        var cells = area.Cells("m40_00", "floor-1")!;
        Assert.Contains(ExploredArea.Key(0, 0), cells);
        // Cells beyond the reveal radius stay hidden, and other floors are untouched.
        Assert.DoesNotContain(ExploredArea.Key(10, 0), cells);
        Assert.Null(area.Cells("m40_00", "floor-2"));
        var version = area.Version;
        Assert.Empty(area.Reveal("m40_00", "floor-1", new Vector3(1, 0, 1)));
        Assert.Equal(version, area.Version);
        Assert.Equal((-3, 7), ExploredArea.Unpack(ExploredArea.Key(-3, 7)));
    }

    [Fact]
    public async Task GroundAtlasRoundTripsAndReportsStaleness()
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"ds3-minimap-{Guid.NewGuid():N}.atlas");
        try
        {
            var generation = DateTimeOffset.UnixEpoch.AddDays(3);
            var atlas = new GroundAtlas(GroundAtlas.CurrentVersion, "m40_00", generation, [new GroundTile("floor-1", 2, -1, [1, 2, 3])]);
            await atlas.SaveAsync(temporary);
            var loaded = await GroundAtlas.ReadAsync(temporary, TestContext.Current.CancellationToken);
            Assert.Equal([1, 2, 3], Assert.Single(loaded!.Tiles).WebP);
            Assert.True(await GroundAtlas.IsCurrentAsync(temporary, "m40_00", generation, TestContext.Current.CancellationToken));
            Assert.False(await GroundAtlas.IsCurrentAsync(temporary, "m40_00", generation.AddSeconds(1), TestContext.Current.CancellationToken));
            Assert.False(await GroundAtlas.IsCurrentAsync(temporary + ".missing", "m40_00", generation, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(temporary); }
    }

    [Fact]
    public void CollisionGroundSeparatesStackedFloorsOutsideNavigation()
    {
        var lower = new MapLayer("lower", "Lower", -5, 16, [], [new CollisionRun(-8, 2, -2, 0)]);
        var upper = new MapLayer("upper", "Upper", -5, 16, [], [new CollisionRun(-8, 2, -2, 20)]);
        var region = new MapRegion("test", "Stack", [lower, upper]);
        Assert.Same(upper, LayerSelector.Select(region, new Vector3(-0.2f, 10, -0.8f)));
        Assert.Same(lower, LayerSelector.Select(region, new Vector3(-0.2f, 0, -0.8f)));
    }

    [Theory]
    [InlineData(1920, 1080, 1, 320, -344, 24)]
    [InlineData(2560, 1440, 1.5, 480, -516, 36)]
    [InlineData(320, 160, 2, 120, -140, 20)]
    public void OverlayFitsClientOnLeftHandMonitor(int w, int h, double scale, int size, int x, int y)
    {
        Assert.Equal(new OverlayPlacement(x, y, size), OverlayPlacement.Fit(-w, 0, w, h, scale, new AppSettings()));
    }

    [Fact]
    public void TurningInPlaceUpdatesHeading()
    {
        var tracker = new HeadingTracker();
        var player = Telemetry(DateTimeOffset.UnixEpoch, Vector3.Zero);
        tracker.Update(player);
        Assert.Equal(MathF.PI / 2, tracker.Update(player with { Heading = MathF.PI / 2 }), 5);
    }

    private static PlayerTelemetry Telemetry(DateTimeOffset timestamp, Vector3 position) =>
        new(timestamp, position, 0, "m30_00", "Test");
}
