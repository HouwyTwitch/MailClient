using System.Globalization;
using System.Xml.Linq;
using MailClient.Core.Models;
using static MailClient.Exchange.Ews.Ews;

namespace MailClient.Exchange.Ews;

/// <summary>
/// Exchange Inbox rules (GetInboxRules / UpdateInboxRules, Exchange 2010 SP1 and later) as
/// <see cref="ForwardingRule"/>s, and the operations that turn the server's rules into the edited list.
/// </summary>
internal static class InboxRules
{
    /// <summary>Conditions the rule editor shows, in protocol order.</summary>
    private static readonly HashSet<string> EditableConditions = ["ContainsBodyStrings", "ContainsSubjectStrings", "FromAddresses"];

    /// <summary>Actions the rule editor shows.</summary>
    private static readonly HashSet<string> EditableActions =
        ["Delete", "ForwardAsAttachmentToRecipients", "ForwardToRecipients", "RedirectToRecipients", "StopProcessingRules"];

    private static readonly (string Element, ForwardingMode Mode)[] ForwardActions =
    [
        ("ForwardAsAttachmentToRecipients", ForwardingMode.ForwardAsAttachment),
        ("ForwardToRecipients", ForwardingMode.Forward),
        ("RedirectToRecipients", ForwardingMode.Redirect),
    ];

    public static ForwardingRule Parse(XElement rule)
    {
        var conditions = rule.Element(T + "Conditions");
        var exceptions = rule.Element(T + "Exceptions");
        var actions = rule.Element(T + "Actions");
        var forwards = ForwardActions.Where(f => actions?.Element(T + f.Element) != null).ToList();

        var result = new ForwardingRule
        {
            Id = rule.Val("RuleId") ?? "",
            Name = rule.Val("DisplayName") ?? "",
            IsEnabled = ParseBool(rule.Val("IsEnabled")),
            FromAddresses = Addresses(conditions?.Element(T + "FromAddresses")).Select(a => a.Address).ToList(),
            SubjectContains = Strings(conditions?.Element(T + "ContainsSubjectStrings")),
            BodyContains = Strings(conditions?.Element(T + "ContainsBodyStrings")),
            KeepCopy = !ParseBool(actions.Val("Delete")),
            StopProcessing = ParseBool(actions.Val("StopProcessingRules")),
            ServerData = rule.ToString(SaveOptions.DisableFormatting),
        };
        if (forwards.Count > 0)
        {
            result.Mode = forwards[0].Mode;
            result.Recipients = Addresses(actions!.Element(T + forwards[0].Element)).ToList();
        }
        // Anything the editor cannot show (other conditions, exceptions, folder moves, several kinds of forwarding,
        // a rule Exchange itself marks unsupported or broken) stays exactly as the server has it.
        result.IsEditable =
            forwards.Count == 1 &&
            !ParseBool(rule.Val("IsNotSupported")) && !ParseBool(rule.Val("IsInError")) &&
            (conditions?.Elements().All(e => EditableConditions.Contains(e.Name.LocalName)) ?? true) &&
            !(exceptions?.HasElements ?? false) &&
            actions!.Elements().All(e => EditableActions.Contains(e.Name.LocalName)) &&
            result.FromAddresses.All(EmailAddress.LooksValid);
        return result;
    }

