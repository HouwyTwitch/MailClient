using System.Xml.Linq;
using MailClient.Core.Models;
using static MailClient.Exchange.Ews.Ews;

namespace MailClient.Exchange.Ews;

/// <summary>Maps EWS XML items/folders to the client's model objects.</summary>
internal static class EwsParser
{
    private static readonly HashSet<string> FolderElementNames = new() { "Folder", "CalendarFolder", "ContactsFolder", "TasksFolder" };

    /// <summary>PR_ATTR_HIDDEN – hidden system folders.</summary>
    public const string HiddenPropTag = "0x10F4";
    /// <summary>PR_FLAG_STATUS – follow-up flag (works on all Exchange versions).</summary>
    public const string FlagStatusPropTag = "0x1090";

    public static bool IsFolderElement(XElement e) => e.Name.Namespace == T && FolderElementNames.Contains(e.Name.LocalName);

    public static MailFolder ParseFolder(XElement e)
    {
        var id = e.Element(T + "FolderId");
        return new MailFolder
        {
            Id = (string?)id?.Attribute("Id") ?? "",
            ChangeKey = (string?)id?.Attribute("ChangeKey") ?? "",
            ParentId = (string?)e.Element(T + "ParentFolderId")?.Attribute("Id"),
            DisplayName = e.Val("DisplayName") ?? "",
            FolderClass = e.Val("FolderClass") ?? e.Name.LocalName switch
            {
                "CalendarFolder" => "IPF.Appointment",
                "ContactsFolder" => "IPF.Contact",
                "TasksFolder" => "IPF.Task",
                _ => "IPF.Note",
            },
            TotalCount = ParseInt(e.Val("TotalCount")),
            UnreadCount = ParseInt(e.Val("UnreadCount")),
            ChildFolderCount = ParseInt(e.Val("ChildFolderCount")),
        };
    }

    public static bool IsHidden(XElement folder) =>
        folder.Elements(T + "ExtendedProperty").Any(p =>
            (string?)p.Element(T + "ExtendedFieldURI")?.Attribute("PropertyTag") is { } tag
            && IsTag(tag, HiddenPropTag)
            && ParseBool(p.Element(T + "Value")?.Value));

    private static bool IsTag(string actual, string expected) =>
        Convert.ToInt32(actual, 16) == Convert.ToInt32(expected, 16);

    public static EmailAddress? ParseMailbox(XElement? mailbox)
    {
        if (mailbox == null) return null;
        if (mailbox.Name.LocalName != "Mailbox") mailbox = mailbox.Element(T + "Mailbox");
        if (mailbox == null) return null;
        return new EmailAddress(mailbox.Val("Name") ?? "", mailbox.Val("EmailAddress") ?? "", mailbox.Val("RoutingType") ?? "SMTP");
    }

    public static Importance ParseImportance(string? s) => s switch
    {
        "Low" => Importance.Low,
        "High" => Importance.High,
        _ => Importance.Normal,
    };

    public static void FillSummary(MessageSummary m, XElement e)
    {
        var id = e.Element(T + "ItemId");
        m.Id = (string?)id?.Attribute("Id") ?? "";
        m.ChangeKey = (string?)id?.Attribute("ChangeKey") ?? "";
        m.Subject = e.Val("Subject") ?? "";
        m.From = ParseMailbox(e.Element(T + "From")) ?? ParseMailbox(e.Element(T + "Sender")) ?? ParseMailbox(e.Element(T + "Organizer"));
        m.DisplayTo = e.Val("DisplayTo") ?? "";
        m.DisplayCc = e.Val("DisplayCc") ?? "";
        m.DateReceived = ParseDate(e.Val("DateTimeReceived") ?? e.Val("DateTimeCreated"));
        m.DateSent = ParseDate(e.Val("DateTimeSent")) is var sent && sent != default ? sent : m.DateReceived;
        // Non-message items (e.g. posts) have no IsRead in some shapes: treat as read.
        m.IsRead = e.Element(T + "IsRead") is not { } r || ParseBool(r.Value);
        m.HasAttachments = ParseBool(e.Val("HasAttachments"));
        m.Importance = ParseImportance(e.Val("Importance"));
        m.Size = ParseLong(e.Val("Size"));
        m.Preview = (e.Val("Preview") ?? "").Trim();
        m.ItemClass = e.Val("ItemClass") ?? (e.Name.LocalName == "Message" ? "IPM.Note" : "IPM." + e.Name.LocalName);
        m.ConversationId = (string?)e.Element(T + "ConversationId")?.Attribute("Id") ?? "";
        m.Categories = e.Element(T + "Categories")?.Elements(T + "String").Select(s => s.Value).ToList() ?? new();
        m.Flag = ParseFlag(e);
    }

