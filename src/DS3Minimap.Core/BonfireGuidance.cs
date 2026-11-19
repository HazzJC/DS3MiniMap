using System.Numerics;

namespace DS3Minimap.Core;

public enum BonfireSymbol { Landmark, Ash, Flame }

public static class BonfireGuidance
{
    private static bool? IsLit(MapMarker marker, EventFlagSnapshot? flags) =>
        flags?.Available == true && marker.EventFlagId > 0 ? flags.SetFlags.Contains(marker.EventFlagId) : null;

    public static BonfireSymbol Symbol(MapMarker marker, EventFlagSnapshot? flags, bool showAsh) =>
        IsLit(marker, flags) switch
        {
            true => BonfireSymbol.Flame,
            false when showAsh => BonfireSymbol.Ash,
            _ => BonfireSymbol.Landmark
        };

    public static MapMarker? Nearest(IReadOnlyList<MapMarker> markers, Vector3 player, EventFlagSnapshot? flags)
    {
        MapMarker? best = null;
        bool bestLit = false;
        float bestDistance = float.PositiveInfinity;
        foreach (var marker in markers)
        {
            if (marker.Kind != MapMarkerKind.Bonfire) continue;
            var lit = IsLit(marker, flags) == true;
            var distance = Vector3.DistanceSquared(player, new(marker.X, marker.Y, marker.Z));
            if (bestLit && !lit || bestLit == lit && distance >= bestDistance) continue;
            best = marker;
            bestLit = lit;
            bestDistance = distance;
        }
        return best;
    }

    public readonly record struct Pointer(Vector2 Offset, Vector2 Direction, bool ShowBearing, string? FloorCue, float Distance);

    public static Pointer? Locate(MapMarker target, Vector3 player, string? displayedFloor, float pixelsPerUnit,
        float screenRotation, MapMask mask, float inset)
    {
        var delta = new Vector3(target.X, target.Y, target.Z) - player;
        var offset = Vector2.Transform(new Vector2(delta.X, delta.Z) * pixelsPerUnit, Matrix3x2.CreateRotation(screenRotation));
        var extent = mask == MapMask.Circle ? offset.Length() : MathF.Max(MathF.Abs(offset.X), MathF.Abs(offset.Y));
        bool sameFloor = target.LayerId == displayedFloor;
        if (inset <= 0 || sameFloor && extent <= inset) return null;

        string? cue = sameFloor ? null : delta.Y switch { > 0.5f => "Above", < -0.5f => "Below", _ => "Other floor" };
        if (extent < 1f)
        {
            // Directly stacked positions have no meaningful compass bearing.
            var vertical = new Vector2(0, delta.Y < 0 ? 1 : -1);
            return new(vertical * inset, vertical, false, cue, delta.Length());
        }
        return new(offset * (inset / extent), Vector2.Normalize(offset), true, cue, delta.Length());
    }
}
