using MailClient.Core.Models;
using MailClient.Core.Services;
using MailClient.Core.Storage;

namespace MailClient.App.Services;

/// <summary>
/// A connected account: provider + offline cache + sync engine + background synchronization loop.
/// Works offline from the cache when the server is unreachable and recovers automatically.
/// </summary>
public sealed class AccountSession : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0);
    private Task? _loop;
    private DateTime _lastFolderRefresh = DateTime.MinValue;
    private bool _authErrorReported;

    public AccountSettings Settings { get; }
    public IMailProvider Provider { get; }
    public LocalCache Cache { get; }
    public SyncEngine Sync { get; }

    /// <summary>Folder currently displayed; synchronized on every cycle in addition to the Inbox.</summary>
    public string? ActiveFolderId { get; set; }

    public bool IsOnline { get; private set; }
    public string Status { get; private set; } = "Подключение…";

    public event EventHandler? StatusChanged;
    public event EventHandler? AuthenticationFailed;

    public AccountSession(AccountSettings settings, ICredentialProvider credentials)
    {
        Settings = settings;
        Provider = ProviderFactory.Create(settings, credentials);
        Cache = new LocalCache(AppPaths.CacheFile(settings.Id));
        Sync = new SyncEngine(Provider, Cache);
        Sync.SyncProgress += (_, p) =>
        {
            if (p.processed < 50) return;
            var name = Cache.GetFolders().FirstOrDefault(f => f.Id == p.folderId) is { } f ? RuText.FolderName(f) : "папка";
            SetStatus($"Синхронизация «{name}»: {RuText.Count(p.processed, "письмо", "письма", "писем")}…", true);
        };
    }

    public string? InboxId => Cache.GetFolders().FirstOrDefault(f => f.WellKnown == WellKnownFolder.Inbox)?.Id;

    public string? FolderId(WellKnownFolder wk) => Cache.GetFolders().FirstOrDefault(f => f.WellKnown == wk)?.Id;

    public void Start()
    {
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Wakes the loop up for an immediate synchronization.</summary>
    public void SyncNow()
    {
        _lastFolderRefresh = DateTime.MinValue;
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private void SetStatus(string status, bool online)
    {
        Status = status;
        IsOnline = online;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetStatus("Синхронизация…", IsOnline);
                // Changes made while offline go first, so the synchronization below already sees them on the server.
                await Sync.SendPendingChangesAsync(ct).ConfigureAwait(false);
                if (DateTime.UtcNow - _lastFolderRefresh > TimeSpan.FromMinutes(5))
                {
                    await Sync.SyncFoldersAsync(ct).ConfigureAwait(false);
                    _lastFolderRefresh = DateTime.UtcNow;
                }

                var targets = new List<string>();
                if (InboxId is { } inbox) targets.Add(inbox);
                if (ActiveFolderId is { } active && !targets.Contains(active)) targets.Add(active);
                // Folders whose counters changed on the server (new mail filed by a rule, read elsewhere).
                foreach (var id in Sync.TakeFoldersOutOfStep().Take(20))
                    if (!targets.Contains(id)) targets.Add(id);
                foreach (var folderId in targets)
                {
                    await Sync.PrimeFolderAsync(folderId, 100, ct).ConfigureAwait(false);
                    await Sync.SyncFolderAsync(folderId, ct).ConfigureAwait(false);
                }

                failures = 0;
                _authErrorReported = false;
                SetStatus($"Обновлено в {DateTime.Now:HH:mm}", true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (MailAuthenticationException ex)
            {
                Log.Warn($"[{Settings.EmailAddress}] Ошибка входа: {ex.Message}");
                if (!_authErrorReported)
                {
                    _authErrorReported = true;
                    AuthenticationFailed?.Invoke(this, EventArgs.Empty);
                }
                // A rejected login is not retried on a timer — repeated failed logins lock the
                // domain account. The next attempt happens only when the user asks (sync now, new password).
                SetStatus("Ошибка входа — проверьте пароль (нажмите «Обновить» для повторной попытки)", false);
                try
                {
                    await _wake.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }
            catch (Exception ex)
            {
                failures++;
                Log.Warn($"[{Settings.EmailAddress}] Ошибка синхронизации: {MailClient.Core.Diagnostics.MailLog.Describe(ex)}");
                SetStatus(ex switch
                {
                    MailConnectionException => "Нет связи с сервером — автономный режим",
                    MailServiceException { ErrorCode: "ErrorServerBusy" or "ErrorExceededConnectionCount" or "ErrorTooManyObjectsOpened" } =>
                        "Сервер временно ограничил число запросов — синхронизация продолжится автоматически",
                    _ => "Ошибка синхронизации: " + ex.Message,
                }, false);
            }

            // Back off on repeated failures (max 10 min), otherwise use the configured interval.
            var delay = failures == 0
                ? TimeSpan.FromSeconds(Math.Max(15, Settings.SyncIntervalSeconds))
                : TimeSpan.FromSeconds(Math.Min(600, 15 * Math.Pow(2, Math.Min(failures, 6))));
            try
            {
                await _wake.WaitAsync(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        Provider.Dispose();
        _cts.Dispose();
    }
}
