using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MailClient.Core.Models;
using MailClient.Core.Security;
using MailClient.Core.Services;

namespace MailClient.Exchange.Http;

/// <summary>
/// Builds the HTTP pipeline for talking to Exchange: NTLM / Kerberos (Negotiate) / Basic via
/// <see cref="NetworkCredential"/>, or Windows single sign-on via default credentials.
/// </summary>
public static class ExchangeHttp
{
    public static HttpMessageHandler CreateHandler(AccountSettings account, ICredentialProvider credentials)
    {
        var sockets = new SocketsHttpHandler
        {
            // NTLM/Negotiate are connection based - keep connections alive.
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };

        if (CertificateTrust.CreateCallback(account) is { } validate)
            sockets.SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = validate };

        switch (account.AuthMethod)
        {
            case AuthMethod.Password:
                sockets.Credentials = BuildCredentials(account, credentials.GetPassword(account.Id) ?? "");
                return sockets;
            case AuthMethod.IntegratedWindows:
                sockets.Credentials = RestrictScheme(CredentialCache.DefaultNetworkCredentials, account.AuthScheme);
                return sockets;
            default:
                throw new ArgumentOutOfRangeException(nameof(account), account.AuthMethod, "Неизвестный способ входа");
        }
    }

    /// <summary>
    /// Limits which challenge scheme the credentials answer. .NET tries Negotiate (Kerberos) first when the
    /// server offers it; if Kerberos is misconfigured for the host (load balancer, DNS alias, missing SPN) that
    /// fails with 401 while plain NTLM succeeds.
    /// </summary>
    public static ICredentials RestrictScheme(NetworkCredential credential, HttpAuthScheme scheme) => scheme switch
    {
        HttpAuthScheme.Ntlm => new SchemeCredentials(credential, "NTLM"),
        HttpAuthScheme.Negotiate => new SchemeCredentials(credential, "Negotiate"),
        HttpAuthScheme.Basic => new SchemeCredentials(credential, "Basic"),
        _ => credential,
    };

    /// <summary>
    /// Credentials for the account: NTLM answers only the plain "NTLM" challenge, never Negotiate/Kerberos
    /// (which fails wherever Kerberos is misconfigured for the host); an empty password means
    /// "sign in as the logged-on Windows user" (SSPI default credentials); "DOMAIN\user" is split into
    /// domain and user, "user@domain" is sent as is.
    /// </summary>
    public static ICredentials BuildCredentials(AccountSettings account, string password)
    {
        var credential = string.IsNullOrEmpty(password) && account.AuthScheme != HttpAuthScheme.Basic
            ? CredentialCache.DefaultNetworkCredentials
            : BuildNetworkCredential(account, password);
        return RestrictScheme(credential, account.AuthScheme);
    }

    public static HttpClient CreateClient(AccountSettings account, ICredentialProvider credentials)
    {
        var client = new HttpClient(CreateHandler(account, credentials), disposeHandler: true)
        {
            // Large attachments travel base64-encoded inside SOAP; allow generous time.
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MailClient", "0.1"));
        // No routing hints (X-AnchorMailbox): on-premises Exchange does not need them and some proxies reject them.
        return client;
    }

    public static NetworkCredential BuildNetworkCredential(AccountSettings account, string password)
    {
        var user = string.IsNullOrWhiteSpace(account.UserName) ? account.EmailAddress : account.UserName.Trim();
        var domain = account.Domain?.Trim() ?? "";
        // Accept DOMAIN\user as well.
        int slash = user.IndexOf('\\');
        if (slash > 0)
        {
            domain = user[..slash];
            user = user[(slash + 1)..];
        }
        // A UPN (user@domain) already identifies the domain; sending a separate domain breaks NTLM.
        else if (user.Contains('@'))
        {
            domain = "";
        }
        return new NetworkCredential(user, password, domain);
    }

    /// <summary>
    /// Explains a 401 from the authentication schemes the server offered (WWW-Authenticate),
    /// e.g. a Microsoft 365 tenant that only accepts OAuth.
    /// </summary>
    public static string DescribeAuthFailure(HttpResponseMessage response)
    {
        var schemes = response.Headers.WwwAuthenticate.Select(h => h.Scheme).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (schemes.Count > 0 && schemes.All(s => s.Equals("Bearer", StringComparison.OrdinalIgnoreCase)))
            return "Сервер принимает только вход через OAuth (Microsoft 365 / Exchange Online), пароль не подходит. " +
                   "Подключитесь по IMAP с паролем приложения или обратитесь к администратору.";
        var offered = schemes.Count == 0 ? "" : $" Сервер поддерживает: {string.Join(", ", schemes)}.";
        return "Сервер отклонил имя пользователя или пароль (HTTP 401)." + offered +
               " Имя пользователя часто отличается от адреса почты: попробуйте формат ДОМЕН\\логин (например, CORP\\ivanov) " +
               "или логин@домен.local — уточните у администратора.";
    }

    internal static bool ValidateServerCertificate(X509Certificate? cert, SslPolicyErrors errors,
        IReadOnlyCollection<X509Certificate2> customRoots, string? pinnedThumbprint) =>
        CertificateTrust.Validate(cert, errors, customRoots, pinnedThumbprint);

    public static List<X509Certificate2> ParseCertificates(string? pem) => CertificateTrust.ParseCertificates(pem);
}

/// <summary>Credentials that are only offered for one authentication scheme.</summary>
internal sealed class SchemeCredentials : ICredentials
{
    private readonly NetworkCredential _credential;
    private readonly string _scheme;

    public SchemeCredentials(NetworkCredential credential, string scheme)
    {
        _credential = credential;
        _scheme = scheme;
    }

    public NetworkCredential? GetCredential(Uri uri, string authType) =>
        string.Equals(authType, _scheme, StringComparison.OrdinalIgnoreCase) ? _credential : null;
}
