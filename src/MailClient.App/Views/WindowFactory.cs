using System.Windows;
using MailClient.App.Services;
using MailClient.App.ViewModels;
using MailClient.Core.Models;

namespace MailClient.App.Views;

/// <summary>Creates and shows windows/dialogs for view models.</summary>
public static class WindowFactory
{
    private static Window? Owner
    {
        get
        {
            var w = Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive)
                    ?? Application.Current?.MainWindow;
            return w is { IsVisible: true } ? w : null;
        }
    }

    private static bool? ShowDialog(Window w)
    {
        var owner = Owner;
        if (owner != null && owner != w)
        {
            w.Owner = owner;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return w.ShowDialog();
    }

    public static void OpenCompose(ComposeViewModel vm, AppSettings settings)
    {
        var w = new ComposeWindow(vm, settings);
        w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Show();
        w.Activate();
    }

    public static void OpenMessage(MessagePreviewViewModel preview, MainViewModel main)
    {
        var w = new MessageWindow(preview, main) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        w.Show();
    }

    public static bool? EditAccount(AccountSettings account, CredentialProvider credentials, bool isNew, AccountSession? session = null) =>
        ShowDialog(new AccountWindow(account, credentials, isNew, session));

    public static FolderNodeViewModel? PickFolder(IEnumerable<FolderNodeViewModel> roots, string title, bool allowRoot = false)
    {
        var w = new FolderPickerWindow(roots, title, allowRoot);
        return ShowDialog(w) == true ? w.Selected : null;
    }

    public static string? Prompt(string title, string label, string initial)
    {
        var w = new PromptWindow(title, label, initial);
        return ShowDialog(w) == true ? w.Value : null;
    }

    public static bool? EditContact(Contact contact, bool isNew) => ShowDialog(new ContactWindow(contact, isNew));

    public static void OutOfOffice(AccountSession session) => ShowDialog(new OofWindow(session));

    public static void ForwardingRules(AccountSession session) => ShowDialog(new RulesWindow(session));

    public static bool? EditSettings(AppSettings settings) => ShowDialog(new SettingsWindow(settings));

    /// <summary>Edits an account's signature; the running session (if any) offers the address book and Outlook on the web.</summary>
    public static bool? EditSignature(AccountSettings account, AccountSession? session) => ShowDialog(new SignatureWindow(account, session));

    public static void About() => ShowDialog(new AboutWindow());
}
