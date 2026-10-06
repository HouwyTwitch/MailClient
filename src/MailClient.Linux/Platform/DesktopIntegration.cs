using System.IO;
using System.Diagnostics;
using Avalonia.Input.Platform;

namespace MailClient.App.Services;

/// <summary>Freedesktop integration (xdg-open, autostart, clipboard) — the Linux counterpart of the Windows shell calls.</summary>
public static class DesktopIntegration
{
    private static string AutostartFile => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "mailclient.desktop");

    /// <summary>Starts the client minimized at login (an XDG autostart entry), or stops doing so.</summary>
    public static void SetAutostart(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(AutostartFile)) File.Delete(AutostartFile);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
            File.WriteAllText(AutostartFile,
                "[Desktop Entry]\nType=Application\nName=Корпоративная почта\n" +
                $"Exec=\"{Environment.ProcessPath}\" --minimized\nIcon=mailclient\nX-GNOME-Autostart-enabled=true\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Автозапуск не изменён: {ex.Message}");
        }
    }

    /// <summary>Opens a URL, file or folder with the default program (xdg-open).</summary>
    public static void ShellOpen(string target)
    {
        try
        {
            var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(target);
            using var process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Dialogs.Error(ex, "Не удалось открыть (нужна утилита xdg-open из пакета xdg-utils)");
        }
    }

    /// <summary>Only http(s) and mailto links from messages may be opened.</summary>
    public static bool IsSafeExternalLink(Uri uri) => uri.Scheme is "http" or "https" or "mailto";

    private static readonly string[] DangerousExtensions =
    [
        // Linux
        ".sh", ".bash", ".run", ".bin", ".desktop", ".appimage", ".deb", ".rpm", ".py", ".pl", ".jar",
        // Windows files that Wine or a shared folder could still run
        ".exe", ".com", ".bat", ".cmd", ".scr", ".pif", ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh",
        ".ps1", ".psm1", ".msi", ".msp", ".hta", ".cpl", ".lnk", ".reg", ".iso", ".img",
    ];

    public static bool IsDangerousFile(string fileName) =>
        DangerousExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    public static void CopyText(string text)
    {
        if (Dialogs.ActiveWindow?.Clipboard is { } clipboard) _ = clipboard.SetTextAsync(text);
    }
}
