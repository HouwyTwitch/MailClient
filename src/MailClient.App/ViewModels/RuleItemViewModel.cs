using CommunityToolkit.Mvvm.ComponentModel;
using MailClient.Core.Models;

namespace MailClient.App.ViewModels;

/// <summary>A forwarding rule in the rules list: on/off switch, name and a one-line description.</summary>
public sealed partial class RuleItemViewModel : ObservableObject
{
    public RuleItemViewModel(ForwardingRule rule) => Rule = rule;

    public ForwardingRule Rule { get; private set; }

    public string Name => string.IsNullOrWhiteSpace(Rule.Name) ? "(без названия)" : Rule.Name;
    public bool IsEditable => Rule.IsEditable;
    public string Summary => Describe(Rule);

    public bool IsEnabled
    {
        get => Rule.IsEnabled;
        set
        {
            if (Rule.IsEnabled == value) return;
            Rule.IsEnabled = value;
            OnPropertyChanged();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when the switch is flipped in the list.</summary>
    public event EventHandler? Changed;

    public void Replace(ForwardingRule rule)
    {
        Rule = rule;
        OnPropertyChanged(string.Empty);
    }

    public static string ModeText(ForwardingMode mode) => mode switch
    {
        ForwardingMode.Redirect => "переадресовать",
        ForwardingMode.Forward => "переслать",
        _ => "переслать вложением",
    };

    /// <summary>"От bank@bank.ru и тема содержит «счёт» → переадресовать: buh@corp.ru".</summary>
    public static string Describe(ForwardingRule r)
    {
        if (!r.IsEditable)
            return "Правило создано в другой программе (например, в Outlook): его можно включить, выключить, переименовать, переместить или удалить.";
        var conditions = new List<string>();
        if (r.FromAddresses.Count > 0) conditions.Add("от " + string.Join(" или ", r.FromAddresses));
        if (r.SubjectContains.Count > 0) conditions.Add("тема содержит " + Quoted(r.SubjectContains));
        if (r.BodyContains.Count > 0) conditions.Add("текст содержит " + Quoted(r.BodyContains));
        var when = conditions.Count == 0 ? "Все входящие письма" : char.ToUpperInvariant(conditions[0][0]) + string.Join(" и ", conditions)[1..];
        var to = string.Join(", ", r.Recipients.Select(a => a.ShortName));
        var extra = (r.KeepCopy ? "" : ", без копии в ящике") + (r.StopProcessing ? ", дальше не проверять" : "");
        return $"{when} → {ModeText(r.Mode)}: {to}{extra}";
    }

    private static string Quoted(IEnumerable<string> words) => string.Join(" или ", words.Select(w => $"«{w}»"));
}
