using System.IO;

namespace MailClient.App.Services;

public static class AppPaths
{
    public const string AppFolderName = "MailClient";

    /// <summary>%APPDATA%\MailClient — settings (roams with the user profile).</summary>
    public static string Roaming { get; } = Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName));

    /// <summary>%LOCALAPPDATA%\MailClient — caches, logs, browser data.</summary>
    public static string Local { get; } = Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName));

    public static string SettingsFile => Path.Combine(Roaming, "settings.json");
    public static string Secrets => Ensure(Path.Combine(Roaming, "secrets"));
    public static string Logs => Ensure(Path.Combine(Local, "logs"));
    public static string WebView2Data => Ensure(Path.Combine(Local, "webview2"));
    public static string Preview => Ensure(Path.Combine(Local, "preview"));
    public static string TempAttachments => Ensure(Path.Combine(Path.GetTempPath(), AppFolderName));

    public static string CacheFile(Guid accountId) => Path.Combine(Ensure(Path.Combine(Local, "accounts", accountId.ToString("N"))), "cache.db");

    public static void DeleteAccountData(Guid accountId)
    {
        try
        {
            var dir = Path.Combine(Local, "accounts", accountId.ToString("N"));
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (IOException ex) { Log.Warn($"Не удалось удалить кэш учётной записи: {ex.Message}"); }
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
