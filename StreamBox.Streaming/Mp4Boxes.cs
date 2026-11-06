using System.Buffers.Binary;

namespace StreamBox.Streaming;

/// <summary>Extrai informações mínimas do MP4 fragmentado produzido pelo ffmpeg.</summary>
internal static class Mp4Boxes
{
    /// <summary>
    /// Lê o codec real (perfil e nível) do box avcC dentro do moov.
    /// O MSS/MSE exige que a string MIME corresponda exatamente ao bitstream.
    /// </summary>
    public static string? TryReadAvcCodec(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset + 8 <= data.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            if (size < 8 || size > data.Length - offset) break;

            var type = data.Slice(offset + 4, 4);
            if (type[0] == (byte)'m' && type[1] == (byte)'o' && type[2] == (byte)'o' && type[3] == (byte)'v')
                return FindAvcC(data.Slice(offset, (int)size));

            offset += (int)size;
        }

        return null;
    }

    private static string? FindAvcC(ReadOnlySpan<byte> moov)
    {
        for (var i = 0; i + 8 <= moov.Length; i++)
        {
            if (moov[i] != (byte)'a' || moov[i + 1] != (byte)'v' ||
                moov[i + 2] != (byte)'c' || moov[i + 3] != (byte)'C')
                continue;

            // avcC: versão, perfil, compatibilidade, nível
            var profile = moov[i + 5];
            var compatibility = moov[i + 6];
            var level = moov[i + 7];
            return $"avc1.{profile:X2}{compatibility:X2}{level:X2}";
        }

        return null;
    }
}
