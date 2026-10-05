using System.Windows;
using System.Windows.Controls;
using MailClient.App.Services;

namespace MailClient.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private bool _clearTrusted;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _s = settings;
        Select(ThemeCombo, settings.Theme.ToString());
        Select(ReadDelayCombo, settings.MarkAsReadDelaySeconds.ToString());
        if (ReadDelayCombo.SelectedIndex < 0) ReadDelayCombo.SelectedIndex = 1;
        RemoteCheck.IsChecked = settings.LoadRemoteImages;
        ConfirmDeleteCheck.IsChecked = settings.ConfirmDelete;
        NotifyCheck.IsChecked = settings.ShowNotifications;
        TrayCheck.IsChecked = settings.MinimizeToTray;
        StartupCheck.IsChecked = settings.StartWithWindows;

        var fonts = EditorFonts.Families.ToList();
        if (!fonts.Contains(settings.ComposeFontFamily, StringComparer.OrdinalIgnoreCase)) fonts.Insert(0, settings.ComposeFontFamily);
        FontCombo.ItemsSource = fonts;
        FontCombo.SelectedItem = fonts.First(f => f.Equals(settings.ComposeFontFamily, StringComparison.OrdinalIgnoreCase));
        var sizes = EditorFonts.Sizes.ToList();
        if (!sizes.Contains(settings.ComposeFontSize)) sizes = [.. sizes.Append(settings.ComposeFontSize).Order()];
        SizeCombo.ItemsSource = sizes;
        SizeCombo.SelectedItem = settings.ComposeFontSize;
    }

    private static void Select(ComboBox combo, string tag) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);

    private static string TagOf(ComboBox combo) => (string)((ComboBoxItem)combo.SelectedItem).Tag;

    private void ClearTrusted_Click(object sender, RoutedEventArgs e)
    {
        _clearTrusted = true;
        Dialogs.Info("Список будет очищен после сохранения параметров.");
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _s.Theme = Enum.Parse<AppTheme>(TagOf(ThemeCombo));
        _s.MarkAsReadDelaySeconds = int.Parse(TagOf(ReadDelayCombo));
        _s.LoadRemoteImages = RemoteCheck.IsChecked == true;
        _s.ConfirmDelete = ConfirmDeleteCheck.IsChecked == true;
        _s.ShowNotifications = NotifyCheck.IsChecked == true;
        _s.MinimizeToTray = TrayCheck.IsChecked == true;
        _s.StartWithWindows = StartupCheck.IsChecked == true;
        if (FontCombo.SelectedItem is string family) _s.ComposeFontFamily = family;
        if (SizeCombo.SelectedItem is double size) _s.ComposeFontSize = size;
        if (_clearTrusted) _s.TrustedSenders.Clear();
        DialogResult = true;
    }
}
