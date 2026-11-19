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
    // Bonfire artwork is authored in "icon units" (-12..12) and rasterised once.
    private const float SpriteExtent = 12f;
    private const int SpritePixelsPerUnit = 8;
    private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    private readonly SKElement _surface = new() { IgnorePixelScaling = true };
    private readonly SKShader _broadGroundNoise = SKShader.CreatePerlinNoiseFractalNoise(0.032f, 0.027f, 4, 19f);
    private readonly SKShader _softGroundClouds = SKShader.CreatePerlinNoiseFractalNoise(0.011f, 0.014f, 3, 47f);
    private readonly SKColorFilter _broadGroundNoiseTint = CreateNoiseTint(new SKColor(65, 48, 31), 0.52f);
    private readonly SKColorFilter _softGroundCloudTint = CreateNoiseTint(new SKColor(235, 220, 185), 0.24f);
    private readonly GroundTextureCache _textures;
    private readonly Dictionary<(uint Color, bool Lit), SKImage> _bonfireSprites = [];
    private PlayerTelemetry? _player;
    private float _screenHeading;
    private MapLayer? _layer;
    private MapRegion? _region;
    private IReadOnlyList<TrailPoint> _trail = [];
    private EventFlagSnapshot? _itemFlags;
    private EventFlagSnapshot? _bonfireFlags;
    private ExploredArea? _explored;
    private AppSettings _settings = new();
    private MapLayer? _cachedLayer;
    private MapLayer? _previousLayer;
    private SKPath? _cachedGeometry;
    private SKPath? _cachedBoundaryGeometry;
    private SKPath? _previousGeometry;
    private SKPath? _previousBoundaryGeometry;
    private string? _cachedMapId;
    private DateTimeOffset _transitionStartedAt;
    private SKPath? _fogPath;
    private (string? Map, string? Layer, int Version) _fogKey;
    private (double Toning, string Tint) _toningKey;
    private SKColorFilter? _toning;

    /// <summary>Animation clock (flicker, motes, floor fades). Replaceable for recorded demos.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    public MinimapView() : this(null) { }

    /// <param name="atlasPath">Where to find a map's ground-texture atlas; defaults to the installed map folder.</param>
    public MinimapView(Func<string, string>? atlasPath)
    {
        _textures = new GroundTextureCache(atlasPath);
        Content = _surface;
        IsHitTestVisible = false;
        _surface.PaintSurface += Paint;
        _textures.Loaded += () => Dispatcher.BeginInvoke(_surface.InvalidateVisual);
        Unloaded += (_, _) =>
        {
            _cachedGeometry?.Dispose();
            _cachedBoundaryGeometry?.Dispose();
            _previousGeometry?.Dispose();
            _previousBoundaryGeometry?.Dispose();
            _cachedGeometry = _cachedBoundaryGeometry = _previousGeometry = _previousBoundaryGeometry = null;
            _cachedLayer = _previousLayer = null;
            _fogPath?.Dispose();
            _fogPath = null;
            _fogKey = default;
            _toning?.Dispose();
            _toning = null;
            _toningKey = default;
            foreach (var sprite in _bonfireSprites.Values) sprite.Dispose();
            _bonfireSprites.Clear();
            _textures.Dispose();
        };
    }

    public void Update(PlayerTelemetry? player, float screenHeading, MapRegion? region, MapLayer? layer,
        IReadOnlyList<TrailPoint> trail, AppSettings settings, EventFlagSnapshot? itemFlags = null,
        EventFlagSnapshot? bonfireFlags = null, ExploredArea? explored = null)
    {
        _player = player;
        _screenHeading = screenHeading;
        _layer = layer;
        _region = region;
        _itemFlags = itemFlags;
        _bonfireFlags = bonfireFlags;
        _explored = explored;
        _textures.Request(region?.MapId);
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
                _previousLayer = _cachedLayer;
                _transitionStartedAt = Clock();
            }
            else
            {
                _cachedGeometry?.Dispose();
                _cachedBoundaryGeometry?.Dispose();
                _previousGeometry = null;
                _previousBoundaryGeometry = null;
                _previousLayer = null;
            }
            _cachedGeometry = BuildGeometry(layer?.Polygons);
            _cachedBoundaryGeometry = BuildBoundaryGeometry(layer?.Polygons);
            _cachedLayer = layer;
            _cachedMapId = region?.MapId;
        }
        _trail = trail;
        if (_settings.BonfireColor != settings.BonfireColor)
        {
            foreach (var sprite in _bonfireSprites.Values) sprite.Dispose();
            _bonfireSprites.Clear();
        }
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

        canvas.Save();
        canvas.Translate(center.X, center.Y);
        if (_settings.Orientation == MapOrientation.PlayerUp)
            canvas.RotateRadians(-_screenHeading);
        var pixelsPerUnit = (float)(_settings.Zoom * 5.5);
        canvas.Scale(pixelsPerUnit, pixelsPerUnit);
        canvas.Translate(-_player.Position.X, -_player.Position.Z);

        var transition = TransitionProgress(_settings.LayerTransitionMilliseconds);
        var easedTransition = transition * transition * (3f - 2f * transition);
        var worldRadius = Math.Max(info.Width, info.Height) / (2f * pixelsPerUnit) * 1.5f;
        var view = new SKRect(_player.Position.X - worldRadius, _player.Position.Z - worldRadius,
            _player.Position.X + worldRadius, _player.Position.Z + worldRadius);
        var fading = _previousGeometry is not null && easedTransition < 1f;
        if (fading && _previousLayer is not null)
            DrawGround(canvas, _previousLayer, view, pixelsPerUnit, 1f - easedTransition, _settings);
        if (_layer is not null)
            DrawGround(canvas, _layer, view, pixelsPerUnit, fading ? easedTransition : 1f, _settings);
        if (fading)
            DrawLayer(canvas, _previousGeometry!, _previousBoundaryGeometry, _previousLayer, _player.Position, pixelsPerUnit, worldRadius, 1f - easedTransition, _settings);
        DrawLayer(canvas, _cachedGeometry, _cachedBoundaryGeometry, _layer, _player.Position, pixelsPerUnit, worldRadius, easedTransition, _settings);
        if (_previousGeometry is not null && transition >= 1f)
        {
            _previousGeometry.Dispose();
            _previousBoundaryGeometry?.Dispose();
            _previousGeometry = null;
            _previousBoundaryGeometry = null;
            _previousLayer = null;
        }
        DrawFog(canvas, view, pixelsPerUnit, _settings);

        DrawTrail(canvas, pixelsPerUnit, worldRadius, Clock().TimeOfDay.TotalSeconds);

        var markerUprightRotation = _settings.Orientation == MapOrientation.PlayerUp ? _screenHeading : 0f;
        var seconds = Clock().TimeOfDay.TotalSeconds;
        DrawMarkers(canvas, _player.Position, worldRadius, pixelsPerUnit, markerUprightRotation, seconds);

        canvas.Restore();
        if (_settings.ShowBonfires && _settings.ShowNearestBonfirePointer)
            DrawNearestBonfirePointer(canvas, info, center, radius, pixelsPerUnit, seconds);
        canvas.Restore();
        DrawPlayer(canvas, center, _screenHeading, _settings);
        DrawBorder(canvas, clipPath, center, radius, _settings);
    }

    private readonly record struct TrailVertex(Vector2 Position, float Opacity);

    /// <summary>
    /// Collects the visible trail on this floor as runs of connected points,
    /// newest first, each with its age-fade opacity.
    /// </summary>
    private List<List<TrailVertex>> TrailRuns(float worldRadius, bool joinPlayer)
    {
        var runs = new List<List<TrailVertex>>();
        if (_player is null) return runs;
        var lifetime = _settings.TrailLifetimeMinutes > 0 ? TimeSpan.FromMinutes(_settings.TrailLifetimeMinutes) : Timeout.InfiniteTimeSpan;
        var cutoff = lifetime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MinValue : _player.Timestamp - lifetime;
        List<TrailVertex>? run = null;
        TrailPoint? newer = null;
        for (int i = _trail.Count - 1; i >= 0; i--)
        {
            var point = _trail[i];
            if (point.Timestamp < cutoff) break;
            if (point.MapId != _player.MapId || (_layer != null && point.LayerId != _layer.Id)
                || MathF.Abs(point.Position.X - _player.Position.X) > worldRadius + 75
                || MathF.Abs(point.Position.Z - _player.Position.Z) > worldRadius + 75)
            {
                run = null;
                newer = null;
                continue;
            }
            var opacity = _settings.FadeTrailWithAge && lifetime != Timeout.InfiniteTimeSpan
                ? (float)Math.Clamp((point.Timestamp - cutoff).TotalSeconds / lifetime.TotalSeconds, 0, 1) : 1f;
            if (run is null || newer?.Segment != point.Segment)
            {
                run = [];
                runs.Add(run);
                // The newest stretch reaches right up to the player marker.
                if (joinPlayer && runs.Count == 1 && i == _trail.Count - 1
                    && Vector3.Distance(point.Position, _player.Position) < 6f)
                    run.Add(new(new Vector2(_player.Position.X, _player.Position.Z), opacity));
            }
            run.Add(new(new Vector2(point.Position.X, point.Position.Z), opacity));
            newer = point;
        }
        return runs;
    }

    private void DrawTrail(SKCanvas canvas, float pixelsPerUnit, float worldRadius, double seconds)
    {
        var style = _settings.TrailStyle;
        if (style == TrailStyle.Hidden || _player is null) return;
        var color = ParseColor(_settings.TrailColor, new SKColor(127, 200, 248));
        var runs = TrailRuns(worldRadius, style is TrailStyle.Soul or TrailStyle.Inked);
        if (style is TrailStyle.Dots or TrailStyle.Connected)
        {
            using var dot = new SKPaint { IsAntialias = true, StrokeWidth = 1.6f / pixelsPerUnit };
            using var halo = new SKPaint { IsAntialias = true };
            var radiusWorld = _settings.TrailDotRadius / pixelsPerUnit;
            foreach (var run in runs)
                for (int i = 0; i < run.Count; i++)
                {
                    var p = run[i];
                    dot.Color = WithOpacity(color, p.Opacity);
                    halo.Color = WithOpacity(new SKColor(8, 12, 15, 225), p.Opacity);
                    if (style == TrailStyle.Connected && i > 0)
                        canvas.DrawLine(p.Position.X, p.Position.Y, run[i - 1].Position.X, run[i - 1].Position.Y, dot);
                    canvas.DrawCircle(p.Position.X, p.Position.Y, radiusWorld + 1f / pixelsPerUnit, halo);
                    canvas.DrawCircle(p.Position.X, p.Position.Y, radiusWorld, dot);
                }
            return;
        }

        var px = 1f / pixelsPerUnit;
        var width = Math.Max(1.2f, _settings.TrailDotRadius * 0.85f) * px;
        var core = Blend(color, SKColors.White, 0.4f);
        const int ChunkPieces = 8;
        foreach (var run in runs)
        {
            if (run.Count < 2) continue;
            float dashPhase = 0;
            // Chunks let opacity follow age along the run while keeping it smooth.
            for (int start = 0; start < run.Count - 1; start += ChunkPieces)
            {
                int end = Math.Min(run.Count - 1, start + ChunkPieces);
                using var path = SmoothChunk(run, start, end);
                var opacity = (run[start].Opacity + run[end].Opacity) / 2f;
                if (opacity <= 0.01f) continue;
                if (style == TrailStyle.Soul)
                {
                    using var shadow = new SKPaint
                    {
                        Color = new SKColor(4, 6, 8, (byte)(135 * opacity)), IsAntialias = true, Style = SKPaintStyle.Stroke,
                        StrokeWidth = width + 2.4f * px, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
                        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.3f * px)
                    };
                    using var glow = new SKPaint
                    {
                        Color = color.WithAlpha((byte)(125 * opacity)), IsAntialias = true, Style = SKPaintStyle.Stroke,
                        StrokeWidth = width * 4f, StrokeCap = SKStrokeCap.Butt, StrokeJoin = SKStrokeJoin.Round,
                        BlendMode = SKBlendMode.Plus, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3.2f * px)
                    };
                    using var line = new SKPaint
                    {
                        Color = core.WithAlpha((byte)(240 * opacity)), IsAntialias = true, Style = SKPaintStyle.Stroke,
                        StrokeWidth = width * 0.8f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
                    };
                    canvas.DrawPath(path, shadow);
                    canvas.DrawPath(path, glow);
                    canvas.DrawPath(path, line);
                }
                else
                {
                    // A route inked onto an old map: a pale ground halo under a
                    // dashed sepia line whose dashes run on across chunks.
                    using var dash = SKPathEffect.CreateDash([6f * px, 4f * px], dashPhase);
                    using var halo = new SKPaint
                    {
                        Color = new SKColor(238, 226, 198, (byte)(130 * opacity)), IsAntialias = true, Style = SKPaintStyle.Stroke,
                        StrokeWidth = width + 2.8f * px, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
                    };
                    using var ink = new SKPaint
                    {
                        Color = new SKColor(58, 40, 24, (byte)(235 * opacity)), IsAntialias = true, Style = SKPaintStyle.Stroke,
                        StrokeWidth = width, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, PathEffect = dash
                    };
                    canvas.DrawPath(path, halo);
                    canvas.DrawPath(path, ink);
                    using var measure = new SKPathMeasure(path);
                    dashPhase = (dashPhase + measure.Length) % (10f * px);
                }
            }
            if (style == TrailStyle.Soul) DrawSoulMotes(canvas, run, color, px, seconds);
        }
    }

    /// <summary>A quadratic curve through the run's points via their midpoints.</summary>
    private static SKPath SmoothChunk(List<TrailVertex> run, int start, int end)
    {
        static SKPoint P(Vector2 v) => new(v.X, v.Y);
        static SKPoint Mid(Vector2 a, Vector2 b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        // Piece k bends through point k, from the midpoint before it to the
        // midpoint after it. A chunk spans the midpoint after its start point to
        // the midpoint after its end point, so consecutive chunks meet exactly.
        var last = run.Count - 1;
        var path = new SKPath();
        if (start == 0)
        {
            path.MoveTo(P(run[0].Position));
            path.LineTo(Mid(run[0].Position, run[1].Position));
        }
        else path.MoveTo(Mid(run[start].Position, run[start + 1].Position));
        for (int k = start + 1; k <= Math.Min(end, last - 1); k++)
            path.QuadTo(P(run[k].Position), Mid(run[k].Position, run[k + 1].Position));
        if (end == last) path.LineTo(P(run[last].Position));
        return path;
    }

    /// <summary>
    /// Small motes of light drift along the soul trail towards the player,
    /// so the direction of travel reads at a glance.
    /// </summary>
    private static void DrawSoulMotes(SKCanvas canvas, List<TrailVertex> run, SKColor color, float px, double seconds)
    {
        const float Spacing = 5f;
        var offset = (float)(seconds * 1.4 % Spacing);
        using var mote = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus };
        using var spark = new SKPaint { IsAntialias = true };
        float travelled = 0, next = Spacing - offset;
        for (int i = 1; i < run.Count; i++)
        {
            var a = run[i - 1].Position; var b = run[i].Position;
            var length = Vector2.Distance(a, b);
            while (next <= travelled + length && length > 0.0001f)
            {
                var t = (next - travelled) / length;
                var at = Vector2.Lerp(a, b, t);
                var opacity = run[i - 1].Opacity + (run[i].Opacity - run[i - 1].Opacity) * t;
                using var glowShader = SKShader.CreateRadialGradient(new SKPoint(at.X, at.Y), 6f * px,
                    [color.WithAlpha((byte)(215 * opacity)), color.WithAlpha((byte)(70 * opacity)), SKColors.Transparent], [0f, 0.45f, 1f], SKShaderTileMode.Clamp);
                mote.Shader = glowShader;
                canvas.DrawCircle(at.X, at.Y, 6f * px, mote);
                spark.Color = new SKColor(250, 253, 255, (byte)(245 * opacity));
                canvas.DrawCircle(at.X, at.Y, 1.6f * px, spark);
                next += Spacing;
            }
            travelled += length;
        }
    }

    private bool UsesTextures(MapLayer layer, AppSettings settings) =>
        settings.GroundStyle == GroundStyle.RealTextures && settings.ShowCollisionGround && _textures.HasFloor(layer.Id);

    private void DrawGround(SKCanvas canvas, MapLayer layer, SKRect view, float pixelsPerUnit, float opacity, AppSettings settings)
    {
        if (!settings.ShowCollisionGround || layer.Ground is not { } runs || opacity <= 0.002f) return;
        var textured = UsesTextures(layer, settings);
        const float unit = CollisionRun.CellSize;
        int startZ = (int)MathF.Floor(view.Top / unit);
        float endZ = view.Bottom / unit;
        using var path = new SKPath();
        for (int i = CollisionRun.FirstRow(runs, startZ); i < runs.Count && runs[i].Z <= endZ; i++)
        {
            var run = runs[i];
            if (run.EndX * unit < view.Left || run.X * unit > view.Right) continue;
            path.AddRect(new SKRect(run.X * unit, run.Z * unit, run.EndX * unit, (run.Z + 1) * unit));
        }
        // Beneath textures this flat fill only shows where a whole tile had no
        // visual model, so keep it darker to sit quietly with real ground.
        var groundColor = ParseColor(settings.GeometryColor, SKColors.Tan);
        using var fill = new SKPaint
        {
            Color = textured ? WithOpacity(Blend(groundColor, new SKColor(40, 36, 30), 0.55f), opacity * 0.9f)
                : WithOpacity(groundColor, 0.49f * opacity)
        };
        canvas.DrawPath(path, fill);
        if (!textured) return;

        const float tileSize = GroundTile.WorldSize;
        // Zoomed out, a tile covers few screen pixels; use its small preview.
        var preview = tileSize * pixelsPerUnit < GroundTextureCache.PreviewSize * 1.25f;
        using var paint = new SKPaint
        {
            ColorFilter = Toning(settings),
            Color = SKColors.White.WithAlpha((byte)Math.Clamp((int)(opacity * 255), 0, 255))
        };
        for (int tz = (int)MathF.Floor(view.Top / tileSize); tz <= (int)MathF.Floor(view.Bottom / tileSize); tz++)
        for (int tx = (int)MathF.Floor(view.Left / tileSize); tx <= (int)MathF.Floor(view.Right / tileSize); tx++)
        {
            var image = _textures.Get(layer.Id, tx, tz, preview);
            if (image is null) continue;
            canvas.DrawImage(image, SKRect.Create(tx * tileSize, tz * tileSize, tileSize, tileSize), Smooth, paint);
        }
    }

    /// <summary>
    /// "Lightly toned" grading for real textures: blend towards a warm,
    /// map-coloured monochrome and dim slightly so the trail, player and
    /// markers always stay the brightest things on the map.
    /// </summary>
    private SKColorFilter Toning(AppSettings settings)
    {
        var key = (settings.TextureToning, settings.GeometryColor);
        if (_toning is not null && _toningKey == key) return _toning;
        _toning?.Dispose();
        _toningKey = key;
        var t = (float)Math.Clamp(settings.TextureToning, 0, 1);
        var tint = ParseColor(settings.GeometryColor, new SKColor(230, 215, 183));
        var tintLuma = Math.Max(1f, 0.299f * tint.Red + 0.587f * tint.Green + 0.114f * tint.Blue);
        float[] tintScale = [tint.Red / tintLuma, tint.Green / tintLuma, tint.Blue / tintLuma];
        var brightness = 0.96f - 0.22f * t;
        var matrix = new float[20];
        for (int row = 0; row < 3; row++)
        {
            matrix[row * 5 + 0] = brightness * (t * tintScale[row] * 0.299f + (row == 0 ? 1 - t : 0));
            matrix[row * 5 + 1] = brightness * (t * tintScale[row] * 0.587f + (row == 1 ? 1 - t : 0));
            matrix[row * 5 + 2] = brightness * (t * tintScale[row] * 0.114f + (row == 2 ? 1 - t : 0));
        }
        matrix[18] = 1;
        return _toning = SKColorFilter.CreateColorMatrix(matrix);
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
        return Math.Clamp((float)((Clock() - _transitionStartedAt).TotalMilliseconds / durationMilliseconds), 0f, 1f);
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

    private void DrawLayer(SKCanvas canvas, SKPath? geometry, SKPath? boundary, MapLayer? layer, Vector3 player,
        float pixelsPerUnit, float worldRadius, float opacity, AppSettings settings)
    {
        if (geometry is null || opacity <= 0.002f) return;
        var textured = layer is not null && UsesTextures(layer, settings);
        var geometryColor = ParseColor(settings.GeometryColor, new SKColor(230, 215, 183, 230));

        // Walls look raised: a soft shadow falls outward and down-right from
        // every outside edge, beneath the inked boundary line.
        if (boundary is not null)
        {
            using var wallShadow = new SKPaint
            {
                Color = WithOpacity(new SKColor(0, 0, 0, 115), opacity), IsAntialias = true,
                Style = SKPaintStyle.Stroke, StrokeWidth = 5f / pixelsPerUnit,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.6f / pixelsPerUnit)
            };
            canvas.Save();
            canvas.ClipPath(geometry, SKClipOperation.Difference, true);
            canvas.Translate(1.6f / pixelsPerUnit, 2.2f / pixelsPerUnit);
            canvas.DrawPath(boundary, wallShadow);
            canvas.Restore();
        }

        // Over real ground the walkable paths are a pale wash, so the texture
        // still shows through; over parchment they are the opaque map fill.
        using var fill = new SKPaint
        {
            Color = WithOpacity(geometryColor, textured ? opacity * 0.2f : opacity), IsAntialias = true, Style = SKPaintStyle.Fill
        };
        canvas.DrawPath(geometry, fill);

        var textureStrength = (float)settings.MapTextureStrength * opacity;
        if (!textured && textureStrength > 0.002f)
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
        // Several placed meshes overlap. Ink only the outside of their combined
        // fill so tile seams cannot look like walls across an open route.
        canvas.Save();
        canvas.ClipPath(geometry, SKClipOperation.Difference, true);
        canvas.DrawPath(boundary, shadow);
        canvas.DrawPath(boundary, edge);
        canvas.Restore();
    }

    /// <summary>
    /// Dims every part of the current floor the player has not yet walked near,
    /// with a soft edge, so explored ground stands out as "places I have been".
    /// </summary>
    private void DrawFog(SKCanvas canvas, SKRect view, float pixelsPerUnit, AppSettings settings)
    {
        if (!settings.FogOfWar || _explored is null || _layer is null || _region is null || settings.FogOfWarStrength <= 0.01) return;
        var key = (_region.MapId, _layer.Id, _explored.Version);
        if (_fogKey != key)
        {
            _fogPath?.Dispose();
            _fogPath = null;
            if (_explored.Cells(_region.MapId, _layer.Id) is { Count: > 0 } cells)
            {
                _fogPath = new SKPath();
                foreach (var cell in cells)
                {
                    var (x, z) = ExploredArea.Unpack(cell);
                    _fogPath.AddRect(SKRect.Create(x * ExploredArea.CellSize, z * ExploredArea.CellSize, ExploredArea.CellSize, ExploredArea.CellSize));
                }
            }
            _fogKey = key;
        }
        canvas.SaveLayer(view, null);
        using var fog = new SKPaint { Color = new SKColor(6, 8, 11, (byte)Math.Clamp((int)(settings.FogOfWarStrength * 255), 0, 255)) };
        canvas.DrawRect(view, fog);
        if (_fogPath is not null)
        {
            using var reveal = new SKPaint
            {
                BlendMode = SKBlendMode.DstOut, IsAntialias = true, Color = SKColors.Black,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(1.2f, 7f / pixelsPerUnit))
            };
            canvas.DrawPath(_fogPath, reveal);
        }
        canvas.Restore();
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

    private void DrawMarkers(SKCanvas canvas, Vector3 position, float radius, float pixelsPerUnit, float uprightRotation, double animationSeconds)
    {
        if (_region?.Markers is null || _layer is null) return;
        foreach (var marker in _region.Markers)
        {
            if (marker.LayerId != _layer.Id || MathF.Abs(marker.X - position.X) > radius + 4 || MathF.Abs(marker.Z - position.Z) > radius + 4) continue;
            if (marker.Kind == MapMarkerKind.Bonfire)
            {
                if (!_settings.ShowBonfires) continue;
                canvas.Save();
                canvas.Translate(marker.X, marker.Z);
                canvas.RotateRadians(uprightRotation);
                // The badge is about 30 px across at marker size 1.
                var scale = (float)_settings.MarkerScale * 1.5f / pixelsPerUnit;
                canvas.Scale(scale, scale);
                DrawBonfireIcon(canvas, marker, animationSeconds, 1f);
                canvas.Restore();
                continue;
            }
            if (!_settings.ShowUncollectedItems || _itemFlags?.Available != true
                || _itemFlags.SetFlags.Contains(marker.EventFlagId)) continue;
            DrawItem(canvas, marker, pixelsPerUnit, uprightRotation, animationSeconds, _settings);
        }
    }

    /// <summary>
    /// Draws a bonfire in icon units at the current origin: a cached, crisp
    /// sprite plus a live additive glow and drifting embers when lit.
    /// </summary>
    private void DrawBonfireIcon(SKCanvas canvas, MapMarker marker, double seconds, float opacity)
    {
        var symbol = BonfireGuidance.Symbol(marker, _bonfireFlags, _settings.ShowUnlitBonfires);
        if (symbol == BonfireSymbol.Landmark)
        {
            using var disc = new SKPaint { IsAntialias = true, Color = new SKColor(30, 26, 23, (byte)(245 * opacity)) };
            canvas.DrawCircle(0, 0, 9, disc);
            using var pin = new SKPaint { IsAntialias = true, Color = new SKColor(230, 212, 177, (byte)(255 * opacity)),
                Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
            canvas.DrawCircle(0, 0, 9, pin);
            canvas.DrawLine(0, -5, 0, 5, pin);
            canvas.DrawLine(-3, -2, 3, -2, pin);
            return;
        }
        bool lit = symbol == BonfireSymbol.Flame;
        var seed = Hash(marker.Id.Length, marker.EventFlagId);
        var color = ParseColor(_settings.BonfireColor, new SKColor(255, 155, 69));
        if (lit)
        {
            var phase = (float)(seconds * 5.2 + (seed & 31));
            var flicker = 1f + MathF.Sin(phase) * 0.06f + MathF.Sin(phase * 2.7f) * 0.03f;
            // A wide, warm halo so a lit bonfire is visible from across the map.
            var glowRadius = 22f * flicker;
            using var glowShader = SKShader.CreateRadialGradient(new SKPoint(0, -1), glowRadius,
                [color.WithAlpha((byte)(185 * opacity)), color.WithAlpha((byte)(95 * opacity)), color.WithAlpha((byte)(30 * opacity)), SKColors.Transparent],
                [0f, .38f, .7f, 1f], SKShaderTileMode.Clamp);
            using var glow = new SKPaint { Shader = glowShader, IsAntialias = true, BlendMode = SKBlendMode.Plus };
            canvas.DrawCircle(0, -1, glowRadius, glow);
        }

        var sprite = BonfireSprite(color, lit);
        using (var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * opacity * (lit ? 1f : 0.78f))) })
            canvas.DrawImage(sprite, new SKRect(-SpriteExtent, -SpriteExtent, SpriteExtent, SpriteExtent), Smooth, paint);
        if (!lit) return;

        using var ember = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus };
        for (int i = 0; i < 3; i++)
        {
            var t = (float)((seconds * 0.55 + i / 3.0 + (seed >> (i * 5) & 7) / 8.0) % 1.0);
            var x = MathF.Sin(i * 2.3f + t * 4.2f) * 2.6f + (i - 1) * 1.4f;
            var y = -3.5f - t * 11f;
            ember.Color = new SKColor(255, 206, 120, (byte)(220 * (1 - t) * opacity));
            canvas.DrawCircle(x, y, 0.75f * (1 - t * 0.5f), ember);
        }
    }

    private SKImage BonfireSprite(SKColor color, bool lit)
    {
        var key = ((uint)color, lit);
        if (_bonfireSprites.TryGetValue(key, out var cached)) return cached;
        var size = (int)(SpriteExtent * 2 * SpritePixelsPerUnit);
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(size / 2f, size / 2f);
        canvas.Scale(SpritePixelsPerUnit);
        PaintBonfire(canvas, color, lit);
        var image = surface.Snapshot();
        _bonfireSprites[key] = image;
        return image;
    }

    /// <summary>The coiled sword planted in an ash mound, on a dark badge so it reads on any ground.</summary>
    private static void PaintBonfire(SKCanvas canvas, SKColor ember, bool lit)
    {

        // Badge: soft drop shadow, dark disc and a fine rim in the ember colour.
        using (var drop = new SKPaint { Color = new SKColor(0, 0, 0, 150), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 0.9f) })
            canvas.DrawCircle(0.4f, 0.9f, 10.6f, drop);
        using (var discShader = SKShader.CreateRadialGradient(new SKPoint(0, -2), 11f,
                   [new SKColor(46, 36, 27, 242), new SKColor(16, 13, 10, 246)], [0f, 1f], SKShaderTileMode.Clamp))
        using (var disc = new SKPaint { Shader = discShader, IsAntialias = true })
            canvas.DrawCircle(0, 0, 10f, disc);
        // Lit: a solid rim in the ember colour. Unlit: a dashed grey ring, so the
        // two read differently at a glance even without colour.
        using (var dash = lit ? null : SKPathEffect.CreateDash([2.1f, 1.5f], 0))
        using (var rim = new SKPaint
               {
                   Color = lit ? ember.WithAlpha(190) : new SKColor(176, 174, 170, 225), IsAntialias = true,
                   Style = SKPaintStyle.Stroke, StrokeWidth = lit ? 0.8f : 1.1f, PathEffect = dash
               })
            canvas.DrawCircle(0, 0, 9.5f, rim);

        // Ash mound with a few charred bones.
        using (var ashShader = SKShader.CreateRadialGradient(new SKPoint(0, 4.2f), 6.5f,
                   lit ? [new SKColor(120, 104, 90), new SKColor(58, 50, 44)] : [new SKColor(108, 106, 102), new SKColor(52, 51, 49)],
                   [0f, 1f], SKShaderTileMode.Clamp))
        using (var ash = new SKPaint { Shader = ashShader, IsAntialias = true })
        using (var mound = new SKPath())
        {
            mound.MoveTo(-7f, 7.2f);
            mound.CubicTo(-5.6f, 2.6f, -2.2f, 2.0f, 0, 2.2f);
            mound.CubicTo(2.4f, 2.0f, 5.8f, 2.8f, 7f, 7.2f);
            mound.Close();
            canvas.DrawPath(mound, ash);
        }
        using (var bone = new SKPaint { Color = lit ? new SKColor(196, 178, 150, 220) : new SKColor(150, 148, 144, 200), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.75f, StrokeCap = SKStrokeCap.Round })
        {
            canvas.DrawLine(-5.4f, 6.2f, -1.8f, 4.6f, bone);
            canvas.DrawLine(5.2f, 6.0f, 2.0f, 4.9f, bone);
            canvas.DrawLine(-2.8f, 6.6f, 1.2f, 6.3f, bone);
        }

        if (lit)
        {
            // Three flame tongues, deep orange at the base to a pale core.
            var deep = new SKColor((byte)(ember.Red * 0.78f), (byte)(ember.Green * 0.34f), (byte)(ember.Blue * 0.2f));
            using var flameShader = SKShader.CreateLinearGradient(new SKPoint(0, 5.5f), new SKPoint(0, -8.5f),
                [deep, ember, new SKColor(255, 224, 150)], [0f, 0.55f, 1f], SKShaderTileMode.Clamp);
            using var flame = new SKPaint { Shader = flameShader, IsAntialias = true };
            using var flameEdge = new SKPaint { Color = new SKColor(20, 9, 3, 200), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.9f, StrokeJoin = SKStrokeJoin.Round };
            using var tongues = new SKPath();
            tongues.MoveTo(-4.6f, 4.8f);
            tongues.CubicTo(-6.2f, 1.2f, -4.2f, -1.4f, -3.4f, -3.6f);
            tongues.CubicTo(-2.6f, -1.8f, -1.6f, -1.2f, -1.2f, -2.4f);
            tongues.CubicTo(-1.4f, -5.2f, 0.6f, -7.4f, 0.9f, -9.4f);
            tongues.CubicTo(2.4f, -6.6f, 3.0f, -4.4f, 2.4f, -2.2f);
            tongues.CubicTo(3.4f, -2.8f, 4.0f, -3.8f, 4.2f, -4.6f);
            tongues.CubicTo(6.0f, -1.6f, 5.6f, 2.2f, 4.4f, 4.8f);
            tongues.Close();
            canvas.DrawPath(tongues, flameEdge);
            canvas.DrawPath(tongues, flame);
            using var core = new SKPaint { Color = new SKColor(255, 240, 196, 235), IsAntialias = true };
            using var corePath = new SKPath();
            corePath.MoveTo(-1.8f, 4.4f);
            corePath.CubicTo(-2.8f, 1.4f, -0.8f, -0.6f, 0.4f, -3.6f);
            corePath.CubicTo(1.6f, -1.2f, 2.6f, 1.6f, 1.6f, 4.4f);
            corePath.Close();
            canvas.DrawPath(corePath, core);
        }

        // The coiled sword: blade twisting down into the ash, guard and hilt above.
        var steel = lit ? new SKColor(214, 205, 186) : new SKColor(150, 148, 144);
        using (var outline = new SKPaint { Color = new SKColor(8, 6, 5, 240), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round })
        using (var blade = new SKPaint { Color = steel, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.25f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round })
        using (var edge = new SKPaint { Color = new SKColor(255, 250, 236, lit ? (byte)210 : (byte)120), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.45f, StrokeCap = SKStrokeCap.Round })
        using (var guard = new SKPaint { Color = lit ? new SKColor(186, 150, 86) : new SKColor(130, 126, 120), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.1f, StrokeCap = SKStrokeCap.Round })
        using (var coil = new SKPath())
        {
            coil.MoveTo(0.2f, 4.6f);
            coil.CubicTo(-0.9f, 2.4f, 1.4f, 0.6f, 0.4f, -1.4f);
            coil.CubicTo(-0.4f, -3.0f, 1.6f, -4.2f, 1.1f, -6.0f);
            canvas.DrawPath(coil, outline);
            canvas.DrawPath(coil, blade);
            using var highlight = new SKPath();
            highlight.MoveTo(0.75f, 3.8f);
            highlight.CubicTo(-0.3f, 2.2f, 1.9f, 0.6f, 0.9f, -1.3f);
            canvas.DrawPath(highlight, edge);
            // Cross-guard, grip and pommel.
            canvas.DrawLine(-1.5f, -6.2f, 3.6f, -5.6f, outline);
            canvas.DrawLine(-1.5f, -6.2f, 3.6f, -5.6f, guard);
            canvas.DrawLine(1.1f, -6.1f, 1.5f, -9.0f, outline);
            canvas.DrawLine(1.1f, -6.1f, 1.5f, -9.0f, guard);
            using var pommel = new SKPaint { Color = guard.Color, IsAntialias = true };
            canvas.DrawCircle(1.6f, -9.5f, 1.05f, outline);
            canvas.DrawCircle(1.6f, -9.5f, 0.75f, pommel);
        }
    }

    /// <summary>
    /// When the nearest bonfire is off the visible map, pins a small bonfire at
    /// the inside edge in its direction, with an arrow and its distance.
    /// </summary>
    private void DrawNearestBonfirePointer(SKCanvas canvas, SKImageInfo info, SKPoint center, float radius, float pixelsPerUnit, double seconds)
    {
        if (_region?.Markers is null || _player is null) return;
        var player = _player.Position;
        var target = BonfireGuidance.Nearest(_region.Markers, player, _bonfireFlags);
        var rotation = _settings.Orientation == MapOrientation.PlayerUp ? -_screenHeading : 0f;
        if (target is null || BonfireGuidance.Locate(target, player, _layer?.Id, pixelsPerUnit, rotation,
                _settings.Mask, radius - 30f) is not { } pointer) return;
        var direction = pointer.Direction;
        var anchor = new SKPoint(center.X + pointer.Offset.X, center.Y + pointer.Offset.Y);

        // Arrow pointing outward, just beyond the glyph.
        canvas.Save();
        canvas.Translate(anchor.X + direction.X * 15f, anchor.Y + direction.Y * 15f);
        canvas.RotateRadians(MathF.Atan2(direction.Y, direction.X));
        using (var arrow = new SKPath())
        using (var arrowFill = new SKPaint { Color = ParseColor(_settings.BonfireColor, new SKColor(255, 155, 69)).WithAlpha(230), IsAntialias = true })
        using (var arrowEdge = new SKPaint { Color = new SKColor(8, 6, 5, 230), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeJoin = SKStrokeJoin.Round })
        {
            arrow.MoveTo(4.5f, 0);
            arrow.LineTo(-2.5f, -4f);
            arrow.LineTo(-1f, 0);
            arrow.LineTo(-2.5f, 4f);
            arrow.Close();
            if (pointer.ShowBearing)
            {
                canvas.DrawPath(arrow, arrowEdge);
                canvas.DrawPath(arrow, arrowFill);
            }
        }
        canvas.Restore();

        canvas.Save();
        canvas.Translate(anchor.X, anchor.Y);
        var iconScale = 0.85f * (float)_settings.MarkerScale;
        canvas.Scale(iconScale, iconScale);
        DrawBonfireIcon(canvas, target, seconds, 0.9f);
        canvas.Restore();

        var metres = pointer.Distance;
        var label = metres >= 1000 ? $"{metres / 1000:0.0} km" : $"{metres:0} m";
        if (pointer.FloorCue is { } cue) label = $"{cue} · {label}";
        // Text goes on the inward side, far enough out of the glyph's way.
        var textAt = new SKPoint(anchor.X - direction.X * 30f, anchor.Y - direction.Y * 22f + 4f);
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 11f);
        DrawOutlinedText(canvas, label, textAt, font, new SKColor(246, 226, 186));
        if (info.Width >= 320 && !string.IsNullOrWhiteSpace(target.Label))
        {
            using var small = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 9.5f);
            // Labels read "Area - Bonfire"; the area is already on screen.
            var name = target.Label.Split(" - ", StringSplitOptions.TrimEntries)[^1];
            DrawOutlinedText(canvas, name, new SKPoint(textAt.X, textAt.Y + 11f), small, new SKColor(214, 198, 166));
        }
    }

    private static void DrawOutlinedText(SKCanvas canvas, string text, SKPoint at, SKFont font, SKColor color)
    {
        using var stroke = new SKPaint { Color = new SKColor(6, 5, 4, 225), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f, StrokeJoin = SKStrokeJoin.Round };
        using var fill = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawText(text, at.X, at.Y, SKTextAlign.Center, font, stroke);
        canvas.DrawText(text, at.X, at.Y, SKTextAlign.Center, font, fill);
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

    private static SKColor Blend(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t), a.Alpha);

    private static SKColor WithOpacity(SKColor color, float opacity) =>
        color.WithAlpha((byte)Math.Clamp((int)MathF.Round(color.Alpha * opacity), 0, 255));
}
