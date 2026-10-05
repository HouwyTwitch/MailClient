using MailClient.Core.Models;
using MailClient.Core.Storage;

namespace MailClient.Core.Services;

/// <summary>
/// Keeps the local cache in step with the server using incremental (sync-state based) synchronization,
/// in the spirit of Evolution's EWS backend: the folder tree is refreshed, and each folder is synced with
/// SyncFolderItems so only changes travel over the wire.
/// </summary>
public sealed class SyncEngine
{
    private readonly IMailProvider _provider;
    private readonly LocalCache _cache;
    /// <summary>One lock per folder: syncing a large Inbox must not block opening another folder.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public SyncEngine(IMailProvider provider, LocalCache cache)
    {
        _provider = provider;
        _cache = cache;
    }

    public LocalCache Cache => _cache;
    public IMailProvider Provider => _provider;

    /// <summary>Raised after the folder tree was refreshed from the server.</summary>
    public event EventHandler? FoldersChanged;
    /// <summary>Raised with the folder id after the folder's cached messages changed.</summary>
    public event EventHandler<string>? FolderContentChanged;
    /// <summary>Raised after each batch: folder id and number of messages processed so far in this sync.</summary>
    public event EventHandler<(string folderId, int processed)>? SyncProgress;

    /// <summary>Raised for unread messages that arrived since the previous sync (not on the initial sync).</summary>
    public event EventHandler<IReadOnlyList<MessageSummary>>? NewMessagesArrived;

    public async Task<IReadOnlyList<MailFolder>> SyncFoldersAsync(CancellationToken ct = default)
    {
        var folders = await _provider.GetFoldersAsync(ct).ConfigureAwait(false);
        _cache.ReplaceFolders(folders);
        FoldersChanged?.Invoke(this, EventArgs.Empty);
        return folders;
    }

    /// <summary>
    /// Fetches the newest page with FindItem when the folder has never been synced, so the user sees
    /// recent mail immediately while the full incremental sync runs.
    /// </summary>
    public async Task PrimeFolderAsync(string folderId, int pageSize, CancellationToken ct = default)
    {
        if (_cache.GetSyncState(folderId) != null && _cache.CountMessages(folderId) > 0) return;
        var page = await _provider.GetMessagesAsync(folderId, 0, pageSize, ct).ConfigureAwait(false);
        foreach (var m in page.Items) m.FolderId = folderId;
        _cache.UpsertMessages(page.Items);
        FolderContentChanged?.Invoke(this, folderId);
    }

    /// <summary>Synchronizes a single folder. Returns the number of changes applied.</summary>
    public async Task<int> SyncFolderAsync(string folderId, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(folderId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SyncFolderCoreAsync(folderId, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> SyncFolderCoreAsync(string folderId, CancellationToken ct)
    {
        var state = _cache.GetSyncState(folderId);
        bool initial = state == null;
        int changes = 0;
        int resets = 0;
        int emptyRounds = 0;
        var arrived = new List<MessageSummary>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            FolderSyncResult result;
            try
            {
                result = await _provider.SyncFolderItemsAsync(folderId, state, 256, ct).ConfigureAwait(false);
            }
            catch (SyncStateInvalidException) when (resets++ == 0)
            {
                // Server discarded our state: start over from scratch (once per call, never in a loop).
                _cache.ClearFolderMessages(folderId);
                _cache.SetSyncState(folderId, null);
                state = null;
                initial = true;
                continue;
            }

            foreach (var m in result.CreatedOrUpdated) m.FolderId = folderId;
            if (result.CreatedOrUpdated.Count > 0)
            {
                _cache.UpsertMessages(result.CreatedOrUpdated);
                foreach (var m in result.CreatedOrUpdated) _cache.InvalidateCachedMessage(m.Id);
                if (!initial) arrived.AddRange(result.CreatedOrUpdated.Where(m => !m.IsRead && result.Created.Contains(m.Id)));
            }
            if (result.Deleted.Count > 0) _cache.DeleteMessages(result.Deleted);
            foreach (var group in result.ReadFlagChanges.GroupBy(kv => kv.Value))
                _cache.SetReadState(group.Select(kv => kv.Key), group.Key);

            changes += result.CreatedOrUpdated.Count + result.Deleted.Count + result.ReadFlagChanges.Count;
            state = result.SyncState;
            _cache.SetSyncState(folderId, state);

            if (changes > 0) FolderContentChanged?.Invoke(this, folderId);
            SyncProgress?.Invoke(this, (folderId, changes));
            if (result.IncludesLastItem) break;
            // Defensive: a server claiming "more to come" without delivering anything must not keep us looping.
            int batch = result.CreatedOrUpdated.Count + result.Deleted.Count + result.ReadFlagChanges.Count;
            emptyRounds = batch == 0 ? emptyRounds + 1 : 0;
            if (emptyRounds >= 3) break;
        }

        if (changes > 0) _cache.RefreshFolderCounts(folderId);

        // Only notify about genuinely new mail (received in the last day), not e.g. moved-in old items.
        var fresh = arrived.Where(m => m.DateReceived > DateTimeOffset.UtcNow.AddDays(-1)).ToList();
        if (fresh.Count > 0) NewMessagesArrived?.Invoke(this, fresh);
        return changes;
    }

    /// <summary>Loads a full message, using the cache when possible.</summary>
    public async Task<MailMessage> GetMessageAsync(string itemId, CancellationToken ct = default)
    {
        var cached = _cache.GetCachedMessage(itemId);
        if (cached != null) return cached;
        var msg = await _provider.GetMessageAsync(itemId, ct).ConfigureAwait(false);
        _cache.PutCachedMessage(msg);
        return msg;
    }
}
