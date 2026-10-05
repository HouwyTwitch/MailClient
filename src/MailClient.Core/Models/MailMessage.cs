namespace MailClient.Core.Models;

public enum Importance { Low, Normal, High }

public enum FlagStatus { NotFlagged, Flagged, Complete }

/// <summary>Lightweight message representation used in message lists.</summary>
public class MessageSummary
{
    public string Id { get; set; } = "";
    public string ChangeKey { get; set; } = "";
    public string FolderId { get; set; } = "";
    public string Subject { get; set; } = "";
    public EmailAddress? From { get; set; }
    public string DisplayTo { get; set; } = "";
    public string DisplayCc { get; set; } = "";
    public DateTimeOffset DateReceived { get; set; }
    public DateTimeOffset DateSent { get; set; }
    public bool IsRead { get; set; }
    public bool HasAttachments { get; set; }
    public Importance Importance { get; set; } = Importance.Normal;
    public FlagStatus Flag { get; set; }
    public long Size { get; set; }
    public string Preview { get; set; } = "";
    public string ItemClass { get; set; } = "IPM.Note";
    public string ConversationId { get; set; } = "";
    public List<string> Categories { get; set; } = new();

    public bool IsMeetingRequest => ItemClass.StartsWith("IPM.Schedule.Meeting.Request", StringComparison.OrdinalIgnoreCase);
    public bool IsMeetingCancellation => ItemClass.StartsWith("IPM.Schedule.Meeting.Canceled", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Fully loaded message, including body, recipients and attachment list.</summary>
public sealed class MailMessage : MessageSummary
{
    public List<EmailAddress> To { get; set; } = new();
    public List<EmailAddress> Cc { get; set; } = new();
    public List<EmailAddress> Bcc { get; set; } = new();
    public List<EmailAddress> ReplyTo { get; set; } = new();
    public EmailAddress? Sender { get; set; }
    public string Body { get; set; } = "";
    public bool BodyIsHtml { get; set; }
    public string InternetMessageId { get; set; } = "";
    public bool IsReadReceiptRequested { get; set; }
    public List<AttachmentInfo> Attachments { get; set; } = new();

    /// <summary>For meeting invitations: time, place and organizer.</summary>
    public MeetingInfo? Meeting { get; set; }
}

public sealed class AttachmentInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public string ContentId { get; set; } = "";
    public long Size { get; set; }
    public bool IsInline { get; set; }
    /// <summary>True when the attachment is an embedded item (e.g. an attached e-mail) rather than a file.</summary>
    public bool IsItemAttachment { get; set; }
}

public sealed class AttachmentContent
{
    public required AttachmentInfo Info { get; init; }
    public required byte[] Content { get; init; }
}

public sealed class MessagePage
{
    public required IReadOnlyList<MessageSummary> Items { get; init; }
    public int TotalCount { get; init; }
    public bool HasMore { get; init; }
}

public sealed class FolderSyncResult
{
    public List<MessageSummary> CreatedOrUpdated { get; } = new();
    /// <summary>
    /// Ids in <see cref="CreatedOrUpdated"/> that are new to the folder (EWS Create change, new IMAP UID), as
    /// opposed to updates of known messages; only these may raise "new mail" notifications.
    /// </summary>
    public HashSet<string> Created { get; } = new();
    public List<string> Deleted { get; } = new();
    /// <summary>Read-flag only changes (item id → read state).</summary>
    public Dictionary<string, bool> ReadFlagChanges { get; } = new();
    public string SyncState { get; set; } = "";
    public bool IncludesLastItem { get; set; }
}
