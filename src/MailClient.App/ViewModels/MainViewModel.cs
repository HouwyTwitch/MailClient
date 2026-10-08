using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailClient.App.Services;
using MailClient.App.Views;
using MailClient.Core.Models;
using MailClient.Core.Services;
using Microsoft.Win32;

namespace MailClient.App.ViewModels;

public enum AppSection { Mail, Contacts }

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 200;

    private readonly AppSettings _settings;
    private readonly CredentialProvider _credentials;
    private readonly Dispatcher _dispatcher;
    private readonly List<AccountSession> _sessions = new();
    private readonly DispatcherTimer _reloadTimer;
    private readonly DispatcherTimer _markReadTimer;
    private readonly HashSet<string> _pendingReloadFolders = new();
    private CancellationTokenSource? _previewCts;
    private int _loadedCount = PageSize;
    private bool _restoringSelection;

    public MainViewModel(AppSettings settings, CredentialProvider credentials)
    {
        _settings = settings;
        _credentials = credentials;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _reloadTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => FlushReloads(), _dispatcher) { IsEnabled = false };
        _markReadTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        _markReadTimer.Tick += async (_, _) => await MarkCurrentAsReadAsync();
        Contacts = new ContactsViewModel(() => CurrentSession, WriteTo);
    }

    public AppSettings Settings => _settings;
    public IReadOnlyList<AccountSession> Sessions => _sessions;
    public ContactsViewModel Contacts { get; }
    public TrayService? Tray { get; set; }

    public ObservableCollection<FolderNodeViewModel> Roots { get; } = new();
    public ObservableCollection<MessageItemViewModel> Messages { get; } = new();

    /// <summary>Multi-selection, maintained by the view.</summary>
    public List<MessageItemViewModel> SelectedMessages { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderTitle), nameof(CanModifyFolder), nameof(IsMailFolderSelected), nameof(IsJunkFolder),
        nameof(SupportsContacts), nameof(SupportsOutOfOffice), nameof(SupportsForwardingRules))]
    private FolderNodeViewModel? _selectedFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanRespond))]
    private MessageItemViewModel? _selectedMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview), nameof(ShowPreview), nameof(ShowEmptyHint))]
    private MessagePreviewViewModel? _preview;

    /// <summary>Number of selected messages; above one the reading pane shows the bulk actions instead of a message.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMultiSelection), nameof(MultiSelectionText), nameof(CanRespond), nameof(ShowPreview), nameof(ShowEmptyHint))]
    private int _selectionCount;

    [ObservableProperty] private bool _isPreviewLoading;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isSearchResult;
    [ObservableProperty] private string _statusText = "Готово";
    [ObservableProperty] private string _listStatus = "";
    [ObservableProperty] private bool _isOnline = true;
    [ObservableProperty] private AppSection _section = AppSection.Mail;
    [ObservableProperty] private string _windowTitle = "Корпоративная почта";

    public bool HasSelection => SelectedMessage != null;
    public bool IsMultiSelection => SelectionCount > 1;
    public string MultiSelectionText => "Выбрано " + RuText.Count(SelectionCount, "письмо", "письма", "писем");
    /// <summary>Reply/forward act on one message.</summary>
    public bool CanRespond => HasSelection && !IsMultiSelection;
    public bool ShowPreview => HasPreview && !IsMultiSelection;
    public bool ShowEmptyHint => !HasPreview && !IsMultiSelection;

    private ProviderCapabilities Caps => CurrentSession?.Provider.Capabilities ?? ProviderCapabilities.All;
    public bool SupportsContacts => Caps.HasFlag(ProviderCapabilities.Contacts);
    public bool SupportsOutOfOffice => Caps.HasFlag(ProviderCapabilities.OutOfOffice);
    public bool SupportsForwardingRules =>
        Caps.HasFlag(ProviderCapabilities.ForwardingRules) && !OrganizationDefaults.Current.DisableForwardingRules;
    public bool HasPreview => Preview != null;
    public bool HasAccounts => _sessions.Count > 0;
    public string FolderTitle => SelectedFolder?.Name ?? "";
    public bool CanModifyFolder => SelectedFolder?.CanModify == true;
    public bool IsMailFolderSelected => SelectedFolder is { IsAccountRoot: false };

    public AccountSession? CurrentSession => SelectedFolder?.Session ?? _sessions.FirstOrDefault();

    private IEnumerable<MessageItemViewModel> Targets =>
        SelectedMessages.Count > 0 ? SelectedMessages.ToList() : SelectedMessage != null ? new[] { SelectedMessage } : Array.Empty<MessageItemViewModel>();

    // ================================================================== startup / accounts

    public void Start()
    {
        foreach (var account in _settings.Accounts.ToList())
        {
            try
            {
                AddSession(account);
            }
            catch (Exception ex)
            {
                Log.Error($"Учётная запись {account.EmailAddress} не открыта", ex);
                // Without a session the account could not be reached from the UI: offer its settings right away.
                if (!Dialogs.Confirm($"Не удалось открыть учётную запись «{account.EmailAddress}».\n\n{RuText.Error(ex)}\n\nОткрыть настройки учётной записи?"))
                    continue;
                var copy = account.Clone();
                if (WindowFactory.EditAccount(copy, _credentials, isNew: false) != true) continue;
                var index = _settings.Accounts.FindIndex(a => a.Id == copy.Id);
                if (index >= 0) _settings.Accounts[index] = copy;
                SettingsStore.Save(_settings);
                try { AddSession(copy); }
                catch (Exception again) { Dialogs.Error(again, "Учётная запись по-прежнему не открывается"); }
            }
        }
        RebuildTree();
        OnPropertyChanged(nameof(HasAccounts));
    }

    private AccountSession AddSession(AccountSettings account)
    {
        var session = new AccountSession(account, _credentials);
        session.Sync.FoldersChanged += (_, _) => _dispatcher.BeginInvoke(RebuildTree);
        session.Sync.FolderContentChanged += (_, folderId) => _dispatcher.BeginInvoke(() => ScheduleReload(folderId));
        session.Sync.NewMessagesArrived += (_, list) => _dispatcher.BeginInvoke(() => NotifyNewMail(session, list));
        session.StatusChanged += (_, _) => _dispatcher.BeginInvoke(UpdateStatus);
        session.AuthenticationFailed += (_, _) => _dispatcher.BeginInvoke(() => OnAuthenticationFailed(session));
        _sessions.Add(session);
        session.Start();
        return session;
    }

    private void UpdateStatus()
    {
        IsOnline = _sessions.All(s => s.IsOnline);
        StatusText = _sessions.Count switch
        {
            0 => "Нет учётных записей",
            1 => _sessions[0].Status,
            _ => string.Join("   ·   ", _sessions.Select(s => $"{s.Settings.EffectiveDisplayName}: {s.Status}")),
        };
    }

    private void OnAuthenticationFailed(AccountSession session)
    {
        if (session.Settings.AuthMethod != AuthMethod.Password) return;
        if (Dialogs.Confirm($"Сервер не принял пароль для «{session.Settings.EmailAddress}».\n\n" +
                            "Возможно, пароль был изменён. Ввести новый пароль?", "Ошибка входа"))
            EditAccount(session);
    }

    [RelayCommand]
    public void AddAccount()
    {
        var account = new AccountSettings();
        if (WindowFactory.EditAccount(account, _credentials, isNew: true) != true) return;
        _settings.Accounts.Add(account);
        SettingsStore.Save(_settings);
        AddSession(account);
        RebuildTree();
        OnPropertyChanged(nameof(HasAccounts));
    }

    [RelayCommand]
    private void EditCurrentAccount()
    {
        if (CurrentSession is { } s) EditAccount(s);
    }

    private void EditAccount(AccountSession session)
    {
        var copy = session.Settings.Clone();
        if (WindowFactory.EditAccount(copy, _credentials, isNew: false, session) != true) return;
        var index = _settings.Accounts.FindIndex(a => a.Id == copy.Id);
        if (index >= 0) _settings.Accounts[index] = copy;
        SettingsStore.Save(_settings);

        // Recreate the session with the new settings (cache is kept).
        var sessionIndex = _sessions.IndexOf(session);
        session.Dispose();
        _sessions.Remove(session);
        var fresh = AddSession(copy);
        _sessions.Remove(fresh);
        _sessions.Insert(Math.Max(0, sessionIndex), fresh);
        RebuildTree();
    }

    [RelayCommand]
    private void RemoveCurrentAccount()
    {
        var session = CurrentSession;
        if (session == null) return;
        if (!Dialogs.Confirm($"Удалить учётную запись «{session.Settings.EmailAddress}» из программы?\n\n" +
                             "Письма на сервере не будут затронуты; будет удалён только локальный кэш и сохранённый пароль."))
            return;
        session.Dispose();
        _sessions.Remove(session);
        _settings.Accounts.RemoveAll(a => a.Id == session.Settings.Id);
        SettingsStore.Save(_settings);
        _credentials.DeletePassword(session.Settings.Id);
        AppPaths.DeleteAccountData(session.Settings.Id);
        SelectedFolder = null;
        Messages.Clear();
        Preview = null;
        RebuildTree();
        OnPropertyChanged(nameof(HasAccounts));
        UpdateStatus();
    }

    // ================================================================== folder tree

    private void RebuildTree()
    {
        var expanded = new HashSet<string>(Roots.SelectMany(r => r.SelfAndDescendants()).Where(n => n.IsExpanded).Select(n => n.Id));
        bool firstBuild = Roots.Count == 0;
        var selected = SelectedFolder;
        var selectedId = selected?.Id;

        _restoringSelection = true;
        try
        {
            Roots.Clear();
            foreach (var session in _sessions)
            {
                var folders = session.Cache.GetFolders();
                var rootFolder = folders.FirstOrDefault(f => f.WellKnown == WellKnownFolder.Root)
                                 ?? new MailFolder { Id = "root:" + session.Settings.Id, DisplayName = session.Settings.EffectiveDisplayName, WellKnown = WellKnownFolder.Root };
                var root = new FolderNodeViewModel(session, rootFolder, isAccountRoot: true) { IsExpanded = true };
                var mailFolders = folders.Where(f => f.WellKnown != WellKnownFolder.Root && f.Kind == FolderKind.Mail).ToList();
                var byParent = mailFolders.ToLookup(f => f.ParentId ?? "");

                void AddChildren(FolderNodeViewModel parent)
                {
                    foreach (var f in byParent[parent.Id].OrderBy(RuText.FolderOrder).ThenBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                    {
                        var node = new FolderNodeViewModel(session, f, false) { Parent = parent, IsExpanded = expanded.Contains(f.Id) };
                        parent.Children.Add(node);
                        AddChildren(node);
                    }
                }
                AddChildren(root);
                Roots.Add(root);
            }

            FolderNodeViewModel? toSelect = null;
            if (selectedId != null) toSelect = Roots.SelectMany(r => r.SelfAndDescendants()).FirstOrDefault(n => n.Id == selectedId);
            // A folder whose id changed (IMAP rename): the same account, parent and name.
            if (toSelect == null && selected is { IsAccountRoot: false })
                toSelect = Roots.Where(r => r.Session == selected.Session).SelectMany(r => r.SelfAndDescendants())
                    .FirstOrDefault(n => !n.IsAccountRoot && n.Name == selected.Name && n.Parent?.Id == selected.Parent?.Id);
            if (toSelect == null && (firstBuild || selectedId == null || selectedId.StartsWith("root:", StringComparison.Ordinal)))
                toSelect = Roots.SelectMany(r => r.SelfAndDescendants()).FirstOrDefault(n => n.Folder.WellKnown == WellKnownFolder.Inbox);
            if (toSelect != null)
            {
                for (var p = toSelect.Parent; p != null; p = p.Parent) p.IsExpanded = true;
                toSelect.IsSelected = true;
                // Same id: only the node object is replaced (no reload, see OnSelectedFolderChanged).
                SelectedFolder = toSelect;
            }
        }
        finally
        {
            _restoringSelection = false;
        }
        UpdateUnreadTotals();
    }

    partial void OnSelectedFolderChanged(FolderNodeViewModel? oldValue, FolderNodeViewModel? newValue)
    {
        if (_restoringSelection && oldValue?.Id == newValue?.Id) return;
        if (oldValue?.Id == newValue?.Id) return;
        _loadedCount = PageSize;
        SearchText = "";
        IsSearchResult = false;
        if (newValue != null) newValue.Session.ActiveFolderId = newValue.IsAccountRoot ? null : newValue.Id;
        _ = LoadFolderAsync(primeFromServer: true);
        if (Section != AppSection.Mail && !SectionSupported(Section)) Section = AppSection.Mail;
        else if (Section != AppSection.Mail && oldValue?.Session != newValue?.Session) _ = LoadSectionAsync();
    }

    private async Task LoadFolderAsync(bool primeFromServer)
    {
        var folder = SelectedFolder;
        if (folder == null || folder.IsAccountRoot)
        {
            Messages.Clear();
            Preview = null;
            ListStatus = folder?.IsAccountRoot == true ? "Выберите папку" : "";
            UpdateTitle();
            return;
        }
        await ReloadMessagesAsync();
        UpdateTitle();

        if (primeFromServer)
        {
            try
            {
                if (Messages.Count == 0) ListStatus = "Загрузка писем…";
                await folder.Session.Sync.PrimeFolderAsync(folder.Id, 100);
                await folder.Session.Sync.SyncFolderAsync(folder.Id);
                // A sync without changes raises no reload: settle the "loading" text for an empty folder here.
                if (SelectedFolder == folder && !IsSearchResult && Messages.Count == 0) ListStatus = "В папке нет писем";
            }
            catch (Exception ex)
            {
                Log.Warn($"Папка {folder.Name} не синхронизирована: {MailClient.Core.Diagnostics.MailLog.Describe(ex)}");
                if (SelectedFolder == folder && Messages.Count == 0) ListStatus = "Нет связи с сервером. " + RuText.Error(ex);
            }
        }
    }

    private void ScheduleReload(string folderId)
    {
        _pendingReloadFolders.Add(folderId);
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    private async void FlushReloads()
    {
        _reloadTimer.Stop();
        var folders = new HashSet<string>(_pendingReloadFolders);
        _pendingReloadFolders.Clear();
        try
        {
            foreach (var root in Roots)
            {
                var counts = root.Session.Cache.GetFolders().ToDictionary(f => f.Id, f => f.UnreadCount);
                foreach (var node in root.SelfAndDescendants().Where(n => folders.Contains(n.Id)))
                    if (counts.TryGetValue(node.Id, out var unread)) node.Unread = unread;
            }
            UpdateUnreadTotals();
            if (SelectedFolder != null && folders.Contains(SelectedFolder.Id) && !IsSearchResult)
                await ReloadMessagesAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось обновить список писем", ex);
        }
    }

    private async Task ReloadMessagesAsync()
    {
        var folder = SelectedFolder;
        if (folder == null || folder.IsAccountRoot) return;
        var count = _loadedCount;
        var list = await Task.Run(() => folder.Session.Cache.GetMessages(folder.Id, 0, count));
        if (SelectedFolder != folder || IsSearchResult) return;
        ApplyMessages(list, folder.ShowsRecipients);
        ListStatus = Messages.Count == 0 ? "В папке нет писем" : "";
    }

    /// <summary>Updates the list in place where possible to keep scroll position and selection.</summary>
    private void ApplyMessages(List<MessageSummary> list, bool showRecipients)
    {
        var selectedId = SelectedMessage?.Id;
        var existing = Messages.ToDictionary(m => m.Id);
        bool sameOrder = list.Count == Messages.Count && list.Select(m => m.Id).SequenceEqual(Messages.Select(m => m.Id));
        if (sameOrder)
        {
            foreach (var s in list) existing[s.Id].Update(s);
            return;
        }

        // Remove vanished items, then insert/move to match the new order.
        var ids = new HashSet<string>(list.Select(m => m.Id));
        for (int i = Messages.Count - 1; i >= 0; i--)
            if (!ids.Contains(Messages[i].Id)) Messages.RemoveAt(i);
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (existing.TryGetValue(s.Id, out var vm) && Messages.Contains(vm))
            {
                vm.Update(s);
                var current = Messages.IndexOf(vm);
                if (current != i) Messages.Move(current, i);
            }
            else
            {
                Messages.Insert(i, new MessageItemViewModel(s, showRecipients));
            }
        }
        if (selectedId != null && SelectedMessage == null)
            SelectedMessage = Messages.FirstOrDefault(m => m.Id == selectedId);
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        var folder = SelectedFolder;
        if (folder == null || IsSearchResult || Messages.Count < _loadedCount) return;
        _loadedCount += PageSize;
        await ReloadMessagesAsync();
    }

    private void UpdateUnreadTotals()
    {
        // The Inbox counter, subfolders included (mail filed there by rules is new mail too).
        var inboxUnread = Roots.SelectMany(r => r.SelfAndDescendants())
            .Where(n => n.Folder.WellKnown == WellKnownFolder.Inbox).Sum(n => n.BadgeCount);
        Tray?.SetUnread(inboxUnread);
        UpdateTitle(inboxUnread);
    }

    private void UpdateTitle(int? inboxUnread = null)
    {
        inboxUnread ??= Roots.SelectMany(r => r.SelfAndDescendants()).Where(n => n.Folder.WellKnown == WellKnownFolder.Inbox).Sum(n => n.BadgeCount);
        var folder = SelectedFolder is { IsAccountRoot: false } f ? f.Name + " — " : "";
        WindowTitle = $"{folder}Корпоративная почта" + (inboxUnread > 0 ? $" ({inboxUnread})" : "");
    }

    private void NotifyNewMail(AccountSession session, IReadOnlyList<MessageSummary> list)
    {
        if (!_settings.ShowNotifications || list.Count == 0 || Tray == null) return;
        var inbox = session.InboxId;
        var inInbox = list.Where(m => m.FolderId == inbox).ToList();
        if (inInbox.Count == 0) return;
        if (Application.Current.MainWindow is { IsActive: true }) return;
        if (inInbox.Count == 1)
        {
            var m = inInbox[0];
            Tray.Notify(m.From?.ShortName ?? "Новое письмо", string.IsNullOrWhiteSpace(m.Subject) ? "(без темы)" : m.Subject);
        }
        else
        {
            Tray.Notify("Новая почта", $"{RuText.Count(inInbox.Count, "новое письмо", "новых письма", "новых писем")} в «{session.Settings.EffectiveDisplayName}»");
        }
    }

    // ================================================================== selection / preview

    partial void OnSelectedMessageChanged(MessageItemViewModel? value)
    {
        _markReadTimer.Stop();
        _previewCts?.Cancel();
        if (value == null)
        {
            Preview = null;
            return;
        }
        _previewCts = new CancellationTokenSource();
        _ = LoadPreviewAsync(value, _previewCts.Token);
    }

    private async Task LoadPreviewAsync(MessageItemViewModel item, CancellationToken ct)
    {
        var session = SelectedFolder?.Session;
        if (session == null) return;
        try
        {
            await Task.Delay(120, ct); // debounce fast keyboard navigation
            IsPreviewLoading = true;
            var preview = await MessagePreviewViewModel.LoadAsync(session, _sessions, _settings, item.Id, ct);
            if (ct.IsCancellationRequested) return;
            Preview = preview;
            if (!item.IsRead && _settings.MarkAsReadDelaySeconds >= 0 && !IsMultiSelection)
            {
                _markReadTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.05, _settings.MarkAsReadDelaySeconds));
                _markReadTimer.Start();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Preview = null;
                ListStatus = "Не удалось открыть письмо: " + RuText.Error(ex);
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsPreviewLoading = false;
        }
    }

    /// <summary>Called by the view whenever the list selection changes.</summary>
    public void SetSelection(IEnumerable<MessageItemViewModel> items)
    {
        SelectedMessages.Clear();
        SelectedMessages.AddRange(items);
        SelectionCount = SelectedMessages.Count;
        // Selecting a range must not mark its first message read.
        if (IsMultiSelection) _markReadTimer.Stop();
    }

    private async Task MarkCurrentAsReadAsync()
    {
        _markReadTimer.Stop();
        if (SelectedMessage is { IsRead: false } m) await SetReadAsync(new[] { m }, true);
    }

    // ================================================================== message actions

    private async Task<bool> RunAsync(string failureText, Func<Task> work)
    {
        try
        {
            await work();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(failureText, ex);
            Dialogs.Error(ex, failureText);
            // Re-sync to restore the true server state after an optimistic UI update.
            CurrentSession?.SyncNow();
            return false;
        }
    }

    private async Task SetReadAsync(IReadOnlyCollection<MessageItemViewModel> items, bool read)
    {
        var folder = SelectedFolder;
        if (folder == null) return;
        var changed = items.Where(i => i.IsRead != read).ToList();
        if (changed.Count == 0) return;
        foreach (var i in changed) i.IsRead = read;
        folder.Unread = Math.Max(0, folder.Unread + (read ? -changed.Count : changed.Count));
        // Without a connection the change waits in the cache and goes to the server with the next synchronization:
        // no error message for every message read offline, and the message does not turn unread again.
        var sent = folder.Session.Sync.SetReadStateAsync(changed.Select(i => i.Id).ToList(), read);
        SettleUnread(folder);
        UpdateUnreadTotals();
        await RunAsync("Не удалось изменить состояние «прочитано»", () => sent);
    }

    /// <summary>
    /// A synchronized folder's counter is the number of unread messages in its cache (what the list shows);
    /// storing it keeps a later folder refresh from bringing back an outdated server counter.
    /// </summary>
    private static void SettleUnread(FolderNodeViewModel folder)
    {
        if (folder.Session.Cache.GetSyncState(folder.Id) == null) return;
        folder.Unread = folder.Session.Cache.RefreshFolderCounts(folder.Id).unread;
    }

    [RelayCommand] private Task MarkRead() => SetReadAsync(Targets.ToList(), true);
    [RelayCommand] private Task MarkUnread() => SetReadAsync(Targets.ToList(), false);

    [RelayCommand]
    private async Task MarkFolderReadAsync()
    {
        var folder = SelectedFolder;
        if (folder == null || folder.IsAccountRoot) return;
        await RunAsync("Не удалось отметить папку как прочитанную", async () =>
        {
            // One server call where supported (Exchange 2013+: MarkAllItemsAsRead).
            List<string> UnreadIds() =>
                folder.Session.Cache.GetMessages(folder.Id, 0, int.MaxValue).Where(m => !m.IsRead).Select(m => m.Id).ToList();
            if (!await folder.Session.Provider.MarkAllReadAsync(folder.Id, true))
            {
                await folder.Session.Sync.SyncFolderAsync(folder.Id);
                var toMark = await Task.Run(UnreadIds);
                if (toMark.Count > 0) await folder.Session.Provider.SetReadStateAsync(toMark, true);
            }
            await Task.Run(() => folder.Session.Cache.SetReadState(UnreadIds(), true));
            folder.Unread = 0;
            SettleUnread(folder);
            foreach (var m in Messages) m.IsRead = true;
            UpdateUnreadTotals();
        });
    }

    [RelayCommand]
    private async Task ToggleFlagAsync()
    {
        var folder = SelectedFolder;
        var items = Targets.ToList();
        if (folder == null || items.Count == 0) return;
        var flag = items.All(i => i.IsFlagged) ? FlagStatus.NotFlagged : FlagStatus.Flagged;
        foreach (var i in items) i.Flag = flag;
        folder.Session.Cache.SetFlag(items.Select(i => i.Id), flag);
        await RunAsync("Не удалось изменить флаг", () => folder.Session.Provider.SetFlagAsync(items.Select(i => i.Id), flag));
    }

    [RelayCommand]
    private async Task MarkFlagCompleteAsync()
    {
        var folder = SelectedFolder;
        var items = Targets.ToList();
        if (folder == null || items.Count == 0) return;
        foreach (var i in items) i.Flag = FlagStatus.Complete;
        folder.Session.Cache.SetFlag(items.Select(i => i.Id), FlagStatus.Complete);
        await RunAsync("Не удалось изменить флаг", () => folder.Session.Provider.SetFlagAsync(items.Select(i => i.Id), FlagStatus.Complete));
    }

    /// <summary>Removes items from the list and selects the next one, so reading can continue.</summary>
    private void RemoveFromList(IReadOnlyCollection<MessageItemViewModel> items)
    {
        var index = items.Select(i => Messages.IndexOf(i)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        var folder = SelectedFolder;
        int removedUnread = items.Count(i => !i.IsRead);
        foreach (var i in items) Messages.Remove(i);
        if (folder != null)
        {
            folder.Unread = Math.Max(0, folder.Unread - removedUnread);
            folder.Session.Cache.DeleteMessages(items.Select(i => i.Id));
            SettleUnread(folder);
        }
        UpdateUnreadTotals();
        if (Messages.Count > 0 && index >= 0) SelectedMessage = Messages[Math.Min(index, Messages.Count - 1)];
        else SelectedMessage = null;
    }

    [RelayCommand]
    private async Task DeleteAsync(object? parameter)
    {
        var folder = SelectedFolder;
        var items = Targets.ToList();
        if (folder == null || items.Count == 0) return;
        bool permanent = parameter is true or "permanent" || folder.Folder.WellKnown == WellKnownFolder.DeletedItems;
        if (permanent && !Dialogs.Confirm($"{RuText.Count(items.Count, "письмо будет удалено", "письма будут удалены", "писем будут удалены")} безвозвратно. Продолжить?"))
            return;
        if (!permanent && _settings.ConfirmDelete && !Dialogs.Confirm($"Удалить {RuText.Count(items.Count, "письмо", "письма", "писем")}?"))
            return;
        RemoveFromList(items);
        await RunAsync("Не удалось удалить письма", () => folder.Session.Provider.DeleteItemsAsync(items.Select(i => i.Id), permanent));
    }

    public bool IsJunkFolder => SelectedFolder?.Folder.WellKnown == WellKnownFolder.JunkEmail;

    /// <summary>"Junk" / "Not junk" (MarkAsJunk on Exchange 2013+, a move on older servers and IMAP).</summary>
    [RelayCommand]
    private async Task MarkAsJunkAsync()
    {
        var folder = SelectedFolder;
        var items = Targets.ToList();
        if (folder == null || items.Count == 0) return;
        bool isJunk = !IsJunkFolder;
        RemoveFromList(items);
        await RunAsync(isJunk ? "Не удалось переместить в нежелательную почту" : "Не удалось вернуть письма во «Входящие»", async () =>
        {
            await folder.Session.Provider.MarkAsJunkAsync(items.Select(i => i.Id), isJunk);
            StatusText = isJunk
                ? $"В нежелательную почту: {RuText.Count(items.Count, "письмо", "письма", "писем")}"
                : $"Во «Входящие»: {RuText.Count(items.Count, "письмо", "письма", "писем")}";
            folder.Session.SyncNow();
        });
    }

    [RelayCommand]
    private async Task MoveToAsync()
    {
        var folder = SelectedFolder;
        var items = Targets.ToList();
        if (folder == null || items.Count == 0) return;
        var target = WindowFactory.PickFolder(Roots.Where(r => r.Session == folder.Session), "Переместить в папку");
        if (target == null || target.Id == folder.Id) return;
        await MoveItemsToAsync(items, folder, target.Id);
    }

    [RelayCommand]
    private async Task CopyToAsync()
    {
        var folder = SelectedFolder;
        var items = Targets.ToList();
        if (folder == null || items.Count == 0) return;
        var target = WindowFactory.PickFolder(Roots.Where(r => r.Session == folder.Session), "Копировать в папку");
        if (target == null) return;
        await RunAsync("Не удалось скопировать письма", async () =>
        {
            await folder.Session.Provider.CopyItemsAsync(items.Select(i => i.Id), target.Id);
            StatusText = $"Скопировано: {RuText.Count(items.Count, "письмо", "письма", "писем")} → «{target.Name}»";
        });
    }

    /// <summary>Called by drag &amp; drop onto a folder in the tree.</summary>
    public async Task DropOnFolderAsync(FolderNodeViewModel target, bool copy)
    {
        var folder = SelectedFolder;
        var items = Targets.ToList();
        if (folder == null || items.Count == 0 || target.IsAccountRoot || target.Id == folder.Id) return;
        if (target.Session != folder.Session)
        {
            Dialogs.Error("Перемещение писем между разными учётными записями не поддерживается. Используйте пересылку.");
            return;
        }
        if (copy)
        {
            await RunAsync("Не удалось скопировать письма", async () =>
            {
                await folder.Session.Provider.CopyItemsAsync(items.Select(i => i.Id), target.Id);
                target.Unread += items.Count(i => !i.IsRead);
                StatusText = $"Скопировано: {RuText.Count(items.Count, "письмо", "письма", "писем")} → «{target.Name}»";
            });
            return;
        }
        await MoveItemsToAsync(items, folder, target.Id);
    }

    private async Task MoveItemsToAsync(List<MessageItemViewModel> items, FolderNodeViewModel from, string targetId)
    {
        if (items.Count == 0) return;
        RemoveFromList(items);
        await RunAsync("Не удалось переместить письма", async () =>
        {
            await from.Session.Provider.MoveItemsAsync(items.Select(i => i.Id), targetId);
            var target = Roots.SelectMany(r => r.SelfAndDescendants()).FirstOrDefault(n => n.Id == targetId);
            if (target != null)
            {
                target.Unread += items.Count(i => !i.IsRead);
                StatusText = $"Перемещено: {RuText.Count(items.Count, "письмо", "письма", "писем")} → «{target.Name}»";
            }
        });
    }

    // ================================================================== compose

    [RelayCommand]
    public void NewMessage()
    {
        if (CurrentSession is not { } s)
        {
            AddAccount();
            return;
        }
        WindowFactory.OpenCompose(ComposeViewModel.New(_sessions, s), _settings);
    }

    public void ComposeMailto(string mailto)
    {
        if (CurrentSession is { } s) WindowFactory.OpenCompose(ComposeViewModel.FromMailto(_sessions, s, mailto), _settings);
    }

    private void WriteTo(EmailAddress address)
    {
        if (CurrentSession is not { } s) return;
        var vm = ComposeViewModel.New(_sessions, s);
        vm.To = EmailAddress.FormatList(new[] { address });
        vm.IsDirty = false;
        WindowFactory.OpenCompose(vm, _settings);
    }

    private async Task RespondAsync(ComposeAction action)
    {
        var session = SelectedFolder?.Session;
        var item = SelectedMessage;
        if (session == null || item == null || IsMultiSelection) return;
        try
        {
            var message = Preview?.Message.Id == item.Id ? Preview.Message : await session.Sync.GetMessageAsync(item.Id);
            WindowFactory.OpenCompose(ComposeViewModel.ForResponse(_sessions, session, message, action), _settings);
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось открыть письмо");
        }
    }

    [RelayCommand] private Task Reply() => RespondAsync(ComposeAction.Reply);
    [RelayCommand] private Task ReplyAll() => RespondAsync(ComposeAction.ReplyAll);
    [RelayCommand] private Task Forward() => RespondAsync(ComposeAction.Forward);

    /// <summary>Double-click: drafts open in the editor, other messages in a separate window.</summary>
    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        var folder = SelectedFolder;
        var item = SelectedMessage;
        if (folder == null || item == null) return;
        try
        {
            if (folder.Folder.WellKnown == WellKnownFolder.Drafts)
            {
                WindowFactory.OpenCompose(await ComposeViewModel.FromDraftAsync(_sessions, folder.Session, item.Id), _settings);
                return;
            }
            var preview = await MessagePreviewViewModel.LoadAsync(folder.Session, _sessions, _settings, item.Id, CancellationToken.None);
            WindowFactory.OpenMessage(preview, this);
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось открыть письмо");
        }
    }

    // ================================================================== search / refresh

    [RelayCommand]
    private async Task SearchAsync()
    {
        var folder = SelectedFolder;
        var query = SearchText.Trim();
        if (folder == null || folder.IsAccountRoot) return;
        if (query.Length == 0)
        {
            await ClearSearchAsync();
            return;
        }
        IsSearchResult = true;
        ListStatus = "Поиск…";
        Messages.Clear();
        try
        {
            var page = await folder.Session.Provider.SearchMessagesAsync(folder.Id, query, 0, 250);
            if (SelectedFolder != folder || !IsSearchResult) return;
            foreach (var m in page.Items) Messages.Add(new MessageItemViewModel(m, folder.ShowsRecipients));
            ListStatus = page.Items.Count == 0 ? $"По запросу «{query}» ничего не найдено" : "";
            StatusText = $"Найдено: {RuText.Count(page.TotalCount, "письмо", "письма", "писем")}";
        }
        catch (Exception ex)
        {
            // Offline: search the local cache instead.
            Log.Warn($"Поиск на сервере не выполнен, используется локальный кэш: {ex.Message}");
            var local = folder.Session.Cache.GetMessages(folder.Id, 0, 500, query);
            foreach (var m in local) Messages.Add(new MessageItemViewModel(m, folder.ShowsRecipients));
            ListStatus = local.Count == 0 ? $"По запросу «{query}» ничего не найдено (поиск в автономном режиме)" : "";
            StatusText = "Поиск выполнен по локальной копии (нет связи с сервером)";
        }
    }

    [RelayCommand]
    private async Task ClearSearchAsync()
    {
        SearchText = "";
        if (!IsSearchResult) return;
        IsSearchResult = false;
        Messages.Clear();
        await ReloadMessagesAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        foreach (var s in _sessions) s.SyncNow();
        switch (Section)
        {
            case AppSection.Mail:
                if (SelectedFolder is { IsAccountRoot: false } f)
                    await RunAsync("Не удалось обновить папку", () => f.Session.Sync.SyncFolderAsync(f.Id));
                break;
            default:
                await LoadSectionAsync();
                break;
        }
    }

    private bool SectionSupported(AppSection s) => s != AppSection.Contacts || SupportsContacts;

    partial void OnSectionChanged(AppSection value) => _ = LoadSectionAsync();

    private Task LoadSectionAsync() => Section == AppSection.Contacts ? Contacts.LoadAsync() : Task.CompletedTask;

    // ================================================================== folder management

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        var parent = SelectedFolder;
        if (parent == null) return;
        var name = WindowFactory.Prompt("Новая папка", $"Имя новой папки в «{parent.Name}»:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync("Не удалось создать папку", async () =>
        {
            await parent.Session.Provider.CreateFolderAsync(parent.Id, name.Trim());
            parent.IsExpanded = true;
            await parent.Session.Sync.SyncFoldersAsync();
        });
    }

    [RelayCommand]
    private async Task RenameFolderAsync()
    {
        var folder = SelectedFolder;
        if (folder is not { CanModify: true }) return;
        var name = WindowFactory.Prompt("Переименование папки", "Новое имя папки:", folder.Name)?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name == folder.Name) return;
        await RunAsync("Не удалось переименовать папку", async () =>
        {
            await folder.Session.Provider.RenameFolderAsync(folder.Id, name);
            // Shown at once; the refresh below brings the server's view (on IMAP the folder also gets a new id,
            // and the tree keeps it selected by its new name).
            folder.Rename(name);
            folder.Session.Cache.RenameFolder(folder.Id, name);
            OnPropertyChanged(nameof(FolderTitle));
            UpdateTitle();
            StatusText = $"Папка переименована: «{name}»";
            await folder.Session.Sync.SyncFoldersAsync();
        });
    }

    [RelayCommand]
    private async Task MoveFolderAsync()
    {
        var folder = SelectedFolder;
        if (folder is not { CanModify: true }) return;
        var target = WindowFactory.PickFolder(Roots.Where(r => r.Session == folder.Session), $"Переместить папку «{folder.Name}» в", allowRoot: true);
        // A folder cannot be moved into itself or one of its subfolders.
        if (target == null || folder.SelfAndDescendants().Any(n => n.Id == target.Id) || target.Id == folder.Parent?.Id)
            return;
        await RunAsync("Не удалось переместить папку", async () =>
        {
            await folder.Session.Provider.MoveFolderAsync(folder.Id, target.Id);
            await folder.Session.Sync.SyncFoldersAsync();
        });
    }

    [RelayCommand]
    private async Task DeleteFolderAsync()
    {
        var folder = SelectedFolder;
        if (folder is not { CanModify: true }) return;
        if (!Dialogs.Confirm($"Удалить папку «{folder.Name}» вместе со всем содержимым?\nПапка будет перемещена в «Удалённые».")) return;
        await RunAsync("Не удалось удалить папку", async () =>
        {
            await folder.Session.Provider.DeleteFolderAsync(folder.Id, permanent: false);
            SelectedFolder = folder.Parent;
            await folder.Session.Sync.SyncFoldersAsync();
        });
    }

    [RelayCommand]
    private async Task EmptyFolderAsync()
    {
        var folder = SelectedFolder;
        if (folder == null || folder.IsAccountRoot) return;
        var isTrash = folder.Folder.WellKnown is WellKnownFolder.DeletedItems or WellKnownFolder.JunkEmail;
        var text = isTrash
            ? $"Очистить папку «{folder.Name}»? Все письма будут удалены безвозвратно."
            : $"Очистить папку «{folder.Name}»? Все письма будут перемещены в «Удалённые».";
        if (!Dialogs.Confirm(text)) return;
        await RunAsync("Не удалось очистить папку", async () =>
        {
            await folder.Session.Provider.EmptyFolderAsync(folder.Id, deleteSubFolders: false);
            folder.Session.Cache.ClearFolderMessages(folder.Id);
            folder.Unread = 0;
            Messages.Clear();
            Preview = null;
            UpdateUnreadTotals();
            folder.Session.SyncNow();
        });
    }

    // ================================================================== import / export

    [RelayCommand]
    private async Task SaveAsEmlAsync()
    {
        var session = SelectedFolder?.Session;
        var items = Targets.ToList();
        if (session == null || items.Count == 0) return;
        try
        {
            if (items.Count == 1)
            {
                var dlg = new SaveFileDialog
                {
                    Title = "Сохранить письмо",
                    FileName = MessagePreviewViewModel.SafeFileName(items[0].Subject) + ".eml",
                    Filter = "Письмо (*.eml)|*.eml",
                };
                if (dlg.ShowDialog() != true) return;
                await File.WriteAllBytesAsync(dlg.FileName, await session.Provider.GetMimeContentAsync(items[0].Id));
            }
            else
            {
                var dlg = new OpenFolderDialog { Title = "Папка для сохранения писем" };
                if (dlg.ShowDialog() != true) return;
                int n = 0;
                foreach (var item in items)
                {
                    var name = $"{item.Summary.DateReceived.ToLocalTime():yyyy-MM-dd HHmm} {MessagePreviewViewModel.SafeFileName(item.Subject)}";
                    if (name.Length > 120) name = name[..120];
                    await File.WriteAllBytesAsync(Path.Combine(dlg.FolderName, $"{name} ({++n}).eml"), await session.Provider.GetMimeContentAsync(item.Id));
                }
                StatusText = $"Сохранено: {RuText.Count(items.Count, "письмо", "письма", "писем")}";
            }
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось сохранить письмо");
        }
    }

    [RelayCommand]
    private async Task ImportEmlAsync()
    {
        var folder = SelectedFolder;
        if (folder == null || folder.IsAccountRoot) return;
        var dlg = new OpenFileDialog { Title = "Импорт писем", Filter = "Письма (*.eml)|*.eml", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        int ok = 0;
        var failed = new List<string>();
        foreach (var file in dlg.FileNames)
        {
            try
            {
                await folder.Session.Provider.ImportMimeAsync(folder.Id, await File.ReadAllBytesAsync(file));
                ok++;
            }
            catch (Exception ex)
            {
                Log.Warn($"Импорт «{file}» не выполнен: {ex.Message}");
                failed.Add($"• {Path.GetFileName(file)}: {RuText.Error(ex)}");
            }
        }
        StatusText = $"Импортировано: {RuText.Count(ok, "письмо", "письма", "писем")}";
        if (failed.Count > 0)
            Dialogs.Error($"Не удалось импортировать {RuText.Count(failed.Count, "файл", "файла", "файлов")}:\n\n" +
                          string.Join("\n", failed.Take(10)) + (failed.Count > 10 ? "\n…" : ""));
        folder.Session.SyncNow();
    }

    // ================================================================== dialogs

    /// <summary>The signature of the current account; takes effect in the next message, no reconnection needed.</summary>
    [RelayCommand]
    private void EditSignature()
    {
        if (CurrentSession is not { } s)
        {
            AddAccount();
            return;
        }
        var copy = s.Settings.Clone();
        if (WindowFactory.EditSignature(copy, s) != true) return;
        foreach (var account in _settings.Accounts.Where(a => a.Id == copy.Id).Append(s.Settings).Distinct())
            account.CopySignatureFrom(copy);
        SettingsStore.Save(_settings);
        StatusText = "Подпись сохранена";
    }

    [RelayCommand]
    private void OpenOutOfOffice()
    {
        if (CurrentSession is not { } s) return;
        if (!SupportsOutOfOffice)
        {
            Dialogs.Info("Автоответы настраиваются только для учётных записей Microsoft Exchange. " +
                         "Для Яндекс 360 и Mail.ru включите автоответ в веб-интерфейсе почты.");
            return;
        }
        WindowFactory.OutOfOffice(s);
    }

    [RelayCommand]
    private void OpenForwardingRules()
    {
        if (CurrentSession is not { } s) return;
        if (OrganizationDefaults.Current.DisableForwardingRules)
        {
            Dialogs.Info("Правила пересылки отключены администратором организации.");
            return;
        }
        if (!SupportsForwardingRules)
        {
            Dialogs.Info("Для этой учётной записи пересылка настраивается в веб-интерфейсе почты.");
            return;
        }
        WindowFactory.ForwardingRules(s);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (WindowFactory.EditSettings(_settings) != true) return;
        SettingsStore.Save(_settings);
        ThemeService.Apply(_settings.Theme);
        DesktopIntegration.SetAutostart(_settings.StartWithWindows);
        if (Preview != null && SelectedMessage != null) OnSelectedMessageChanged(SelectedMessage);
    }

    [RelayCommand]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Commands are generated for instance methods")]
    private void OpenLogs() => DesktopIntegration.ShellOpen(AppPaths.Logs);

    /// <summary>ZIP with logs, settings without secrets and system data for the IT department.</summary>
    [RelayCommand]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Commands are generated for instance methods")]
    private void SaveSupportBundle()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Отчёт для техподдержки",
            FileName = $"mailclient-report-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            Filter = "Архив ZIP (*.zip)|*.zip",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            SupportBundle.Create(dlg.FileName);
            Dialogs.Info($"Отчёт сохранён:\n{dlg.FileName}\n\nОн содержит журналы работы и настройки программы без паролей. Передайте файл в техподдержку.");
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось сохранить отчёт");
        }
    }

    [RelayCommand]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Commands are generated for instance methods")]
    private void ShowAbout() => WindowFactory.About();

    public void Dispose()
    {
        _reloadTimer.Stop();
        _markReadTimer.Stop();
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        foreach (var s in _sessions) s.Dispose();
        _sessions.Clear();
    }
}
