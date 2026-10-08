using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MailClient.App.Services;
using MailClient.Core.Models;
using MailClient.Core.Rendering;
using MailClient.Exchange.Ews;

namespace MailClient.Linux.Views;

/// <summary>
/// Edits the signature of one account: formatted text, when it is added, and ready-made signatures from the
/// address book or from Outlook on the web. The account settings change only on «Сохранить».
/// </summary>
public partial class SignatureWindow : Window
{
    private readonly AccountSettings _account;
    private readonly AccountSession? _session;
    private bool _saved;
    private bool _updatingFormat;

    public SignatureWindow() : this(new AccountSettings(), null) { }

    public SignatureWindow(AccountSettings account, AccountSession? session)
    {
        InitializeComponent();
        _account = account;
        _session = session;
        Title = $"Подпись — {account.EmailAddress}";
        HeaderText.Text = $"Подпись для {account.EmailAddress}";
        OnNewCheck.IsChecked = account.SignatureOnNew;
        OnReplyCheck.IsChecked = account.SignatureOnReply;
        DirectoryButton.IsVisible = session != null;
        WebButton.IsVisible = session?.Provider is ExchangeProvider;
        FormatBar.IsVisible = Editor.IsHtml;

        var settings = SettingsStore.Load();
        FontCombo.ItemsSource = EditorFonts.Families;
        SizeCombo.ItemsSource = EditorFonts.Sizes.Select(s => s.ToString("0.#", CultureInfo.InvariantCulture)).ToList();
        _updatingFormat = true;
        FontCombo.SelectedItem = settings.ComposeFontFamily;
        SizeCombo.SelectedItem = settings.ComposeFontSize.ToString("0.#", CultureInfo.InvariantCulture);
        _updatingFormat = false;
        Editor.FormatStateChanged += (_, state) =>
        {
            _updatingFormat = true;
            BoldButton.IsChecked = state.Bold;
            ItalicButton.IsChecked = state.Italic;
            UnderlineButton.IsChecked = state.Underline;
            if (EditorFonts.Families.Contains(state.FontFamily)) FontCombo.SelectedItem = state.FontFamily;
            SizeCombo.SelectedItem = state.FontSizePt.ToString("0.#", CultureInfo.InvariantCulture);
            _updatingFormat = false;
        };
        Opened += async (_, _) =>
        {
            await Editor.SetBaseFontAsync(settings.ComposeFontFamily, settings.ComposeFontSize);
            await Editor.SetHtmlAsync(MessageSignature.Html(account));
            await Editor.FocusEditorAsync();
        };
    }

    /// <summary>Shows the dialog; true when the signature was saved into <paramref name="account"/>.</summary>
    public static bool Edit(AccountSettings account, AccountSession? session)
    {
        var window = new SignatureWindow(account, session);
        return Dialogs.ShowModal(window, () => window._saved);
    }

    private async void Format_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string command }) await Editor.ExecAsync(command);
        await Editor.FocusEditorAsync();
    }

    private async void FontCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingFormat || FontCombo.SelectedItem is not string family) return;
        await Editor.SetFontFamilyAsync(family);
    }

    private async void SizeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingFormat || SizeCombo.SelectedItem is not string text ||
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size)) return;
        await Editor.SetFontSizeAsync(size);
    }

    private async void Directory_Click(object? sender, RoutedEventArgs e)
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

    private async void Web_Click(object? sender, RoutedEventArgs e)
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

    private async void Clear_Click(object? sender, RoutedEventArgs e)
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

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var content = await Editor.GetContentAsync();
        var text = MessageHtmlBuilder.HtmlToText(content).Trim();
        bool hasPicture = content.Contains("<img", StringComparison.OrdinalIgnoreCase);
        _account.SignatureHtml = text.Length == 0 && !hasPicture ? ""
            : Editor.IsHtml ? content : MessageSignature.FromText(content);
        _account.Signature = "";
        _account.SignatureOnNew = OnNewCheck.IsChecked == true;
        _account.SignatureOnReply = OnReplyCheck.IsChecked == true;
        _saved = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
