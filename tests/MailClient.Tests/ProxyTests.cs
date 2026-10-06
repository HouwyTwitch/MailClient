using System.Net;
using MailClient.Core.Services;
using MailClient.Exchange.Autodiscover;
using MailClient.Exchange.Http;
using Xunit;

namespace MailClient.Tests;

/// <summary>Corporate proxies: Windows sign-in to the proxy and a clear message when it still refuses (HTTP 407).</summary>
public class ProxyTests
{
    private sealed class ProxyRefuses : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired)
            {
                Content = new StringContent("<html><body>Access denied by proxy</body></html>"),
            };
            response.Headers.ProxyAuthenticate.ParseAdd("NTLM");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task Ews_request_refused_by_proxy_is_explained()
    {
        var fake = new FakeEws().On("GetFolder", _ =>
            new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired) { Content = new StringContent("<html>denied</html>") });
        using var p = fake.CreateProvider();

        var ex = await Assert.ThrowsAsync<MailConnectionException>(() => p.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Contains("HTTP 407", ex.Message, StringComparison.Ordinal);
        Assert.Contains("исключения прокси", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Autodiscover_refused_by_proxy_is_a_connection_problem_not_a_wrong_password()
    {
        var proxy = new ProxyRefuses();
        var client = new AutodiscoverClient(() => new HttpClient(proxy), new HttpClient(proxy));

        var ex = await Assert.ThrowsAsync<MailConnectionException>(() => client.DiscoverAsync("ivanov@company.ru", TestContext.Current.CancellationToken));

        Assert.Contains("HTTP 407", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Handlers_sign_in_to_the_system_proxy_as_the_windows_user()
    {
        using var handler = ExchangeHttp.NewSocketsHandler();

        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy); // the system proxy (Windows settings, PAC/WPAD)
        Assert.Same(CredentialCache.DefaultCredentials, handler.DefaultProxyCredentials);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Matches(@"^\d+\.\d+\.\d+$", ExchangeHttp.ProductVersion);
    }
}
