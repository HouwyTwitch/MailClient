using System.IO;
using System.Text.Json;
using MailClient.Core.Models;

namespace MailClient.App.Services;

/// <summary>
/// Pre-configuration for corporate roll-out on Linux, with the same parameters as the Windows group policies:
/// /etc/mailclient/policy.json (deployed by ALD Pro, Ansible, Astra Automation…) has priority over
/// organization.json next to the program.
/// </summary>
public sealed class OrganizationDefaults
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>System-wide policy file (overridable for tests).</summary>
    public static string PolicyFile { get; set; } = "/etc/mailclient/policy.json";

    public string EwsUrl { get; set; } = "";
    public string Domain { get; set; } = "";
    public string EmailDomain { get; set; } = "";
    public string AuthMethod { get; set; } = "";
    public string ServerVersion { get; set; } = "";
    public string TrustedRootCertificateFile { get; set; } = "";
    public bool LockServerSettings { get; set; }
    public string Protocol { get; set; } = "";
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; }
    public bool DisableForwardingRules { get; set; }

    private static readonly Lazy<OrganizationDefaults> CurrentLazy = new(Load);

    public static OrganizationDefaults Current => CurrentLazy.Value;

    public static OrganizationDefaults Load()
    {
        var result = new OrganizationDefaults();
        foreach (var file in new[] { Path.Combine(AppContext.BaseDirectory, "organization.json"), PolicyFile })
        {
            try
            {
                if (!File.Exists(file)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                Merge(result, doc.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Log.Warn($"{file} не прочитан: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>Values present in <paramref name="json"/> replace the current ones (later files win).</summary>
    private static void Merge(OrganizationDefaults target, JsonElement json)
    {
        var parsed = json.Deserialize<OrganizationDefaults>(JsonOptions) ?? new();
        foreach (var property in json.EnumerateObject())
        {
            var p = typeof(OrganizationDefaults).GetProperty(property.Name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (p is { CanWrite: true }) p.SetValue(target, p.GetValue(parsed));
        }
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                Log.Warn($"Корневой сертификат из политики не загружен: {ex.Message}");
            }
        }
    }
}
