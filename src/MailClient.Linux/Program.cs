using Avalonia;
using MailClient.App.Services;
using MailClient.Core;

namespace MailClient.Linux;

public static class Program
{
    /// <summary>
    /// Arguments: <c>mailto:…</c> (compose), <c>--minimized</c> (autostart), <c>--version</c>,
    /// <c>--smoke-test</c> (open the main window, close it after a few seconds and exit with code 0 — used in CI).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            Console.WriteLine($"mailclient {AppInfo.Version}");
            return 0;
        }

        if (args.Contains(Controls.WebKitProbe.ProbeArgument)) return Controls.WebKitProbe.RunInChild();

        CodePages.EnsureRegistered();
        using var instance = SingleInstance.TryAcquire(args);
        if (instance == null)
        {
            // Another window is open: it received our arguments (mailto:) and came to the front.
            return 0;
        }

        Log.Info($"Запуск {AppInfo.Version}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Controls.WebKitProbe.Apply();
        App.StartupArgs = args;
        App.Instance = instance;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Also used by the visual designer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
