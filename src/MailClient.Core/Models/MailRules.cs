namespace MailClient.Core.Models;

/// <summary>How a rule sends a message on.</summary>
public enum ForwardingMode
{
    /// <summary>A new message from the user with the original quoted ("FW:").</summary>
    Forward,
    /// <summary>The original message itself, still from its sender (redirect / переадресация).</summary>
    Redirect,
    /// <summary>A new message with the original attached as a file.</summary>
    ForwardAsAttachment,
}

/// <summary>
/// A server-side mail rule that forwards incoming messages. Conditions of different kinds must all match;
/// within one kind any value matches (any of the senders, any of the words). No conditions means every message.
/// </summary>
public sealed class ForwardingRule
{
    /// <summary>Server id; empty for a rule that has not been saved yet.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; } = true;

    public List<string> FromAddresses { get; set; } = new();
    public List<string> SubjectContains { get; set; } = new();
    public List<string> BodyContains { get; set; } = new();

    public ForwardingMode Mode { get; set; } = ForwardingMode.Redirect;
    public List<EmailAddress> Recipients { get; set; } = new();
    /// <summary>Keep the message in the mailbox too (otherwise it goes to Deleted Items / is not kept).</summary>
    public bool KeepCopy { get; set; } = true;
    /// <summary>Do not apply the rules below this one to a message it matched.</summary>
    public bool StopProcessing { get; set; }

    /// <summary>
    /// False for a rule created elsewhere (Outlook, OWA) with conditions or actions this client does not show:
    /// it can be switched on/off, renamed, reordered or deleted, but its contents stay as they are.
    /// </summary>
    public bool IsEditable { get; set; } = true;

    /// <summary>The server's own representation, kept so a rule that is not editable is written back unchanged.</summary>
    public string? ServerData { get; set; }

    public bool HasConditions => FromAddresses.Count > 0 || SubjectContains.Count > 0 || BodyContains.Count > 0;

    public ForwardingRule Clone() => new()
    {
        Id = Id, Name = Name, IsEnabled = IsEnabled,
        FromAddresses = FromAddresses.ToList(), SubjectContains = SubjectContains.ToList(), BodyContains = BodyContains.ToList(),
        Mode = Mode, Recipients = Recipients.ToList(), KeepCopy = KeepCopy, StopProcessing = StopProcessing,
        IsEditable = IsEditable, ServerData = ServerData,
    };
}

/// <summary>The rules of a mailbox in the order they are applied, plus what the server can do.</summary>
public sealed class ForwardingRuleSet
{
    public List<ForwardingRule> Rules { get; init; } = new();
    public IReadOnlyList<ForwardingMode> SupportedModes { get; init; } = [ForwardingMode.Forward, ForwardingMode.Redirect, ForwardingMode.ForwardAsAttachment];
    public bool SupportsBodyConditions { get; init; } = true;
    /// <summary>
    /// Exchange: Outlook keeps its own copy of the rules; saving replaces it, and rules that only run in
    /// Outlook on a computer may be switched off. The user is asked before that happens.
    /// </summary>
    public bool OutlookRulesPresent { get; init; }
    /// <summary>Text about rules on the server that this list does not include (IMAP: other Sieve filters).</summary>
    public string Note { get; init; } = "";
}
