using CommunityToolkit.Mvvm.ComponentModel;
using MailClient.App.Services;
using MailClient.Core.Models;

namespace MailClient.App.ViewModels;

public sealed partial class MessageItemViewModel : ObservableObject
{
    public MessageItemViewModel(MessageSummary summary, bool showRecipients)
    {
        Summary = summary;
        ShowRecipients = showRecipients;
        _isRead = summary.IsRead;
        _flag = summary.Flag;
    }

    public MessageSummary Summary { get; private set; }
    public bool ShowRecipients { get; }
    public string Id => Summary.Id;

    public string Correspondent => OneLine(ShowRecipients
        ? (string.IsNullOrWhiteSpace(Summary.DisplayTo) ? "(нет получателей)" : Summary.DisplayTo)
        : Summary.From?.ShortName is { Length: > 0 } n ? n : "(без отправителя)", 120);

    public string Subject => string.IsNullOrWhiteSpace(Summary.Subject) ? "(без темы)" : OneLine(Summary.Subject, 200);
    public string Preview => OneLine(Summary.Preview, 160);

    private static string OneLine(string? text, int max) => MailClient.Core.Rendering.TextUtil.OneLine(text, max);
    public string DateText => RuText.ShortDate(Summary.DateReceived);
    public string DateTooltip => RuText.FullDate(Summary.DateReceived);
    public bool HasAttachments => Summary.HasAttachments;
    public bool IsHighImportance => Summary.Importance == Importance.High;
    public bool IsMeeting => Summary.IsMeetingRequest || Summary.IsMeetingCancellation;
    public string Categories => string.Join(", ", Summary.Categories);

    [ObservableProperty] private bool _isRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFlagged))]
    private FlagStatus _flag;

    public bool IsFlagged => Flag == FlagStatus.Flagged;

    partial void OnIsReadChanged(bool value) => Summary.IsRead = value;
    partial void OnFlagChanged(FlagStatus value) => Summary.Flag = value;

    /// <summary>Refreshes from a newer copy of the same item without recreating the view model.</summary>
    public void Update(MessageSummary s)
    {
        Summary = s;
        IsRead = s.IsRead;
        Flag = s.Flag;
        OnPropertyChanged(string.Empty);
    }
}
