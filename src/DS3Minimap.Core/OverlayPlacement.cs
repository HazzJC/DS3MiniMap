namespace DS3Minimap.Core;

public readonly record struct OverlayPlacement(int X, int Y, int Size)
{
    public static OverlayPlacement Fit(int x, int y, int width, int height, double scale, AppSettings settings)
    {
        int available = Math.Max(1, Math.Min(width, height));
        int margin = Math.Clamp((int)Math.Round(settings.EdgeMargin * scale), 0, available / 8);
        int size = Math.Clamp((int)Math.Round(settings.Size * scale), 1, available - 2 * margin);
        bool left = settings.Anchor is OverlayAnchor.TopLeft or OverlayAnchor.BottomLeft;
        bool top = settings.Anchor is OverlayAnchor.TopLeft or OverlayAnchor.TopRight;
        return new(x + (left ? margin : width - size - margin), y + (top ? margin : height - size - margin), size);
    }
}