    /// <summary>The t:Rule element for a create (no id) or set operation.</summary>
    public static XElement Build(ForwardingRule rule, int priority)
    {
        if (!rule.IsEditable && rule.ServerData != null)
        {
            // Only the name, order and on/off switch change; everything else is written back as read.
            var kept = XElement.Parse(rule.ServerData);
            kept.Element(T + "IsNotSupported")?.Remove();
            kept.Element(T + "IsInError")?.Remove();
            kept.SetElementValue(T + "DisplayName", rule.Name);
            kept.SetElementValue(T + "Priority", priority.ToString(CultureInfo.InvariantCulture));
            kept.SetElementValue(T + "IsEnabled", rule.IsEnabled ? "true" : "false");
            return kept;
        }

        var conditions = new XElement(T + "Conditions");
        if (rule.BodyContains.Count > 0) conditions.Add(StringArray("ContainsBodyStrings", rule.BodyContains));
        if (rule.SubjectContains.Count > 0) conditions.Add(StringArray("ContainsSubjectStrings", rule.SubjectContains));
        if (rule.FromAddresses.Count > 0)
            conditions.Add(new XElement(T + "FromAddresses", rule.FromAddresses.Select(a => Address(new EmailAddress("", a)))));

        var actions = new XElement(T + "Actions");
        if (!rule.KeepCopy) actions.Add(Bool(T + "Delete", true));
        var forward = ForwardActions.First(f => f.Mode == rule.Mode).Element;
        actions.Add(new XElement(T + forward, rule.Recipients.Select(Address)));
        if (rule.StopProcessing) actions.Add(Bool(T + "StopProcessingRules", true));

        var e = new XElement(T + "Rule");
        if (rule.Id.Length > 0) e.Add(new XElement(T + "RuleId", rule.Id));
        e.Add(new XElement(T + "DisplayName", rule.Name),
            new XElement(T + "Priority", priority.ToString(CultureInfo.InvariantCulture)),
            Bool(T + "IsEnabled", rule.IsEnabled),
            // An explicit empty element: a rule edited down to "every message" loses its old conditions.
            conditions,
            actions);
        return e;
    }

    /// <summary>
    /// The operations that turn the rules on the server into <paramref name="desired"/> (order = priority):
    /// create the new ones, set the ones that differ, delete the ones that are gone.
    /// </summary>
    public static List<XElement> Operations(IReadOnlyList<XElement> serverRules, IReadOnlyList<ForwardingRule> desired)
    {
        var current = serverRules.ToDictionary(r => r.Val("RuleId") ?? "", Parse);
        var priorities = serverRules.ToDictionary(r => r.Val("RuleId") ?? "", r => ParseInt(r.Val("Priority")));
        var ops = new List<XElement>();
        var kept = new HashSet<string>(desired.Where(r => r.Id.Length > 0).Select(r => r.Id));

        // Deletions first, so the priorities of the remaining rules can be set without collisions.
        foreach (var id in current.Keys.Where(id => id.Length > 0 && !kept.Contains(id)))
            ops.Add(new XElement(T + "DeleteRuleOperation", new XElement(T + "RuleId", id)));

        for (int i = 0; i < desired.Count; i++)
        {
            var rule = desired[i];
            var element = Build(rule, i + 1);
            if (rule.Id.Length == 0)
            {
                ops.Add(new XElement(T + "CreateRuleOperation", element));
            }
            else if (!current.TryGetValue(rule.Id, out var before))
            {
                throw new Core.Services.MailServiceException(
                    $"Правило «{rule.Name}» уже удалено на сервере (например, в Outlook). Откройте список правил заново.", "ErrorRuleNotFound");
            }
            else if (!XNode.DeepEquals(Build(before, priorities[rule.Id]), element))
            {
                ops.Add(new XElement(T + "SetRuleOperation", element));
            }
        }
        return ops;
    }

    /// <summary>Validation errors of a rejected UpdateInboxRules, as the server worded them.</summary>
    public static string ValidationErrors(XElement response) =>
        string.Join("; ", response.Descendants(T + "ErrorMessage").Select(e => e.Value.Trim()).Where(t => t.Length > 0).Distinct());

    private static List<string> Strings(XElement? array) =>
        array?.Elements(T + "String").Select(s => s.Value).Where(s => s.Length > 0).ToList() ?? new();

    private static IEnumerable<EmailAddress> Addresses(XElement? array) =>
        array?.Elements(T + "Address").Select(a => new EmailAddress(a.Val("Name") ?? "", a.Val("EmailAddress") ?? ""))
            .Where(a => a.Address.Length > 0 || a.Name.Length > 0) ?? [];

    private static XElement StringArray(string name, IEnumerable<string> values) =>
        new(T + name, values.Select(v => new XElement(T + "String", v)));

    private static XElement Address(EmailAddress a)
    {
        var e = new XElement(T + "Address");
        if (!string.IsNullOrWhiteSpace(a.Name) && a.Name != a.Address) e.Add(new XElement(T + "Name", a.Name));
        e.Add(new XElement(T + "EmailAddress", a.Address));
        return e;
    }
}
