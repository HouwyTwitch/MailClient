using System.Windows;
using Microsoft.Win32;

namespace MailClient.App.Services;

public static class ThemeService
{
    public static bool IsDark { get; private set; }

    public static event EventHandler? ThemeChanged;

    public static void Apply(AppTheme theme)
    {
        IsDark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemUsesDarkTheme(),
        };
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d => d.Source != null &&
            (d.Source.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
             d.Source.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)));
        var replacement = new ResourceDictionary
        {
            Source = new Uri(IsDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative),
        };
        if (existing != null) dictionaries[dictionaries.IndexOf(existing)] = replacement;
        else dictionaries.Insert(0, replacement);
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}
