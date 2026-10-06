using System.Diagnostics;
using Microsoft.Win32;

namespace MailClient.App.Services;

/// <summary>Windows shell: default programs, autostart, clipboard. The Linux build has its own implementation.</summary>
public static class DesktopIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "MailClient";

    public static void SetAutostart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue(RunValue, false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Автозапуск не изменён: {ex.Message}");
        }
    }

    /// <summary>Opens a URL or file with the default program.</summary>
    public static void ShellOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось открыть");
        }
    }

    /// <summary>Puts text on the clipboard; the clipboard can be locked by another program for a moment.</summary>
    public static void CopyText(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Dialogs.Error(ex, "Не удалось скопировать в буфер обмена");
        }
    }

    /// <summary>Only http(s) and mailto links from messages may be opened.</summary>
    public static bool IsSafeExternalLink(Uri uri) =>
        uri.Scheme is "http" or "https" or "mailto";

    private static readonly string[] DangerousExtensions =
    {
        ".exe", ".com", ".bat", ".cmd", ".scr", ".pif", ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh",
        ".ps1", ".psm1", ".msi", ".msp", ".hta", ".cpl", ".jar", ".lnk", ".reg", ".iso", ".img", ".vhd", ".vhdx",
    };

    public static bool IsDangerousFile(string fileName) =>
        DangerousExtensions.Contains(System.IO.Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);
}
