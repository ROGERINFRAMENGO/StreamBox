using System.Diagnostics;
using System.Globalization;
using StreamBox.Core;

namespace StreamBox.Streaming;

public sealed record EncodeOptions
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Fps { get; init; }
    public required int BitrateKbps { get; init; }
    public required EncoderProfile Encoder { get; init; }
    public bool WithAudio { get; init; }
    public int AudioPort { get; init; }
    public int AudioSampleRate { get; init; } = 48000;
    public int AudioChannels { get; init; } = 2;
    public string AudioSampleFormat { get; init; } = "f32le";
}

/// <summary>Processo ffmpeg: vídeo bruto pelo stdin, áudio por TCP local, MP4 fragmentado pelo stdout.</summary>
public sealed class FfmpegEncoder : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _input;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();

    private FfmpegEncoder(Process process, EncoderProfile profile)
    {
        _process = process;
        Profile = profile;
        _input = process.StandardInput.BaseStream;

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => _exit.TrySetResult(SafeExitCode());

        _ = Task.Run(PumpStdoutAsync);
        _ = Task.Run(PumpStderrAsync);
    }

    public event Action<byte[]>? DataAvailable;

    public event Action<string>? LogLine;

    public EncoderProfile Profile { get; }

    public Task<int> Exit => _exit.Task;

    public bool IsRunning
    {
        get
        {
            try { return !_process.HasExited; }
            catch { return false; }
        }
    }

    public void ReportError(string message) => LogLine?.Invoke(message);

    public static string ResolvePath()
    {
        var env = Environment.GetEnvironmentVariable("STREAMBOX_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(baseDir, "ffmpeg.exe"),
        };

        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 7 && dir is not null; i++)
        {
            candidates.Add(Path.Combine(dir.FullName, "tools", "ffmpeg.exe"));
            dir = dir.Parent;
        }

        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return candidate;

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder.Trim('"'), "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            "ffmpeg.exe não encontrado. Coloque o ffmpeg em tools\\ffmpeg.exe ou defina STREAMBOX_FFMPEG.");
    }

    /// <summary>Testa os encoders em ordem de preferência e devolve o primeiro que realmente funciona.</summary>
    public static async Task<EncoderProfile> SelectAsync(
        string ffmpegPath, int width, int height, int fps, int bitrateKbps, CancellationToken cancellationToken = default)
    {
        foreach (var profile in EncoderProfile.All)
        {
            if (await ProbeAsync(ffmpegPath, profile, width, height, fps, bitrateKbps, cancellationToken).ConfigureAwait(false))
            {
                AppLog.Info($"Encoder selecionado: {profile.DisplayName} ({profile.Name})");
                return profile;
            }
        }

        throw new InvalidOperationException("Nenhum encoder H.264 disponível no ffmpeg selecionado.");
    }

    private static async Task<bool> ProbeAsync(
        string ffmpegPath, EncoderProfile profile, int width, int height, int fps, int bitrateKbps, CancellationToken cancellationToken)
    {
        var args = string.Join(' ', new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostats",
            "-f", "lavfi", "-i", $"color=c=black:s={width}x{height}:r={fps}:d=0.4",
            "-an", "-c:v", profile.Name,
            profile.BuildVideoArguments(bitrateKbps, fps),
            "-pix_fmt", "yuv420p", "-f", "null", "-",
        });

        try
        {
            using var process = Process.Start(new ProcessStartInfo(ffmpegPath, args)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null) return false;

            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                AppLog.Warn($"Encoder {profile.Name} excedeu o tempo de teste.");
                return false;
            }

            if (process.ExitCode == 0) return true;

            var detail = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            AppLog.Warn($"Encoder {profile.Name} indisponível: {detail}");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Encoder {profile.Name} falhou no teste: {ex.Message}");
            return false;
        }
    }

    public static FfmpegEncoder Start(string ffmpegPath, EncodeOptions options)
    {
        var arguments = BuildArguments(options);

        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = null,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        AppLog.Info($"ffmpeg: {options.Encoder.Name} {options.Width}x{options.Height}@{options.Fps} {options.BitrateKbps}kbps audio={options.WithAudio}");

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Não foi possível iniciar o ffmpeg.");
        return new FfmpegEncoder(process, options.Encoder);
    }

    internal static List<string> BuildArguments(EncodeOptions options)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostats",
            "-f", "rawvideo",
            "-pix_fmt", "bgra",
            "-video_size", $"{options.Width}x{options.Height}",
            "-framerate", options.Fps.ToString(CultureInfo.InvariantCulture),
            "-i", "pipe:0",
        };

        if (options.WithAudio)
        {
            arguments.AddRange(new[]
            {
                "-f", options.AudioSampleFormat,
                "-ar", options.AudioSampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", options.AudioChannels.ToString(CultureInfo.InvariantCulture),
                "-i", $"tcp://127.0.0.1:{options.AudioPort}",
            });
        }

        arguments.AddRange(["-map", "0:v:0"]);
        if (options.WithAudio) arguments.AddRange(["-map", "1:a:0"]);

        arguments.AddRange(["-c:v", options.Encoder.Name]);
        arguments.AddRange(SplitArguments(options.Encoder.BuildVideoArguments(options.BitrateKbps, options.Fps)));
        arguments.AddRange(["-pix_fmt", "yuv420p"]);

        if (options.WithAudio)
            arguments.AddRange(["-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2"]);

        // MP4 fragmentado: o receptor consome via Media Source Extensions.
        arguments.AddRange(new[]
        {
            "-f", "mp4",
            "-movflags", "+empty_moov+default_base_moof",
            "-frag_duration", "200000",
            "-flush_packets", "1",
            "pipe:1",
        });

        return arguments;
    }

    private static IEnumerable<string> SplitArguments(string arguments) =>
        arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public async ValueTask WriteFrameAsync(byte[] buffer, int length, CancellationToken cancellationToken)
    {
        if (!IsRunning) throw new IOException("O encoder foi encerrado.");

        try
        {
            await _input.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new IOException("Falha ao enviar quadro para o encoder: " + ex.Message, ex);
        }
    }

    private async Task PumpStdoutAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            var stream = _process.StandardOutput.BaseStream;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (read <= 0) break;

                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);

                try { DataAvailable?.Invoke(chunk); }
                catch (Exception ex) { AppLog.Error("Erro ao distribuir dados do encoder", ex); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn($"Saída do encoder encerrada: {ex.Message}"); }
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            var stream = _process.StandardError;
            while (true)
            {
                var line = await stream.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;

                AppLog.Info("ffmpeg: " + line);
                try { LogLine?.Invoke(line); } catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private int SafeExitCode()
    {
        try { return _process.ExitCode; }
        catch { return -1; }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        try { _input.Close(); } catch { }
        try { _process.StandardInput.Close(); } catch { }

        try
        {
            await _exit.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }

        try { _process.Dispose(); } catch { }
        _cts.Dispose();
    }
}
