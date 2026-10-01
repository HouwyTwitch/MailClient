namespace MailClient.Core.Models;

public enum ComposeAction { New, Reply, ReplyAll, Forward, EditDraft }

public sealed class OutgoingAttachment
{
    public required string Name { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public required byte[] Content { get; init; }
    public bool IsInline { get; init; }
    public string ContentId { get; init; } = "";
}

public sealed class OutgoingMessage
{
    public ComposeAction Action { get; set; } = ComposeAction.New;

    /// <summary>The original item when replying/forwarding, or the draft being edited.</summary>
    public string? ReferenceItemId { get; set; }

    public List<EmailAddress> To { get; set; } = new();
    public List<EmailAddress> Cc { get; set; } = new();
    public List<EmailAddress> Bcc { get; set; } = new();
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public bool BodyIsHtml { get; set; } = true;
    public Importance Importance { get; set; } = Importance.Normal;
    public bool RequestReadReceipt { get; set; }
    public bool RequestDeliveryReceipt { get; set; }
    public List<OutgoingAttachment> Attachments { get; set; } = new();

    public IEnumerable<EmailAddress> AllRecipients => To.Concat(Cc).Concat(Bcc);
}
