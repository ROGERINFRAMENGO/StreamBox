namespace StreamBox.Streaming;

public sealed record PreviewFrame(byte[] Bgra, int Width, int Height);

/// <summary>Redução por amostragem de vizinho mais próximo, usada apenas no preview da UI.</summary>
public static class FrameDownscaler
{
    public static PreviewFrame? Subsample(byte[] source, int width, int height, int targetWidth, byte[]? reuse = null)
    {
        if (width < 2 || height < 2 || source.Length < width * height * 4) return null;

        var step = Math.Max(1, width / Math.Max(1, targetWidth));
        var outWidth = width / step;
        var outHeight = height / step;
        if (outWidth < 2 || outHeight < 2) return null;

        var stride = outWidth * 4;
        var size = stride * outHeight;

        // O preview roda várias vezes por segundo: reaproveitar o buffer evita ~4 MB/s
        // de alocação no LOH (que só é liberado em coletas Gen2).
        var result = reuse is not null && reuse.Length == size ? reuse : new byte[size];

        for (var y = 0; y < outHeight; y++)
        {
            var sourceRow = y * step * width * 4;
            var targetRow = y * stride;
            for (var x = 0; x < outWidth; x++)
            {
                var src = sourceRow + x * step * 4;
                var dst = targetRow + x * 4;
                result[dst] = source[src];
                result[dst + 1] = source[src + 1];
                result[dst + 2] = source[src + 2];
                result[dst + 3] = 255;
            }
        }

        return new PreviewFrame(result, outWidth, outHeight);
    }
}
