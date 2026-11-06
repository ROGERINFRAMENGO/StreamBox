using System.Diagnostics;
using System.Runtime.InteropServices;
using StreamBox.Core;

namespace StreamBox.Streaming;

/// <summary>
/// Orquestra captura de janela, áudio do sistema e encoder ffmpeg.
/// Um único fluxo MP4 fragmentado sai pelo evento <see cref="DataAvailable"/>.
/// </summary>
public sealed class FfmpegStreamEngine : IStreamEngine, IAsyncDisposable
{
    private static EncoderProfile? preferredEncoder;

    private readonly object gate = new();
    private readonly List<byte> initBuffer = new();

    private WindowCapture? capture;
    private FfmpegEncoder? encoder;
    private LoopbackAudioCapture? audio;
    private AudioFeedServer? audioServer;
    private Task? pacerTask;

    // Cadência de envio: só o quadro mais recente interessa; os demais são substituídos.
    private readonly object frameGate = new();
    private CapturedFrame? latestFrame;
    private CapturedFrame? heldFrame;
    private CancellationTokenSource? cts;
    private System.Threading.Timer? statsTimer;
    private Action? captureClosedHandler;

    private StreamStatus status = StreamStatus.Idle;
    private long frames;
    private long framesSent;
    private long audioBytes;
    private long writeTicks;
    private long writeCount;
    private long dropped;
    private long bytesOut;
    private long lastFrames;
    private long lastSent;
    private long lastBytes;
    private long lastPreviewTick;
    private readonly byte[]?[] previewBuffers = new byte[2][];
    private int previewBufferIndex;
    private long firstFrameTick;
    private DateTime lastStatsAt = DateTime.UtcNow;
    private bool initParsed;
    private byte[]? initSegment;

    public event Action<byte[]>? DataAvailable;

    public event Action<PreviewFrame>? PreviewAvailable;

    public StreamStatus Status => status;

    public bool IsStreaming => status.Streaming;

    public ReadOnlyMemory<byte>? InitSegment => initSegment;

