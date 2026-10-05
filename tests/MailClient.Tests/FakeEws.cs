using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using MailClient.Core.Models;
using MailClient.Exchange.Ews;

namespace MailClient.Tests;

/// <summary>
/// Fake EWS endpoint: records requests, checks each operation and SOAP header against EwsRequestRules.txt
/// and replies with canned responses keyed by operation name. When EWS_SCHEMA_DIR points to a folder with the
/// server's own messages.xsd/types.xsd (served by every Exchange at /EWS/messages.xsd), requests are also
/// validated against that schema.
/// </summary>
internal sealed class FakeEws : HttpMessageHandler
{
    public static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    public static readonly XNamespace T = "http://schemas.microsoft.com/exchange/services/2006/types";
    public static readonly XNamespace M = "http://schemas.microsoft.com/exchange/services/2006/messages";

    private static readonly Lazy<EwsRequestValidator> Rules = new(EwsRequestValidator.Load);

    private static readonly Lazy<XmlSchemaSet?> ServerSchema = new(() =>
    {
        var dir = Environment.GetEnvironmentVariable("EWS_SCHEMA_DIR");
        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "messages.xsd"))) return null;
        var set = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
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

    public IEnumerable<XElement> All(string operation) => Requests.Where(r => r.Name.LocalName == operation);

    /// <summary>
    /// Answers GetItem like Exchange: one response message per requested id, from <paramref name="items"/>
    /// (full item XML keyed by id) or ErrorItemNotFound.
    /// </summary>
    public FakeEws ServeItems(IDictionary<string, string> items) => On("GetItem", req =>
    {
        var ids = req.Descendants(T + "ItemId").Select(e => e.Attribute("Id")!.Value);
        var messages = ids.Select(id => items.TryGetValue(id, out var xml)
            ? Success("GetItem", $"<m:Items>{xml}</m:Items>")
            : Error("GetItem", "ErrorItemNotFound", "The specified object was not found in the store."));
        return Xml(Envelope(Response("GetItem", messages.ToArray())));
    });

    /// <summary>Answers GetFolder (AllProperties) for the requested FolderIds from <paramref name="folders"/>.</summary>
    public FakeEws ServeFolders(IDictionary<string, string> folders) => On("GetFolder", req =>
    {
        var ids = req.Descendants(T + "FolderId").Select(e => e.Attribute("Id")!.Value);
        var messages = ids.Select(id => folders.TryGetValue(id, out var xml)
            ? Success("GetFolder", $"<m:Folders>{xml}</m:Folders>")
            : Error("GetFolder", "ErrorFolderNotFound"));
        return Xml(Envelope(Response("GetFolder", messages.ToArray())));
    });

    /// <summary>Item with only an id, as returned by IdOnly FindItem/SyncFolderItems.</summary>
    public static string IdOnly(string id) => $"<t:Message><t:ItemId Id=\"{id}\"/></t:Message>";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var text = await request.Content!.ReadAsStringAsync(ct);
        var doc = XDocument.Parse(text);
        Envelopes.Add(doc);
        HttpRequests.Add(request);
        var op = doc.Root!.Element(Soap + "Body")!.Elements().First();
        Requests.Add(op);
        Validate(op);
        foreach (var header in doc.Root.Element(Soap + "Header")?.Elements() ?? []) Validate(header);

        if (!_responders.TryGetValue(op.Name.LocalName, out var q) || q.Count == 0)
            throw new InvalidOperationException($"No canned response for {op.Name.LocalName}");
        var responder = q.Count > 1 ? q.Dequeue() : q.Peek();
        return responder(op);
    }

    private void Validate(XElement element)
    {
        ValidationErrors.AddRange(Rules.Value.Validate(element).Select(e => $"{element.Name.LocalName}: {e}"));
        if (ServerSchema.Value is { } schema)
            new XDocument(new XElement(element)).Validate(schema, (_, e) => ValidationErrors.Add($"{element.Name.LocalName} (XSD): {e.Message}"));
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
