namespace StreamBox.Streaming;

/// <summary>
/// Normaliza o PCM do loopback para s16le estéreo. O dispositivo pode entregar float 32 bits
/// com 8 canais (com canais não usados contendo NaN/Inf), o que faz o encoder AAC recusar quadros.
/// </summary>
public static class PcmConverter
{
    /// <summary>Bytes por quadro de saída (2 canais × 2 bytes).</summary>
    public const int OutputFrameBytes = 4;

    /// <summary>
    /// Converte <paramref name="count"/> bytes de <paramref name="source"/> em s16le estéreo dentro de
    /// <paramref name="destination"/>. Devolve quantos bytes foram escritos.
    /// </summary>
    public static int ToStereo16(
        byte[] source, int count, int channels, int bytesPerSample, bool isFloat, byte[] destination)
    {
        if (count <= 0 || channels <= 0 || bytesPerSample < 2) return 0;

        var frameBytes = bytesPerSample * channels;
        var frames = Math.Min(count / frameBytes, destination.Length / OutputFrameBytes);
        if (frames <= 0) return 0;

        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * frameBytes;

            var left = ReadSample(source, offset, bytesPerSample, isFloat);
            var right = channels > 1 ? ReadSample(source, offset + bytesPerSample, bytesPerSample, isFloat) : left;

            var l = ToPcm16(left);
            var r = ToPcm16(right);

            destination[frame * OutputFrameBytes + 0] = (byte)l;
            destination[frame * OutputFrameBytes + 1] = (byte)(l >> 8);
            destination[frame * OutputFrameBytes + 2] = (byte)r;
            destination[frame * OutputFrameBytes + 3] = (byte)(r >> 8);
        }

        return frames * OutputFrameBytes;
    }

    private static float ReadSample(byte[] source, int offset, int bytesPerSample, bool isFloat)
    {
        if (offset + bytesPerSample > source.Length) return 0f;

        if (!isFloat)
        {
            var value = BitConverter.ToInt16(source, offset) / 32768f;
            return float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
        }

        var sample = BitConverter.ToSingle(source, offset);
        return float.IsNaN(sample) || float.IsInfinity(sample) ? 0f : sample;
    }

    private static short ToPcm16(float value)
    {
        if (float.IsNaN(value)) return 0;
        if (value > 1f) value = 1f;
        else if (value < -1f) value = -1f;

        return (short)(value * 32767f);
    }
}
