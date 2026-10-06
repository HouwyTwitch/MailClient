using Avalonia.Controls;
using Avalonia.Input;
using MailClient.App.ViewModels;
using MailClient.Linux.ViewModels;

namespace MailClient.Linux.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow() : this(new MainViewModel(new global::MailClient.App.Services.AppSettings(), new global::MailClient.App.Services.CredentialProvider(new global::MailClient.App.Services.LinuxSecretStore()))) { }

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        var s = vm.Settings;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
        Panes.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(s.FolderPaneWidth, 170, 500));
        Panes.ColumnDefinitions[2].Width = new GridLength(Math.Clamp(s.MessageListWidth, 260, 900));
        Closing += (_, _) => SaveLayout();
    }

    private void SaveLayout()
    {
        var s = _vm.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = Bounds.Width;
            s.WindowHeight = Bounds.Height;
        }
        s.FolderPaneWidth = Panes.ColumnDefinitions[0].ActualWidth;
        s.MessageListWidth = Panes.ColumnDefinitions[2].ActualWidth;
        global::MailClient.App.Services.SettingsStore.Save(s);
    }

    private void MessageList_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        _vm.SetSelection(MessageList.SelectedItems?.OfType<MessageItemViewModel>() ?? []);

    private void MessageList_DoubleTapped(object? sender, TappedEventArgs e) => _vm.OpenSelectedCommand.Execute(null);
}
