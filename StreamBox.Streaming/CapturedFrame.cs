namespace StreamBox.Streaming;

/// <summary>
/// Quadro capturado. O buffer pertence a um pool e deve ser devolvido com <see cref="Release"/>
/// depois de consumido, para evitar alocação de ~8 MB por quadro.
/// </summary>
public sealed class CapturedFrame
{
    private Action<CapturedFrame>? _release;

    public CapturedFrame(byte[] buffer, int length, int width, int height, long timestamp, Action<CapturedFrame> release)
    {
        Buffer = buffer;
        Length = length;
        Width = width;
        Height = height;
        Timestamp = timestamp;
        _release = release;
    }

    public byte[] Buffer { get; }
    public int Length { get; }
    public int Width { get; }
    public int Height { get; }
    public long Timestamp { get; }

    public void Release() => Interlocked.Exchange(ref _release, null)?.Invoke(this);
}
