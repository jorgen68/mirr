namespace mirr.Media;

public enum CodecFamily
{
    Hevc,
    Avc,
}

public sealed record CodecDescription(CodecFamily Family, string CodecString)
{
    public static CodecDescription? Parse(string? codecString, string? base64Record)
    {
        if (string.IsNullOrWhiteSpace(codecString) || string.IsNullOrWhiteSpace(base64Record))
        {
            return null;
        }
        try
        {
            if (Convert.FromBase64String(base64Record).Length < 8)
            {
                return null;
            }
        }
        catch (FormatException)
        {
            return null;
        }

        string normalized = codecString.Trim();
        CodecFamily family = normalized.StartsWith("hvc", StringComparison.OrdinalIgnoreCase)
            ? CodecFamily.Hevc
            : normalized.StartsWith("avc", StringComparison.OrdinalIgnoreCase)
                ? CodecFamily.Avc
                : CodecFamily.Hevc;
        return new CodecDescription(family, normalized);
    }
}

public static class NalUtils
{
    public static byte[] ToAnnexB(IReadOnlyList<byte[]> nals)
    {
        int total = 0;
        foreach (byte[] nal in nals)
        {
            total += nal.Length + 4;
        }
        byte[] result = new byte[total];
        int offset = 0;
        foreach (byte[] nal in nals)
        {
            result[offset++] = 0;
            result[offset++] = 0;
            result[offset++] = 0;
            result[offset++] = 1;
            nal.CopyTo(result, offset);
            offset += nal.Length;
        }
        return result;
    }

    public static List<byte[]> SplitLengthPrefixed(ReadOnlySpan<byte> payload)
    {
        List<byte[]> nals = [];
        int cursor = 0;
        while (cursor + 4 <= payload.Length)
        {
            int length = (payload[cursor] << 24) | (payload[cursor + 1] << 16) | (payload[cursor + 2] << 8) | payload[cursor + 3];
            cursor += 4;
            if (length < 0 || length > payload.Length - cursor)
            {
                break;
            }
            nals.Add(payload.Slice(cursor, length).ToArray());
            cursor += length;
        }
        return nals;
    }
}
