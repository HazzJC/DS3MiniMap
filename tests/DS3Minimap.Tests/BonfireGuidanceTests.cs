using System.Numerics;
using DS3Minimap.Core;
using Xunit;

namespace DS3Minimap.Tests;

public sealed class BonfireGuidanceTests
{
    // These positions and flags are from the two stacked Ringed City bonfires.
    private static readonly MapMarker Grave = new("bonfire-m51_00-77", MapMarkerKind.Bonfire,
        -398.03458f, -94.28555f, -267.31958f, "floor-3", "Shared Grave", 15100005);
    private static readonly MapMarker Church = new("bonfire-m51_00-78", MapMarkerKind.Bonfire,
        -393.284f, -51.319748f, -252.16785f, "floor-4", "Church of Filianore", 15100000);
    private static readonly EventFlagSnapshot ChurchLit = new(true, new HashSet<int> { Church.EventFlagId }, "");

    [Theory]
    [InlineData(true, BonfireSymbol.Ash)]
    [InlineData(false, BonfireSymbol.Landmark)]
    public void AshStyleOnlyAffectsAppearance(bool ash, BonfireSymbol expected)
    {
        var atGrave = new Vector3(Grave.X, Grave.Y, Grave.Z);
        Assert.Equal(expected, BonfireGuidance.Symbol(Grave, ChurchLit, ash));
        Assert.Equal(BonfireSymbol.Flame, BonfireGuidance.Symbol(Church, ChurchLit, ash));
        Assert.Same(Church, BonfireGuidance.Nearest([Grave, Church], atGrave, ChurchLit));
    }

    [Fact]
    public void UnknownActivationUsesLandmarkAndNearestThreeDimensionalDistance()
    {
        var underChurch = new Vector3(Church.X, Grave.Y, Church.Z);
        var unavailable = new EventFlagSnapshot(false, new HashSet<int> { Church.EventFlagId }, "");
        Assert.Equal(BonfireSymbol.Landmark, BonfireGuidance.Symbol(Church, unavailable, true));
        Assert.Equal(BonfireSymbol.Landmark, BonfireGuidance.Symbol(Church with { EventFlagId = -1 }, ChurchLit, true));
        Assert.Same(Grave, BonfireGuidance.Nearest([Church, Grave], underChurch, unavailable));
        Assert.Same(Grave, BonfireGuidance.Nearest([Church, Grave], underChurch, null));
        Assert.Null(BonfireGuidance.Nearest([Church with { Kind = MapMarkerKind.Item }], underChurch, null));
    }

    [Theory]
    [InlineData(MapMask.Circle)]
    [InlineData(MapMask.Square)]
    [InlineData(MapMask.RoundedSquare)]
    public void DifferentFloorKeepsPointerUntilMarkerIsVisible(MapMask mask)
    {
        var player = new Vector3(Grave.X, Grave.Y, Grave.Z);
        var above = BonfireGuidance.Locate(Church, player, Grave.LayerId, 3, 0, mask, 100);
        Assert.True(above.HasValue);
        Assert.Equal("Above", above.Value.FloorCue);
        Assert.InRange(above.Value.Distance, 45f, 47f);
        Assert.True(above.Value.ShowBearing);
        Assert.Null(BonfireGuidance.Locate(Church, player, Church.LayerId, 3, 0, mask, 100));
    }

    [Theory]
    [InlineData(12, "Below", 75)]
    [InlineData(-12, "Above", -75)]
    public void DirectlyStackedBonfireShowsVerticalCueWithoutCompassArrow(int dy, string cue, float edge)
    {
        var player = new Vector3(Church.X, Church.Y + dy, Church.Z);
        var pointer = BonfireGuidance.Locate(Church, player, Grave.LayerId, 6, -2f, MapMask.Circle, 75);
        Assert.True(pointer.HasValue);
        Assert.Equal(new Vector2(0, edge), pointer.Value.Offset);
        Assert.Equal(cue, pointer.Value.FloorCue);
        Assert.False(pointer.Value.ShowBearing);
        Assert.Equal(12f, pointer.Value.Distance);
    }

    [Fact]
    public void SameFloorOffscreenPointerTracksPlayerUpRotation()
    {
        var west = new Vector3(Church.X - 20, Church.Y, Church.Z);
        var pointer = BonfireGuidance.Locate(Church, west, Church.LayerId, 10, -MathF.PI / 2, MapMask.Circle, 100);
        Assert.True(pointer.HasValue);
        Assert.InRange(Vector2.Distance(pointer.Value.Offset, new(0, -100)), 0f, 0.001f);
        Assert.True(pointer.Value.ShowBearing);
        Assert.Null(pointer.Value.FloorCue);
    }
}
