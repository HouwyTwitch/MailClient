using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using MailClient.Core.Models;
using MailClient.Exchange.Ews;

namespace MailClient.Tests;

/// <summary>
/// Fake EWS endpoint: records requests, validates each operation against the official EWS
/// schema (messages.xsd/types.xsd) and replies with canned responses keyed by operation name.
/// </summary>
internal sealed class FakeEws : HttpMessageHandler
{
    public static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    public static readonly XNamespace T = "http://schemas.microsoft.com/exchange/services/2006/types";
    public static readonly XNamespace M = "http://schemas.microsoft.com/exchange/services/2006/messages";

    private static readonly Lazy<XmlSchemaSet> Schemas = new(() =>
    {
        var set = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        var dir = Path.Combine(AppContext.BaseDirectory, "Schemas");
        set.Add(null, Path.Combine(dir, "messages.xsd"));
        set.Compile();
        return set;
    });

    private readonly Dictionary<string, Queue<Func<XElement, HttpResponseMessage>>> _responders = new();
    public List<XElement> Requests { get; } = new();
    public List<XDocument> Envelopes { get; } = new();
    public List<HttpRequestMessage> HttpRequests { get; } = new();
    public List<string> ValidationErrors { get; } = new();

    public FakeEws On(string operation, string bodyXml, HttpStatusCode status = HttpStatusCode.OK) =>
        On(operation, _ => Xml(Envelope(bodyXml), status));

    public FakeEws On(string operation, Func<XElement, HttpResponseMessage> responder)
    {
        if (!_responders.TryGetValue(operation, out var q)) _responders[operation] = q = new();
        q.Enqueue(responder);
        return this;
    }

    public XElement Last(string operation) => Requests.Last(r => r.Name.LocalName == operation);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var text = await request.Content!.ReadAsStringAsync(ct);
        var doc = XDocument.Parse(text);
        Envelopes.Add(doc);
        HttpRequests.Add(request);
        var op = doc.Root!.Element(Soap + "Body")!.Elements().First();
        Requests.Add(op);
        Validate(op);

        if (!_responders.TryGetValue(op.Name.LocalName, out var q) || q.Count == 0)
            throw new InvalidOperationException($"No canned response for {op.Name.LocalName}");
        var responder = q.Count > 1 ? q.Dequeue() : q.Peek();
        return responder(op);
    }

    private void Validate(XElement op)
    {
        var doc = new XDocument(new XElement(op));
        doc.Validate(Schemas.Value, (_, e) => ValidationErrors.Add($"{op.Name.LocalName}: {e.Message}"));
    }

    public static string Envelope(string body) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Header><h:ServerVersionInfo MajorVersion="15" MinorVersion="2" MajorBuildNumber="1544" MinorBuildNumber="4"
              xmlns:h="http://schemas.microsoft.com/exchange/services/2006/types"/></s:Header>
          <s:Body xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages"
                  xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types">{body}</s:Body>
        </s:Envelope>
        """;

    public static HttpResponseMessage Xml(string xml, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };

    public static AccountSettings Account(ExchangeServerVersion version = ExchangeServerVersion.Exchange2016) => new()
    {
        EmailAddress = "jane@contoso.com",
        EwsUrl = "https://mail.contoso.com/EWS/Exchange.asmx",
        ServerVersion = version,
    };

    public ExchangeProvider CreateProvider(ExchangeServerVersion version = ExchangeServerVersion.Exchange2016) =>
        new(Account(version), new HttpClient(this));

    /// <summary>Wraps response messages in the standard XxxResponse/ResponseMessages structure.</summary>
    public static string Response(string operation, params string[] messages) =>
        $"<m:{operation}Response><m:ResponseMessages>{string.Concat(messages)}</m:ResponseMessages></m:{operation}Response>";

    public static string Success(string operation, string content = "") =>
        $"<m:{operation}ResponseMessage ResponseClass=\"Success\"><m:ResponseCode>NoError</m:ResponseCode>{content}</m:{operation}ResponseMessage>";

    public static string Error(string operation, string code, string text = "error") =>
        $"<m:{operation}ResponseMessage ResponseClass=\"Error\"><m:MessageText>{text}</m:MessageText><m:ResponseCode>{code}</m:ResponseCode></m:{operation}ResponseMessage>";
}
