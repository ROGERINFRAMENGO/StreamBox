namespace StreamBox.Core;

/// <summary>Configuração de uma sessão de transmissão.</summary>
public sealed record StreamSettings
{
    public required nint WindowHandle { get; init; }
    public required string WindowTitle { get; init; }
    public required QualityPreset Quality { get; init; }
    public bool IncludeAudio { get; init; } = true;

    /// <summary>Captura a janela como está, sem forçar o primeiro plano.</summary>
    public bool CaptureHiddenWindows { get; init; }
}
