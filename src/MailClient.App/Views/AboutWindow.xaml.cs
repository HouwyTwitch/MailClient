using System.IO;
using System.Windows;
using MailClient.App.Services;

namespace MailClient.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Версия {AppInfo.Version}";
        PathsText.Text = $"Настройки: {AppPaths.Roaming}\nКэш и журналы: {AppPaths.Local}";
        NoticesButton.IsEnabled = File.Exists(NoticesFile);
    }

    private static string NoticesFile => Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");

    private void Notices_Click(object sender, RoutedEventArgs e) => WindowsIntegration.ShellOpen(NoticesFile);
}
