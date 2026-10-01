using System.Windows;
using MailClient.App.Services;

namespace MailClient.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Версия {typeof(AboutWindow).Assembly.GetName().Version}";
        PathsText.Text = $"Настройки: {AppPaths.Roaming}\nКэш и журналы: {AppPaths.Local}";
    }
}
