using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using DS3Minimap.Core;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace DS3Minimap.Rendering;

public sealed class MinimapView : UserControl
{
    private readonly SKElement _surface = new();
    private readonly SKShader _broadGroundNoise = SKShader.CreatePerlinNoiseFractalNoise(0.032f, 0.027f, 4, 19f);
    private readonly SKShader _softGroundClouds = SKShader.CreatePerlinNoiseFractalNoise(0.011f, 0.014f, 3, 47f);
    private readonly SKColorFilter _broadGroundNoiseTint = CreateNoiseTint(new SKColor(65, 48, 31), 0.52f);
    private readonly SKColorFilter _softGroundCloudTint = CreateNoiseTint(new SKColor(235, 220, 185), 0.24f);
    private PlayerTelemetry? _player;
    private float _screenHeading;
    private MapLayer? _layer;
    private MapRegion? _region;
    private IReadOnlyList<TrailPoint> _trail = [];
    private EventFlagSnapshot? _itemFlags;
    private AppSettings _settings = new();
    private MapLayer? _cachedLayer;
    private SKPath? _cachedGeometry;
    private SKPath? _cachedBoundaryGeometry;
    private SKPath? _previousGeometry;
    private SKPath? _previousBoundaryGeometry;
    private string? _cachedMapId;
    private DateTimeOffset _transitionStartedAt;

    public MinimapView()
    {
        Content = _surface;
        IsHitTestVisible = false;
        _surface.PaintSurface += Paint;
        Unloaded += (_, _) =>
        {
            _cachedGeometry?.Dispose();
            _cachedBoundaryGeometry?.Dispose();
            _previousGeometry?.Dispose();
            _previousBoundaryGeometry?.Dispose();
            _broadGroundNoise.Dispose();
            _softGroundClouds.Dispose();
            _broadGroundNoiseTint.Dispose();
            _softGroundCloudTint.Dispose();
        };
    }

    public void Update(PlayerTelemetry? player, float screenHeading, MapRegion? region, MapLayer? layer,
        IReadOnlyList<TrailPoint> trail, AppSettings settings, EventFlagSnapshot? itemFlags = null)
    {
        _player = player;
        _screenHeading = screenHeading;
        _layer = layer;
        _region = region;
        _itemFlags = itemFlags;
        if (!ReferenceEquals(_cachedLayer, layer))
        {
            _previousGeometry?.Dispose();
            _previousBoundaryGeometry?.Dispose();
            var animate = _cachedGeometry is not null && layer is not null && _cachedMapId == region?.MapId
                && settings.LayerTransitionMilliseconds > 0;
            if (animate)
            {
                _previousGeometry = _cachedGeometry;
                _previousBoundaryGeometry = _cachedBoundaryGeometry;
                _transitionStartedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                _cachedGeometry?.Dispose();
                _cachedBoundaryGeometry?.Dispose();
                _previousGeometry = null;
                _previousBoundaryGeometry = null;
            }
            _cachedGeometry = BuildGeometry(layer?.Polygons);
            _cachedBoundaryGeometry = BuildBoundaryGeometry(layer?.Polygons);
            _cachedLayer = layer;
            _cachedMapId = region?.MapId;
        }
        _trail = trail;
        _settings = settings;
        _surface.InvalidateVisual();
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        var info = args.Info;
        canvas.Clear(SKColors.Transparent);

        var center = new SKPoint(info.Width / 2f, info.Height / 2f);
        var radius = Math.Min(info.Width, info.Height) / 2f - 5f;
        using var clipPath = BuildClip(center, radius, _settings.Mask);
        canvas.Save();
        canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);
        DrawBackdrop(canvas, info, center, radius, _settings);

        if (_player is null)
        {
            DrawWaiting(canvas, center);
            canvas.Restore();
            DrawBorder(canvas, clipPath, center, radius, _settings);
            return;
        }

        canvas.Translate(center.X, center.Y);
        if (_settings.Orientation == MapOrientation.PlayerUp)
            canvas.RotateRadians(-_screenHeading);
        var pixelsPerUnit = (float)(_settings.Zoom * 5.5);
        canvas.Scale(pixelsPerUnit, pixelsPerUnit);
        canvas.Translate(-_player.Position.X, -_player.Position.Z);

