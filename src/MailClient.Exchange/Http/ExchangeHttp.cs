using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MailClient.Core.Models;
using MailClient.Core.Services;

namespace MailClient.Exchange.Http;

/// <summary>
/// Builds the HTTP pipeline for talking to Exchange: NTLM / Kerberos (Negotiate) / Basic via
/// <see cref="NetworkCredential"/>, Windows single sign-on via default credentials, or OAuth 2.0 bearer tokens.
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

        var customRoots = ParseCertificates(account.TrustedRootCertificatesPem);
        var pinned = string.IsNullOrWhiteSpace(account.TrustedCertificateThumbprint)
            ? null : NormalizeThumbprint(account.TrustedCertificateThumbprint);
        if (customRoots.Count > 0 || pinned != null)
        {
            sockets.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                    ValidateServerCertificate(cert, errors, customRoots, pinned),
            };
        }

        switch (account.AuthMethod)
        {
            case AuthMethod.Password:
                sockets.Credentials = BuildNetworkCredential(account, credentials.GetPassword(account.Id) ?? "");
                return sockets;
            case AuthMethod.IntegratedWindows:
                sockets.Credentials = CredentialCache.DefaultNetworkCredentials;
                return sockets;
            case AuthMethod.OAuth2:
                return new BearerTokenHandler(account, credentials) { InnerHandler = sockets };
            default:
                throw new ArgumentOutOfRangeException(nameof(account), account.AuthMethod, "Неизвестный способ входа");
        }
    }

    public static HttpClient CreateClient(AccountSettings account, ICredentialProvider credentials)
    {
        var client = new HttpClient(CreateHandler(account, credentials), disposeHandler: true)
        {
            // Large attachments travel base64-encoded inside SOAP; allow generous time.
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MailClient", "0.1"));
        client.DefaultRequestHeaders.Add("X-AnchorMailbox", string.IsNullOrWhiteSpace(account.SharedMailbox) ? account.EmailAddress : account.SharedMailbox);
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
        return new NetworkCredential(user, password, domain);
    }

    /// <summary>
    /// Accepts a certificate that Windows trusts, one that chains to a user-supplied root CA
    /// (host name must still match), or the exact pinned certificate.
    /// </summary>
    internal static bool ValidateServerCertificate(X509Certificate? cert, SslPolicyErrors errors,
        IReadOnlyCollection<X509Certificate2> customRoots, string? pinnedThumbprint)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (cert == null) return false;
        using var leaf = new X509Certificate2(cert);

        if (pinnedThumbprint != null &&
            (NormalizeThumbprint(Convert.ToHexString(SHA256.HashData(leaf.RawData))) == pinnedThumbprint ||
             NormalizeThumbprint(leaf.Thumbprint) == pinnedThumbprint))
            return true;

        if (customRoots.Count == 0 || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
                                   || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        foreach (var root in customRoots) chain.ChainPolicy.CustomTrustStore.Add(root);
        return chain.Build(leaf);
    }

    /// <summary>Parses one or more PEM certificates (or a single Base64 DER blob).</summary>
    public static List<X509Certificate2> ParseCertificates(string? pem)
    {
        var result = new List<X509Certificate2>();
        if (string.IsNullOrWhiteSpace(pem)) return result;
        const string begin = "-----BEGIN CERTIFICATE-----", end = "-----END CERTIFICATE-----";
        int pos = 0;
        while ((pos = pem.IndexOf(begin, pos, StringComparison.Ordinal)) >= 0)
        {
            int stop = pem.IndexOf(end, pos, StringComparison.Ordinal);
            if (stop < 0) break;
            var b64 = pem[(pos + begin.Length)..stop];
            result.Add(new X509Certificate2(Convert.FromBase64String(new string(b64.Where(c => !char.IsWhiteSpace(c)).ToArray()))));
            pos = stop + end.Length;
        }
        if (result.Count == 0)
            result.Add(new X509Certificate2(Convert.FromBase64String(new string(pem.Where(c => !char.IsWhiteSpace(c)).ToArray()))));
        return result;
    }

    private static string NormalizeThumbprint(string s) =>
        new string(s.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
}

/// <summary>Adds an OAuth2 bearer token and retries once with a refreshed token on 401.</summary>
internal sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly AccountSettings _account;
    private readonly ICredentialProvider _credentials;

    public BearerTokenHandler(AccountSettings account, ICredentialProvider credentials)
    {
        _account = account;
        _credentials = credentials;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Buffer the content so the request can be replayed.
        byte[]? body = request.Content == null ? null : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var contentHeaders = request.Content?.Headers.ToList();

        var token = await _credentials.GetAccessTokenAsync(_account, false, ct).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        var retry = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var h in request.Headers) retry.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (body != null)
        {
            retry.Content = new ByteArrayContent(body);
            foreach (var h in contentHeaders!) retry.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        token = await _credentials.GetAccessTokenAsync(_account, true, ct).ConfigureAwait(false);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(retry, ct).ConfigureAwait(false);
    }
}
