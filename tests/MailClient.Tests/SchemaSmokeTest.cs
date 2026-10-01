using System.Xml.Linq;
using Xunit;

namespace MailClient.Tests;

public class SchemaSmokeTest
{
    [Fact]
    public async Task Validator_rejects_out_of_order_elements()
    {
        // Sanity check that schema validation is really active.
        var fake = new FakeEws();
        var bad = new XElement(FakeEws.M + "GetFolder",
            new XElement(FakeEws.M + "FolderIds"),
            new XElement(FakeEws.M + "FolderShape", new XElement(FakeEws.T + "BaseShape", "IdOnly")));
        fake.On("GetFolder", FakeEws.Response("GetFolder", FakeEws.Success("GetFolder")));
        using var http = new HttpClient(fake);
        var ews = new MailClient.Exchange.Ews.EwsClient(http, new Uri("https://x/EWS/Exchange.asmx"), "Exchange2016");
        await ews.SendAsync(bad, CancellationToken.None);
        Assert.NotEmpty(fake.ValidationErrors);
    }
}
