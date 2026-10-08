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
        Children.CollectionChanged += (_, _) => RecountSubfolders();
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

    /// <summary>Unread messages in this folder itself.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread), nameof(BadgeCount), nameof(BadgeTip))]
    private int _unread;

    /// <summary>Unread messages in all subfolders (except those that never show a counter, such as Deleted Items).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread), nameof(BadgeCount), nameof(BadgeTip))]
    private int _unreadInSubfolders;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isDropTarget;

    /// <summary>Folders whose unread messages are not new mail: no counter, and nothing added to the parent's.</summary>
    private bool IsQuiet => Folder.WellKnown is WellKnownFolder.DeletedItems or WellKnownFolder.JunkEmail or WellKnownFolder.SentItems or WellKnownFolder.Drafts;

    /// <summary>The counter next to the folder: its own unread messages plus those of its subfolders.</summary>
    public int BadgeCount => Unread + UnreadInSubfolders;

    public bool HasUnread => BadgeCount > 0 && !IsQuiet;

    public string BadgeTip => UnreadInSubfolders == 0
        ? $"Непрочитанных: {Unread}"
        : $"Непрочитанных: {BadgeCount} (в этой папке — {Unread}, в подпапках — {UnreadInSubfolders})";

    partial void OnUnreadChanged(int value) => Parent?.RecountSubfolders();

    partial void OnUnreadInSubfoldersChanged(int value) => Parent?.RecountSubfolders();

    private void RecountSubfolders() => UnreadInSubfolders = Children.Where(c => !c.IsQuiet).Sum(c => c.BadgeCount);

    public void Update(MailFolder folder)
    {
        Folder = folder;
        if (!IsAccountRoot) Unread = folder.UnreadCount;
        OnPropertyChanged(nameof(Name));
        Parent?.RecountSubfolders();
    }

    /// <summary>Shows a new name right after a successful rename on the server.</summary>
    public void Rename(string name)
    {
        Folder = Folder.WithName(name);
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Path));
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