        var transition = TransitionProgress(_settings.LayerTransitionMilliseconds);
        var easedTransition = transition * transition * (3f - 2f * transition);
        var worldRadius = Math.Max(info.Width, info.Height) / (2f * pixelsPerUnit) * 1.5f;
        if (_previousGeometry is not null && easedTransition < 1f)
            DrawLayer(canvas, _previousGeometry, _previousBoundaryGeometry, _player.Position, pixelsPerUnit, worldRadius, 1f - easedTransition, _settings);
        DrawLayer(canvas, _cachedGeometry, _cachedBoundaryGeometry, _player.Position, pixelsPerUnit, worldRadius, easedTransition, _settings);
        if (_previousGeometry is not null && transition >= 1f)
        {
            _previousGeometry.Dispose();
            _previousBoundaryGeometry?.Dispose();
            _previousGeometry = null;
            _previousBoundaryGeometry = null;
        }

        var lifetime = _settings.TrailLifetimeMinutes > 0
            ? TimeSpan.FromMinutes(_settings.TrailLifetimeMinutes)
            : Timeout.InfiniteTimeSpan;
        var cutoff = lifetime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MinValue : _player.Timestamp - lifetime;
        var visibleTrail = _trail.Where(point => point.Timestamp >= cutoff && point.MapId == _player.MapId && (_layer is null || point.LayerId == _layer.Id)).ToArray();
        var trailColor = ParseColor(_settings.TrailColor, new SKColor(127, 200, 248));
        using var trailHalo = new SKPaint
        {
            Color = new SKColor(8, 12, 15, 210), IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = 5f / pixelsPerUnit, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
        };
        using var trailPaint = new SKPaint
        {
            Color = trailColor, IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f / pixelsPerUnit,
            StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
        };
        if (_settings.FadeTrailWithAge && lifetime != Timeout.InfiniteTimeSpan)
        {
            TrailPoint? previous = null;
            foreach (var point in visibleTrail)
            {
                var remaining = Math.Clamp((lifetime - (_player.Timestamp - point.Timestamp)).TotalSeconds / lifetime.TotalSeconds, 0, 1);
                trailPaint.Color = WithOpacity(trailColor, (float)remaining);
                trailHalo.Color = WithOpacity(new SKColor(8, 12, 15, 210), (float)remaining);
                if (previous is not null && previous.Segment == point.Segment)
                {
                    canvas.DrawLine(previous.Position.X, previous.Position.Z, point.Position.X, point.Position.Z, trailHalo);
                    canvas.DrawLine(previous.Position.X, previous.Position.Z, point.Position.X, point.Position.Z, trailPaint);
                }
                trailPaint.Style = SKPaintStyle.Fill;
                canvas.DrawCircle(point.Position.X, point.Position.Z, _settings.TrailDotRadius / pixelsPerUnit, trailPaint);
                trailPaint.Style = SKPaintStyle.Stroke;
                previous = point;
            }
        }
        else
        {
            using var trailPath = new SKPath();
            int? segment = null;
            foreach (var point in visibleTrail)
            {
                if (segment != point.Segment) trailPath.MoveTo(point.Position.X, point.Position.Z);
                else trailPath.LineTo(point.Position.X, point.Position.Z);
                segment = point.Segment;
            }
            canvas.DrawPath(trailPath, trailHalo);
            canvas.DrawPath(trailPath, trailPaint);
            trailPaint.Style = SKPaintStyle.Fill;
            foreach (var point in visibleTrail)
                canvas.DrawCircle(point.Position.X, point.Position.Z, _settings.TrailDotRadius / pixelsPerUnit, trailPaint);
        }

        var markerUprightRotation = _settings.Orientation == MapOrientation.PlayerUp ? _screenHeading : 0f;
        DrawMarkers(canvas, pixelsPerUnit, markerUprightRotation, DateTimeOffset.UtcNow.TimeOfDay.TotalSeconds,
            _region, _layer, _itemFlags, _settings);

