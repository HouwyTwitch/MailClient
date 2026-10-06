using System.Runtime.InteropServices;

namespace MailClient.Linux.Controls;

/// <summary>Whether a web engine for <c>NativeWebView</c> (WebKitGTK 4.1 / 4.0 or WPE WebKit) is installed.</summary>
public static class WebKit
{
    private static readonly string[] Libraries =
        ["libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.0.so.37", "libWPEWebKit-2.0.so.1"];

    /// <summary>
    /// False without WebKitGTK (then messages are shown as text and written in plain text), and when
    /// MAILCLIENT_NO_WEBVIEW=1 (diagnostics, headless runs).
    /// </summary>
    public static bool IsAvailable { get; } =
        Environment.GetEnvironmentVariable("MAILCLIENT_NO_WEBVIEW") != "1" &&
        OperatingSystem.IsLinux() &&
        Libraries.Any(lib =>
        {
            if (!NativeLibrary.TryLoad(lib, out var handle)) return false;
            NativeLibrary.Free(handle);
            return true;
        });
}
