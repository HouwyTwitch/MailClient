namespace MailClient.Core.Models;

public enum AuthMethod
{
    /// <summary>User name + password. The server picks NTLM, Negotiate (Kerberos) or Basic.</summary>
    Password,
    /// <summary>Single sign-on with the logged-in Windows account (Kerberos/NTLM).</summary>
    IntegratedWindows,
}

/// <summary>HTTP authentication scheme for Exchange (EWS).</summary>
public enum HttpAuthScheme
{
    /// <summary>Whatever the server offers, in .NET order: Negotiate (Kerberos), NTLM, Basic.</summary>
    Auto,
    /// <summary>NTLM only: works with any on-premises Exchange, also where Kerberos is misconfigured.</summary>
    Ntlm,
    /// <summary>Negotiate (Kerberos with NTLM fallback).</summary>
    Negotiate,
    /// <summary>Basic (password sent base64-encoded inside TLS).</summary>
    Basic,
}

/// <summary>EWS schema version requested from the server. Determines which features are available.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707", Justification = "Names are the EWS RequestServerVersion values and are stored in settings files")]
public enum ExchangeServerVersion
{
    Exchange2010_SP2,
    Exchange2013,
    Exchange2013_SP1,
    Exchange2016,
}

public enum MailProtocol
{
    /// <summary>Microsoft Exchange via EWS: mail, contacts, address book, out-of-office.</summary>
    Exchange,
    /// <summary>IMAP for reading + SMTP for sending (Yandex 360, Mail.ru, Exchange with IMAP, Dovecot, ...).</summary>
    Imap,
}

public enum ConnectionSecurity
{
    /// <summary>TLS from the first byte (IMAPS 993, SMTPS 465).</summary>
    SslOnConnect,
    /// <summary>Plain connection upgraded with STARTTLS (IMAP 143, SMTP 587).</summary>
    StartTls,
    /// <summary>No encryption (only for testing inside a trusted network).</summary>
    None,
}

public sealed class AccountSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = "";
    public string EmailAddress { get; set; } = "";

    /// <summary>EWS endpoint, e.g. https://mail.contoso.com/EWS/Exchange.asmx.</summary>
    public string EwsUrl { get; set; } = "";

    public AuthMethod AuthMethod { get; set; } = AuthMethod.Password;

    /// <summary>Which HTTP authentication scheme to use with Exchange. NTLM by default (see <see cref="HttpAuthScheme.Ntlm"/>).</summary>
    public HttpAuthScheme AuthScheme { get; set; } = HttpAuthScheme.Ntlm;

    /// <summary>Login name: user@domain (UPN), DOMAIN\user or just user (with <see cref="Domain"/>).</summary>
    public string UserName { get; set; } = "";
    public string Domain { get; set; } = "";

    public ExchangeServerVersion ServerVersion { get; set; } = ExchangeServerVersion.Exchange2013_SP1;

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

    public MailProtocol Protocol { get; set; } = MailProtocol.Exchange;

    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public ConnectionSecurity ImapSecurity { get; set; } = ConnectionSecurity.SslOnConnect;
    /// <summary>ManageSieve port for server-side rules (forwarding) on IMAP servers.</summary>
    public int SievePort { get; set; } = 4190;
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 465;
    public ConnectionSecurity SmtpSecurity { get; set; } = ConnectionSecurity.SslOnConnect;
    /// <summary>Append sent messages to the Sent folder (disable for servers that do it themselves, e.g. Gmail).</summary>
    public bool SaveSentCopy { get; set; } = true;

    public int SyncIntervalSeconds { get; set; } = 60;

    /// <summary>Plain text signature appended to new messages.</summary>
    public string Signature { get; set; } = "";

    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? EmailAddress : DisplayName;

    public AccountSettings Clone() => (AccountSettings)MemberwiseClone();
}
