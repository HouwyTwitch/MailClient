using System.Net;
using System.Net.Sockets;
using System.Text;
using MailClient.Core.Models;
using MailClient.Core.Services;
using MailClient.Exchange.Http;
using Xunit;

namespace MailClient.Tests;

/// <summary>
/// Checks which authentication scheme the HTTP client answers when a server offers both
/// "Negotiate" and "NTLM" (as Exchange/IIS does), using a raw socket server.
/// </summary>
public class NtlmSchemeTests
{
    static NtlmSchemeTests()
    {
        // Linux has no SSPI; use .NET's managed NTLM so the client can produce NTLM messages in tests.
        AppContext.SetSwitch("System.Net.Security.UseManagedNtlm", true);
    }

    private sealed class Creds : ICredentialProvider
    {
        public string? GetPassword(Guid accountId) => "Пароль-123";
    }

    /// <summary>Serves 401 with both challenges and records the Authorization headers it receives.</summary>
    private static async Task<List<string>> CaptureAuthorizationAsync(HttpAuthScheme scheme)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var seen = new List<string>();

        var server = Task.Run(async () =>
        {
            for (int i = 0; i < 3; i++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[16384];
                int read = await stream.ReadAsync(buffer);
                var request = Encoding.ASCII.GetString(buffer, 0, read);
                var auth = request.Split("\r\n").FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));
                if (auth != null) seen.Add(auth["Authorization:".Length..].Trim().Split(' ')[0]);
                var response = "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Negotiate\r\nWWW-Authenticate: NTLM\r\nWWW-Authenticate: Basic realm=\"mail\"\r\n" +
                               "Content-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
        });

        var account = new AccountSettings
        {
            EmailAddress = "ivanov@company.ru", UserName = "CORP\\ivanov",
            EwsUrl = $"http://127.0.0.1:{port}/EWS/Exchange.asmx", AuthScheme = scheme,
        };
        using var http = ExchangeHttp.CreateClient(account, new Creds());
        http.Timeout = TimeSpan.FromSeconds(10);
        try
        {
            await http.PostAsync(account.EwsUrl, new StringContent("<x/>"));
        }
        catch (Exception)
        {
            // The fake server cannot complete an NTLM handshake; only the first answer matters here.
        }
        listener.Stop();
        await Task.WhenAny(server, Task.Delay(2000));
        return seen;
    }

    [Fact]
    public async Task Forced_ntlm_never_answers_the_negotiate_challenge()
    {
        var seen = await CaptureAuthorizationAsync(HttpAuthScheme.Ntlm);
        Assert.Contains("NTLM", seen);
        Assert.DoesNotContain("Negotiate", seen);
    }

    [Fact]
    public async Task Forced_basic_sends_basic()
    {
        var seen = await CaptureAuthorizationAsync(HttpAuthScheme.Basic);
        Assert.Equal("Basic", Assert.Single(seen));
    }

    [Fact]
    public void Scheme_filter_offers_credentials_only_for_the_chosen_scheme()
    {
        var c = (ICredentials)ExchangeHttp.RestrictScheme(new NetworkCredential("u", "p", "D"), HttpAuthScheme.Ntlm);
        var uri = new Uri("https://mail.company.ru/EWS/Exchange.asmx");
        Assert.Null(c.GetCredential(uri, "Negotiate"));
        Assert.Null(c.GetCredential(uri, "Basic"));
        Assert.Equal("u", c.GetCredential(uri, "NTLM")!.UserName);
        Assert.Same(CredentialCache.DefaultNetworkCredentials,
            ExchangeHttp.RestrictScheme(CredentialCache.DefaultNetworkCredentials, HttpAuthScheme.Ntlm).GetCredential(uri, "NTLM"));
    }
}
