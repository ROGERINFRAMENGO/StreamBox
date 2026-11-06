using System.Text.Json;

namespace StreamBox.Core;

public sealed class AppSettings
{
    public string QualityId { get; set; } = QualityPreset.Default.Id;
    public bool IncludeAudio { get; set; } = true;
    public int Port { get; set; } = 8730;
    public string EncoderPreference { get; set; } = "auto";
}

/// <summary>Persistência simples em %LOCALAPPDATA%\StreamBox\settings.json.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamBox");

    public static string FilePath { get; } = Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is not null)
                    return settings;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Falha ao ler configurações: {ex.Message}");
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Falha ao salvar configurações: {ex.Message}");
        }
    }
}
