using System.Net;
using System.Text;
using System.Xml.Linq;
using MailClient.Core.Models;
using MailClient.Core.Services;
using MailClient.Exchange.Http;

namespace MailClient.Exchange.Autodiscover;

public sealed class AutodiscoverResult
{
    public required string EwsUrl { get; init; }
    public string ExternalEwsUrl { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ServerVersionHex { get; init; } = "";
    /// <summary>The URL that finally answered.</summary>
    public string AutodiscoverUrl { get; init; } = "";
}

/// <summary>
/// Exchange "POX" Autodiscover (autodiscover.xml), as used by Outlook and Evolution, to find the EWS URL
/// from an e-mail address. Tries the standard endpoints and follows redirectAddr/redirectUrl and the
/// HTTP-redirect method (only to HTTPS targets).
/// </summary>
public sealed class AutodiscoverClient
{
    private const string RequestSchema = "http://schemas.microsoft.com/exchange/autodiscover/outlook/requestschema/2006";
    private const string ResponseSchema = "http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a";
    private const int MaxRedirects = 10;

    private readonly Func<HttpClient> _clientFactory;
    private readonly HttpClient _anonymous;

    public AutodiscoverClient(AccountSettings account, ICredentialProvider credentials)
        : this(() => ExchangeHttp.CreateClient(account, credentials),
               new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) }) { }

    internal AutodiscoverClient(Func<HttpClient> authenticatedClientFactory, HttpClient anonymousClient)
    {
        _clientFactory = authenticatedClientFactory;
        _anonymous = anonymousClient;
    }

    /// <summary>Log of attempted URLs and outcomes, useful for troubleshooting in the UI.</summary>
    public List<string> Log { get; } = new();

    /// <summary>Explanation of the first 401 seen, reported if no endpoint succeeds.</summary>
    public string? LastAuthFailure { get; private set; }

    public async Task<AutodiscoverResult> DiscoverAsync(string emailAddress, CancellationToken ct = default)
    {
        using var client = _clientFactory();
        client.Timeout = TimeSpan.FromSeconds(30);
        var email = emailAddress.Trim();

        for (int redirect = 0; redirect < MaxRedirects; redirect++)
        {
            var domain = email[(email.IndexOf('@') + 1)..];
            var candidates = new List<string>
            {
                $"https://{domain}/autodiscover/autodiscover.xml",
                $"https://autodiscover.{domain}/autodiscover/autodiscover.xml",
            };

            // HTTP redirect method: an unauthenticated GET to http://autodiscover.domain may 302 to an HTTPS endpoint.
            var redirected = await TryHttpRedirectAsync(domain, ct).ConfigureAwait(false);
            if (redirected != null) candidates.Add(redirected);

            string? nextEmail = null;
            foreach (var url in candidates)
            {
                var outcome = await TryEndpointAsync(client, url, email, 0, ct).ConfigureAwait(false);
                if (outcome.Result != null) return outcome.Result;
                // Like Thunderbird: one login attempt. Once a server has rejected our credentials, they are not sent
                // to further addresses — repeated failed logins lock the domain account.
                if (outcome.LoginRejected) throw new MailAuthenticationException(LastAuthFailure!);
                if (outcome.RedirectAddress != null)
                {
                    nextEmail = outcome.RedirectAddress;
                    break;
                }
            }
            if (nextEmail == null) break;

            Log.Add($"Redirected to address {nextEmail}");
            email = nextEmail;
        }
        if (LastAuthFailure != null) throw new MailAuthenticationException(LastAuthFailure);
        throw new MailServiceException(
            "Не удалось автоматически найти сервер Exchange для этого адреса. Укажите адрес EWS вручную " +
            "(обычно https://mail.<ваш-домен>/EWS/Exchange.asmx) или уточните его у администратора.", "AutodiscoverFailed");
    }

    private sealed record Outcome(AutodiscoverResult? Result, string? RedirectAddress, bool LoginRejected = false);

    private async Task<Outcome> TryEndpointAsync(HttpClient client, string url, string email, int depth, CancellationToken ct)
    {
        if (depth > MaxRedirects) return new Outcome(null, null);
        try
        {
            var body = new XDocument(
                new XElement(XName.Get("Autodiscover", RequestSchema),
                    new XElement(XName.Get("Request", RequestSchema),
                        new XElement(XName.Get("EMailAddress", RequestSchema), email),
                        new XElement(XName.Get("AcceptableResponseSchema", RequestSchema), ResponseSchema))));
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml"),
            };
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if ((int)response.StatusCode is 301 or 302 or 307 or 308 && response.Headers.Location is { } loc)
            {
                var target = loc.IsAbsoluteUri ? loc : new Uri(new Uri(url), loc);
                Log.Add($"{url} → HTTP redirect to {target}");
                if (target.Scheme != Uri.UriSchemeHttps) return new Outcome(null, null);
                return await TryEndpointAsync(client, target.ToString(), email, depth + 1, ct).ConfigureAwait(false);
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Credentials sent and rejected: stop. A 401 without our credentials having been tried (e.g. an
                // unrelated web site at https://domain/ asking for another scheme) lets the next endpoint be tried.
                bool credentialsSent = request.Headers.Authorization != null;
                Log.Add($"{url} → 401 Unauthorized ({string.Join(", ", response.Headers.WwwAuthenticate.Select(h => h.Scheme))})" +
                        (credentialsSent ? ", login rejected" : ""));
                LastAuthFailure ??= Http.ExchangeHttp.DescribeAuthFailure(response);
                return new Outcome(null, null, credentialsSent);
            }
            if (!response.IsSuccessStatusCode)
            {
                Log.Add($"{url} → HTTP {(int)response.StatusCode}");
                return new Outcome(null, null);
            }

            var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = Parse(xml);
            if (parsed.Result != null)
            {
                Log.Add($"{url} → EWS {parsed.Result.EwsUrl}");
                return new Outcome(new AutodiscoverResult
                {
                    EwsUrl = parsed.Result.EwsUrl,
                    ExternalEwsUrl = parsed.Result.ExternalEwsUrl,
                    DisplayName = parsed.Result.DisplayName,
                    ServerVersionHex = parsed.Result.ServerVersionHex,
                    AutodiscoverUrl = url,
                }, null);
            }
            if (parsed.RedirectUrl != null)
            {
                Log.Add($"{url} → redirectUrl {parsed.RedirectUrl}");
                if (!parsed.RedirectUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return new Outcome(null, null);
                return await TryEndpointAsync(client, parsed.RedirectUrl, email, depth + 1, ct).ConfigureAwait(false);
            }
            if (parsed.RedirectAddress != null) return new Outcome(null, parsed.RedirectAddress);
            Log.Add($"{url} → {parsed.Error ?? "no EWS settings in response"}");
            return new Outcome(null, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            if (ct.IsCancellationRequested) throw;
            Log.Add($"{url} → {ex.Message}");
            return new Outcome(null, null);
        }
    }

    private async Task<string?> TryHttpRedirectAsync(string domain, CancellationToken ct)
    {
        var url = $"http://autodiscover.{domain}/autodiscover/autodiscover.xml";
        try
        {
            using var response = await _anonymous.GetAsync(url, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 307 or 308 && response.Headers.Location is { } loc
                && loc.IsAbsoluteUri && loc.Scheme == Uri.UriSchemeHttps)
            {
                Log.Add($"{url} → redirect {loc}");
                return loc.ToString();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
        }
        return null;
    }

    internal sealed record ParsedResponse(AutodiscoverResult? Result, string? RedirectAddress, string? RedirectUrl, string? Error);

    /// <summary>Parses a POX autodiscover response (namespace-agnostic).</summary>
    internal static ParsedResponse Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        IEnumerable<XElement> Named(XContainer c, string local) => c.Descendants().Where(e => e.Name.LocalName == local);
        string? Child(XElement? e, string local) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == local)?.Value;

        var error = Named(doc, "Error").FirstOrDefault();
        if (error != null) return new ParsedResponse(null, null, null, Child(error, "Message") ?? "Autodiscover error");

        var account = Named(doc, "Account").FirstOrDefault();
        var action = Child(account, "Action");
        if (string.Equals(action, "redirectAddr", StringComparison.OrdinalIgnoreCase))
            return new ParsedResponse(null, Child(account, "RedirectAddr"), null, null);
        if (string.Equals(action, "redirectUrl", StringComparison.OrdinalIgnoreCase))
            return new ParsedResponse(null, null, Child(account, "RedirectUrl"), null);

        var protocols = Named(doc, "Protocol").ToList();
        string? Pick(string type, string element) =>
            protocols.Where(p => string.Equals(Child(p, "Type"), type, StringComparison.OrdinalIgnoreCase))
                     .Select(p => Child(p, element)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        // EXCH = internal (inside the corporate network), EXPR = external (Outlook Anywhere), WEB = OWA.
        var ews = Pick("EXCH", "EwsUrl") ?? Pick("EXPR", "EwsUrl") ?? Pick("WEB", "EwsUrl")
                  ?? protocols.Select(p => Child(p, "EwsUrl")).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var external = Pick("EXPR", "EwsUrl") ?? Pick("EXCH", "ExternalEwsUrl") ?? "";
        if (string.IsNullOrWhiteSpace(ews)) return new ParsedResponse(null, null, null, "No EWS URL in response");

        return new ParsedResponse(new AutodiscoverResult
        {
            EwsUrl = ews.Trim(),
            ExternalEwsUrl = external.Trim(),
            DisplayName = Child(Named(doc, "User").FirstOrDefault(), "DisplayName") ?? "",
            ServerVersionHex = Pick("EXCH", "ServerVersion") ?? "",
        }, null, null, null);
    }

    /// <summary>Maps the hex "ServerVersion" from autodiscover (e.g. 73C18880) to the best EWS schema version.</summary>
    public static ExchangeServerVersion SuggestVersion(string serverVersionHex)
    {
        if (!int.TryParse(serverVersionHex, System.Globalization.NumberStyles.HexNumber, null, out var v))
            return ExchangeServerVersion.Exchange2013_SP1;
        int major = (v >> 22) & 0x3F;
        int minor = (v >> 16) & 0x3F;
        if (major < 15) return ExchangeServerVersion.Exchange2010_SP2;
        if (major == 15 && minor == 0) return ExchangeServerVersion.Exchange2013_SP1;
        return ExchangeServerVersion.Exchange2016;
    }
}
