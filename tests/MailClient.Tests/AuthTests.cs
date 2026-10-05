using System.Net;
using System.Net.Http.Headers;
using MailClient.Core.Models;
using MailClient.Core.Services;
using MailClient.Exchange.Autodiscover;
using MailClient.Exchange.Http;
using Xunit;

namespace MailClient.Tests;

public class AuthTests
{
    private static AccountSettings Account(string user, string domain = "") =>
        new() { EmailAddress = "ivanov@company.ru", UserName = user, Domain = domain };

    [Theory]
    [InlineData("CORP\\ivanov", "", "ivanov", "CORP")]
    [InlineData("ivanov", "CORP", "ivanov", "CORP")]
    [InlineData("ivanov@corp.local", "CORP", "ivanov@corp.local", "")]   // UPN must not carry a domain
    [InlineData("", "", "ivanov@company.ru", "")]
    public void Network_credential_handles_login_formats(string user, string domain, string expectedUser, string expectedDomain)
    {
        var c = ExchangeHttp.BuildNetworkCredential(Account(user, domain), "Пароль123");
        Assert.Equal(expectedUser, c.UserName);
        Assert.Equal(expectedDomain, c.Domain);
        Assert.Equal("Пароль123", c.Password);
    }

    [Fact]
    public void Oauth_only_server_is_explained()
    {
        var r = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        r.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer", "authorization_uri=\"https://login.microsoftonline.com/common\""));
        Assert.Contains("OAuth", ExchangeHttp.DescribeAuthFailure(r));
    }

    [Fact]
    public void Ntlm_server_rejection_lists_schemes_and_login_hint()
    {
        var r = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        r.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Negotiate"));
        r.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("NTLM"));
        var text = ExchangeHttp.DescribeAuthFailure(r);
        Assert.Contains("Negotiate, NTLM", text);
        Assert.Contains("ДОМЕН\\логин", text);
    }

    private sealed class Router : HttpMessageHandler
    {
        public List<string> Hits { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Hits.Add(request.RequestUri!.ToString());
            if (request.RequestUri!.Host == "company.ru")
            {
                var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                unauthorized.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Basic"));
                return Task.FromResult(unauthorized);
            }
            if (request.RequestUri.Host == "autodiscover.company.ru")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        <Autodiscover xmlns="http://schemas.microsoft.com/exchange/autodiscover/responseschema/2006">
                          <Response xmlns="http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a">
                            <User><DisplayName>Иванов Иван</DisplayName></User>
                            <Account><Action>settings</Action>
                              <Protocol><Type>EXCH</Type><EwsUrl>https://mail.company.ru/EWS/Exchange.asmx</EwsUrl><ServerVersion>73C18880</ServerVersion></Protocol>
                            </Account>
                          </Response>
                        </Autodiscover>
                        """),
                });
            throw new HttpRequestException("unreachable");
        }
    }

    [Fact]
    public async Task Autodiscover_continues_after_401_from_unrelated_site()
    {
        var router = new Router();
        var client = new AutodiscoverClient(() => new HttpClient(router), new HttpClient(router));
        var result = await client.DiscoverAsync("ivanov@company.ru");
        Assert.Equal("https://mail.company.ru/EWS/Exchange.asmx", result.EwsUrl);
        Assert.Equal("Иванов Иван", result.DisplayName);
        Assert.Equal(ExchangeServerVersion.Exchange2016, AutodiscoverClient.SuggestVersion(result.ServerVersionHex));
    }

    [Fact]
    public async Task Autodiscover_reports_auth_failure_when_nothing_else_works()
    {
        var only401 = new OnlyUnauthorized();
        var client = new AutodiscoverClient(() => new HttpClient(only401), new HttpClient(only401));
        var ex = await Assert.ThrowsAsync<MailAuthenticationException>(() => client.DiscoverAsync("ivanov@company.ru"));
        Assert.Contains("NTLM", ex.Message);
    }

    [Fact]
    public async Task Autodiscover_stops_after_credentials_are_rejected_once()
    {
        var handler = new RejectsLogin();
        var client = new AutodiscoverClient(() => new HttpClient(handler), new HttpClient(handler));
        await Assert.ThrowsAsync<MailAuthenticationException>(() => client.DiscoverAsync("ivanov@company.ru"));
        Assert.Equal(1, handler.Posts);
    }

    /// <summary>Simulates HttpClientHandler having sent NTLM credentials that the server rejected.</summary>
    private sealed class RejectsLogin : HttpMessageHandler
    {
        public int Posts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) throw new HttpRequestException("no http");
            Posts++;
            request.Headers.Authorization = new AuthenticationHeaderValue("NTLM", "TlRMTVNTUAADAAAA");
            var r = new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };
            r.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("NTLM"));
            return Task.FromResult(r);
        }
    }

    private sealed class OnlyUnauthorized : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) throw new HttpRequestException("no http");
            var r = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            r.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("NTLM"));
            return Task.FromResult(r);
        }
    }
}
