using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MailClient.App.Services;
using MailClient.Linux.ViewModels;
using MailClient.Linux.Views;

namespace MailClient.Linux;

public sealed class App : Application
{
    internal static string[] StartupArgs { get; set; } = [];
    internal static SingleInstance? Instance { get; set; }
    internal static bool IsSmokeTest => StartupArgs.Contains("--smoke-test");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = SettingsStore.Load();
            ThemeService.Apply(settings.Theme);
            _ = Task.Run(AppPaths.CleanupTemporaryFiles);

            var vm = new MainViewModel(settings, new CredentialProvider(new LinuxSecretStore()));
            var window = new MainWindow(vm);
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => vm.Dispose();
            if (StartupArgs.Contains("--minimized")) window.WindowState = Avalonia.Controls.WindowState.Minimized;
            window.Opened += (_, _) =>
            {
                vm.Start();
                if (StartupArgs.FirstOrDefault(a => a.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) is { } mailto)
                    vm.ComposeMailto(mailto);
            };

            if (Instance != null)
            {
                Instance.ArgumentsReceived += (_, args) => Dispatcher.UIThread.Post(() =>
                {
                    if (window.WindowState == Avalonia.Controls.WindowState.Minimized) window.WindowState = Avalonia.Controls.WindowState.Normal;
                    window.Activate();
                    if (args.FirstOrDefault(a => a.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) is { } link) vm.ComposeMailto(link);
                });
            }

            if (IsSmokeTest)
            {
                // CI: the window must open and render; then leave cleanly.
                window.Opened += async (_, _) =>
                {
                    MailClient.App.Services.Log.Info("diag: window opened");
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    Console.WriteLine($"smoke-test: окно открыто, WebKit: {(Controls.WebKit.IsAvailable ? "есть" : "нет")}");
                    desktop.Shutdown(0);
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
