using System.Windows;

namespace MailClient.App.Services;

public static class Dialogs
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                                     ?? Application.Current?.MainWindow;

    public static void Error(string message, string title = "Ошибка")
    {
        Log.Warn($"Показана ошибка: {message}");
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public static void Error(Exception ex, string action) => Error($"{action}.\n\n{RuText.Error(ex)}");

    public static void Info(string message, string title = "Корпоративная почта") =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(string message, string title = "Подтверждение") =>
        Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>Returns true = yes, false = no, null = cancel.</summary>
    public static bool? YesNoCancel(string message, string title = "Подтверждение") =>
        Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var owner = Owner;
        return owner != null && owner.IsVisible
            ? MessageBox.Show(owner, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);
    }
}
