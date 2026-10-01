using System.IO;
using System.Text;

namespace MailClient.App.Services;

/// <summary>
/// Minimal thread-safe file log (%LOCALAPPDATA%\MailClient\logs). Never logs passwords or message bodies.
/// Logs older than 14 days are removed at startup.
/// </summary>
public static class Log
{
    private static readonly object Sync = new();

    public static string CurrentFile => Path.Combine(AppPaths.Logs, $"mailclient-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
            lock (Sync) File.AppendAllText(CurrentFile, line, Encoding.UTF8);
        }
        catch
        {
            // Logging must never crash the application.
        }
    }

    public static void Cleanup()
    {
        try
        {
            foreach (var f in Directory.GetFiles(AppPaths.Logs, "mailclient-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
        }
        catch { }
    }
}
