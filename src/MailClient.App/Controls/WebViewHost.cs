using MailClient.App.Services;
using Microsoft.Web.WebView2.Core;

namespace MailClient.App.Controls;

/// <summary>Shared WebView2 environment (one browser process group, data under %LOCALAPPDATA%).</summary>
public static class WebViewHost
{
    private static Task<CoreWebView2Environment>? _environment;

    public static Task<CoreWebView2Environment> GetEnvironmentAsync() =>
        _environment ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebView2Data,
            new CoreWebView2EnvironmentOptions { Language = "ru-RU" });

    public static bool IsRuntimeInstalled()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            return false;
        }
    }

    public static void HardenSettings(CoreWebView2Settings s, bool allowScripts)
    {
        s.IsScriptEnabled = allowScripts;
        s.AreDevToolsEnabled = false;
        s.AreDefaultScriptDialogsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsWebMessageEnabled = allowScripts;
        s.IsZoomControlEnabled = true;
    }
}
