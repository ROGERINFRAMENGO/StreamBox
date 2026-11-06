namespace StreamBox.Core;

/// <summary>Estado público da transmissão, consumido pela UI do desktop e pelo receptor web.</summary>
public sealed class StreamStatus
{
    public bool Streaming { get; init; }
    public string? WindowTitle { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? Encoder { get; init; }
    public string? QualityId { get; init; }

    /// <summary>Codec reportado ao receptor web, no formato MSE (ex.: avc1.64002A).</summary>
    public string Codec { get; private set; } = "avc1.64002A";
    public int TargetFps { get; init; }
    public bool Audio { get; init; }

    /// <summary>FPS efetivamente entregues ao encoder (inclui repetições para manter a cadência).</summary>
    public double MeasuredFps { get; init; }

    /// <summary>FPS que a captura conseguiu produzir (limite real da janela/GPU).</summary>
    public double SourceFps { get; init; }

    public long TotalFrames { get; init; }
    public long DroppedFrames { get; init; }

    /// <summary>Bitrate medido na saída do encoder, em kbps.</summary>
    public double BitrateKbps { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public double UptimeSeconds =>
        StartedAt is null ? 0 : Math.Max(0, (DateTimeOffset.UtcNow - StartedAt.Value).TotalSeconds);

    public static StreamStatus Idle { get; } = new();

    /// <summary>Cópia com o codec real lido do bitstream, para o receptor negociar o MSE corretamente.</summary>
    public StreamStatus WithCodec(string codec)
    {
        var copy = (StreamStatus)MemberwiseClone();
        copy.Codec = codec;
        return copy;
    }

    public StreamStatus WithMeasured(double fps, double sourceFps, double kbps, long frames, long dropped) => new()
    {
        SourceFps = sourceFps,
        Streaming = Streaming,
        WindowTitle = WindowTitle,
        Width = Width,
        Height = Height,
        Encoder = Encoder,
        QualityId = QualityId,
        Codec = Codec,
        TargetFps = TargetFps,
        Audio = Audio,
        MeasuredFps = fps,
        BitrateKbps = kbps,
        TotalFrames = frames,
        DroppedFrames = dropped,
        StartedAt = StartedAt,
    };
}
