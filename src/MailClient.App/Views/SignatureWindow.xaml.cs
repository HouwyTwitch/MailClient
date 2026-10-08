using System.Windows;
using MailClient.App.Services;
using MailClient.Core.Models;
using MailClient.Core.Rendering;
using MailClient.Exchange.Ews;

namespace MailClient.App.Views;

/// <summary>
/// Edits the signature of one account: formatted text, when it is added, and ready-made signatures from the
/// address book or from Outlook on the web. The account settings are changed only on «Сохранить».
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Released in OnClosed, where a window's lifetime ends")]
public partial class SignatureWindow : Window
{
    private readonly AccountSettings _account;
    private readonly AccountSession? _session;

    /// <param name="account">Settings to change (on «Сохранить»).</param>
    /// <param name="session">The running account, for the address book and Outlook on the web; null for an account not connected yet.</param>
    public SignatureWindow(AccountSettings account, AccountSession? session)
    {
        InitializeComponent();
        _account = account;
        _session = session;
        Title = $"Подпись — {account.EmailAddress}";
        HeaderText.Text = $"Подпись для {account.EmailAddress}";
        OnNewCheck.IsChecked = account.SignatureOnNew;
        OnReplyCheck.IsChecked = account.SignatureOnReply;
        Formatting.Attach(Editor);
        DirectoryButton.Visibility = session != null ? Visibility.Visible : Visibility.Collapsed;
        WebButton.Visibility = session?.Provider is ExchangeProvider ? Visibility.Visible : Visibility.Collapsed;

        Loaded += async (_, _) =>
        {
            var settings = SettingsStore.Load();
            await Editor.SetBaseFontAsync(settings.ComposeFontFamily, settings.ComposeFontSize);
            await Editor.SetHtmlAsync(MessageSignature.Html(account));
            if (!Editor.IsHtml) FormatBar.Visibility = Visibility.Collapsed;
            await Editor.FocusEditorAsync();
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        Editor.Dispose();
        base.OnClosed(e);
    }

    private async void Directory_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        var address = string.IsNullOrWhiteSpace(_account.SharedMailbox) ? _account.EmailAddress : _account.SharedMailbox;
        StatusText.Text = "Поиск в адресной книге…";
        try
        {
            var entries = await _session.Provider.ResolveNamesAsync(address);
            var me = entries.FirstOrDefault(c => c.EmailAddresses.Any(a => a.Equals(address, StringComparison.OrdinalIgnoreCase)))
                     ?? (entries.Count > 0 ? entries[0] : null);
            if (me == null)
            {
                StatusText.Text = "";
                Dialogs.Info($"В адресной книге нет записи для {address}. Введите подпись вручную.");
                return;
            }
            if (!await ConfirmReplaceAsync()) return;
            await Editor.SetHtmlAsync(MessageSignature.FromContact(me, address));
            StatusText.Text = "Подпись заполнена из адресной книги — проверьте и при необходимости дополните её.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            Dialogs.Error(ex, "Не удалось получить данные из адресной книги");
        }
    }

    private async void Web_Click(object sender, RoutedEventArgs e)
    {
        if (_session?.Provider is not ExchangeProvider exchange) return;
        StatusText.Text = "Загрузка подписи с сервера…";
        try
        {
            var html = await exchange.GetWebSignatureAsync();
            if (string.IsNullOrWhiteSpace(html))
            {
                StatusText.Text = "";
                Dialogs.Info("В Outlook в Интернете подпись для этого почтового ящика не настроена.");
                return;
            }
            if (!await ConfirmReplaceAsync()) return;
            await Editor.SetHtmlAsync(html);
            StatusText.Text = "Подпись загружена из Outlook в Интернете.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            Dialogs.Error(ex, "Не удалось загрузить подпись с сервера");
        }
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        await Editor.SetHtmlAsync("");
        await Editor.FocusEditorAsync();
    }

    /// <summary>Asks before a ready-made signature replaces text the user has typed.</summary>
    private async Task<bool> ConfirmReplaceAsync()
    {
        var current = MessageHtmlBuilder.HtmlToText(await Editor.GetContentAsync()).Trim();
        if (current.Length == 0 || Dialogs.Confirm("Заменить текущую подпись?")) return true;
        StatusText.Text = "";
        return false;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var content = await Editor.GetContentAsync();
        var text = MessageHtmlBuilder.HtmlToText(content).Trim();
        bool hasPicture = content.Contains("<img", StringComparison.OrdinalIgnoreCase);
        _account.SignatureHtml = text.Length == 0 && !hasPicture ? ""
            : Editor.IsHtml ? content : MessageSignature.FromText(content);
        _account.Signature = "";
        _account.SignatureOnNew = OnNewCheck.IsChecked == true;
        _account.SignatureOnReply = OnReplyCheck.IsChecked == true;
        DialogResult = true;
    }
}