    public static FlagStatus ParseFlag(XElement e)
    {
        var status = e.Element(T + "Flag")?.Val("FlagStatus");
        if (status != null)
            return status switch { "Flagged" => FlagStatus.Flagged, "Complete" => FlagStatus.Complete, _ => FlagStatus.NotFlagged };
        var ext = e.Elements(T + "ExtendedProperty").FirstOrDefault(p =>
            (string?)p.Element(T + "ExtendedFieldURI")?.Attribute("PropertyTag") is { } tag && IsTag(tag, FlagStatusPropTag));
        return ParseInt(ext?.Element(T + "Value")?.Value) switch
        {
            1 => FlagStatus.Complete,
            2 => FlagStatus.Flagged,
            _ => FlagStatus.NotFlagged,
        };
    }

    public static MessageSummary ParseSummary(XElement e)
    {
        var m = new MessageSummary();
        FillSummary(m, e);
        return m;
    }

    public static List<AttachmentInfo> ParseAttachments(XElement? list)
    {
        var result = new List<AttachmentInfo>();
        if (list == null) return result;
        foreach (var a in list.Elements())
        {
            var isItem = a.Name.LocalName == "ItemAttachment" || a.Name.LocalName == "ReferenceAttachment";
            var name = a.Val("Name") ?? "attachment";
            result.Add(new AttachmentInfo
            {
                Id = (string?)a.Element(T + "AttachmentId")?.Attribute("Id") ?? "",
                Name = isItem && !name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) && a.Name.LocalName == "ItemAttachment" ? name + ".eml" : name,
                ContentType = a.Val("ContentType") ?? (isItem ? "message/rfc822" : "application/octet-stream"),
                ContentId = (a.Val("ContentId") ?? "").Trim('<', '>'),
                Size = ParseLong(a.Val("Size")),
                IsInline = ParseBool(a.Val("IsInline")),
                IsItemAttachment = isItem,
            });
        }
        return result;
    }

    public static MeetingInfo ParseMeeting(XElement e) => new()
    {
        Subject = e.Val("Subject") ?? "",
        Location = e.Val("Location") ?? "",
        Start = ParseDate(e.Val("Start")),
        End = ParseDate(e.Val("End")),
        IsAllDay = ParseBool(e.Val("IsAllDayEvent")),
        Organizer = ParseMailbox(e.Element(T + "Organizer")),
        MyResponse = Enum.TryParse<ResponseStatus>(e.Val("MyResponseType"), out var rs) ? rs : ResponseStatus.Unknown,
        IsCancelled = ParseBool(e.Val("IsCancelled")),
    };

    public static Contact ParseContact(XElement e)
    {
        var id = e.Element(T + "ItemId");
        string Entry(string container, string key) =>
            e.Element(T + container)?.Elements(T + "Entry").FirstOrDefault(x => (string?)x.Attribute("Key") == key)?.Value ?? "";

        var emails = e.Element(T + "EmailAddresses")?.Elements(T + "Entry")
            .OrderBy(x => (string?)x.Attribute("Key"))
            .Select(x => StripSmtpPrefix(x.Value))
            .Where(v => v.Length > 0).ToList() ?? new();

        var c = new Contact
        {
            Id = (string?)id?.Attribute("Id") ?? "",
            ChangeKey = (string?)id?.Attribute("ChangeKey") ?? "",
            DisplayName = e.Val("DisplayName") ?? e.Val("Subject") ?? "",
            GivenName = e.Val("GivenName") ?? "",
            Surname = e.Val("Surname") ?? "",
            CompanyName = e.Val("CompanyName") ?? "",
            JobTitle = e.Val("JobTitle") ?? "",
            Department = e.Val("Department") ?? "",
            EmailAddresses = emails,
            BusinessPhone = Entry("PhoneNumbers", "BusinessPhone"),
            MobilePhone = Entry("PhoneNumbers", "MobilePhone"),
            HomePhone = Entry("PhoneNumbers", "HomePhone"),
            Notes = e.Element(T + "Body")?.Value ?? "",
        };
        return c;
    }

    public static string StripSmtpPrefix(string s) =>
        s.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase) ? s[5..] : s;

    /// <summary>Parses one ResolveNames resolution (mailbox + optional contact data).</summary>
    public static Contact ParseResolution(XElement resolution)
    {
        var mailbox = resolution.Element(T + "Mailbox");
        var contactEl = resolution.Element(T + "Contact");
        var c = contactEl != null ? ParseContact(contactEl) : new Contact();
        var address = mailbox.Val("EmailAddress") ?? "";
        var routing = mailbox.Val("RoutingType") ?? "SMTP";
        if (routing.Equals("SMTP", StringComparison.OrdinalIgnoreCase) && address.Length > 0)
        {
            c.EmailAddresses.Remove(address);
            c.EmailAddresses.Insert(0, address);
        }
        if (string.IsNullOrEmpty(c.DisplayName)) c.DisplayName = mailbox.Val("Name") ?? address;
        c.Id = (string?)mailbox?.Element(T + "ItemId")?.Attribute("Id") ?? "";
        c.IsDirectoryEntry = (mailbox.Val("MailboxType") ?? "Mailbox") != "Contact";
        return c;
    }
}
