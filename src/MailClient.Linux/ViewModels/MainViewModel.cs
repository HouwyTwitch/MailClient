using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailClient.App.Services;
using MailClient.App.ViewModels;
using MailClient.App.Views;
using MailClient.Core.Models;

namespace MailClient.Linux.ViewModels;

/// <summary>
/// Main window of the Linux client: accounts, folder tree, message list and reading pane. The mail logic is the
/// same as on Windows (AccountSession, SyncEngine, LocalCache and the shared view models).
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 200;

    private readonly AppSettings _settings;
    private readonly CredentialProvider _credentials;
    private readonly List<AccountSession> _sessions = new();
    private readonly HashSet<string> _pendingReloadFolders = new();
    private readonly DispatcherTimer _reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _markReadTimer = new();
    private CancellationTokenSource? _previewCts;
    private bool _restoringSelection;
    private int _loadedCount = PageSize;

    public MainViewModel(AppSettings settings, CredentialProvider credentials)
    {
        _settings = settings;
        _credentials = credentials;
        _reloadTimer.Tick += (_, _) => FlushReloads();
        _markReadTimer.Tick += async (_, _) => await MarkCurrentAsReadAsync();
    }

    public AppSettings Settings => _settings;
    public IReadOnlyList<AccountSession> Sessions => _sessions;
    public ObservableCollection<FolderNodeViewModel> Roots { get; } = new();
    public ObservableCollection<MessageItemViewModel> Messages { get; } = new();

    /// <summary>Multi-selection, maintained by the view.</summary>
    public List<MessageItemViewModel> SelectedMessages { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderTitle), nameof(CanModifyFolder))]
    private FolderNodeViewModel? _selectedFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private MessageItemViewModel? _selectedMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private MessagePreviewViewModel? _preview;

    [ObservableProperty] private string _statusText = "Готово";
    [ObservableProperty] private string _listStatus = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isSearchResult;
    [ObservableProperty] private string _windowTitle = "Корпоративная почта";

    public bool HasSelection => SelectedMessage != null;
    public bool HasPreview => Preview != null;
    public bool HasAccounts => _sessions.Count > 0;
    public string FolderTitle => SelectedFolder?.Name ?? "";
    public bool CanModifyFolder => SelectedFolder?.CanModify == true;
    public AccountSession? CurrentSession => SelectedFolder?.Session ?? _sessions.FirstOrDefault();

    private List<MessageItemViewModel> Targets =>
        SelectedMessages.Count > 0 ? SelectedMessages.ToList() : SelectedMessage != null ? [SelectedMessage] : [];

    // ================================================================== accounts

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
                Dialogs.Error(ex, $"Не удалось открыть учётную запись «{account.EmailAddress}»");
            }
        }
        RebuildTree();
        OnPropertyChanged(nameof(HasAccounts));
        UpdateStatus();
        if (_sessions.Count == 0 && !App.IsSmokeTest) Dispatcher.UIThread.Post(AddAccount, DispatcherPriority.Background);
    }

    private AccountSession AddSession(AccountSettings account)
    {
        var session = new AccountSession(account, _credentials);
        session.Sync.FoldersChanged += (_, _) => Dispatcher.UIThread.Post(RebuildTree);
        session.Sync.FolderContentChanged += (_, folderId) => Dispatcher.UIThread.Post(() => ScheduleReload(folderId));
        session.Sync.NewMessagesArrived += (_, list) => Dispatcher.UIThread.Post(() => NotifyNewMail(session, list));
        session.StatusChanged += (_, _) => Dispatcher.UIThread.Post(UpdateStatus);
        session.AuthenticationFailed += (_, _) => Dispatcher.UIThread.Post(() => OnAuthenticationFailed(session));
        _sessions.Add(session);
        session.Start();
        return session;
    }

    private void UpdateStatus() =>
        StatusText = _sessions.Count switch
        {
            0 => "Нет учётных записей",
            1 => _sessions[0].Status,
            _ => string.Join("   ·   ", _sessions.Select(s => $"{s.Settings.EffectiveDisplayName}: {s.Status}")),
        };

    private void OnAuthenticationFailed(AccountSession session)
    {
        if (session.Settings.AuthMethod != AuthMethod.Password) return;
        if (Dialogs.Confirm($"Сервер не принял пароль для «{session.Settings.EmailAddress}».\n\nВозможно, пароль был изменён. Ввести новый пароль?", "Ошибка входа"))
            EditAccount(session);
    }

    [RelayCommand]
    public void AddAccount()
    {
        var account = new AccountSettings();
        OrganizationDefaults.Current.ApplyTo(account);
        if (!Views.AccountWindow.Edit(account, _credentials, isNew: true)) return;
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
        if (!Views.AccountWindow.Edit(copy, _credentials, isNew: false)) return;
        var index = _settings.Accounts.FindIndex(a => a.Id == copy.Id);
        if (index >= 0) _settings.Accounts[index] = copy;
        SettingsStore.Save(_settings);
        var position = _sessions.IndexOf(session);
        session.Dispose();
        _sessions.Remove(session);
        var fresh = AddSession(copy);
        _sessions.Remove(fresh);
        _sessions.Insert(Math.Max(0, position), fresh);
        RebuildTree();
    }

    [RelayCommand]
    private void RemoveCurrentAccount()
    {
        if (CurrentSession is not { } session) return;
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
                var byParent = folders.Where(f => f.WellKnown != WellKnownFolder.Root && f.Kind == FolderKind.Mail).ToLookup(f => f.ParentId ?? "");

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

            var all = Roots.SelectMany(r => r.SelfAndDescendants()).ToList();
            var toSelect = selected == null ? null : all.FirstOrDefault(n => n.Id == selected.Id);
            // A folder whose id changed (IMAP rename): the same account, parent and name.
            if (toSelect == null && selected is { IsAccountRoot: false })
                toSelect = all.FirstOrDefault(n => n.Session == selected.Session && !n.IsAccountRoot && n.Name == selected.Name && n.Parent?.Id == selected.Parent?.Id);
            if (toSelect == null && (firstBuild || selected == null || selected.IsAccountRoot))
                toSelect = all.FirstOrDefault(n => n.Folder.WellKnown == WellKnownFolder.Inbox);
            if (toSelect != null)
            {
                for (var p = toSelect.Parent; p != null; p = p.Parent) p.IsExpanded = true;
                toSelect.IsSelected = true;
                SelectedFolder = toSelect;
            }
        }
        finally
        {
            _restoringSelection = false;
        }
        UpdateTitle();
    }

    partial void OnSelectedFolderChanged(FolderNodeViewModel? oldValue, FolderNodeViewModel? newValue)
    {
        if (oldValue?.Id == newValue?.Id) return;
        _loadedCount = PageSize;
        SearchText = "";
        IsSearchResult = false;
        if (newValue != null) newValue.Session.ActiveFolderId = newValue.IsAccountRoot ? null : newValue.Id;
        if (!_restoringSelection || oldValue?.Id != newValue?.Id) _ = LoadFolderAsync(primeFromServer: true);
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
        if (!primeFromServer) return;
        try
        {
            if (Messages.Count == 0) ListStatus = "Загрузка писем…";
            await folder.Session.Sync.PrimeFolderAsync(folder.Id, 100);
            await folder.Session.Sync.SyncFolderAsync(folder.Id);
            if (SelectedFolder == folder && !IsSearchResult && Messages.Count == 0) ListStatus = "В папке нет писем";
        }
        catch (Exception ex)
        {
            Log.Warn($"Папка {folder.Name} не синхронизирована: {Core.Diagnostics.MailLog.Describe(ex)}");
            if (SelectedFolder == folder && Messages.Count == 0) ListStatus = "Нет связи с сервером. " + RuText.Error(ex);
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
            UpdateTitle();
            if (SelectedFolder != null && folders.Contains(SelectedFolder.Id) && !IsSearchResult) await ReloadMessagesAsync();
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

    /// <summary>Updates the list in place to keep the scroll position and the selection.</summary>
    private void ApplyMessages(List<MessageSummary> list, bool showRecipients)
    {
        var selectedId = SelectedMessage?.Id;
        var existing = Messages.ToDictionary(m => m.Id);
        if (list.Count == Messages.Count && list.Select(m => m.Id).SequenceEqual(Messages.Select(m => m.Id)))
        {
            foreach (var s in list) existing[s.Id].Update(s);
            return;
        }
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
        if (selectedId != null && SelectedMessage == null) SelectedMessage = Messages.FirstOrDefault(m => m.Id == selectedId);
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (SelectedFolder == null || IsSearchResult || Messages.Count < _loadedCount) return;
        _loadedCount += PageSize;
        await ReloadMessagesAsync();
    }

    private void UpdateTitle()
    {
        var inboxUnread = Roots.SelectMany(r => r.SelfAndDescendants()).Where(n => n.Folder.WellKnown == WellKnownFolder.Inbox).Sum(n => n.Unread);
        var folder = SelectedFolder is { IsAccountRoot: false } f ? f.Name + " — " : "";
        WindowTitle = $"{folder}Корпоративная почта" + (inboxUnread > 0 ? $" ({inboxUnread})" : "");
    }

    // ================================================================== selection / preview

    /// <summary>Called by the view whenever the list selection changes.</summary>
    public void SetSelection(IEnumerable<MessageItemViewModel> items)
    {
        SelectedMessages.Clear();
        SelectedMessages.AddRange(items);
        if (SelectedMessages.Count > 1) _markReadTimer.Stop();
    }

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
            await Task.Delay(120, ct);
            var preview = await MessagePreviewViewModel.LoadAsync(session, _sessions, _settings, item.Id, ct);
            if (ct.IsCancellationRequested) return;
            Preview = preview;
            if (!item.IsRead && _settings.MarkAsReadDelaySeconds >= 0 && SelectedMessages.Count <= 1)
            {
                _markReadTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.05, _settings.MarkAsReadDelaySeconds));
                _markReadTimer.Start();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Preview = null;
                ListStatus = "Не удалось открыть письмо: " + RuText.Error(ex);
            }
        }
    }

    private async Task MarkCurrentAsReadAsync()
    {
        _markReadTimer.Stop();
        if (SelectedMessage is { IsRead: false } m) await SetReadAsync([m], true);
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
            CurrentSession?.SyncNow();
            return false;
        }
    }

    /// <summary>A synchronized folder's counter is the number of unread messages in its cache.</summary>
    private static void SettleUnread(FolderNodeViewModel folder)
    {
        if (folder.Session.Cache.GetSyncState(folder.Id) == null) return;
        folder.Unread = folder.Session.Cache.RefreshFolderCounts(folder.Id).unread;
    }

    private async Task SetReadAsync(IReadOnlyCollection<MessageItemViewModel> items, bool read)
    {
        var folder = SelectedFolder;
        if (folder == null) return;
        var changed = items.Where(i => i.IsRead != read).ToList();
        if (changed.Count == 0) return;
        foreach (var i in changed) i.IsRead = read;
        folder.Unread = Math.Max(0, folder.Unread + (read ? -changed.Count : changed.Count));
        folder.Session.Cache.SetReadState(changed.Select(i => i.Id), read);
        SettleUnread(folder);
        UpdateTitle();
        await RunAsync("Не удалось изменить состояние «прочитано»",
            () => folder.Session.Provider.SetReadStateAsync(changed.Select(i => i.Id), read));
    }

    [RelayCommand] private Task MarkRead() => SetReadAsync(Targets, true);
    [RelayCommand] private Task MarkUnread() => SetReadAsync(Targets, false);

    [RelayCommand]
    private async Task MarkFolderReadAsync()
    {
        var folder = SelectedFolder;
        if (folder == null || folder.IsAccountRoot) return;
        await RunAsync("Не удалось отметить папку как прочитанную", async () =>
        {
            List<string> UnreadIds() => folder.Session.Cache.GetMessages(folder.Id, 0, int.MaxValue).Where(m => !m.IsRead).Select(m => m.Id).ToList();
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
            UpdateTitle();
        });
    }

    [RelayCommand]
    private async Task ToggleFlagAsync()
    {
        var folder = SelectedFolder;
        var items = Targets;
        if (folder == null || items.Count == 0) return;
        var flag = items.All(i => i.IsFlagged) ? FlagStatus.NotFlagged : FlagStatus.Flagged;
        foreach (var i in items) i.Flag = flag;
        folder.Session.Cache.SetFlag(items.Select(i => i.Id), flag);
        await RunAsync("Не удалось изменить флажок", () => folder.Session.Provider.SetFlagAsync(items.Select(i => i.Id), flag));
    }

    private void RemoveFromList(IReadOnlyCollection<MessageItemViewModel> items)
    {
        var index = items.Select(i => Messages.IndexOf(i)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        var folder = SelectedFolder;
        foreach (var i in items) Messages.Remove(i);
        if (folder != null)
        {
            folder.Unread = Math.Max(0, folder.Unread - items.Count(i => !i.IsRead));
            folder.Session.Cache.DeleteMessages(items.Select(i => i.Id));
            SettleUnread(folder);
        }
        UpdateTitle();
        SelectedMessage = Messages.Count > 0 && index >= 0 ? Messages[Math.Min(index, Messages.Count - 1)] : null;
    }

    [RelayCommand]
    private async Task DeleteAsync(string? mode)
    {
        var folder = SelectedFolder;
        var items = Targets;
        if (folder == null || items.Count == 0) return;
        bool permanent = mode == "permanent" || folder.Folder.WellKnown == WellKnownFolder.DeletedItems;
        if (permanent && !Dialogs.Confirm($"Удалить безвозвратно {RuText.Count(items.Count, "письмо", "письма", "писем")}?")) return;
        RemoveFromList(items);
        await RunAsync("Не удалось удалить письма", () => folder.Session.Provider.DeleteItemsAsync(items.Select(i => i.Id), permanent));
    }

    [RelayCommand]
    private async Task MoveToAsync()
    {
        var folder = SelectedFolder;
        var items = Targets;
        if (folder == null || items.Count == 0) return;
        var target = Views.FolderPickerWindow.Pick(Roots.Where(r => r.Session == folder.Session), "Переместить в папку");
        if (target == null || target.Id == folder.Id) return;
        await MoveItemsToAsync(items, folder, target);
    }

    /// <summary>Drag &amp; drop onto a folder in the tree.</summary>
    public async Task DropOnFolderAsync(FolderNodeViewModel target)
    {
        var folder = SelectedFolder;
        var items = Targets;
        if (folder == null || items.Count == 0 || target.IsAccountRoot || target.Id == folder.Id) return;
        if (target.Session != folder.Session)
        {
            Dialogs.Error("Перемещение писем между разными учётными записями не поддерживается. Используйте пересылку.");
            return;
        }
        await MoveItemsToAsync(items, folder, target);
    }

    private async Task MoveItemsToAsync(List<MessageItemViewModel> items, FolderNodeViewModel from, FolderNodeViewModel target)
    {
        RemoveFromList(items);
        await RunAsync("Не удалось переместить письма", async () =>
        {
            await from.Session.Provider.MoveItemsAsync(items.Select(i => i.Id), target.Id);
            target.Unread += items.Count(i => !i.IsRead);
            StatusText = $"Перемещено: {RuText.Count(items.Count, "письмо", "письма", "писем")} → «{target.Name}»";
        });
    }

    // ================================================================== compose

    [RelayCommand]
    private void NewMessage()
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

    private async Task RespondAsync(ComposeAction action)
    {
        var session = SelectedFolder?.Session;
        var item = SelectedMessage;
        if (session == null || item == null || SelectedMessages.Count > 1) return;
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

    /// <summary>Double-click: a draft opens for editing.</summary>
    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        var folder = SelectedFolder;
        var item = SelectedMessage;
        if (folder == null || item == null || folder.Folder.WellKnown != WellKnownFolder.Drafts) return;
        try
        {
            WindowFactory.OpenCompose(await ComposeViewModel.FromDraftAsync(_sessions, folder.Session, item.Id), _settings);
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось открыть черновик");
        }
    }

    // ================================================================== search / refresh

    [RelayCommand]
    private async Task SearchAsync()
    {
        var folder = SelectedFolder;
        if (folder == null || folder.IsAccountRoot) return;
        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            IsSearchResult = false;
            await ReloadMessagesAsync();
            return;
        }
        IsSearchResult = true;
        ListStatus = "Поиск…";
        try
        {
            var page = await folder.Session.Provider.SearchMessagesAsync(folder.Id, query, 0, 200);
            foreach (var m in page.Items) m.FolderId = folder.Id;
            Messages.Clear();
            foreach (var m in page.Items) Messages.Add(new MessageItemViewModel(m, folder.ShowsRecipients));
            ListStatus = Messages.Count == 0 ? "Ничего не найдено" : "";
        }
        catch (Exception ex)
        {
            // Offline: search the local copy.
            Log.Warn($"Поиск на сервере не выполнен: {ex.Message}");
            var local = await Task.Run(() => folder.Session.Cache.GetMessages(folder.Id, 0, 500, query));
            Messages.Clear();
            foreach (var m in local) Messages.Add(new MessageItemViewModel(m, folder.ShowsRecipients));
            ListStatus = Messages.Count == 0 ? "Ничего не найдено (поиск по сохранённым письмам)" : "";
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        foreach (var s in _sessions) s.SyncNow();
        StatusText = "Синхронизация…";
    }

    // ================================================================== folders

    [RelayCommand]
    private async Task CreateFolderAsync()
    {
        var parent = SelectedFolder;
        if (parent == null) return;
        var name = Dialogs.Prompt("Новая папка", $"Имя папки в «{parent.Name}»:", "")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync("Не удалось создать папку", async () =>
        {
            await parent.Session.Provider.CreateFolderAsync(parent.Id, name);
            await parent.Session.Sync.SyncFoldersAsync();
        });
    }

    [RelayCommand]
    private async Task RenameFolderAsync()
    {
        var folder = SelectedFolder;
        if (folder is not { CanModify: true }) return;
        var name = Dialogs.Prompt("Переименование папки", "Новое имя папки:", folder.Name)?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name == folder.Name) return;
        await RunAsync("Не удалось переименовать папку", async () =>
        {
            await folder.Session.Provider.RenameFolderAsync(folder.Id, name);
            folder.Rename(name);
            folder.Session.Cache.RenameFolder(folder.Id, name);
            OnPropertyChanged(nameof(FolderTitle));
            UpdateTitle();
            await folder.Session.Sync.SyncFoldersAsync();
        });
    }

    [RelayCommand]
    private async Task DeleteFolderAsync()
    {
        var folder = SelectedFolder;
        if (folder is not { CanModify: true }) return;
        if (!Dialogs.Confirm($"Удалить папку «{folder.Name}» вместе с письмами?")) return;
        await RunAsync("Не удалось удалить папку", async () =>
        {
            await folder.Session.Provider.DeleteFolderAsync(folder.Id, permanent: false);
            SelectedFolder = null;
            await folder.Session.Sync.SyncFoldersAsync();
        });
    }

    // ================================================================== misc

    private void NotifyNewMail(AccountSession session, IReadOnlyList<MessageSummary> list)
    {
        if (!_settings.ShowNotifications || list.Count == 0) return;
        var inInbox = list.Where(m => m.FolderId == session.InboxId).ToList();
        if (inInbox.Count == 0) return;
        var (title, body) = inInbox.Count == 1
            ? (inInbox[0].From?.ShortName ?? "Новое письмо", string.IsNullOrWhiteSpace(inInbox[0].Subject) ? "(без темы)" : inInbox[0].Subject)
            : ("Новая почта", $"{RuText.Count(inInbox.Count, "новое письмо", "новых письма", "новых писем")} в «{session.Settings.EffectiveDisplayName}»");
        try
        {
            // Desktop notification through libnotify (notify-send), shown by Fly and other desktops.
            var start = new ProcessStartInfo("notify-send") { UseShellExecute = false };
            foreach (var a in new[] { "--app-name=Корпоративная почта", "--icon=mailclient", title, body }) start.ArgumentList.Add(a);
            using var p = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Info($"Уведомление не показано (нет notify-send): {ex.Message}");
        }
    }

    [RelayCommand]
    private static void ShowAbout() =>
        Dialogs.Info($"Корпоративная почта для Linux\nВерсия {AppInfo.Version}\n\nMicrosoft Exchange (EWS) и IMAP/SMTP.\n" +
                     $"Настройки: {AppPaths.Roaming}\nКэш и журналы: {AppPaths.Local}\n\n" +
                     $"Просмотр HTML-писем: {(Controls.WebKit.IsAvailable ? "WebKitGTK" : "текстовый режим (WebKitGTK не установлен)")}",
            "О программе");

    [RelayCommand]
    private static void OpenLogs() => DesktopIntegration.ShellOpen(AppPaths.Logs);

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
