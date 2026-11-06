using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using StreamBox.Core;

namespace StreamBox.Network;

/// <summary>
/// Servidor local: entrega o receptor web e distribui o MP4 fragmentado por WebSocket.
/// Nenhuma dependência de nuvem — tudo na rede local.
/// </summary>
public sealed class StreamServer : IAsyncDisposable
{
    private readonly IStreamEngine _engine;
    private readonly ConcurrentDictionary<Guid, Viewer> _viewers = new();
    private WebApplication? _app;
    private bool _subscribed;

    public StreamServer(IStreamEngine engine, int port)
    {
        _engine = engine;
        Port = port;
    }

    public int Port { get; }

    public string MachineName => Environment.MachineName;

    public int ViewerCount => _viewers.Count;

    public bool IsRunning => _app is not null;

    public string? Url { get; private set; }

    public IReadOnlyList<string> LocalAddresses { get; private set; } = [];

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null) return;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(Port);
            options.Limits.MaxRequestBodySize = 64 * 1024;
        });

        var app = builder.Build();

        var webRoot = Path.Combine(AppContext.BaseDirectory, "web");
        if (!Directory.Exists(webRoot))
            throw new DirectoryNotFoundException($"Pasta do receptor web não encontrada: {webRoot}");

        var provider = new PhysicalFileProvider(webRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = provider,
            ContentTypeProvider = new FileExtensionContentTypeProvider(),
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

        app.MapGet("/api/status", () => Results.Json(BuildStatusDto()));
        app.MapGet("/api/info", () => Results.Json(new { machineName = MachineName, port = Port }));
        app.Map("/ws/stream", HandleWebSocketAsync);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        _app = app;
        LocalAddresses = LanInfo.GetLocalAddresses();
        Url = $"http://{LocalAddresses.FirstOrDefault() ?? "localhost"}:{Port}";

        _engine.DataAvailable += OnEngineData;
        _subscribed = true;

        AppLog.Info($"Receptor disponível em {Url} ({LocalAddresses.Count} interface(s))");
    }

    public async Task StopAsync()
    {
        if (_subscribed)
        {
            _engine.DataAvailable -= OnEngineData;
            _subscribed = false;
        }

        foreach (var viewer in _viewers.Values)
            viewer.Close();

        _viewers.Clear();

        var app = _app;
        _app = null;

        if (app is not null)
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await app.StopAsync(stopTimeout.Token).ConfigureAwait(false); } catch { }
            try { await app.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    private object BuildStatusDto()
    {
        var status = _engine.Status;
        var mseCodec = status.Audio ? $"{status.Codec},mp4a.40.2" : status.Codec;

        return new
        {
            machineName = MachineName,
            port = Port,
            streaming = status.Streaming,
            windowTitle = status.WindowTitle,
            width = status.Width,
            height = status.Height,
            encoder = status.Encoder,
            qualityId = status.QualityId,
            targetFps = status.TargetFps,
            measuredFps = Math.Round(status.MeasuredFps, 1),
            sourceFps = Math.Round(status.SourceFps, 1),
            bitrateKbps = Math.Round(status.BitrateKbps),
            audio = status.Audio,
            codec = status.Codec,
            mseCodec,
            viewers = ViewerCount,
            uptimeSeconds = Math.Round(status.UptimeSeconds),
        };
    }

    private void OnEngineData(byte[] chunk)
    {
        foreach (var viewer in _viewers.Values)
            viewer.Enqueue(chunk);
    }

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket esperado.").ConfigureAwait(false);
            return;
        }

        var name = context.Request.Query["name"].ToString();
        if (string.IsNullOrWhiteSpace(name)) name = "TV Box";
        if (name.Length > 48) name = name[..48];

        if (!_engine.IsStreaming)
        {
            using var rejected = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            // Não é um erro fatal: o receptor deve voltar a esperar em vez de mostrar falha.
            await SendTextAsync(rejected, "{\"type\":\"idle\",\"detail\":\"Nenhuma transmissão em andamento no computador.\"}")
                .ConfigureAwait(false);
            try
            {
                await rejected.CloseAsync(WebSocketCloseStatus.NormalClosure, "idle", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { }
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var viewer = new Viewer(name, socket, _engine.InitSegment?.ToArray());

        if (_engine.InitSegment is { Length: > 0 } init)
            viewer.Enqueue(init.ToArray());

        _viewers[viewer.Id] = viewer;
        AppLog.Info($"Receptor conectado: {name} — total {_viewers.Count}");

        try
        {
            await viewer.RunAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warn($"Receptor {name} desconectado: {ex.Message}");
        }
        finally
        {
            _viewers.TryRemove(viewer.Id, out _);
            AppLog.Info($"Receptor {name} saiu — total {_viewers.Count}");
        }
    }

    private static async Task SendTextAsync(WebSocket socket, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Um receptor conectado, com fila limitada: receptor lento perde fragmentos, nunca trava o encoder.
    /// Ao descartar, avisa o navegador (<c>reset</c>) e reenvia o segmento inicial para ele se recuperar.
    /// </summary>
    private sealed class Viewer
    {
        private const int Capacity = 48;

        private readonly Queue<byte[]> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly object _gate = new();
        private readonly WebSocket _socket;
        private readonly byte[]? _init;
        private bool _gap;
        private bool _closed;

        public Viewer(string name, WebSocket socket, byte[]? init)
        {
            Name = name;
            _socket = socket;
            _init = init;
        }

        public Guid Id { get; } = Guid.NewGuid();

        public string Name { get; }

        public void Enqueue(byte[] chunk)
        {
            lock (_gate)
            {
                if (_closed) return;

                if (_queue.Count >= Capacity)
                {
                    _queue.Dequeue();
                    _gap = true;
                }

                _queue.Enqueue(chunk);
            }

            try { _signal.Release(); } catch (ObjectDisposedException) { }
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

                byte[]? chunk;
                bool gap;
                lock (_gate)
                {
                    gap = _gap;
                    _gap = false;
                    chunk = _queue.Count > 0 ? _queue.Dequeue() : null;
                }

                if (_socket.State != WebSocketState.Open) return;

                if (gap)
                {
                    // O receptor perdeu o alinhamento das caixas MP4: recomeça do segmento inicial.
                    await SendTextAsync("{\"type\":\"reset\"}", cancellationToken).ConfigureAwait(false);
                    if (_init is { Length: > 0 })
                        await _socket.SendAsync(_init, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
                }

                if (chunk is null) continue;

                await _socket.SendAsync(chunk, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
            }
        }

        private Task SendTextAsync(string text, CancellationToken cancellationToken) =>
            _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);

        public void Close()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                _queue.Clear();
            }

            try { _signal.Release(); } catch (ObjectDisposedException) { }

            // Não bloqueia o thread de parada: o socket é descartado junto com a conexão.
            var socket = _socket;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "stop", CancellationToken.None)
                            .ConfigureAwait(false);
                }
                catch { }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
