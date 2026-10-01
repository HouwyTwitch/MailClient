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
/// TrustedRootCertificateFile (path to .cer/.crt/.pem), LockServerSettings (1 = users cannot change EWS URL).
/// </remarks>
public sealed class OrganizationDefaults
{
    public string EwsUrl { get; set; } = "";
    public string Domain { get; set; } = "";
    public string EmailDomain { get; set; } = "";
    public string AuthMethod { get; set; } = "";
    public string ServerVersion { get; set; } = "";
    public string TrustedRootCertificateFile { get; set; } = "";
    public bool LockServerSettings { get; set; }

    private const string PolicyKey = @"SOFTWARE\Policies\MailClient";

    public static OrganizationDefaults Load()
    {
        var d = new OrganizationDefaults();
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "organization.json");
            if (File.Exists(file))
                d = JsonSerializer.Deserialize<OrganizationDefaults>(File.ReadAllText(file),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? d;
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
        if (!string.IsNullOrWhiteSpace(TrustedRootCertificateFile))
        {
            try { a.TrustedRootCertificatesPem = CertificateImport.ToPem(File.ReadAllBytes(TrustedRootCertificateFile)); }
            catch (Exception ex) { Log.Warn($"Корневой сертификат из политики не загружен: {ex.Message}"); }
        }
    }
}
