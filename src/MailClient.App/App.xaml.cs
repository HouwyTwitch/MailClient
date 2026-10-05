using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using MailClient.App.Services;
using MailClient.App.ViewModels;
using MailClient.App.Views;

namespace MailClient.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2213", Justification = "Disposed in OnExit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A WPF Application ends in OnExit, where its resources are released")]
public partial class App : Application
{
    private const string InstanceMutexName = "MailClient.SingleInstance.{5D1C8E1B-6C1F-4E1E-9A3E-2B7F0F3E9D41}";
    private const string ActivateEventName = "MailClient.Activate.{5D1C8E1B-6C1F-4E1E-9A3E-2B7F0F3E9D41}";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private MainViewModel? _main;
    private TrayService? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Russian single-byte charsets (windows-1251, KOI8-R, CP866) for MimeKit/MailKit — before any mail is read.
        MailClient.Core.CodePages.EnsureRegistered();
        // Russian culture for dates, numbers and WPF controls (DatePicker etc.).
        var ru = RuText.Culture;
        Thread.CurrentThread.CurrentCulture = ru;
        Thread.CurrentThread.CurrentUICulture = ru;
        CultureInfo.DefaultThreadCurrentCulture = ru;
        CultureInfo.DefaultThreadCurrentUICulture = ru;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(ru.IetfLanguageTag)));

        var mailto = e.Args.FirstOrDefault(a => a.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));

        // Single instance: hand over to the running copy.
        _instanceMutex = new Mutex(true, InstanceMutexName, out bool isFirst);
        if (!isFirst)
        {
            if (mailto != null) File.WriteAllText(Path.Combine(AppPaths.Local, "pending-mailto.txt"), mailto);
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Log.Cleanup();
        _ = Task.Run(AppPaths.CleanupTemporaryFiles);
        MailClient.Core.Diagnostics.MailLog.Info = Log.Info;
        MailClient.Core.Diagnostics.MailLog.Warn = Log.Warn;
        Log.Info($"Запуск {AppInfo.Version}, Windows {Environment.OSVersion.Version}");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Необработанное исключение", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Необработанное исключение в задаче", args.Exception);
            args.SetObserved();
        };

        var settings = SettingsStore.Load();
        ThemeService.Apply(settings.Theme);

        if (!Controls.WebViewHost.IsRuntimeInstalled())
        {
            Log.Warn("WebView2 Runtime не установлен");
            Dialogs.Info("Не найден компонент Microsoft Edge WebView2 Runtime, необходимый для отображения писем в формате HTML.\n\n" +
                         "Программа будет работать в упрощённом текстовом режиме. Попросите администратора установить WebView2 Runtime.");
        }

        var credentials = new CredentialProvider(new DpapiSecretStore());
        _main = new MainViewModel(settings, credentials);

        if (settings.Accounts.Count == 0)
        {
            var account = new MailClient.Core.Models.AccountSettings();
            if (WindowFactory.EditAccount(account, credentials, isNew: true) != true)
            {
                Shutdown();
                return;
            }
            settings.Accounts.Add(account);
            SettingsStore.Save(settings);
        }

        _tray = new TrayService();
        _main.Tray = _tray;
        var window = new MainWindow(_main);
        MainWindow = window;
        _tray.OpenRequested += (_, _) => window.ShowFromTray();
        _tray.NewMessageRequested += (_, _) => _main.NewMessage();
        _tray.ExitRequested += (_, _) => window.ExitApplication();

        SessionEnding += (_, _) =>
        {
            Log.Info("Завершение сеанса Windows");
            window.AllowClose();
        };

        _main.Start();
        bool startMinimized = e.Args.Contains("--minimized") && settings.MinimizeToTray;
        if (!startMinimized) window.Show();

        if (mailto != null) _main.ComposeMailto(mailto);
        ListenForActivation(window);
    }

    private void ListenForActivation(MainWindow window)
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var thread = new Thread(() =>
        {
            while (_activateEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(() =>
                {
                    window.ShowFromTray();
                    var pending = Path.Combine(AppPaths.Local, "pending-mailto.txt");
                    if (File.Exists(pending))
                    {
                        var mailto = File.ReadAllText(pending);
                        File.Delete(pending);
                        _main?.ComposeMailto(mailto);
                    }
                });
            }
        }) { IsBackground = true, Name = "ActivationListener" };
        thread.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (e.Exception is OperationCanceledException)
        {
            // A superseded or aborted operation (typing during an address lookup, closing a window): not an error.
            Log.Info($"Операция отменена: {e.Exception.TargetSite?.DeclaringType?.Name}");
            e.Handled = true;
            return;
        }
        Log.Error("Необработанное исключение в интерфейсе", e.Exception);
        Dialogs.Error($"Произошла непредвиденная ошибка. Подробности записаны в журнал:\n{Log.CurrentFile}\n\n{e.Exception.Message}");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Завершение работы");
        _main?.Dispose();
        _tray?.Dispose();
        _instanceMutex?.Dispose();
        _activateEvent?.Dispose();
        base.OnExit(e);
    }
}
