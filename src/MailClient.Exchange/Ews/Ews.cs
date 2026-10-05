using System.Globalization;
using System.Xml.Linq;

namespace MailClient.Exchange.Ews;

/// <summary>EWS XML namespaces and small element-building helpers.</summary>
internal static class Ews
{
    public static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    public static readonly XNamespace T = "http://schemas.microsoft.com/exchange/services/2006/types";
    public static readonly XNamespace M = "http://schemas.microsoft.com/exchange/services/2006/messages";

    public static XElement FieldUri(string uri) => new(T + "FieldURI", new XAttribute("FieldURI", uri));

    public static XElement ExtendedFieldUri(string tag, string type) =>
        new(T + "ExtendedFieldURI", new XAttribute("PropertyTag", tag), new XAttribute("PropertyType", type));

    public static XElement IndexedFieldUri(string uri, string index) =>
        new(T + "IndexedFieldURI", new XAttribute("FieldURI", uri), new XAttribute("FieldIndex", index));

    public static XElement ItemId(string id, string? changeKey = null)
    {
        var e = new XElement(T + "ItemId", new XAttribute("Id", id));
        if (!string.IsNullOrEmpty(changeKey)) e.Add(new XAttribute("ChangeKey", changeKey));
        return e;
    }

    public static XElement ItemIds(IEnumerable<string> ids) => new(M + "ItemIds", ids.Select(id => ItemId(id)));

    public static XElement Mailbox(string? name, string address)
    {
        var e = new XElement(T + "Mailbox");
        if (!string.IsNullOrWhiteSpace(name)) e.Add(new XElement(T + "Name", name));
        e.Add(new XElement(T + "EmailAddress", address));
        return e;
    }

    public static XElement Bool(XName name, bool value) => new(name, value ? "true" : "false");

    public static string Date(DateTimeOffset d) =>
        d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseDate(string? s) =>
        string.IsNullOrEmpty(s)
            ? default
            : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static DateTimeOffset? ParseDateOrNull(string? s) => string.IsNullOrEmpty(s) ? null : ParseDate(s);

    public static bool ParseBool(string? s) => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);

    public static int ParseInt(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public static long ParseLong(string? s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public static string? Val(this XElement? e, string localName) => e?.Element(T + localName)?.Value;

    public static string VersionString(Core.Models.ExchangeServerVersion v) => v switch
    {
        Core.Models.ExchangeServerVersion.Exchange2010_SP2 => "Exchange2010_SP2",
        Core.Models.ExchangeServerVersion.Exchange2013 => "Exchange2013",
        Core.Models.ExchangeServerVersion.Exchange2013_SP1 => "Exchange2013_SP1",
        _ => "Exchange2016",
    };

    /// <summary>Distinguished (well-known) folder names understood by EWS.</summary>
    public static readonly IReadOnlyDictionary<string, Core.Models.WellKnownFolder> DistinguishedFolders =
        new Dictionary<string, Core.Models.WellKnownFolder>(StringComparer.OrdinalIgnoreCase)
        {
            ["msgfolderroot"] = Core.Models.WellKnownFolder.Root,
            ["inbox"] = Core.Models.WellKnownFolder.Inbox,
            ["drafts"] = Core.Models.WellKnownFolder.Drafts,
            ["sentitems"] = Core.Models.WellKnownFolder.SentItems,
            ["deleteditems"] = Core.Models.WellKnownFolder.DeletedItems,
            ["junkemail"] = Core.Models.WellKnownFolder.JunkEmail,
            ["outbox"] = Core.Models.WellKnownFolder.Outbox,
            ["contacts"] = Core.Models.WellKnownFolder.Contacts,
        };
}
