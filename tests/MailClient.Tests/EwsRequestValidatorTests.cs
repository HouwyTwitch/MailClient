using System.Xml.Linq;
using MailClient.Exchange.Ews;
using Xunit;
using static MailClient.Tests.FakeEws;

namespace MailClient.Tests;

/// <summary>The request checker itself must catch the mistakes Exchange rejects.</summary>
public class EwsRequestValidatorTests
{
    private static readonly EwsRequestValidator Rules = EwsRequestValidator.Load();

    private static XElement GetItem(params object[] content) => new(M + "GetItem", content);
    private static XElement Shape(string baseShape = "IdOnly") => new(M + "ItemShape", new XElement(T + "BaseShape", baseShape));
    private static XElement Ids() => new(M + "ItemIds", new XElement(T + "ItemId", new XAttribute("Id", "AAA=")));

    [Fact]
    public void Well_formed_request_passes() => Assert.Empty(Rules.Validate(GetItem(Shape(), Ids())));

    [Fact]
    public void Wrong_order_is_reported() =>
        Assert.Contains(Rules.Validate(GetItem(Ids(), Shape())), e => e.Contains("не на своём месте"));

    [Fact]
    public void Missing_required_child_is_reported() =>
        Assert.Contains(Rules.Validate(GetItem(Ids())), e => e.Contains("нет обязательного элемента m:ItemShape"));

    [Fact]
    public void Unknown_element_and_attribute_are_reported()
    {
        var errors = Rules.Validate(GetItem(Shape(), Ids(), new XElement(M + "Bogus")));
        Assert.Contains(errors, e => e.Contains("m:Bogus"));
        var badAttr = GetItem(Shape(), new XElement(M + "ItemIds", new XElement(T + "ItemId", new XAttribute("Id", "A"), new XAttribute("Bogus", "1"))));
        Assert.Contains(Rules.Validate(badAttr), e => e.Contains("атрибут Bogus"));
    }

    [Fact]
    public void Values_are_checked()
    {
        Assert.Contains(Rules.Validate(GetItem(Shape("Everything"), Ids())), e => e.Contains("\"Everything\""));
        var badBool = new XElement(M + "GetItem",
            new XElement(M + "ItemShape", new XElement(T + "BaseShape", "IdOnly"), new XElement(T + "IncludeMimeContent", "yes")), Ids());
        Assert.Contains(Rules.Validate(badBool), e => e.Contains("\"yes\""));
        var noId = GetItem(Shape(), new XElement(M + "ItemIds", new XElement(T + "ItemId")));
        Assert.Contains(Rules.Validate(noId), e => e.Contains("атрибута Id"));
    }

    [Fact]
    public async Task Fake_server_applies_the_rules_to_operations_and_headers()
    {
        var fake = new FakeEws().On("GetItem", Response("GetItem", Success("GetItem")));
        using var http = new HttpClient(fake);
        var ews = new EwsClient(http, new Uri("https://x/EWS/Exchange.asmx"), "Exchange2016");
        await ews.SendAsync(GetItem(Ids(), Shape()), TestContext.Current.CancellationToken);
        Assert.NotEmpty(fake.ValidationErrors);
    }
}
