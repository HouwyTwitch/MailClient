using System.Net;
using MailClient.Core.Models;
using MailClient.Core.Services;
using MailClient.Exchange.Ews;
using MailClient.Exchange.Http;
using Xunit;
using static MailClient.Tests.FakeEws;

namespace MailClient.Tests;

/// <summary>
/// Exchange sign-in: NTLM only, an empty password means the logged-on Windows user, one connectivity request.
/// </summary>
public class ExchangeSignInTests
{
    private static readonly Uri Ews = new("https://mail.company.ru/EWS/Exchange.asmx");

    [Fact]
    public void Ntlm_is_the_default_scheme()
    {
        Assert.Equal(HttpAuthScheme.Ntlm, new AccountSettings().AuthScheme);
    }

    [Fact]
    public void Empty_password_signs_in_as_the_logged_on_windows_user()
    {
        var account = new AccountSettings { UserName = "CORP\\ivanov", AuthScheme = HttpAuthScheme.Ntlm };
        var creds = ExchangeHttp.BuildCredentials(account, "");
        Assert.Same(CredentialCache.DefaultNetworkCredentials, creds.GetCredential(Ews, "NTLM"));
        Assert.Null(creds.GetCredential(Ews, "Negotiate"));
    }

    [Fact]
    public void Password_with_domain_login_is_split_into_domain_and_user()
    {
        var account = new AccountSettings { UserName = "CORP\\ivanov", AuthScheme = HttpAuthScheme.Ntlm };
        var c = ExchangeHttp.BuildCredentials(account, "Пароль").GetCredential(Ews, "NTLM")!;
        Assert.Equal(("CORP", "ivanov", "Пароль"), (c.Domain, c.UserName, c.Password));

        var upn = new AccountSettings { UserName = "ivanov@corp.local", AuthScheme = HttpAuthScheme.Ntlm };
        var u = ExchangeHttp.BuildCredentials(upn, "x").GetCredential(Ews, "NTLM")!;
        Assert.Equal(("", "ivanov@corp.local"), (u.Domain, u.UserName));
    }

    [Fact]
    public void Basic_never_uses_windows_default_credentials()
    {
        var account = new AccountSettings { UserName = "CORP\\ivanov", AuthScheme = HttpAuthScheme.Basic };
        var c = ExchangeHttp.BuildCredentials(account, "").GetCredential(Ews, "Basic");
        Assert.NotSame(CredentialCache.DefaultNetworkCredentials, c);
    }

    private sealed class NoCreds : ICredentialProvider
    {
        public string? GetPassword(Guid accountId) => null;
    }

    [Fact]
    public void No_anchor_mailbox_header_for_on_premises_sign_in()
    {
        using var http = ExchangeHttp.CreateClient(new AccountSettings { EmailAddress = "a@b.ru" }, new NoCreds());
        Assert.False(http.DefaultRequestHeaders.Contains("X-AnchorMailbox"));
    }

    [Fact]
    public async Task Connectivity_check_is_one_root_getfolder_with_conservative_version()
    {
        var fake = new FakeEws().On("GetFolder", Response("GetFolder", Success("GetFolder", "<m:Folders><t:Folder><t:FolderId Id=\"ROOT\"/></t:Folder></m:Folders>")));
        using var p = fake.CreateProvider();

        var info = await p.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Empty(fake.ValidationErrors);
        var req = fake.Last("GetFolder");
        Assert.Equal("msgfolderroot", req.Descendants(T + "DistinguishedFolderId").Single().Attribute("Id")!.Value);
        Assert.Equal("IdOnly", req.Descendants(T + "BaseShape").Single().Value);
        var version = fake.Envelopes.Last().Descendants(T + "RequestServerVersion").Single().Attribute("Version")!.Value;
        Assert.Equal("Exchange2007_SP1", version);
        Assert.Equal("text/xml; charset=utf-8", fake.HttpRequests.Last().Content!.Headers.ContentType!.ToString());
        Assert.Equal(ExchangeServerVersion.Exchange2016, ExchangeProvider.SuggestVersion(info.ServerVersion));
    }

    [Theory]
    [InlineData("14.3.123.4", ExchangeServerVersion.Exchange2010_SP2)]
    [InlineData("15.0.1497.2", ExchangeServerVersion.Exchange2013_SP1)]
    [InlineData("15.1.2507.6", ExchangeServerVersion.Exchange2016)]
    [InlineData("15.2.1544.4", ExchangeServerVersion.Exchange2016)]
    public void Server_version_is_learned_from_the_response(string build, ExchangeServerVersion expected) =>
        Assert.Equal(expected, ExchangeProvider.SuggestVersion(build));
}