    public async Task StartAsync(StreamSettings settings, CancellationToken cancellationToken = default)
    {
        if (IsStreaming) throw new InvalidOperationException("Já existe uma transmissão em andamento.");

        var ffmpegPath = FfmpegEncoder.ResolvePath();

        WindowCapture? newCapture = null;
        LoopbackAudioCapture? newAudio = null;
        AudioFeedServer? newAudioServer = null;
        FfmpegEncoder? newEncoder = null;
        CancellationTokenSource? newCts = null;

        try
        {
            newCapture = new WindowCapture(settings.WindowHandle, settings.Quality.MaxHeight, settings.Quality.Fps);

            // O preset é um teto: o bitrate acompanha a resolução real da janela.
            var bitrateKbps = settings.Quality.BitrateFor(newCapture.Width, newCapture.Height);

            preferredEncoder ??= await FfmpegEncoder
                .SelectAsync(ffmpegPath, newCapture.Width, newCapture.Height, settings.Quality.Fps, bitrateKbps, cancellationToken)
                .ConfigureAwait(false);

            var withAudio = false;
            var sampleRate = 48000;
            var channels = 2;
            var sampleFormat = "f32le";

            if (settings.IncludeAudio)
            {
                newAudio = new LoopbackAudioCapture();
                if (newAudio.TryInitialize() && newAudio.Format is not null)
                {
                    var format = newAudio.Format;

                    // A captura normaliza para s16le estéreo na taxa nativa do dispositivo.
                    sampleRate = Math.Clamp(format.SampleRate, 8000, 192000);
                    channels = 2;
                    sampleFormat = "s16le";

                    AppLog.Info($"Áudio do sistema: {format.SampleRate} Hz, {format.Channels} canais, " +
                                $"{format.BitsPerSample} bits ({format.Encoding}) → s16le estéreo");

                    newAudioServer = new AudioFeedServer(sampleRate, channels, 2);
                    var feed = newAudioServer;
                    newAudio.DataAvailable += (buffer, count) =>
                    {
                        Interlocked.Add(ref audioBytes, count);
                        feed.Write(buffer, count);
                    };
                    withAudio = true;
                }
                else
                {
                    newAudio.Dispose();
                    newAudio = null;
                    AppLog.Warn("Áudio solicitado, mas nenhum dispositivo de saída foi encontrado.");
                }
            }

            var options = new EncodeOptions
            {
                Width = newCapture.Width,
                Height = newCapture.Height,
                Fps = settings.Quality.Fps,
                BitrateKbps = bitrateKbps,
                Encoder = preferredEncoder,
                WithAudio = withAudio,
                AudioPort = newAudioServer?.Port ?? 0,
                AudioSampleRate = sampleRate,
                AudioChannels = channels,
                AudioSampleFormat = sampleFormat,
            };

            newEncoder = FfmpegEncoder.Start(ffmpegPath, options);
            newEncoder.DataAvailable += OnEncoderData;

            newCts = new CancellationTokenSource();

            var encoderRef = newEncoder;
            var token = newCts.Token;
            pacerTask = Task.Run(() => PaceFramesAsync(encoderRef, settings.Quality.Fps, token), CancellationToken.None);

            captureClosedHandler = () => _ = Task.Run(HandleCaptureClosedAsync);
            newCapture.Closed += captureClosedHandler;
            newCapture.FrameArrived += OnFrame;

            // O vídeo precisa começar a fluir antes: o ffmpeg só abre a segunda entrada
            // (áudio por TCP) depois de resolver a primeira.
            newCapture.Start();

            if (withAudio && newAudioServer is not null && newAudio is not null)
            {
                var audioFeed = newAudioServer;
                var audioCapture = newAudio;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await audioFeed.WaitForClientAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);

                        // Alinha o áudio com o vídeo: o primeiro quadro já foi enviado,
                        // então o áudio precisa de silêncio inicial equivalente.
                        var firstTick = Interlocked.Read(ref firstFrameTick);
                        var lead = firstTick > 0
                            ? (Stopwatch.GetTimestamp() - firstTick) / (double)Stopwatch.Frequency
                            : 0;

                        var bytesPerSample = sampleFormat == "f32le" ? 4 : 2;
                        audioFeed.WriteSilence(lead, sampleRate, channels, bytesPerSample);

                        audioCapture.Start();
                        AppLog.Info("Áudio do sistema conectado ao encoder.");
                    }
                    catch (Exception)
                    {
                        AppLog.Warn("ffmpeg não abriu o canal de áudio; seguindo apenas com vídeo.");
                    }
                });
            }

            lock (gate)
            {
                capture = newCapture;
                audio = newAudio;
                audioServer = newAudioServer;
                encoder = newEncoder;
                cts = newCts;
                frames = 0;
                framesSent = 0;
                dropped = 0;
                bytesOut = 0;
                lastFrames = 0;
                lastSent = 0;
                lastBytes = 0;
                lastStatsAt = DateTime.UtcNow;
                lastPreviewTick = 0;
                initBuffer.Clear();
                initParsed = false;
                initSegment = null;
                firstFrameTick = 0;

                status = new StreamStatus
                {
                    Streaming = true,
                    WindowTitle = settings.WindowTitle,
                    Width = newCapture.Width,
                    Height = newCapture.Height,
                    Encoder = preferredEncoder.DisplayName,
                    QualityId = settings.Quality.Id,
                    TargetFps = settings.Quality.Fps,
                    Audio = withAudio,
                    StartedAt = DateTimeOffset.UtcNow,
                };

                statsTimer = new System.Threading.Timer(_ => UpdateStats(), null, 1000, 1000);
            }

            AppLog.Info($"Transmissão iniciada: \"{settings.WindowTitle}\" {newCapture.Width}x{newCapture.Height}@{settings.Quality.Fps} " +
                        $"{bitrateKbps} kbps áudio={withAudio}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Falha ao iniciar a transmissão", ex);

            try { if (newCapture is not null) newCapture.Dispose(); } catch { }
            try { if (newEncoder is not null) await newEncoder.DisposeAsync().ConfigureAwait(false); } catch { }
            try { newAudio?.Dispose(); } catch { }
            try { newAudioServer?.Dispose(); } catch { }
            newCts?.Dispose();
            await ResetAsync().ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Entrega quadros ao encoder em cadência fixa. O ffmpeg numera os quadros pela taxa de entrada,
    /// então enviar menos quadros do que o FPS alvo faria o vídeo andar mais devagar que o áudio
    /// (deriva de A/V). Quando a captura atrasa, o último quadro é repetido para manter o relógio.
    /// </summary>
    private async Task PaceFramesAsync(FfmpegEncoder target, int fps, CancellationToken token)
    {
        var interval = Stopwatch.Frequency / (double)Math.Max(1, fps);
        var next = (double)Stopwatch.GetTimestamp();

        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = Stopwatch.GetTimestamp();
                if (now < next)
                {
                    var ms = (next - now) * 1000.0 / Stopwatch.Frequency;

                    // Task.Delay tem granularidade de ~15 ms: dormir perto do alvo e girar o
                    // restante evita que cada quadro chegue 10–15 ms atrasado.
                    if (ms > 3)
                        await Task.Delay(TimeSpan.FromMilliseconds(ms - 2), token).ConfigureAwait(false);
                    else
                        Thread.SpinWait(120);

                    continue;
                }

                CapturedFrame? fresh;
                lock (frameGate)
                {
                    fresh = latestFrame;
                    latestFrame = null;
                }

                if (fresh is not null)
                {
                    heldFrame?.Release();
                    heldFrame = fresh;
                }

                if (heldFrame is null)
                {
                    // Nada capturado ainda: espera sem acumular atraso.
                    next = Stopwatch.GetTimestamp() + interval;
                    continue;
                }

                try
                {
                    var started = Stopwatch.GetTimestamp();
                    await target.WriteFrameAsync(heldFrame.Buffer, heldFrame.Length, token).ConfigureAwait(false);
                    Interlocked.Increment(ref framesSent);
                    Interlocked.Add(ref writeTicks, Stopwatch.GetTimestamp() - started);
                    Interlocked.Increment(ref writeCount);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLog.Warn("Encoder recusou o quadro: " + ex.Message);
                    return;
                }

                next += interval;

                // Recupera atraso mandando quadros em sequência; só desiste se estiver
                // mais de meio segundo atrás (encoder realmente não acompanha).
                if (Stopwatch.GetTimestamp() - next > Stopwatch.Frequency / 2)
                    next = Stopwatch.GetTimestamp();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("Cadência de quadros encerrada: " + ex.Message); }
        finally
        {
            lock (frameGate)
            {
                latestFrame?.Release();
                latestFrame = null;
            }

            heldFrame?.Release();
            heldFrame = null;
        }
    }

    private void OnFrame(CapturedFrame frame)
    {
        Interlocked.Increment(ref frames);
        if (Interlocked.Read(ref firstFrameTick) == 0)
            Interlocked.Exchange(ref firstFrameTick, Stopwatch.GetTimestamp());

        var now = Environment.TickCount64;
        if (now - lastPreviewTick >= 200)
        {
            lastPreviewTick = now;

            // Dois buffers alternados: a UI ainda pode estar copiando o anterior.
            var slot = previewBufferIndex ^ 1;
            var preview = FrameDownscaler.Subsample(
                frame.Buffer, frame.Width, frame.Height, 400, previewBuffers[slot]);

            if (preview is not null)
            {
                previewBufferIndex = slot;
                previewBuffers[slot] = preview.Bgra;
                try { PreviewAvailable?.Invoke(preview); } catch { }
            }
        }

        var superseded = false;
        lock (frameGate)
        {
            if (latestFrame is not null)
            {
                latestFrame.Release();
                superseded = true;
            }

            latestFrame = frame;
        }

        if (superseded) Interlocked.Increment(ref dropped);
    }

    private void OnEncoderData(byte[] chunk)
    {
        Interlocked.Add(ref bytesOut, chunk.Length);

        if (!initParsed)
        {
            initBuffer.AddRange(chunk);
            var codec = Mp4Boxes.TryReadAvcCodec(CollectionsMarshal.AsSpan(initBuffer));

            if (codec is not null)
            {
                initParsed = true;
                var init = initBuffer.ToArray();
                initBuffer.Clear();
                initSegment = init;

                lock (gate) { status = status.WithCodec(codec); }
                AppLog.Info($"Codec negociado com o receptor: {codec}");

                Raise(init);
                return;
            }

            if (initBuffer.Count > 4_000_000)
            {
                initParsed = true;
                initBuffer.Clear();
                AppLog.Warn("Cabeçalho MP4 não reconhecido; seguindo com o codec padrão.");
            }

            return;
        }

        Raise(chunk);
    }

    private void Raise(byte[] data)
    {
        try { DataAvailable?.Invoke(data); }
        catch (Exception ex) { AppLog.Error("Erro ao distribuir o fluxo", ex); }
    }

    private void UpdateStats()
    {
        var current = status;
        if (!current.Streaming) return;

        var now = DateTime.UtcNow;
        var seconds = (now - lastStatsAt).TotalSeconds;
        if (seconds <= 0.2) return;

        var currentFrames = Interlocked.Read(ref frames);
        var currentSent = Interlocked.Read(ref framesSent);
        var currentBytes = Interlocked.Read(ref bytesOut);

        var fps = Math.Max(0, (currentSent - lastSent) / seconds);
        var sourceFps = Math.Max(0, (currentFrames - lastFrames) / seconds);
        var kbps = Math.Max(0, (currentBytes - lastBytes) * 8 / 1000.0 / seconds);

        lastFrames = currentFrames;
        lastSent = currentSent;
        lastBytes = currentBytes;
        lastStatsAt = now;

        lock (gate)
        {
            status = status.WithMeasured(fps, sourceFps, kbps, currentSent, Interlocked.Read(ref dropped));
        }
    }

    private async Task HandleCaptureClosedAsync()
    {
        if (!IsStreaming) return;
        AppLog.Info("A janela transmitida foi fechada; encerrando a transmissão.");
        await StopAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        WindowCapture? captureToDispose;
        LoopbackAudioCapture? audioToDispose;
        AudioFeedServer? serverToDispose;
        FfmpegEncoder? encoderToDispose;
        CancellationTokenSource? ctsToDispose;
        Task? pacer;
        System.Threading.Timer? timer;
        Action? closedHandler;

        lock (gate)
        {
            captureToDispose = capture;
            audioToDispose = audio;
            serverToDispose = audioServer;
            encoderToDispose = encoder;
            ctsToDispose = cts;
            timer = statsTimer;
            closedHandler = captureClosedHandler;

            capture = null;
            audio = null;
            audioServer = null;
            encoder = null;
            cts = null;
            pacer = pacerTask;
            pacerTask = null;
            statsTimer = null;
            captureClosedHandler = null;
            status = StreamStatus.Idle;
            initParserReset();
        }

        timer?.Dispose();

        try
        {
            if (captureToDispose is not null)
            {
                if (closedHandler is not null) captureToDispose.Closed -= closedHandler;

                var seconds = captureToDispose.SecondsSinceStart;
                AppLog.Info($"Captura: {captureToDispose.OfferedFrames} quadros oferecidos " +
                            $"({captureToDispose.OfferedFrames / seconds:F1}/s), {captureToDispose.AcceptedFrames} " +
                            $"aceitos ({captureToDispose.AcceptedFrames / seconds:F1}/s) em {seconds:F0}s");
                var writes = Interlocked.Read(ref writeCount);
                var writeMs = writes > 0
                    ? Interlocked.Read(ref writeTicks) * 1000.0 / Stopwatch.Frequency / writes
                    : 0;

                AppLog.Info($"Áudio capturado: {Interlocked.Read(ref audioBytes) / 1024.0 / Math.Max(1, seconds):F1} KB/s; " +
                            $"quadros enviados ao encoder: {Interlocked.Read(ref framesSent)}; " +
                            $"escrita média por quadro: {writeMs:F1} ms");

                captureToDispose.Dispose();
            }
        }
        catch { }

        try { audioToDispose?.Stop(); } catch { }

        // O pacer para antes do encoder: ele é o único que escreve no stdin do ffmpeg.
        try { ctsToDispose?.Cancel(); } catch { }
        if (pacer is not null)
        {
            try { await pacer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }

        try { audioToDispose?.Dispose(); } catch { }
        try { if (encoderToDispose is not null) await encoderToDispose.DisposeAsync().ConfigureAwait(false); } catch { }
        try { serverToDispose?.Dispose(); } catch { }
        ctsToDispose?.Dispose();

        AppLog.Info("Transmissão encerrada");
    }

    private void initParserReset()
    {
        initBuffer.Clear();
        initParsed = false;
        initSegment = null;
    }

    private async Task ResetAsync()
    {
        cts?.Dispose();
        cts = null;
        pacerTask = null;
        statsTimer?.Dispose();
        statsTimer = null;
        initParserReset();
        status = StreamStatus.Idle;
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
