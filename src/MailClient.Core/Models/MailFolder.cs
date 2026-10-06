namespace MailClient.Core.Models;

public enum WellKnownFolder
{
    None,
    Root,
    Inbox,
    Drafts,
    SentItems,
    DeletedItems,
    JunkEmail,
    Outbox,
    Archive,
    Contacts,
}

public enum FolderKind
{
    Mail,
    Calendar,
    Contacts,
    Tasks,
    Notes,
    Other,
}

public sealed class MailFolder
{
    public string Id { get; set; } = "";
    public string ChangeKey { get; set; } = "";
    public string? ParentId { get; set; }
    public string DisplayName { get; set; } = "";
    public string FolderClass { get; set; } = "";
    public int TotalCount { get; set; }
    public int UnreadCount { get; set; }
    public int ChildFolderCount { get; set; }
    public WellKnownFolder WellKnown { get; set; }

    public FolderKind Kind => FolderClassToKind(FolderClass);

    public static FolderKind FolderClassToKind(string? folderClass)
    {
        if (string.IsNullOrEmpty(folderClass) || folderClass.StartsWith("IPF.Note", StringComparison.OrdinalIgnoreCase))
            return FolderKind.Mail;
        if (folderClass.StartsWith("IPF.Appointment", StringComparison.OrdinalIgnoreCase)) return FolderKind.Calendar;
        if (folderClass.StartsWith("IPF.Contact", StringComparison.OrdinalIgnoreCase)) return FolderKind.Contacts;
        if (folderClass.StartsWith("IPF.Task", StringComparison.OrdinalIgnoreCase)) return FolderKind.Tasks;
        if (folderClass.StartsWith("IPF.StickyNote", StringComparison.OrdinalIgnoreCase)) return FolderKind.Notes;
        return FolderKind.Other;
    }

    /// <summary>A copy with another display name.</summary>
    public MailFolder WithName(string name) => new()
    {
        Id = Id, ChangeKey = ChangeKey, ParentId = ParentId, DisplayName = name, FolderClass = FolderClass,
        TotalCount = TotalCount, UnreadCount = UnreadCount, ChildFolderCount = ChildFolderCount, WellKnown = WellKnown,
    };

    public override string ToString() => DisplayName;
}