        canvas.Restore();
        DrawPlayer(canvas, center, _screenHeading, _settings);
        DrawBorder(canvas, clipPath, center, radius, _settings);
    }

    private static SKPath BuildClip(SKPoint center, float radius, MapMask mask)
    {
        var path = new SKPath();
        var rect = new SKRect(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);
        if (mask == MapMask.Circle) path.AddCircle(center.X, center.Y, radius);
        else path.AddRoundRect(rect, mask == MapMask.RoundedSquare ? 28 : 0, mask == MapMask.RoundedSquare ? 28 : 0);
        return path;
    }

    private float TransitionProgress(double durationMilliseconds)
    {
        if (_previousGeometry is null || durationMilliseconds <= 0) return 1f;
        return Math.Clamp((float)((DateTimeOffset.UtcNow - _transitionStartedAt).TotalMilliseconds / durationMilliseconds), 0f, 1f);
    }

    private static void DrawBackdrop(SKCanvas canvas, SKImageInfo info, SKPoint center, float radius, AppSettings settings)
    {
        using var basePaint = new SKPaint
        {
            Color = ParseColor(settings.BackgroundColor, new SKColor(18, 18, 18, 235)),
            IsAntialias = true
        };
        canvas.DrawRect(0, 0, info.Width, info.Height, basePaint);

        using var glow = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(center, radius * 1.08f,
                [new SKColor(116, 91, 55, 54), new SKColor(34, 29, 23, 24), new SKColor(0, 0, 0, 175)],
                [0f, 0.57f, 1f], SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(0, 0, info.Width, info.Height, glow);

        using var scratch = new SKPaint
        {
            Color = new SKColor(206, 183, 133, 12), Style = SKPaintStyle.Stroke,
            StrokeWidth = 1, IsAntialias = true
        };
        for (var offset = -info.Height; offset < info.Width + info.Height; offset += 23)
            canvas.DrawLine(offset, 0, offset - info.Height, info.Height, scratch);
    }

    private void DrawLayer(SKCanvas canvas, SKPath? geometry, SKPath? boundary, Vector3 player,
        float pixelsPerUnit, float worldRadius, float opacity, AppSettings settings)
    {
        if (geometry is null || opacity <= 0.002f) return;
        var geometryColor = ParseColor(settings.GeometryColor, new SKColor(230, 215, 183, 230));
        using var fill = new SKPaint
        {
            Color = WithOpacity(geometryColor, opacity), IsAntialias = true, Style = SKPaintStyle.Fill
        };
        canvas.DrawPath(geometry, fill);

        var textureStrength = (float)settings.MapTextureStrength * opacity;
        if (textureStrength > 0.002f)
        {
            canvas.Save();
            canvas.ClipPath(geometry, SKClipOperation.Intersect, true);
            var noiseBounds = new SKRect(player.X - worldRadius, player.Z - worldRadius,
                player.X + worldRadius, player.Z + worldRadius);
            using var broadNoisePaint = new SKPaint
            {
                Shader = _broadGroundNoise,
                ColorFilter = _broadGroundNoiseTint,
                Color = SKColors.White.WithAlpha((byte)Math.Clamp((int)(textureStrength * 255), 0, 255)),
                BlendMode = SKBlendMode.Multiply,
                IsAntialias = true
            };
            canvas.DrawRect(noiseBounds, broadNoisePaint);

            // A second, larger-scale field breaks up the regularity and gives
            // the parchment/stone fill the soft cloudy variation of DS3 fog.
            using var cloudNoisePaint = new SKPaint
            {
                Shader = _softGroundClouds,
                ColorFilter = _softGroundCloudTint,
                Color = SKColors.White.WithAlpha((byte)Math.Clamp((int)(textureStrength * 255), 0, 255)),
                BlendMode = SKBlendMode.SoftLight,
                IsAntialias = true
            };
            canvas.DrawRect(noiseBounds, cloudNoisePaint);

            var spacing = Math.Max(5f, worldRadius / 30f);
            using var etching = new SKPaint
            {
                Color = WithOpacity(new SKColor(68, 52, 34, 58), textureStrength),
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.75f / pixelsPerUnit
            };
            for (var offset = -worldRadius * 2; offset <= worldRadius * 2; offset += spacing)
                canvas.DrawLine(player.X - worldRadius, player.Z - worldRadius + offset,
                    player.X + worldRadius, player.Z + worldRadius + offset, etching);

            using var grain = new SKPaint
            {
                Color = WithOpacity(new SKColor(42, 33, 23, 72), textureStrength),
                IsAntialias = true, Style = SKPaintStyle.Fill
            };
            var minX = (int)MathF.Floor((player.X - worldRadius) / spacing);
            var maxX = (int)MathF.Ceiling((player.X + worldRadius) / spacing);
            var minZ = (int)MathF.Floor((player.Z - worldRadius) / spacing);
            var maxZ = (int)MathF.Ceiling((player.Z + worldRadius) / spacing);
            for (var gridX = minX; gridX <= maxX; gridX++)
            for (var gridZ = minZ; gridZ <= maxZ; gridZ++)
            {
                var hash = Hash(gridX, gridZ);
                if ((hash & 3) != 0) continue;
                var x = (gridX + ((hash >> 8) & 255) / 255f) * spacing;
                var z = (gridZ + ((hash >> 16) & 255) / 255f) * spacing;
                canvas.DrawCircle(x, z, 0.7f / pixelsPerUnit, grain);
            }
            canvas.Restore();
        }

        if (boundary is null) return;
        var boundaryColor = ParseColor(settings.BoundaryColor, new SKColor(14, 13, 11, 230));
        using var shadow = new SKPaint
        {
            Color = WithOpacity(new SKColor(0, 0, 0, 225), opacity), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 4.4f / pixelsPerUnit,
            StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
        };
        using var edge = new SKPaint
        {
            Color = WithOpacity(boundaryColor, opacity), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 1.45f / pixelsPerUnit,
            StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
        };
        canvas.DrawPath(boundary, shadow);
        canvas.DrawPath(boundary, edge);
    }

    private static uint Hash(int x, int z)
    {
        unchecked
        {
            var value = (uint)x * 0x9E3779B1u ^ (uint)z * 0x85EBCA77u;
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            return value ^ (value >> 15);
        }
    }

    private static SKColorFilter CreateNoiseTint(SKColor tint, float alpha)
    {
        // Convert Perlin RGB into luminance, tint it, and move that same
        // luminance into alpha. This prevents Skia's independent noise channels
        // from producing coloured speckles.
        var opacity = Math.Clamp(alpha, 0f, 1f);
        const float third = 1f / 3f;
        var red = tint.Red / 255f;
        var green = tint.Green / 255f;
        var blue = tint.Blue / 255f;
        return SKColorFilter.CreateColorMatrix(
        [
            red * third, red * third, red * third, 0, 0,
            green * third, green * third, green * third, 0, 0,
            blue * third, blue * third, blue * third, 0, 0,
            opacity * third, opacity * third, opacity * third, 0, 0
        ]);
    }

    private static SKPath? BuildGeometry(IEnumerable<MapPolygon>? polygons)
    {
        if (polygons is null) return null;
        var path = new SKPath();
        foreach (var polygon in polygons)
        {
            if (polygon.Points.Count < 3) continue;
            path.MoveTo(polygon.Points[0].X, polygon.Points[0].Z);
            for (var index = 1; index < polygon.Points.Count; index++) path.LineTo(polygon.Points[index].X, polygon.Points[index].Z);
            path.Close();
        }
        return path;
    }

    private static SKPath? BuildBoundaryGeometry(IEnumerable<MapPolygon>? polygons)
    {
        if (polygons is null) return null;
        var edges = new Dictionary<BoundaryEdge, (MapPoint Start, MapPoint End, int Count)>();
        foreach (var polygon in polygons)
        {
            for (var index = 0; index < polygon.Points.Count; index++)
            {
                var start = polygon.Points[index];
                var end = polygon.Points[(index + 1) % polygon.Points.Count];
                var key = BoundaryEdge.Create(start, end);
                if (edges.TryGetValue(key, out var existing)) edges[key] = (existing.Start, existing.End, existing.Count + 1);
                else edges.Add(key, (start, end, 1));
            }
        }

        var path = new SKPath();
        foreach (var edge in edges.Values.Where(edge => edge.Count == 1))
        {
            path.MoveTo(edge.Start.X, edge.Start.Z);
            path.LineTo(edge.End.X, edge.End.Z);
        }
        return path;
    }

    private readonly record struct BoundaryEdge(long AX, long AZ, long BX, long BZ)
    {
        internal static BoundaryEdge Create(MapPoint start, MapPoint end)
        {
            static long Quantize(float value) => (long)MathF.Round(value * 1000f);
            var sx = Quantize(start.X);
            var sz = Quantize(start.Z);
            var ex = Quantize(end.X);
            var ez = Quantize(end.Z);
            return sx < ex || (sx == ex && sz <= ez)
                ? new BoundaryEdge(sx, sz, ex, ez)
                : new BoundaryEdge(ex, ez, sx, sz);
        }
    }

    private static void DrawPlayer(SKCanvas canvas, SKPoint center, float screenHeading, AppSettings settings)
    {
        canvas.Save();
        canvas.Translate(center.X, center.Y);
        if (settings.Orientation == MapOrientation.NorthUp) canvas.RotateRadians(screenHeading);
        using var halo = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 105), IsAntialias = true, Style = SKPaintStyle.Fill
        };
        canvas.DrawCircle(0, 1, 17, halo);
        using var path = new SKPath();
        path.MoveTo(0, -14);
        path.LineTo(9, 10);
        path.LineTo(0, 6);
        path.LineTo(-9, 10);
        path.Close();
        using var outline = new SKPaint
        {
            Color = ParseColor(settings.PlayerOutlineColor, new SKColor(10, 9, 7, 242)), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 4, StrokeJoin = SKStrokeJoin.Round
        };
        using var paint = new SKPaint { Color = ParseColor(settings.PlayerColor, new SKColor(255, 200, 87)), IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawPath(path, outline);
        canvas.DrawPath(path, paint);
        using var glint = new SKPaint
        {
            Color = new SKColor(255, 244, 199, 155), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, StrokeCap = SKStrokeCap.Round
        };
        canvas.DrawLine(0, -10, 0, 4, glint);
        canvas.Restore();
    }

    private static void DrawMarkers(SKCanvas canvas, float pixelsPerUnit, float uprightRotation, double animationSeconds,
        MapRegion? region, MapLayer? layer,
        EventFlagSnapshot? itemFlags, AppSettings settings)
    {
        if (region?.Markers is null || layer is null) return;
        foreach (var marker in region.Markers)
        {
            if (marker.LayerId != layer.Id) continue;
            if (marker.Kind == MapMarkerKind.Bonfire)
            {
                if (settings.ShowBonfires) DrawBonfire(canvas, marker, pixelsPerUnit, uprightRotation, animationSeconds, settings);
                continue;
            }
            if (!settings.ShowUncollectedItems || itemFlags?.Available != true
                || itemFlags.SetFlags.Contains(marker.EventFlagId)) continue;
            DrawItem(canvas, marker, pixelsPerUnit, uprightRotation, animationSeconds, settings);
        }
    }

    private static void DrawBonfire(SKCanvas canvas, MapMarker marker, float pixelsPerUnit, float uprightRotation,
        double animationSeconds, AppSettings settings)
    {
        var scale = (float)settings.MarkerScale;
        var phase = (float)(animationSeconds * 5.2 + (Hash(marker.Id.Length, marker.EventFlagId) & 31));
        var flicker = 1f + MathF.Sin(phase) * 0.055f;
        canvas.Save();
        canvas.Translate(marker.X, marker.Z);
        canvas.RotateRadians(uprightRotation);
        canvas.Scale(scale / pixelsPerUnit, scale / pixelsPerUnit);
        var bonfire = ParseColor(settings.BonfireColor, new SKColor(255, 155, 69));
        using var auraShader = SKShader.CreateRadialGradient(SKPoint.Empty, 11f * flicker,
            [bonfire.WithAlpha(118), bonfire.WithAlpha(42), SKColors.Transparent], [0f, .48f, 1f], SKShaderTileMode.Clamp);
        using var aura = new SKPaint { Shader = auraShader, IsAntialias = true };
        canvas.DrawCircle(0, 0, 11f * flicker, aura);

        using var shadow = new SKPaint { Color = new SKColor(5, 4, 3, 225), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        using var ember = new SKPaint { Color = bonfire, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.55f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        // Ash mound and the bent, coiled weapons at the base.
        canvas.DrawArc(new SKRect(-7, 2, 7, 8), 12, 156, false, shadow);
        canvas.DrawArc(new SKRect(-7, 2, 7, 8), 12, 156, false, ember);
        canvas.DrawLine(-6, 5, 3, 1, shadow);
        canvas.DrawLine(-6, 5, 3, 1, ember);
        canvas.DrawLine(6, 5, -2, 0, shadow);
        canvas.DrawLine(6, 5, -2, 0, ember);

        using var outerFlame = new SKPath();
        outerFlame.MoveTo(-1, 5);
        outerFlame.CubicTo(-7, 1, -3, -3, -1, -5);
        outerFlame.CubicTo(-2, -1, 2, -3, 2, -8 * flicker);
        outerFlame.CubicTo(7, -2, 5, 3, 1, 6);
        outerFlame.Close();
        using var flameOutline = new SKPaint { Color = new SKColor(13, 8, 4, 235), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.1f, StrokeJoin = SKStrokeJoin.Round };
        using var flameFill = new SKPaint { Color = bonfire.WithAlpha(235), IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawPath(outerFlame, flameOutline);
        canvas.DrawPath(outerFlame, flameFill);
        using var innerFlame = new SKPaint { Color = new SKColor(255, 232, 162, 238), IsAntialias = true, Style = SKPaintStyle.Fill };
        using var innerPath = new SKPath();
        innerPath.MoveTo(0, 4);
        innerPath.CubicTo(-2.5f, 1, 1, -1, .5f, -4.5f * flicker);
        innerPath.CubicTo(4, -.5f, 3, 3, 0, 4);
        innerPath.Close();
        canvas.DrawPath(innerPath, innerFlame);

        // The sword is deliberately offset and leaning, like DS3's coiled sword.
        using var bladeShadow = new SKPaint { Color = new SKColor(7, 6, 5, 245), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.4f, StrokeCap = SKStrokeCap.Round };
        using var blade = new SKPaint { Color = new SKColor(218, 207, 177, 245), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.25f, StrokeCap = SKStrokeCap.Round };
        canvas.DrawLine(2.6f, -9, -.3f, 5.5f, bladeShadow);
        canvas.DrawLine(2.6f, -9, -.3f, 5.5f, blade);
        canvas.DrawLine(-2.1f, -.2f, 3.1f, .9f, bladeShadow);
        canvas.DrawLine(-2.1f, -.2f, 3.1f, .9f, blade);
        canvas.Restore();
    }

    private static void DrawItem(SKCanvas canvas, MapMarker marker, float pixelsPerUnit, float uprightRotation,
        double animationSeconds, AppSettings settings)
    {
        var scale = (float)settings.MarkerScale;
        var phase = (float)(animationSeconds * 2.6 + (Hash(marker.ItemLotId, marker.EventFlagId) & 63));
        var pulse = 1f + MathF.Sin(phase) * 0.08f;
        canvas.Save();
        canvas.Translate(marker.X, marker.Z);
        canvas.RotateRadians(uprightRotation);
        canvas.Scale(scale / pixelsPerUnit, scale / pixelsPerUnit);
        var item = ParseColor(settings.ItemColor, new SKColor(217, 242, 255));
        using var auraShader = SKShader.CreateRadialGradient(new SKPoint(0, 1), 10f * pulse,
            [item.WithAlpha(115), item.WithAlpha(38), SKColors.Transparent], [0f, .42f, 1f], SKShaderTileMode.Clamp);
        using var aura = new SKPaint { Shader = auraShader, IsAntialias = true };
        canvas.DrawOval(new SKRect(-8 * pulse, -10 * pulse, 8 * pulse, 9 * pulse), aura);

        using var wispShadow = new SKPaint { Color = new SKColor(5, 10, 14, 235), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f, StrokeCap = SKStrokeCap.Round };
        using var wisp = new SKPaint { Color = item.WithAlpha(235), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.35f, StrokeCap = SKStrokeCap.Round };
        using var wispPath = new SKPath();
        wispPath.MoveTo(-1.8f, 4.5f);
        wispPath.CubicTo(4.5f, 1.5f, -3.5f, -1, 1.4f, -4.1f);
        wispPath.CubicTo(4.2f, -5.9f, 1.8f, -7.2f * pulse, .2f, -8.6f * pulse);
        canvas.DrawPath(wispPath, wispShadow);
        canvas.DrawPath(wispPath, wisp);
        using var moteOutline = new SKPaint { Color = new SKColor(4, 8, 11, 245), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.4f };
        using var mote = new SKPaint { Color = new SKColor(249, 253, 255, 248), IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawCircle(-1.8f, 4.8f, 2.35f * pulse, moteOutline);
        canvas.DrawCircle(-1.8f, 4.8f, 1.55f * pulse, mote);
        using var spark = new SKPaint { Color = item.WithAlpha(205), IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawCircle(3.8f, -2.8f, .75f * pulse, spark);
        canvas.DrawCircle(-3.4f, -.8f, .55f * pulse, spark);
        canvas.Restore();
    }

    private static void DrawWaiting(SKCanvas canvas, SKPoint center)
    {
        using var dark = new SKPaint { Color = new SKColor(0, 0, 0, 175), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5 };
        using var bronze = new SKPaint { Color = new SKColor(170, 139, 82, 190), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        canvas.DrawCircle(center.X, center.Y, 20, dark);
        canvas.DrawCircle(center.X, center.Y, 20, bronze);
        canvas.DrawCircle(center.X, center.Y, 6, bronze);
    }

    private static void DrawBorder(SKCanvas canvas, SKPath clipPath, SKPoint center, float radius, AppSettings settings)
    {
        var frameColor = ParseColor(settings.FrameColor, new SKColor(154, 124, 72));
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 220), Style = SKPaintStyle.Stroke, StrokeWidth = 8, IsAntialias = true };
        using var frame = new SKPaint { Color = frameColor, Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
        using var highlight = new SKPaint { Color = new SKColor(226, 204, 154, 125), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawPath(clipPath, shadow);
        canvas.DrawPath(clipPath, frame);
        canvas.DrawPath(clipPath, highlight);

        if (settings.Mask != MapMask.Circle) return;
        using var notch = new SKPaint { Color = frameColor.WithAlpha(215), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        foreach (var angle in new[] { 0f, MathF.PI / 2f, MathF.PI, MathF.PI * 1.5f })
        {
            var dx = MathF.Sin(angle);
            var dy = -MathF.Cos(angle);
            canvas.DrawLine(center.X + dx * (radius - 7), center.Y + dy * (radius - 7),
                center.X + dx * (radius + 3), center.Y + dy * (radius + 3), notch);
        }
    }

    private static SKColor ParseColor(string value, SKColor fallback)
    {
        var hex = value.Trim().TrimStart('#');
        try
        {
            if (hex.Length == 8)
            {
                var argb = Convert.ToUInt32(hex, 16);
                return new SKColor((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
            }
            if (hex.Length == 6)
            {
                var rgb = Convert.ToUInt32(hex, 16);
                return new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
            }
        }
        catch (FormatException) { }
        catch (OverflowException) { }
        return fallback;
    }

    private static SKColor WithOpacity(SKColor color, float opacity) =>
        color.WithAlpha((byte)Math.Clamp((int)MathF.Round(color.Alpha * opacity), 0, 255));
}
