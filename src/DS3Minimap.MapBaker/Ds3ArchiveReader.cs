using SoulsFormats;
using SoulsFormats.Util;

namespace DS3Minimap.MapBaker;

internal sealed class Ds3ArchiveReader : IDisposable
{
    private readonly List<(BHD5 Header, FileStream Data)> _archives = [];

    internal Ds3ArchiveReader(string gameDirectory)
    {
        foreach (var name in Enumerable.Range(1, 5).Select(index => $"Data{index}"))
        {
            var bhdPath = Path.Combine(gameDirectory, name + ".bhd");
            var bdtPath = Path.Combine(gameDirectory, name + ".bdt");
            if (!File.Exists(bhdPath) || !File.Exists(bdtPath)) throw new FileNotFoundException($"Required archive {name} is missing.");
            using var decrypted = CryptographyUtility.DecryptRsaParallel(bhdPath, ArchiveKeys.For(name), Environment.ProcessorCount);
            var header = BHD5.Read(decrypted.ToArray(), BHD5.Game.DarkSouls3);
            _archives.Add((header, new FileStream(bdtPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.RandomAccess)));
        }
    }

    internal byte[] Read(string virtualPath)
    {
        var normalized = virtualPath.Replace('\\', '/').ToLowerInvariant();
        var hash = SFUtil.FromPathHash(normalized);
        foreach (var (header, data) in _archives)
        {
            var bucket = header.Buckets[(int)(hash % (uint)header.Buckets.Count)];
            var file = bucket.FirstOrDefault(candidate => candidate.FileNameHash == hash);
            if (file is null) continue;
            var bytes = file.ReadFileThreaded(data);
            if (file.UnpaddedFileSize > 0 && file.UnpaddedFileSize < bytes.Length) Array.Resize(ref bytes, checked((int)file.UnpaddedFileSize));
            return bytes;
        }
        throw new FileNotFoundException($"{virtualPath} was not found in the base-game archives.");
    }

    public void Dispose()
    {
        foreach (var (_, data) in _archives) data.Dispose();
        _archives.Clear();
    }
}

