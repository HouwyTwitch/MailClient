using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
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
    /// <summary>Product version sent in the User-Agent header and shown in diagnostics ("1.2.3").</summary>
    public static string ProductVersion { get; } = typeof(ExchangeHttp).Assembly.GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{v.Build}"
        : "0.0.0";

    /// <summary>
    /// A handler that goes through the system proxy (Windows proxy settings, including PAC/WPAD scripts) and
    /// signs in to it as the logged-on Windows user: corporate proxies usually require NTLM/Kerberos (HTTP 407).
    /// </summary>
    public static SocketsHttpHandler NewSocketsHandler() => new()
    {
        AllowAutoRedirect = false,
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
    };

    public static HttpMessageHandler CreateHandler(AccountSettings account, ICredentialProvider credentials)
    {
        var sockets = NewSocketsHandler();
        // NTLM/Negotiate are connection based - keep connections alive, but give up idle ones before the usual
        // one-minute idle limit of firewalls and load balancers, which drop them without telling the client.
        sockets.PooledConnectionLifetime = TimeSpan.FromMinutes(10);
        sockets.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(50);
        sockets.ConnectCallback = ConnectWithKeepAliveAsync;
        sockets.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        sockets.UseCookies = true;
        sockets.CookieContainer = new CookieContainer();

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
    /// Opens the TCP connection (to the server or to the proxy) with keep-alive probes every 15 seconds, so a
    /// firewall between the computer and Exchange keeps it open while it waits in the pool.
    /// </summary>
    internal static async ValueTask<Stream> ConnectWithKeepAliveAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
            }
            catch (SocketException)
            {
                // Older systems without per-socket timings use the system keep-alive settings.
            }
            await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
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
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MailClient", ProductVersion));
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
