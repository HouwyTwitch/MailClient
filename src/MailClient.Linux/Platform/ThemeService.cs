using Avalonia;
using Avalonia.Styling;

namespace MailClient.App.Services;

/// <summary>Light or dark appearance (follows the desktop unless chosen in the settings).</summary>
public static class ThemeService
{
    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public static void Apply(AppTheme theme)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
