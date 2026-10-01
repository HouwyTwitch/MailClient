using System.IO;
using System.Windows;
using System.Windows.Controls;
using MailClient.App.Services;
using MailClient.Core.Models;
using MailClient.Core.Services;
using MailClient.Exchange.Autodiscover;
using MailClient.Exchange.Ews;
using Microsoft.Win32;

namespace MailClient.App.Views;

public partial class AccountWindow : Window
{
    private readonly AccountSettings _account;
    private readonly CredentialProvider _credentials;
    private readonly bool _isNew;
    private readonly OrganizationDefaults _org;
    private string _certPem;
    private bool _passwordChanged;
    private bool _initialized;

    private bool IsImap => TagOf(ProtocolCombo) == "Imap";

    /// <summary>True when Windows is logged on with a domain account (user domain differs from the computer name).</summary>
    private static bool IsDomainJoined =>
        !string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    private static string WindowsAccount => $"{Environment.UserDomainName}\\{Environment.UserName}";

    public AccountWindow(AccountSettings account, CredentialProvider credentials, bool isNew)
    {
        InitializeComponent();
        _account = account;
        _credentials = credentials;
        _isNew = isNew;
        _org = OrganizationDefaults.Load();
        if (isNew)
        {
            // On a domain computer default to Windows single sign-on (as Thunderbird/Outlook do), unless policy says otherwise.
            if (IsDomainJoined && string.IsNullOrWhiteSpace(_org.AuthMethod)) account.AuthMethod = AuthMethod.IntegratedWindows;
            _org.ApplyTo(account);
        }
        _certPem = account.TrustedRootCertificatesPem;

        HeaderText.Text = isNew ? "Новая учётная запись" : "Настройки учётной записи";
        EmailBox.Text = account.EmailAddress;
        DisplayNameBox.Text = account.DisplayName;
        UserBox.Text = account.UserName;
        DomainBox.Text = account.Domain;
        EwsBox.Text = account.EwsUrl;
        SharedBox.Text = account.SharedMailbox;
        SignatureBox.Text = account.Signature;
        Select(AuthCombo, account.AuthMethod == AuthMethod.IntegratedWindows ? "IntegratedWindows" : "Password");
        Select(VersionCombo, account.ServerVersion.ToString());
        Select(SchemeCombo, account.AuthScheme.ToString());
        Select(IntervalCombo, account.SyncIntervalSeconds.ToString());
        ImapHostBox.Text = account.ImapHost;
        ImapPortBox.Text = account.ImapPort.ToString();
        SmtpHostBox.Text = account.SmtpHost;
        SmtpPortBox.Text = account.SmtpPort.ToString();
        Select(ImapSecurityCombo, account.ImapSecurity.ToString());
        Select(SmtpSecurityCombo, account.SmtpSecurity.ToString());
        SaveSentCheck.IsChecked = account.SaveSentCopy;
        PresetCombo.SelectedIndex = 0;
        Select(ProtocolCombo, account.Protocol.ToString());
        _initialized = true;
        ApplyProtocolUi();
        if (IntervalCombo.SelectedIndex < 0) IntervalCombo.SelectedIndex = 1;
        UpdateCertText();

        // Never prefill the password box: typing into a placeholder would corrupt the password.
        if (!isNew && credentials.GetPassword(account.Id) is { Length: > 0 })
            PasswordHint.Text = "Пароль сохранён. Оставьте поле пустым, чтобы не менять его.";
        PasswordBox.PasswordChanged += (_, _) => _passwordChanged = PasswordBox.Password.Length > 0;

        if (_org.LockServerSettings && !string.IsNullOrWhiteSpace(_org.EwsUrl))
        {
            EwsBox.IsReadOnly = true;
            DiscoverButton.IsEnabled = false;
            DiscoverStatus.Text = "Адрес сервера задан администратором.";
        }
        Loaded += (_, _) => (string.IsNullOrEmpty(EmailBox.Text) ? EmailBox : (Control)PasswordBox).Focus();
    }

    private static void Select(ComboBox combo, string tag) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? combo.Items[0];

    private static string TagOf(ComboBox combo) => (string)((ComboBoxItem)combo.SelectedItem).Tag;

    private void AuthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyProtocolUi();

