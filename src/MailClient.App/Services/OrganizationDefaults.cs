using System.IO;
using System.Text.Json;
using MailClient.Core.Models;
using Microsoft.Win32;

namespace MailClient.App.Services;

/// <summary>
/// Pre-configuration for corporate roll-out. An administrator can set defaults via Group Policy
/// (registry HKLM\SOFTWARE\Policies\MailClient or HKCU\...) or an "organization.json" file next to the
/// executable, so that users only enter their login and password.
/// </summary>
/// <remarks>
/// Supported values: EwsUrl, Domain, EmailDomain, AuthMethod (Password|IntegratedWindows),
/// ServerVersion (Exchange2010_SP2|Exchange2013|Exchange2013_SP1|Exchange2016),
/// TrustedRootCertificateFile (path to .cer/.crt/.pem), LockServerSettings (1 = users cannot change EWS URL),
/// DisableForwardingRules (1 = users cannot create mail forwarding rules). Administrative templates for these
/// policies: deploy\admx.
/// </remarks>
public sealed class OrganizationDefaults
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string EwsUrl { get; set; } = "";
    public string Domain { get; set; } = "";
    public string EmailDomain { get; set; } = "";
    public string AuthMethod { get; set; } = "";
    public string ServerVersion { get; set; } = "";
    public string TrustedRootCertificateFile { get; set; } = "";
    public bool LockServerSettings { get; set; }
    /// <summary>Exchange (default) or Imap.</summary>
    public string Protocol { get; set; } = "";
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; }
    /// <summary>Hides the forwarding rules window (data leak prevention; enforce it on the server too).</summary>
    public bool DisableForwardingRules { get; set; }

    private static readonly Lazy<OrganizationDefaults> CurrentLazy = new(Load);

    /// <summary>The defaults and policies in effect, read once per run.</summary>
    public static OrganizationDefaults Current => CurrentLazy.Value;

    private const string PolicyKey = @"SOFTWARE\Policies\MailClient";

    public static OrganizationDefaults Load()
    {
        var d = new OrganizationDefaults();
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "organization.json");
            if (File.Exists(file))
                d = JsonSerializer.Deserialize<OrganizationDefaults>(File.ReadAllText(file), JsonOptions) ?? d;
        }
        catch (Exception ex)
        {
            Log.Warn($"organization.json не прочитан: {ex.Message}");
        }

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(PolicyKey);
                if (key == null) continue;
                d.EwsUrl = key.GetValue("EwsUrl") as string ?? d.EwsUrl;
                d.Domain = key.GetValue("Domain") as string ?? d.Domain;
                d.EmailDomain = key.GetValue("EmailDomain") as string ?? d.EmailDomain;
                d.AuthMethod = key.GetValue("AuthMethod") as string ?? d.AuthMethod;
                d.ServerVersion = key.GetValue("ServerVersion") as string ?? d.ServerVersion;
                d.TrustedRootCertificateFile = key.GetValue("TrustedRootCertificateFile") as string ?? d.TrustedRootCertificateFile;
                if (key.GetValue("LockServerSettings") is int lockValue) d.LockServerSettings = lockValue != 0;
                d.Protocol = key.GetValue("Protocol") as string ?? d.Protocol;
                d.ImapHost = key.GetValue("ImapHost") as string ?? d.ImapHost;
                if (key.GetValue("ImapPort") is int imapPort) d.ImapPort = imapPort;
                d.SmtpHost = key.GetValue("SmtpHost") as string ?? d.SmtpHost;
                if (key.GetValue("SmtpPort") is int smtpPort) d.SmtpPort = smtpPort;
                if (key.GetValue("DisableForwardingRules") is int noRules) d.DisableForwardingRules = noRules != 0;
            }
            catch (Exception ex)
            {
                Log.Warn($"Политика {hive.Name}\\{PolicyKey} не прочитана: {ex.Message}");
            }
        }
        return d;
    }

    public void ApplyTo(AccountSettings a)
    {
        if (!string.IsNullOrWhiteSpace(EwsUrl)) a.EwsUrl = EwsUrl;
        if (!string.IsNullOrWhiteSpace(Domain)) a.Domain = Domain;
        if (Enum.TryParse<AuthMethod>(AuthMethod, true, out var am)) a.AuthMethod = am;
        if (Enum.TryParse<ExchangeServerVersion>(ServerVersion, true, out var sv)) a.ServerVersion = sv;
        if (Enum.TryParse<MailProtocol>(Protocol, true, out var protocol)) a.Protocol = protocol;
        if (!string.IsNullOrWhiteSpace(ImapHost)) a.ImapHost = ImapHost;
        if (ImapPort > 0)
        {
            a.ImapPort = ImapPort;
            a.ImapSecurity = ImapPort == 993 ? ConnectionSecurity.SslOnConnect : ConnectionSecurity.StartTls;
        }
        if (!string.IsNullOrWhiteSpace(SmtpHost)) a.SmtpHost = SmtpHost;
        if (SmtpPort > 0)
        {
            a.SmtpPort = SmtpPort;
            a.SmtpSecurity = SmtpPort == 465 ? ConnectionSecurity.SslOnConnect : ConnectionSecurity.StartTls;
        }
        if (!string.IsNullOrWhiteSpace(TrustedRootCertificateFile))
        {
            try { a.TrustedRootCertificatesPem = CertificateImport.ToPem(File.ReadAllBytes(TrustedRootCertificateFile)); }
            catch (Exception ex) { Log.Warn($"Корневой сертификат из политики не загружен: {ex.Message}"); }
        }
    }
}
