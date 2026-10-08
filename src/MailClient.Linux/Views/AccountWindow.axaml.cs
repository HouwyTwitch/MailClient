using System.IO;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MailClient.App.Services;
using MailClient.Core.Models;
using MailClient.Core.Services;
using MailClient.Exchange.Autodiscover;
using MailClient.Exchange.Ews;

namespace MailClient.Linux.Views;

/// <summary>
/// Adds or edits an account. Like on Windows, the connection test makes exactly one login attempt with the
/// entered settings: repeated failed logins would lock the domain account.
/// </summary>
public partial class AccountWindow : Window
{
    private readonly AccountSettings _account;
    private readonly CredentialProvider _credentials;
    private readonly bool _isNew;
    private readonly AccountSession? _session;
    private string _certPem;
    private bool _saved;

    public AccountWindow() : this(new AccountSettings(), new CredentialProvider(new LinuxSecretStore()), true) { }

    public AccountWindow(AccountSettings account, CredentialProvider credentials, bool isNew, AccountSession? session = null)
    {
        InitializeComponent();
        _account = account;
        _session = session;
        _credentials = credentials;
        _isNew = isNew;
        _certPem = account.TrustedRootCertificatesPem;
        Title = isNew ? "Новая учётная запись" : $"Учётная запись — {account.EmailAddress}";

        ExchangeRadio.IsChecked = account.Protocol == MailProtocol.Exchange;
        ImapRadio.IsChecked = account.Protocol == MailProtocol.Imap;
        EmailBox.Text = account.EmailAddress;
        DisplayNameBox.Text = account.DisplayName;
        UserBox.Text = account.UserName;
        PasswordBox.PlaceholderText = isNew ? "" : "Сохранён (введите новый, чтобы заменить)";
        EwsBox.Text = account.EwsUrl;
        DomainBox.Text = account.Domain;
        Select(AuthCombo, account.AuthScheme.ToString());
        Select(VersionCombo, account.ServerVersion.ToString());
        ImapHostBox.Text = account.ImapHost;
        ImapPortBox.Text = account.ImapPort.ToString(CultureInfo.InvariantCulture);
        Select(ImapSecurityCombo, account.ImapSecurity.ToString());
        SmtpHostBox.Text = account.SmtpHost;
        SmtpPortBox.Text = account.SmtpPort.ToString(CultureInfo.InvariantCulture);
        Select(SmtpSecurityCombo, account.SmtpSecurity.ToString());
        ShowSignature();
        if (OrganizationDefaults.Current.LockServerSettings && !string.IsNullOrWhiteSpace(OrganizationDefaults.Current.EwsUrl))
            EwsBox.IsReadOnly = true;
        UpdateCertText();
        ApplyProtocol();
        Opened += (_, _) => EmailBox.Focus();
    }

    /// <summary>Shows the dialog; true when the account was saved (the password is already in the secret store).</summary>
    public static bool Edit(AccountSettings account, CredentialProvider credentials, bool isNew, AccountSession? session = null)
    {
        var window = new AccountWindow(account, credentials, isNew, session);
        return Dialogs.ShowModal(window, () => window._saved);
    }

    private bool IsImap => ImapRadio.IsChecked == true;

    /// <summary>A short preview of the signature (its text) next to the «Изменить подпись…» button.</summary>
    private void ShowSignature()
    {
        var text = MailClient.Core.Rendering.MessageHtmlBuilder.HtmlToText(MailClient.Core.Rendering.MessageSignature.Html(_account)).Trim();
        SignatureText.Text = text.Length == 0 ? "Не задана" : text;
    }

    /// <summary>The signature is part of these settings: it is kept with «Сохранить» of this window.</summary>
    private void EditSignature_Click(object? sender, RoutedEventArgs e)
    {
        _account.EmailAddress = EmailBox.Text?.Trim() ?? "";
        if (SignatureWindow.Edit(_account, _session)) ShowSignature();
    }

