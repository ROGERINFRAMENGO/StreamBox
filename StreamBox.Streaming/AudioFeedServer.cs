using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace StreamBox.Streaming;

/// <summary>
/// Recebe o PCM do loopback por um socket local e entrega ao ffmpeg como segunda entrada.
/// Evita disputa pelo stdin, que já é usado pelo vídeo bruto.
/// O canal se autoalimenta com silêncio em tempo real: sem isso, um computador em silêncio
/// deixa o ffmpeg bloqueado esperando a segunda entrada e o vídeo para de fluir.
/// </summary>
public sealed class AudioFeedServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<NetworkStream> _client = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<byte[]> _pending = new();
    private readonly object _gate = new();

    private readonly int _bytesPerSecond;
    private readonly int _blockBytes;
    private readonly long _maxLeadBytes;
    private readonly byte[] _silence;

    private NetworkStream? _stream;
    private Task? _writer;
    private long _producedBytes;
    private long _queueBytes;
    private long _clockStartTick;
    private bool _disposed;

    /// <param name="sampleRate">Taxa do PCM enviado ao ffmpeg (já normalizado).</param>
    /// <param name="channels">Canais do PCM (normalmente 2).</param>
    /// <param name="bytesPerSample">2 para s16le.</param>
    public AudioFeedServer(int sampleRate = 48000, int channels = 2, int bytesPerSample = 2)
    {
        _bytesPerSecond = Math.Max(1, sampleRate * channels * bytesPerSample);
        _blockBytes = Math.Max(bytesPerSample * channels, _bytesPerSecond / 100); // 10 ms
        _maxLeadBytes = _bytesPerSecond / 2L; // tolerância de 500 ms
        _silence = new byte[_blockBytes];

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(1);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public int Port { get; }

    private async Task AcceptAsync()
    {
        try
        {
            var socket = await _listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            _stream = stream;
            _client.TrySetResult(stream);
            _writer = Task.Run(() => WriteLoopAsync(stream, _cts.Token));
        }
        catch (Exception)
        {
            _client.TrySetCanceled();
        }
    }

    public Task<NetworkStream> WaitForClientAsync(TimeSpan timeout) => _client.Task.WaitAsync(timeout);

    /// <summary>Enfileira PCM capturado do sistema. Nunca bloqueia a thread de captura.</summary>
    public void Write(byte[] buffer, int count)
    {
        if (_disposed || count <= 0) return;

        // Cópia obrigatória: o buffer de captura é reaproveitado no próximo bloco.
        Enqueue(buffer.AsSpan(0, count).ToArray());
    }

    /// <summary>
    /// Insere silêncio no início do fluxo de áudio para alinhar a linha do tempo com o vídeo
    /// (o áudio começa a ser capturado depois do primeiro quadro). Também inicia o relógio.
    /// </summary>
    public void WriteSilence(double seconds, int sampleRate, int channels, int bytesPerSample)
    {
        if (_disposed || sampleRate <= 0 || channels <= 0) return;

        StartClock();

        var bytesPerSecond = sampleRate * channels * bytesPerSample;
        var total = (int)Math.Round(Math.Clamp(seconds, 0, 2.0) * bytesPerSecond);
        if (total <= 0) return;

        var blockBytes = Math.Max(bytesPerSample * channels, bytesPerSecond / 50);
        var silence = new byte[blockBytes];

        lock (_gate)
        {
            var written = 0;
            while (written < total)
            {
                var size = Math.Min(blockBytes, total - written);
                EnqueueLocked(size == blockBytes ? silence : silence.AsSpan(0, size).ToArray());
                written += size;
            }
        }
    }

    /// <summary>Liga o relógio de tempo real sem inserir nada (usado quando não há atraso a compensar).</summary>
    public void StartClock()
    {
        if (Interlocked.Read(ref _clockStartTick) == 0)
            Interlocked.CompareExchange(ref _clockStartTick, Stopwatch.GetTimestamp(), 0);
    }

    private void Enqueue(byte[] block)
    {
        lock (_gate) { EnqueueLocked(block); }
    }

    private void EnqueueLocked(byte[] block)
    {
        if (_disposed) return;

        // Áudio atrasado não pode crescer sem limite: descarta o mais antigo.
        while (_pending.Count > 0 && _queueBytes + block.Length > _bytesPerSecond)
        {
            var dropped = _pending.Dequeue();
            _queueBytes -= dropped.Length;
            Interlocked.Add(ref _producedBytes, -dropped.Length);
        }

        _pending.Enqueue(block);
        _queueBytes += block.Length;
        Interlocked.Add(ref _producedBytes, block.Length);
    }

    private async Task WriteLoopAsync(NetworkStream stream, CancellationToken token)
    {
        var started = false;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (Interlocked.Read(ref _clockStartTick) == 0) StartClock();

                byte[]? block = null;
                lock (_gate)
                {
                    if (_pending.Count > 0)
                    {
                        block = _pending.Dequeue();
                        _queueBytes -= block.Length;
                    }
                }

                if (block is null)
                {
                    block = FillGap();
                    if (block is null)
                    {
                        await Task.Delay(4, token).ConfigureAwait(false);
                        continue;
                    }
                }

                if (!started) started = true;
                await stream.WriteAsync(block, token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Conexão encerrada quando a sessão termina.
        }
    }

    /// <summary>
    /// Mantém o tempo de mídia do áudio colado ao relógio: se o sistema está em silêncio
    /// (ou o dispositivo não entrega dados), gera silêncio equivalente ao tempo decorrido.
    /// </summary>
    private byte[]? FillGap()
    {
        var start = Interlocked.Read(ref _clockStartTick);
        if (start == 0) return null;

        var elapsed = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
        var expected = (long)(elapsed * _bytesPerSecond);
        var produced = Interlocked.Read(ref _producedBytes);
        var deficit = expected - produced;

        if (deficit < _blockBytes)
        {
            // Áudio adiantado (dados atrasados do dispositivo): não empurra a linha do tempo.
            if (deficit < -_maxLeadBytes)
            {
                lock (_gate)
                {
                    while (_pending.Count > 0 && -deficit > _maxLeadBytes)
                    {
                        var dropped = _pending.Dequeue();
                        _queueBytes -= dropped.Length;
                        Interlocked.Add(ref _producedBytes, -dropped.Length);
                        deficit += dropped.Length;
                    }
                }
            }

            return null;
        }

        var size = (int)Math.Min(Math.Min(deficit, _maxLeadBytes), _blockBytes * 10);
        size -= size % (_bytesPerSecond / 100); // múltiplo de 10 ms
        if (size < _blockBytes) size = _blockBytes;

        Interlocked.Add(ref _producedBytes, size);
        return size == _blockBytes ? _silence : new byte[size];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _stream?.Dispose(); } catch { }

        lock (_gate) { _pending.Clear(); _queueBytes = 0; }
        _cts.Dispose();
    }
}
