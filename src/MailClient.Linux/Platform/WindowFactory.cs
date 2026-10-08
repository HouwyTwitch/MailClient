using MailClient.App.Services;
using MailClient.App.ViewModels;
using MailClient.Core.Models;

namespace MailClient.App.Views;

/// <summary>Opens windows for the shared view models (Linux build).</summary>
public static class WindowFactory
{
    public static void OpenCompose(ComposeViewModel vm, AppSettings settings)
    {
        var window = new Linux.Views.ComposeWindow(vm, settings);
        window.Show();
        window.Activate();
    }

    /// <summary>Personal contacts are edited in Outlook Web Access in this version.</summary>
    public static bool? EditContact(Contact contact, bool isNew)
    {
        Log.Info($"Добавление контакта «{contact.DisplayName}» (новый: {isNew}) недоступно в версии для Linux");
        Dialogs.Info("Редактирование контактов пока недоступно в версии для Linux. Добавьте контакт в веб-интерфейсе почты (Outlook Web Access).");
        return false;
    }
}
