namespace StreamBox.Streaming;

/// <summary>Perfil de encoder H.264 do ffmpeg.</summary>
public sealed record EncoderProfile(
    string Name,
    string DisplayName,
    bool Hardware,
    Func<int, int, string> VideoArguments)
{
    /// <summary>Argumentos de vídeo para o bitrate (kbps) e FPS desejados.</summary>
    public string BuildVideoArguments(int bitrateKbps, int fps) => VideoArguments(bitrateKbps, fps);

    public static IReadOnlyList<EncoderProfile> All { get; } =
    [
        new("h264_nvenc", "NVIDIA NVENC", true,
            (kbps, fps) => $"-preset p1 -tune ll -rc cbr -b:v {kbps}k -maxrate {kbps}k -bufsize {Math.Max(1000, kbps)}k -profile:v high -level 4.2 -g {fps} -bf 0"),

        new("h264_qsv", "Intel Quick Sync", true,
            (kbps, fps) => $"-preset veryfast -b:v {kbps}k -maxrate {kbps}k -bufsize {Math.Max(1000, kbps)}k -profile:v high -g {fps}"),

        new("h264_amf", "AMD AMF", true,
            (kbps, fps) => $"-quality speed -rc cbr -b:v {kbps}k -maxrate {kbps}k -bufsize {Math.Max(1000, kbps)}k -profile:v high -g {fps}"),

        new("h264_mf", "Media Foundation", true,
            (kbps, fps) => $"-b:v {kbps}k -profile:v high -g {fps}"),

        new("libx264", "x264 (CPU)", false,
            (kbps, fps) => $"-preset ultrafast -tune zerolatency -b:v {kbps}k -maxrate {kbps}k -bufsize {Math.Max(1000, kbps)}k -profile:v high -level 4.2 -g {fps} -sc_threshold 0"),
    ];

    public static EncoderProfile ByName(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All[^1];
}
