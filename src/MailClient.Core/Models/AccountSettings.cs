namespace MailClient.Core.Models;

public enum AuthMethod
{
    /// <summary>User name + password. The server picks NTLM, Negotiate (Kerberos) or Basic.</summary>
    Password,
    /// <summary>Single sign-on with the logged-in Windows account (Kerberos/NTLM).</summary>
    IntegratedWindows,
    /// <summary>OAuth 2.0 (Modern Authentication) via Microsoft Entra ID.</summary>
    OAuth2,
}

/// <summary>EWS schema version requested from the server. Determines which features are available.</summary>
public enum ExchangeServerVersion
{
    Exchange2010_SP2,
    Exchange2013,
    Exchange2013_SP1,
    Exchange2016,
}

public sealed class AccountSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = "";
    public string EmailAddress { get; set; } = "";

    /// <summary>EWS endpoint, e.g. https://mail.contoso.com/EWS/Exchange.asmx.</summary>
    public string EwsUrl { get; set; } = "";

    public AuthMethod AuthMethod { get; set; } = AuthMethod.Password;

    /// <summary>Login name: user@domain (UPN), DOMAIN\user or just user (with <see cref="Domain"/>).</summary>
    public string UserName { get; set; } = "";
    public string Domain { get; set; } = "";

    public ExchangeServerVersion ServerVersion { get; set; } = ExchangeServerVersion.Exchange2013_SP1;

    /// <summary>OAuth2 application (client) id registered in Entra ID.</summary>
    public string OAuthClientId { get; set; } = "";
    /// <summary>OAuth2 tenant (directory id or domain). "organizations" when empty.</summary>
    public string OAuthTenant { get; set; } = "";
    /// <summary>OAuth2 scope. Defaults to https://{ews-host}/EWS.AccessAsUser.All when empty.</summary>
    public string OAuthScope { get; set; } = "";

    /// <summary>
    /// Optional SHA-256 thumbprint of a self-signed/internal server certificate to trust.
    /// Only that exact certificate is accepted in addition to normally trusted ones.
    /// </summary>
    public string TrustedCertificateThumbprint { get; set; } = "";

    /// <summary>
    /// Optional PEM/Base64 root CA certificate(s) to trust for this server, e.g. the "Russian Trusted Root CA"
    /// (Минцифры) or a corporate CA that is not installed in the Windows certificate store.
    /// </summary>
    public string TrustedRootCertificatesPem { get; set; } = "";

    /// <summary>Optional mailbox to open instead of the user's own (requires delegate/full access).</summary>
    public string SharedMailbox { get; set; } = "";

    public int SyncIntervalSeconds { get; set; } = 60;

    /// <summary>Plain text signature appended to new messages.</summary>
    public string Signature { get; set; } = "";

    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? EmailAddress : DisplayName;

    public string EffectiveOAuthScope
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OAuthScope)) return OAuthScope;
            if (Uri.TryCreate(EwsUrl, UriKind.Absolute, out var u))
                return $"https://{u.Host}/EWS.AccessAsUser.All";
            return "https://outlook.office365.com/EWS.AccessAsUser.All";
        }
    }

    public AccountSettings Clone() => (AccountSettings)MemberwiseClone();
}