    private void ProtocolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyProtocolUi();
        if (_initialized && IsImap && string.IsNullOrWhiteSpace(ImapHostBox.Text) && EmailBox.Text.Contains('@')) ApplyGuessedPreset();
    }

    private void ApplyProtocolUi()
    {
        if (!_initialized) return;
        bool imap = IsImap;
        AuthPanel.Visibility = imap ? Visibility.Collapsed : Visibility.Visible;
        ExchangePanel.Visibility = imap ? Visibility.Collapsed : Visibility.Visible;
        ExchangeAdvanced.Visibility = imap ? Visibility.Collapsed : Visibility.Visible;
        ImapPanel.Visibility = imap ? Visibility.Visible : Visibility.Collapsed;
        PasswordPanel.Visibility = imap || TagOf(AuthCombo) == "Password" ? Visibility.Visible : Visibility.Collapsed;
        SsoHint.Visibility = !imap && TagOf(AuthCombo) == "IntegratedWindows" ? Visibility.Visible : Visibility.Collapsed;
        SsoHint.Text = IsDomainJoined
            ? $"Будет использована текущая учётная запись Windows: {WindowsAccount}. Пароль вводить не нужно."
            : "Компьютер не входит в домен: единый вход, скорее всего, не сработает. Выберите вход по имени пользователя и паролю.";
        UserHint.Text = imap
            ? "Обычно это полный адрес электронной почты."
            : "Логин Windows (ДОМЕН\\логин или логин@домен). Он может отличаться от адреса почты — при проверке подключения программа попробует несколько вариантов.";
    }

    private void ApplyPreset(MailPresets.Preset p)
    {
        ImapHostBox.Text = p.ImapHost;
        ImapPortBox.Text = p.ImapPort.ToString();
        Select(ImapSecurityCombo, p.ImapSecurity.ToString());
        SmtpHostBox.Text = p.SmtpHost;
        SmtpPortBox.Text = p.SmtpPort.ToString();
        Select(SmtpSecurityCombo, p.SmtpSecurity.ToString());
    }

    private void ApplyGuessedPreset()
    {
        var (key, preset) = MailPresets.Guess(EmailBox.Text.Trim());
        ApplyPreset(preset);
        _initialized = false;
        Select(PresetCombo, key ?? "");
        _initialized = true;
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (!EmailBox.Text.Contains('@'))
        {
            Dialogs.Error("Сначала введите адрес электронной почты.");
            return;
        }
        ApplyGuessedPreset();
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        var key = TagOf(PresetCombo);
        if (key == "exchange") ApplyPreset(MailPresets.ExchangeImap(EmailBox.Text.Trim()));
        else if (MailPresets.ByKey.TryGetValue(key, out var p)) ApplyPreset(p);
    }

    /// <summary>Switching encryption moves the port to the matching standard port.</summary>
    private void Security_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (sender == ImapSecurityCombo && ImapPortBox.Text is "993" or "143" or "")
            ImapPortBox.Text = TagOf(ImapSecurityCombo) == "SslOnConnect" ? "993" : "143";
        if (sender == SmtpSecurityCombo && SmtpPortBox.Text is "465" or "587" or "25" or "")
            SmtpPortBox.Text = TagOf(SmtpSecurityCombo) switch { "SslOnConnect" => "465", "StartTls" => "587", _ => "25" };
    }

    private void EmailBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        if (!email.Contains('@') && _org.EmailDomain.Length > 0 && email.Length > 0)
        {
            email = $"{email}@{_org.EmailDomain}";
            EmailBox.Text = email;
        }
        if (string.IsNullOrWhiteSpace(UserBox.Text)) UserBox.Text = email;
        if (IsImap && string.IsNullOrWhiteSpace(ImapHostBox.Text) && email.Contains('@')) ApplyGuessedPreset();
    }

    private void UpdateCertText() =>
        CertText.Text = string.IsNullOrWhiteSpace(_certPem) ? "Не задан (используются сертификаты Windows)" : CertificateImport.Describe(_certPem);

    private void ImportCert_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Корневой сертификат",
            Filter = "Сертификаты (*.cer;*.crt;*.pem)|*.cer;*.crt;*.pem|Все файлы (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _certPem = CertificateImport.ToPem(File.ReadAllBytes(dlg.FileName));
            UpdateCertText();
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось прочитать сертификат");
        }
    }

    private void ClearCert_Click(object sender, RoutedEventArgs e)
    {
        _certPem = "";
        UpdateCertText();
    }

    /// <summary>Settings as currently entered (password is applied to the store separately).</summary>
    private AccountSettings Collect()
    {
        var a = _account.Clone();
        a.EmailAddress = EmailBox.Text.Trim();
        a.DisplayName = DisplayNameBox.Text.Trim();
        a.AuthMethod = TagOf(AuthCombo) == "IntegratedWindows" ? AuthMethod.IntegratedWindows : AuthMethod.Password;
        a.UserName = UserBox.Text.Trim();
        a.Domain = DomainBox.Text.Trim();
        a.EwsUrl = EwsBox.Text.Trim();
        a.ServerVersion = Enum.Parse<ExchangeServerVersion>(TagOf(VersionCombo));
        a.SharedMailbox = SharedBox.Text.Trim();
        a.Signature = SignatureBox.Text;
        a.SyncIntervalSeconds = int.Parse(TagOf(IntervalCombo));
        a.TrustedRootCertificatesPem = _certPem;
        a.AuthScheme = Enum.Parse<HttpAuthScheme>(TagOf(SchemeCombo));
        a.Protocol = IsImap ? MailProtocol.Imap : MailProtocol.Exchange;
        a.ImapHost = ImapHostBox.Text.Trim();
        a.ImapPort = int.TryParse(ImapPortBox.Text.Trim(), out var ip) ? ip : 0;
        a.ImapSecurity = Enum.Parse<ConnectionSecurity>(TagOf(ImapSecurityCombo));
        a.SmtpHost = SmtpHostBox.Text.Trim();
        a.SmtpPort = int.TryParse(SmtpPortBox.Text.Trim(), out var sp) ? sp : 0;
        a.SmtpSecurity = Enum.Parse<ConnectionSecurity>(TagOf(SmtpSecurityCombo));
        a.SaveSentCopy = SaveSentCheck.IsChecked == true;
        if (a.Protocol == MailProtocol.Imap) a.AuthMethod = AuthMethod.Password;
        return a;
    }

    /// <summary>Credentials for testing without persisting the password yet.</summary>
    private sealed class TemporaryCredentials : ICredentialProvider
    {
        private readonly string _password;
        public TemporaryCredentials(string password) => _password = password;
        public string? GetPassword(Guid accountId) => _password;
        public Task<string> GetAccessTokenAsync(AccountSettings account, bool forceRefresh, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private string EffectivePassword() =>
        _passwordChanged || _isNew ? PasswordBox.Password : _credentials.GetPassword(_account.Id) ?? "";

    private string? Validate(AccountSettings a, bool requireEws)
    {
        if (!EmailAddress.LooksValid(a.EmailAddress)) return "Введите корректный адрес электронной почты.";
        if (a.AuthMethod == AuthMethod.Password && string.IsNullOrEmpty(EffectivePassword()))
        {
            if (a.Protocol == MailProtocol.Exchange && IsDomainJoined)
            {
                Select(AuthCombo, "IntegratedWindows");
                ApplyProtocolUi();
                a.AuthMethod = AuthMethod.IntegratedWindows;
            }
            else
            {
                return "Введите пароль.";
            }
        }
        if (a.Protocol == MailProtocol.Imap)
        {
            if (a.ImapHost.Length == 0 || a.SmtpHost.Length == 0) return "Укажите серверы входящей (IMAP) и исходящей (SMTP) почты или нажмите «Заполнить по адресу».";
            if (a.ImapPort is < 1 or > 65535 || a.SmtpPort is < 1 or > 65535) return "Укажите корректные номера портов (например, 993 для IMAP и 465 для SMTP).";
            if ((a.ImapSecurity == ConnectionSecurity.None || a.SmtpSecurity == ConnectionSecurity.None) &&
                !Dialogs.Confirm("Выбрано подключение без шифрования — пароль и письма будут передаваться в открытом виде. Продолжить?"))
                return "";
            return null;
        }
        if (requireEws && !(Uri.TryCreate(a.EwsUrl, UriKind.Absolute, out var u) && u.Scheme is "https" or "http"))
            return "Укажите адрес EWS (например, https://mail.company.ru/EWS/Exchange.asmx) или нажмите «Найти автоматически».";
        if (requireEws && a.EwsUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !Dialogs.Confirm("Адрес EWS использует незащищённый протокол HTTP — пароль и письма будут передаваться без шифрования. Продолжить?"))
            return "";
        return null;
    }

    private void SetBusy(bool busy)
    {
        IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private async Task<bool> DiscoverAsync(AccountSettings a)
    {
        DiscoverStatus.Text = "Поиск сервера…";
        var client = new AutodiscoverClient(a, new TemporaryCredentials(EffectivePassword()));
        try
        {
            var result = await client.DiscoverAsync(a.EmailAddress);
            EwsBox.Text = result.EwsUrl;
            if (!string.IsNullOrWhiteSpace(result.ServerVersionHex))
                Select(VersionCombo, AutodiscoverClient.SuggestVersion(result.ServerVersionHex).ToString());
            if (string.IsNullOrWhiteSpace(DisplayNameBox.Text) && result.DisplayName.Length > 0)
                DisplayNameBox.Text = result.DisplayName;
            DiscoverStatus.Text = $"Сервер найден: {new Uri(result.EwsUrl).Host}";
            Log.Info($"Автообнаружение: {string.Join(" | ", client.Log)}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Автообнаружение не удалось: {ex.Message}; {string.Join(" | ", client.Log)}");
            DiscoverStatus.Text = RuText.Error(ex);
            return false;
        }
    }

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        var a = Collect();
        if (Validate(a, requireEws: false) is { } error)
        {
            if (error.Length > 0) Dialogs.Error(error);
            return;
        }
        SetBusy(true);
        try { await DiscoverAsync(a); }
        finally { SetBusy(false); }
    }

    /// <summary>
    /// Alternative login spellings tried automatically when the server rejects the one entered:
    /// the Windows login often differs from the e-mail address.
    /// </summary>
    private static IEnumerable<(string user, string domain)> LoginVariants(AccountSettings a)
    {
        if (a.Protocol == MailProtocol.Imap)
        {
            var login = string.IsNullOrWhiteSpace(a.UserName) ? a.EmailAddress : a.UserName.Trim();
            return new[] { (login, ""), (a.EmailAddress, ""), (a.EmailAddress.Split('@')[0], "") }
                .Where(v => v.Item1.Length > 0).DistinctBy(v => v.Item1.ToLowerInvariant());
        }
        var entered = string.IsNullOrWhiteSpace(a.UserName) ? a.EmailAddress : a.UserName.Trim();
        var local = a.EmailAddress.Split('@')[0];
        var mailDomain = a.EmailAddress.Contains('@') ? a.EmailAddress.Split('@')[1] : "";
        var bareUser = entered.Contains('\\') ? entered[(entered.IndexOf('\\') + 1)..] : entered.Split('@')[0];
        var netbios = a.Domain.Length > 0 ? a.Domain : mailDomain.Split('.')[0].ToUpperInvariant();

        var list = new List<(string, string)>
        {
            (entered, a.Domain),
            (a.EmailAddress, ""),
            ($"{bareUser}@{mailDomain}", ""),
            ($"{netbios}\\{bareUser}", ""),
            ($"{netbios}\\{local}", ""),
        };
        return list.Where(v => v.Item1.Length > 0 && !v.Item1.StartsWith('@') && !v.Item1.EndsWith('@') && !v.Item1.StartsWith('\\'))
                   .DistinctBy(v => (v.Item1.ToLowerInvariant(), v.Item2.ToLowerInvariant()));
    }

    private static string SchemeName(HttpAuthScheme s) => s switch
    {
        HttpAuthScheme.Ntlm => "NTLM",
        HttpAuthScheme.Negotiate => "Kerberos (Negotiate)",
        HttpAuthScheme.Basic => "Basic",
        _ => "автоматически",
    };

    /// <summary>Authentication schemes to try: the chosen one, or (for "Auto") Auto, then NTLM-only, then Basic.</summary>
    private static IReadOnlyList<HttpAuthScheme> SchemesToTry(AccountSettings a)
    {
        if (a.Protocol == MailProtocol.Imap) return new[] { HttpAuthScheme.Auto };
        if (a.AuthScheme != HttpAuthScheme.Auto) return new[] { a.AuthScheme };
        return a.AuthMethod == AuthMethod.IntegratedWindows
            ? new[] { HttpAuthScheme.Auto, HttpAuthScheme.Ntlm }
            : new[] { HttpAuthScheme.Auto, HttpAuthScheme.Ntlm, HttpAuthScheme.Basic };
    }

    /// <summary>Tries one combination; returns mailbox info or null on an authentication failure.</summary>
    private async Task<(MailboxInfo? info, Exception? error)> TryConnectAsync(AccountSettings attempt)
    {
        try
        {
            using var provider = ProviderFactory.Create(attempt, new TemporaryCredentials(EffectivePassword()));
            return (await provider.ConnectAsync(), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    private async Task<bool> TestAsync(AccountSettings a, bool showSuccess)
    {
        Exception? firstError = null;
        var attempts = new List<AccountSettings>();
        var variants = a.AuthMethod == AuthMethod.Password ? LoginVariants(a).ToList() : new() { (a.UserName, a.Domain) };
        foreach (var scheme in SchemesToTry(a))
            foreach (var (user, domain) in variants)
            {
                var attempt = a.Clone();
                attempt.UserName = user;
                attempt.Domain = domain;
                attempt.AuthScheme = scheme;
                attempts.Add(attempt);
            }
        // Last resort on a domain computer: Windows single sign-on (what Thunderbird does with NTLM).
        if (a.Protocol == MailProtocol.Exchange && a.AuthMethod == AuthMethod.Password && IsDomainJoined)
        {
            foreach (var scheme in new[] { HttpAuthScheme.Auto, HttpAuthScheme.Ntlm })
            {
                var sso = a.Clone();
                sso.AuthMethod = AuthMethod.IntegratedWindows;
                sso.AuthScheme = scheme;
                attempts.Add(sso);
            }
        }

        foreach (var attempt in attempts)
        {
            var who = attempt.AuthMethod == AuthMethod.IntegratedWindows ? $"единый вход Windows ({WindowsAccount})" : $"«{attempt.UserName}»";
            var (info, error) = await TryConnectAsync(attempt);
            if (info != null)
            {
                Log.Info($"Проверка подключения успешна: {a.EmailAddress} — {who}, протокол {SchemeName(attempt.AuthScheme)}, сервер {info.ServerVersion}");
                var changes = new List<string>();
                if (attempt.AuthMethod != a.AuthMethod)
                {
                    Select(AuthCombo, "IntegratedWindows");
                    changes.Add($"способ входа — единый вход Windows ({WindowsAccount})");
                }
                else if (!string.Equals(attempt.UserName, a.UserName, StringComparison.OrdinalIgnoreCase) || attempt.Domain != a.Domain)
                {
                    UserBox.Text = attempt.UserName;
                    DomainBox.Text = attempt.Domain;
                    changes.Add($"имя пользователя «{attempt.UserName}»");
                }
                if (attempt.AuthScheme != a.AuthScheme)
                {
                    Select(SchemeCombo, attempt.AuthScheme.ToString());
                    changes.Add($"протокол проверки подлинности {SchemeName(attempt.AuthScheme)}");
                }
                ApplyProtocolUi();
                if (showSuccess || changes.Count > 0)
                    Dialogs.Info("Подключение установлено." +
                                 (changes.Count > 0 ? $"\n\nПодобраны и сохранены в настройках: {string.Join("; ", changes)}." : "") +
                                 $"\n\nПочтовый ящик: {info.EmailAddress}\nВерсия сервера: {info.ServerVersion}");
                return true;
            }

            Log.Warn($"Вход {who}, протокол {SchemeName(attempt.AuthScheme)}: {error!.Message}");
            if (error is not MailAuthenticationException)
            {
                firstError = error;
                break; // network, certificate or URL problem - other credentials will not help
            }
            firstError ??= error;
            // OAuth-only servers reject every password variant; an SMTP rejection is not a login-format problem.
            if (error.Message.Contains("OAuth") || error.Message.StartsWith("SMTP")) break;
        }

        Log.Warn($"Проверка подключения не удалась ({attempts.Count} вариантов): {firstError?.Message}");
        Dialogs.Error(firstError!, $"Не удалось подключиться к серверу (проверено вариантов входа: {attempts.Count}; подробности — в журнале)");
        return false;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var a = Collect();
        SetBusy(true);
        try
        {
            if (!IsImap && string.IsNullOrWhiteSpace(a.EwsUrl) && Validate(a, false) == null && await DiscoverAsync(a)) a = Collect();
            if (Validate(a, requireEws: !IsImap) is { } error)
            {
                if (error.Length > 0) Dialogs.Error(error);
                return;
            }
            await TestAsync(a, showSuccess: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var a = Collect();
        SetBusy(true);
        try
        {
            if (!IsImap && string.IsNullOrWhiteSpace(a.EwsUrl) && Validate(a, false) == null && await DiscoverAsync(a)) a = Collect();
            if (Validate(a, requireEws: !IsImap) is { } error)
            {
                if (error.Length > 0) Dialogs.Error(error);
                return;
            }
            if (!await TestAsync(a, showSuccess: false) &&
                !Dialogs.Confirm("Подключиться к серверу не удалось. Сохранить настройки всё равно (например, для работы вне корпоративной сети)?"))
                return;

            if (a.AuthMethod == AuthMethod.Password)
            {
                if (_passwordChanged || _isNew) _credentials.SetPassword(a.Id, PasswordBox.Password);
            }
            else
            {
                _credentials.DeletePassword(a.Id);
            }
            a = Collect(); // the connection test may have corrected the login
            CopyInto(a, _account);
            DialogResult = true;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static void CopyInto(AccountSettings from, AccountSettings to)
    {
        foreach (var p in typeof(AccountSettings).GetProperties().Where(p => p.CanWrite))
            p.SetValue(to, p.GetValue(from));
    }
}
