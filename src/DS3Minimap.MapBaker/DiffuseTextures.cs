using System.Numerics;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using BCnEncoder.Shared.ImageFiles;
using SoulsFormats;

namespace DS3Minimap.MapBaker;

internal sealed record DiffuseTexture(int Width, int Height, ColorRgba32[] Pixels)
{
    /// <summary>
    /// Bilinear, wrapping sample. DS3 map materials repeat their UVs, including
    /// across negative coordinates. Returns RGBA in 0-255 floating point.
    /// </summary>
    internal Vector4 Sample(float u, float v)
    {
        float fx = (u - MathF.Floor(u)) * Width - 0.5f;
        float fy = (v - MathF.Floor(v)) * Height - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        int Wrap(int value, int size) => ((value % size) + size) % size;
        int xa = Wrap(x0, Width), xb = Wrap(x0 + 1, Width);
        int ya = Wrap(y0, Height), yb = Wrap(y0 + 1, Height);
        static Vector4 Load(ColorRgba32 c) => new(c.r, c.g, c.b, c.a);
        var top = Vector4.Lerp(Load(Pixels[ya * Width + xa]), Load(Pixels[ya * Width + xb]), tx);
        var bottom = Vector4.Lerp(Load(Pixels[yb * Width + xa]), Load(Pixels[yb * Width + xb]), tx);
        return Vector4.Lerp(top, bottom, ty);
    }
}

internal static class DiffuseTextures
{
    internal static Dictionary<string, DiffuseTexture> Load(Ds3ArchiveReader archives, string area)
    {
        var result = new Dictionary<string, DiffuseTexture>(StringComparer.OrdinalIgnoreCase);
        // Release each archive bank before opening the next. Only small diffuse
        // mipmaps survive; the 256px level is ample at 8 map pixels per unit.
        // Reading the bank is sequential; decompressing and decoding is not.
        for (int bank = 0; bank < 4; bank++)
        {
            var prefix = $"/map/{area}/{area}_{bank:D4}";
            var files = new List<Memory<byte>>();
            using (var binder = new BXF4Reader(archives.Read(prefix + ".tpfbhd"), archives.Read(prefix + ".tpfbdt")))
                foreach (var file in binder.Files)
                {
                    var name = Path.GetFileName(file.Name);
                    if (name != null && name.EndsWith("_a.tpf.dcx", StringComparison.OrdinalIgnoreCase)) files.Add(binder.ReadFile(file));
                }
            var decoded = new List<(string Name, DiffuseTexture Texture)>[files.Count];
            Parallel.For(0, files.Count, () => new BcDecoder { Options = { IsParallel = false } }, (i, _, decoder) =>
            {
                decoded[i] = [];
                foreach (var texture in TPF.Read(files[i]).Textures)
                {
                    try
                    {
                        using var data = new MemoryStream(texture.Bytes, false);
                        var dds = DdsFile.Load(data);
                        var levels = dds.Faces[0].MipMaps;
                        var mip = levels.FirstOrDefault(m => Math.Max(m.Width, m.Height) <= 256) ?? levels[^1];
                        var format = decoder.GetFormat(dds);
                        // HDR (BC6H) maps are emissive/lighting data, not ground colour.
                        if (format is CompressionFormat.Unknown or CompressionFormat.Bc6U or CompressionFormat.Bc6S) continue;
                        var pixels = decoder.DecodeRaw(mip.Data, (int)mip.Width, (int)mip.Height, format);
                        decoded[i].Add((texture.Name, new((int)mip.Width, (int)mip.Height, pixels)));
                    }
                    catch (Exception exception) when (exception is NotSupportedException or InvalidDataException
                        or ArgumentException or IndexOutOfRangeException or EndOfStreamException)
                    {
                        // One unreadable texture only leaves its surfaces untextured.
                    }
                }
                return decoder;
            }, _ => { });
            // Duplicate names use archive order, independent of decoder completion.
            foreach (var file in decoded)
            foreach (var entry in file)
                result[entry.Name] = entry.Texture;
        }
        return result;
    }
}
