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

    public AccountWindow(AccountSettings account, CredentialProvider credentials, bool isNew)
    {
        InitializeComponent();
        _account = account;
        _credentials = credentials;
        _isNew = isNew;
        _org = OrganizationDefaults.Load();
        if (isNew) _org.ApplyTo(account);
        _certPem = account.TrustedRootCertificatesPem;

        HeaderText.Text = isNew ? "Подключение к Microsoft Exchange" : "Настройки учётной записи";
        EmailBox.Text = account.EmailAddress;
        DisplayNameBox.Text = account.DisplayName;
        UserBox.Text = account.UserName;
        DomainBox.Text = account.Domain;
        EwsBox.Text = account.EwsUrl;
        SharedBox.Text = account.SharedMailbox;
        SignatureBox.Text = account.Signature;
        Select(AuthCombo, account.AuthMethod == AuthMethod.IntegratedWindows ? "IntegratedWindows" : "Password");
        Select(VersionCombo, account.ServerVersion.ToString());
        Select(IntervalCombo, account.SyncIntervalSeconds.ToString());
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

    private void AuthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PasswordPanel != null)
            PasswordPanel.Visibility = TagOf(AuthCombo) == "Password" ? Visibility.Visible : Visibility.Collapsed;
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
        if (a.AuthMethod == AuthMethod.Password && string.IsNullOrEmpty(EffectivePassword())) return "Введите пароль.";
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

    private async Task<bool> TestAsync(AccountSettings a, bool showSuccess)
    {
        Exception? firstError = null;
        var variants = a.AuthMethod == AuthMethod.Password ? LoginVariants(a).ToList() : new() { (a.UserName, a.Domain) };
        foreach (var (user, domain) in variants)
        {
            var attempt = a.Clone();
            attempt.UserName = user;
            attempt.Domain = domain;
            try
            {
                using var provider = new ExchangeProvider(attempt, new TemporaryCredentials(EffectivePassword()));
                var info = await provider.ConnectAsync();
                Log.Info($"Проверка подключения успешна: {a.EmailAddress} как «{user}», сервер {info.ServerVersion}");
                bool changed = !string.Equals(user, a.UserName, StringComparison.OrdinalIgnoreCase) || domain != a.Domain;
                if (changed)
                {
                    UserBox.Text = user;
                    DomainBox.Text = domain;
                }
                if (showSuccess || changed)
                    Dialogs.Info("Подключение установлено." +
                                 (changed ? $"\n\nСервер принял имя пользователя «{user}» — оно подставлено в настройки." : "") +
                                 $"\n\nПочтовый ящик: {info.EmailAddress}\nВерсия сервера: {info.ServerVersion}");
                return true;
            }
            catch (MailAuthenticationException ex)
            {
                Log.Warn($"Вход как «{user}» отклонён: {ex.Message}");
                firstError ??= ex;
                // OAuth-only servers reject every password variant - no point trying further.
                if (ex.Message.Contains("OAuth")) break;
            }
            catch (Exception ex)
            {
                firstError = ex;
                break; // not a credentials problem (network, certificate, URL)
            }
        }
        Log.Warn($"Проверка подключения не удалась: {firstError?.Message}");
        Dialogs.Error(firstError!, "Не удалось подключиться к серверу");
        return false;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var a = Collect();
        SetBusy(true);
        try
        {
            if (string.IsNullOrWhiteSpace(a.EwsUrl) && Validate(a, false) == null && await DiscoverAsync(a)) a = Collect();
            if (Validate(a, requireEws: true) is { } error)
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
            if (string.IsNullOrWhiteSpace(a.EwsUrl) && Validate(a, false) == null && await DiscoverAsync(a)) a = Collect();
            if (Validate(a, requireEws: true) is { } error)
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
