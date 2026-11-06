using System.Collections.Concurrent;

namespace StreamBox.Core;

/// <summary>Log em arquivo, com sink opcional para a UI.</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<string> Recent = new();

    public static string LogDirectory { get; } = Path.Combine(SettingsStore.Directory, "logs");

    /// <summary>Recebe linhas já formatadas; usado pela UI do desktop.</summary>
    public static event Action<string>? LineWritten;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    public static IReadOnlyCollection<string> GetRecent() => Recent.ToArray();

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";

        Recent.Enqueue(line);
        while (Recent.Count > 400 && Recent.TryDequeue(out _)) { }

        try
        {
            LineWritten?.Invoke(line);
        }
        catch
        {
            // A UI nunca deve derrubar o motor de transmissão.
        }

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, $"streambox-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // Sem log em disco: seguir com a operação.
            }
        }
    }
}
