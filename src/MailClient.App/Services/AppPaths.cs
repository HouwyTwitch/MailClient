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
            // Pooled SQLite connections keep the database file open on Windows.
            MailClient.Core.Storage.LocalCache.ReleaseFiles();
            var dir = Path.Combine(Local, "accounts", accountId.ToString("N"));
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось удалить кэш учётной записи: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes copies of opened attachments (they may be confidential) and leftover preview files. Files still
    /// open in another program are skipped and removed on a later start.
    /// </summary>
    public static void CleanupTemporaryFiles()
    {
        DeleteOlderThan(TempAttachments, TimeSpan.FromDays(1));
        DeleteOlderThan(Preview, TimeSpan.FromHours(1));
    }

    private static void DeleteOlderThan(string dir, TimeSpan age)
    {
        var limit = DateTime.UtcNow - age;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < limit) File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use (e.g. a document still open in Word): next time.
                }
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Not empty or in use.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Временные файлы в {dir} не очищены: {ex.Message}");
        }
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
