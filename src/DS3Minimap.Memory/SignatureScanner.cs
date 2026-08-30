using System.Globalization;

namespace DS3Minimap.Memory;

public sealed class BytePattern
{
    public BytePattern(string pattern)
    {
        var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Bytes = new byte?[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
            Bytes[i] = tokens[i] is "?" or "??" ? null : byte.Parse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public byte?[] Bytes { get; }
}

public static class SignatureScanner
{
    public static int Find(ReadOnlySpan<byte> haystack, BytePattern needle)
    {
        var pattern = needle.Bytes;
        if (pattern.Length == 0 || haystack.Length < pattern.Length) return -1;
        for (var start = 0; start <= haystack.Length - pattern.Length; start++)
        {
            var matches = true;
            for (var index = 0; index < pattern.Length; index++)
            {
                if (pattern[index].HasValue && pattern[index]!.Value != haystack[start + index])
                {
                    matches = false;
                    break;
                }
            }
            if (matches) return start;
        }
        return -1;
    }
}

