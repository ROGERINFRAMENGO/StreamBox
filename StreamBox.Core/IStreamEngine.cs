namespace StreamBox.Core;

/// <summary>
/// Motor de transmissão. O servidor de rede consome esta abstração, sem conhecer captura ou encoder.
/// </summary>
public interface IStreamEngine
{
    StreamStatus Status { get; }

    bool IsStreaming { get; }

    /// <summary>Blocos de vídeo já codificados em MP4 fragmentado, prontos para fan-out.</summary>
    event Action<byte[]>? DataAvailable;

    /// <summary>
    /// Segmento inicial do MP4 (ftyp + moov). Receptores que conectam no meio da sessão
    /// precisam dele antes dos fragmentos.
    /// </summary>
    ReadOnlyMemory<byte>? InitSegment { get; }

    Task StartAsync(StreamSettings settings, CancellationToken cancellationToken = default);

    Task StopAsync();
}
