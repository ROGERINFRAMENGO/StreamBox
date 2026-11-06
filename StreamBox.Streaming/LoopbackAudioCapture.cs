using NAudio.Wave;
using StreamBox.Core;

namespace StreamBox.Streaming;

/// <summary>Captura o áudio reproduzido no sistema (loopback WASAPI) em tempo real.</summary>
public sealed class LoopbackAudioCapture : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private byte[] _pcm = new byte[64 * 1024];

    public event Action<byte[], int>? DataAvailable;

    public WaveFormat? Format { get; private set; }

    public bool IsAvailable => _capture is not null;

    public bool TryInitialize()
    {
        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            Format = _capture.WaveFormat;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Áudio do sistema indisponível: {ex.Message}");
            _capture = null;
            return false;
        }
    }

    public void Start()
    {
        try { _capture?.StartRecording(); }
        catch (Exception ex) { AppLog.Warn($"Falha ao iniciar captura de áudio: {ex.Message}"); }
    }

    public void Stop()
    {
        try { _capture?.StopRecording(); }
        catch { /* dispositivo pode ter sido removido */ }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        try
        {
            // Normaliza para PCM 16 bits estéreo já aqui (o formato nativo pode ter 8 canais
            // com NaN/Inf nos canais não usados, o que faz o AAC recusar quadros inteiros).
            var format = Format;
            if (format is null) return;

            var channels = Math.Max(1, format.Channels);
            var bytesPerSample = Math.Max(2, format.BitsPerSample / 8);
            var frames = e.BytesRecorded / (bytesPerSample * channels);
            if (frames <= 0) return;

            var needed = frames * PcmConverter.OutputFrameBytes;
            if (_pcm.Length < needed) _pcm = new byte[needed];

            // Buffer reaproveitado: ~100 blocos por segundo não podem virar lixo no Gen0.
            var written = PcmConverter.ToStereo16(
                e.Buffer, e.BytesRecorded, channels, bytesPerSample, format.BitsPerSample >= 32, _pcm);

            if (written <= 0) return;
            DataAvailable?.Invoke(_pcm, written);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Erro no áudio: {ex.Message}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            AppLog.Warn($"Captura de áudio interrompida: {e.Exception.Message}");
    }

    public void Dispose()
    {
        try
        {
            if (_capture is not null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
            }
        }
        catch { }

        _capture = null;
    }
}
