namespace StreamBox.Core;

/// <summary>Qualidade de transmissão. O tamanho real é derivado do tamanho da janela.</summary>
public sealed record QualityPreset(string Id, string Label, int MaxHeight, int Fps, int BitrateKbps)
{
    public static readonly IReadOnlyList<QualityPreset> All =
    [
        new("1080p60", "1080p · 60 FPS", 1080, 60, 14000),
        new("1080p30", "1080p · 30 FPS", 1080, 30, 8000),
        new("720p60", "720p · 60 FPS", 720, 60, 6000),
        new("720p30", "720p · 30 FPS", 720, 30, 3500),
    ];

    public static QualityPreset Default => All[1];

    public static QualityPreset ById(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;

    /// <summary>
    /// Bitrate ajustado à resolução real da janela: uma janela menor que 1080p não precisa
    /// do orçamento cheio (desperdício de banda) e uma janela maior não deve ficar subalimentada.
    /// </summary>
    public int BitrateFor(int width, int height)
    {
        const double referencePixels = 1920.0 * 1080.0;

        var pixels = (double)width * height;
        if (pixels <= 0) return BitrateKbps;

        var scale = Math.Pow(Math.Min(1.0, pixels / referencePixels), 0.7);
        var scaled = BitrateKbps * scale;

        var floor = Math.Min(BitrateKbps, 1200.0);
        return (int)Math.Round(Math.Clamp(scaled, floor, BitrateKbps));
    }
}

/// <summary>Resolução efetiva de captura (sempre par, no máximo 1920 de largura).</summary>
public readonly record struct CaptureSize(int Width, int Height)
{
    public const int MaxWidth = 1920;

    public static CaptureSize Fit(int sourceWidth, int sourceHeight, int maxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return new CaptureSize(1280, 720);

        var scale = Math.Min(1.0, Math.Min((double)MaxWidth / sourceWidth, (double)maxHeight / sourceHeight));
        var width = Math.Max(2, (int)Math.Round(sourceWidth * scale / 2.0) * 2);
        var height = Math.Max(2, (int)Math.Round(sourceHeight * scale / 2.0) * 2);
        return new CaptureSize(width, height);
    }
}
