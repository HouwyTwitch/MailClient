namespace MailClient.Core.Models;

public enum MeetingResponse { Accept, Tentative, Decline }

public enum ResponseStatus { Unknown, Organizer, Tentative, Accept, Decline, NoResponseReceived }

/// <summary>Meeting data shown on an invitation in the reading pane (Accept / Tentative / Decline).</summary>
public sealed class MeetingInfo
{
    public string Subject { get; set; } = "";
    public string Location { get; set; } = "";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public bool IsAllDay { get; set; }
    public EmailAddress? Organizer { get; set; }
    public ResponseStatus MyResponse { get; set; }
    public bool IsCancelled { get; set; }
}

public sealed class Contact
{
    public string Id { get; set; } = "";
    public string ChangeKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string GivenName { get; set; } = "";
    public string Surname { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string JobTitle { get; set; } = "";
    public string Department { get; set; } = "";
    public List<string> EmailAddresses { get; set; } = new();
    public string BusinessPhone { get; set; } = "";
    public string MobilePhone { get; set; } = "";
    public string HomePhone { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>True when the entry comes from the Global Address List rather than the user's contacts folder.</summary>
    public bool IsDirectoryEntry { get; set; }

    public string PrimaryEmail => EmailAddresses.FirstOrDefault() ?? "";
}

public enum OofState { Disabled, Enabled, Scheduled }

public enum OofExternalAudience { None, Known, All }

public sealed class OofSettings
{
    public OofState State { get; set; }
    public OofExternalAudience ExternalAudience { get; set; } = OofExternalAudience.All;
    public DateTimeOffset StartTime { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset EndTime { get; set; } = DateTimeOffset.Now.AddDays(1);
    public string InternalReply { get; set; } = "";
    public string ExternalReply { get; set; } = "";
}

public sealed class MailboxInfo
{
    public required string EmailAddress { get; init; }
    public string DisplayName { get; init; } = "";
    public string ServerVersion { get; init; } = "";
}
