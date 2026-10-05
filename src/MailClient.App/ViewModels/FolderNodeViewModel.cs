using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MailClient.App.Services;
using MailClient.Core.Models;

namespace MailClient.App.ViewModels;

public sealed partial class FolderNodeViewModel : ObservableObject
{
    public FolderNodeViewModel(AccountSession session, MailFolder folder, bool isAccountRoot)
    {
        Session = session;
        Folder = folder;
        IsAccountRoot = isAccountRoot;
        _unread = isAccountRoot ? 0 : folder.UnreadCount;
    }

    public AccountSession Session { get; }
    public MailFolder Folder { get; private set; }
    public bool IsAccountRoot { get; }
    public string Id => Folder.Id;
    public FolderNodeViewModel? Parent { get; set; }
    public ObservableCollection<FolderNodeViewModel> Children { get; } = new();

    public string Name => IsAccountRoot ? Session.Settings.EffectiveDisplayName : RuText.FolderName(Folder);
    public string Icon => IsAccountRoot ? "\uE77B" : RuText.FolderIcon(Folder);
    public bool IsWellKnown => Folder.WellKnown != WellKnownFolder.None;
    public bool CanModify => !IsAccountRoot && !IsWellKnown;

    /// <summary>Folders where the list shows recipients instead of senders.</summary>
    public bool ShowsRecipients => Folder.WellKnown is WellKnownFolder.SentItems or WellKnownFolder.Drafts or WellKnownFolder.Outbox;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unread;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isDropTarget;

    public bool HasUnread => Unread > 0 && Folder.WellKnown is not (WellKnownFolder.DeletedItems or WellKnownFolder.JunkEmail or WellKnownFolder.SentItems or WellKnownFolder.Drafts);

    public void Update(MailFolder folder)
    {
        Folder = folder;
        if (!IsAccountRoot) Unread = folder.UnreadCount;
        OnPropertyChanged(nameof(Name));
    }

    public IEnumerable<FolderNodeViewModel> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants())
                yield return d;
    }

    public string Path => Parent == null || Parent.IsAccountRoot ? Name : $"{Parent.Path} / {Name}";

    public override string ToString() => Name;
}