    private static void Select(ComboBox combo, string tag) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == tag) ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();

    private static string TagOf(ComboBox combo) => (string?)(combo.SelectedItem as ComboBoxItem)?.Tag ?? "";

    private void ApplyProtocol()
    {
        ExchangePanel.IsVisible = !IsImap;
        ImapPanel.IsVisible = IsImap;
        PasswordHint.IsVisible = !IsImap;
    }

    private void Protocol_Changed(object? sender, RoutedEventArgs e)
    {
        if (ExchangePanel != null) ApplyProtocol();
    }

    private void UpdateCertText() =>
        CertText.Text = _certPem.Length == 0 ? "Не задан (используются сертификаты системы)" : "Корневой сертификат задан";

    private void ChooseCert_Click(object? sender, RoutedEventArgs e)
    {
        var files = FileDialogs.OpenFiles("Корневой сертификат", "Сертификаты (*.cer;*.crt;*.pem)|*.cer;*.crt;*.pem", multiple: false);
        if (files.Count == 0) return;
        var file = files[0];
        try
        {
            _certPem = CertificateImport.ToPem(File.ReadAllBytes(file));
            UpdateCertText();
        }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException)
        {
            Dialogs.Error(ex, "Не удалось прочитать сертификат");
        }
    }

    private void ClearCert_Click(object? sender, RoutedEventArgs e)
    {
        _certPem = "";
        UpdateCertText();
    }

    private void Preset_Click(object? sender, RoutedEventArgs e)
    {
        var (_, preset) = MailPresets.Guess(EmailBox.Text?.Trim() ?? "");
        ImapHostBox.Text = preset.ImapHost;
        ImapPortBox.Text = preset.ImapPort.ToString(CultureInfo.InvariantCulture);
        Select(ImapSecurityCombo, preset.ImapSecurity.ToString());
        SmtpHostBox.Text = preset.SmtpHost;
        SmtpPortBox.Text = preset.SmtpPort.ToString(CultureInfo.InvariantCulture);
        Select(SmtpSecurityCombo, preset.SmtpSecurity.ToString());
    }

    private AccountSettings Collect()
    {
        var a = _account.Clone();
        a.Protocol = IsImap ? MailProtocol.Imap : MailProtocol.Exchange;
        a.EmailAddress = EmailBox.Text?.Trim() ?? "";
        a.DisplayName = DisplayNameBox.Text?.Trim() ?? "";
        a.UserName = UserBox.Text?.Trim() ?? "";
        a.AuthMethod = AuthMethod.Password;
        a.AuthScheme = Enum.TryParse<HttpAuthScheme>(TagOf(AuthCombo), out var scheme) ? scheme : HttpAuthScheme.Ntlm;
        a.EwsUrl = EwsBox.Text?.Trim() ?? "";
        a.Domain = DomainBox.Text?.Trim() ?? "";
        a.ServerVersion = Enum.TryParse<ExchangeServerVersion>(TagOf(VersionCombo), out var v) ? v : ExchangeServerVersion.Exchange2016;
        a.ImapHost = ImapHostBox.Text?.Trim() ?? "";
        a.ImapPort = int.TryParse(ImapPortBox.Text?.Trim(), out var ip) ? ip : 0;
        a.ImapSecurity = Enum.TryParse<ConnectionSecurity>(TagOf(ImapSecurityCombo), out var isec) ? isec : ConnectionSecurity.SslOnConnect;
        a.SmtpHost = SmtpHostBox.Text?.Trim() ?? "";
        a.SmtpPort = int.TryParse(SmtpPortBox.Text?.Trim(), out var sp) ? sp : 0;
        a.SmtpSecurity = Enum.TryParse<ConnectionSecurity>(TagOf(SmtpSecurityCombo), out var ssec) ? ssec : ConnectionSecurity.SslOnConnect;
        a.TrustedRootCertificatesPem = _certPem;
        return a;
    }

    private string EffectivePassword() =>
        !string.IsNullOrEmpty(PasswordBox.Text) || _isNew ? PasswordBox.Text ?? "" : _credentials.GetPassword(_account.Id) ?? "";

    private string? Validate(AccountSettings a, bool requireEws)
    {
        if (!EmailAddress.LooksValid(a.EmailAddress)) return "Введите корректный адрес электронной почты.";
        if (string.IsNullOrEmpty(EffectivePassword()) && (a.Protocol == MailProtocol.Imap || a.AuthScheme == HttpAuthScheme.Basic))
            return "Введите пароль.";
        if (a.Protocol == MailProtocol.Imap)
        {
            if (a.ImapHost.Length == 0 || a.SmtpHost.Length == 0) return "Укажите серверы IMAP и SMTP или нажмите «Заполнить по адресу».";
            if (a.ImapPort is < 1 or > 65535 || a.SmtpPort is < 1 or > 65535) return "Укажите корректные номера портов (например, 993 и 465).";
            return null;
        }
        if (requireEws && !(Uri.TryCreate(a.EwsUrl, UriKind.Absolute, out var u) && u.Scheme is "https" or "http"))
            return "Укажите адрес EWS (например, https://mail.company.ru/EWS/Exchange.asmx) или нажмите «Найти автоматически».";
        return null;
    }

    private sealed class TemporaryCredentials(string password) : ICredentialProvider
    {
        public string? GetPassword(Guid accountId) => password;
    }

    private void SetBusy(bool busy, string status = "")
    {
        TestButton.IsEnabled = SaveButton.IsEnabled = !busy;
        StatusText.Text = status;
    }

    private async Task<bool> DiscoverAsync(AccountSettings a)
    {
        SetBusy(true, "Поиск сервера…");
        var client = new AutodiscoverClient(a, new TemporaryCredentials(EffectivePassword()));
        try
        {
            var result = await client.DiscoverAsync(a.EmailAddress);
            EwsBox.Text = result.EwsUrl;
            if (!string.IsNullOrWhiteSpace(result.ServerVersionHex)) Select(VersionCombo, AutodiscoverClient.SuggestVersion(result.ServerVersionHex).ToString());
            if (string.IsNullOrWhiteSpace(DisplayNameBox.Text) && result.DisplayName.Length > 0) DisplayNameBox.Text = result.DisplayName;
            Log.Info($"Автообнаружение: {string.Join(" | ", client.Log)}");
            SetBusy(false, $"Сервер найден: {new Uri(result.EwsUrl).Host}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Автообнаружение не удалось: {ex.Message}; {string.Join(" | ", client.Log)}");
            SetBusy(false, RuText.Error(ex));
            return false;
        }
    }

    private async void Discover_Click(object? sender, RoutedEventArgs e)
    {
        var a = Collect();
        if (Validate(a, requireEws: false) is { } error)
        {
            Dialogs.Error(error);
            return;
        }
        await DiscoverAsync(a);
    }

    /// <summary>One login attempt with exactly the entered settings.</summary>
    private async Task<bool> TestAsync(AccountSettings a, bool showSuccess)
    {
        SetBusy(true, "Проверка подключения…");
        try
        {
            using var provider = ProviderFactory.Create(a, new TemporaryCredentials(EffectivePassword()));
            var info = await provider.ConnectAsync();
            if (a.Protocol == MailProtocol.Exchange && ExchangeProvider.SuggestVersion(info.ServerVersion) is { } version && version != a.ServerVersion)
                Select(VersionCombo, version.ToString());
            Log.Info($"Проверка подключения успешна: {a.EmailAddress}, сервер {info.ServerVersion}");
            SetBusy(false, "Подключение установлено");
            if (showSuccess) Dialogs.Info($"Подключение установлено.\n\nПочтовый ящик: {info.EmailAddress}\nВерсия сервера: {info.ServerVersion}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Проверка подключения: {ex.Message}");
            SetBusy(false, "");
            Dialogs.Error(ex, "Не удалось подключиться к серверу");
            return false;
        }
    }

    private async Task<AccountSettings?> PrepareAsync()
    {
        var a = Collect();
        if (!IsImap && string.IsNullOrWhiteSpace(a.EwsUrl) && Validate(a, false) == null && await DiscoverAsync(a)) a = Collect();
        if (Validate(a, requireEws: !IsImap) is { } error)
        {
            Dialogs.Error(error);
            return null;
        }
        return a;
    }

    private async void Test_Click(object? sender, RoutedEventArgs e)
    {
        if (await PrepareAsync() is { } a) await TestAsync(a, showSuccess: true);
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (await PrepareAsync() is not { } a) return;
        if (!await TestAsync(a, showSuccess: false) &&
            !Dialogs.Confirm("Подключиться к серверу не удалось. Сохранить настройки всё равно (например, для работы вне корпоративной сети)?"))
            return;
        var password = EffectivePassword();
        if (password.Length == 0) _credentials.DeletePassword(a.Id);
        else if (!string.IsNullOrEmpty(PasswordBox.Text) || _isNew) _credentials.SetPassword(a.Id, password);
        a = Collect();
        foreach (var p in typeof(AccountSettings).GetProperties().Where(p => p.CanWrite)) p.SetValue(_account, p.GetValue(a));
        _saved = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
