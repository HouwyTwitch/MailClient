using System.Globalization;
using System.Net;
using System.Net.Sockets;
using MailClient.Core;
using MailClient.Core.Mime;
using MailClient.Core.Models;
using MailClient.Core.Rules;
using MailClient.Core.Security;
using MailClient.Core.Services;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MailFolder = MailClient.Core.Models.MailFolder;
using MessageSummary = MailClient.Core.Models.MessageSummary;

namespace MailClient.Imap;

/// <summary>
/// <see cref="IMailProvider"/> for IMAP (reading) + SMTP (sending): Yandex 360, Mail.ru, Exchange with IMAP
/// enabled, Dovecot, CommuniGate and other standard servers. Built on MailKit.
/// </summary>
public sealed class ImapProvider : IMailProvider
{
    public const string RootId = "imap:root";
    private const char IdSeparator = '\u001F';

    private readonly ICredentialProvider _credentials;
    private readonly ImapClient _imap = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<WellKnownFolder, string> _special = new();

    public ImapProvider(AccountSettings account, ICredentialProvider credentials)
    {
        CodePages.EnsureRegistered(); // MailKit parses headers and bodies itself
        Account = account;
        _credentials = credentials;
        if (string.IsNullOrWhiteSpace(account.ImapHost))
            throw new ArgumentException("Не указан адрес IMAP-сервера.", nameof(account));
        _imap.Timeout = 120_000;
        if (CertificateTrust.CreateCallback(account) is { } cb) _imap.ServerCertificateValidationCallback = cb;
    }

    public AccountSettings Account { get; }
    /// <summary>Forwarding rules need ManageSieve on the server; without it the rules window explains where to set them up.</summary>
    public ProviderCapabilities Capabilities => ProviderCapabilities.ForwardingRules;

    // ===================================================================== connection

    private NetworkCredential Credential()
    {
        var user = string.IsNullOrWhiteSpace(Account.UserName) ? Account.EmailAddress : Account.UserName.Trim();
        return new NetworkCredential(user, _credentials.GetPassword(Account.Id) ?? "");
    }

    private static SecureSocketOptions Socket(ConnectionSecurity s) => s switch
    {
        ConnectionSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        ConnectionSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None,
    };

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_imap.IsConnected && _imap.IsAuthenticated) return;
        if (_imap.IsConnected) await _imap.DisconnectAsync(true, ct).ConfigureAwait(false);
        await ConnectAndAuthenticateAsync(_imap, Account.ImapHost, Account.ImapPort, Account.ImapSecurity, "IMAP", ct).ConfigureAwait(false);
        _special = new();
    }

    private async Task ConnectAndAuthenticateAsync(MailService client, string host, int port, ConnectionSecurity security, string protocol, CancellationToken ct)
    {
        try
        {
            await client.ConnectAsync(host.Trim(), port, Socket(security), ct).ConfigureAwait(false);
        }
        catch (SslHandshakeException ex)
        {
            throw new MailConnectionException(
                $"Не удалось установить защищённое соединение с {protocol}-сервером {host}:{port}. Если сертификат выдан внутренним " +
                "удостоверяющим центром или «Russian Trusted Root CA», импортируйте корневой сертификат в настройках учётной записи. " +
                "Также проверьте, что выбран правильный порт и тип шифрования.", ex);
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or ProtocolException)
        {
            throw new MailConnectionException($"Не удаётся подключиться к {protocol}-серверу {host}:{port}: {ex.Message}", ex);
        }

        client.AuthenticationMechanisms.Remove("XOAUTH2");
        client.AuthenticationMechanisms.Remove("OAUTHBEARER");
        if (client is SmtpClient smtp && !smtp.Capabilities.HasFlag(SmtpCapabilities.Authentication)) return;
        try
        {
            // A single attempt with one method (AUTH PLAIN, else AUTH LOGIN,
            // else the IMAP LOGIN command). MailKit's default would try every mechanism in turn after a rejection,
            // and repeated failed logins lock the account.
            var credential = Credential();
            if (client.AuthenticationMechanisms.Contains("PLAIN"))
                await client.AuthenticateAsync(new SaslMechanismPlain(credential), ct).ConfigureAwait(false);
            else if (client.AuthenticationMechanisms.Contains("LOGIN"))
                await client.AuthenticateAsync(new SaslMechanismLogin(credential), ct).ConfigureAwait(false);
            else
            {
                client.AuthenticationMechanisms.Clear(); // IMAP LOGIN command only
                await client.AuthenticateAsync(credential, ct).ConfigureAwait(false);
            }
        }
        catch (AuthenticationException ex)
        {
            throw new MailAuthenticationException(
                $"{protocol}-сервер {host} отклонил имя пользователя или пароль ({ex.Message}). " +
                "Для Яндекс 360 и Mail.ru нужен «пароль приложения» (создаётся в настройках безопасности почты), " +
                "а доступ по IMAP должен быть включён в настройках ящика.", ex);
        }
    }

    /// <summary>Serializes access to the IMAP connection and reconnects once if the connection dropped.</summary>
    private async Task<T> RunAsync<T>(Func<Task<T>> op, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            try
            {
                return await op().ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                                       ex is ServiceNotConnectedException or ImapProtocolException or IOException or SocketException)
            {
                // Dropped connection (server timeout, network change): reconnect and retry once.
                try { await _imap.DisconnectAsync(false, CancellationToken.None).ConfigureAwait(false); } catch { }
                await EnsureConnectedAsync(ct).ConfigureAwait(false);
                return await op().ConfigureAwait(false);
            }
        }
        catch (ImapCommandException ex)
        {
            throw new MailServiceException($"IMAP-сервер отклонил команду: {ex.ResponseText}", ex.Response.ToString(), ex);
        }
        catch (FolderNotFoundException ex)
        {
            throw new MailServiceException($"Папка «{ex.FolderName}» не найдена — возможно, она удалена или переименована.", "FolderNotFound", ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RunAsync(Func<Task> op, CancellationToken ct) =>
        await RunAsync(async () => { await op().ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);

    public async Task<MailboxInfo> ConnectAsync(CancellationToken ct = default)
    {
        await RunAsync(() => Task.CompletedTask, ct).ConfigureAwait(false);
        // Verify SMTP credentials too, so a misconfigured outgoing server is caught during setup.
        using (var smtp = CreateSmtp())
        {
            await ConnectAndAuthenticateAsync(smtp, Account.SmtpHost, Account.SmtpPort, Account.SmtpSecurity, "SMTP", ct).ConfigureAwait(false);
            await smtp.DisconnectAsync(true, ct).ConfigureAwait(false);
        }
        var id = _imap.Capabilities.HasFlag(ImapCapabilities.Id) ? await TryGetServerIdAsync(ct).ConfigureAwait(false) : null;
        return new MailboxInfo { EmailAddress = Account.EmailAddress, DisplayName = Account.EffectiveDisplayName, ServerVersion = id ?? "IMAP" };
    }

    private async Task<string?> TryGetServerIdAsync(CancellationToken ct)
    {
        try
        {
            var id = await RunAsync(() => _imap.IdentifyAsync(new ImapImplementation { Name = "MailClient", Version = "1.0" }, ct), ct).ConfigureAwait(false);
            return id == null ? null : $"{id.Name} {id.Version}".Trim();
        }
        catch
        {
            return null;
        }
    }

    private SmtpClient CreateSmtp()
    {
        var smtp = new SmtpClient { Timeout = 300_000 };
        if (CertificateTrust.CreateCallback(Account) is { } cb) smtp.ServerCertificateValidationCallback = cb;
        return smtp;
    }

    // ===================================================================== ids

    private static string MessageId(IMailFolder folder, UniqueId uid) => $"{folder.FullName}{IdSeparator}{folder.UidValidity}{IdSeparator}{uid.Id}";

    private static (string folder, uint validity, UniqueId uid) ParseId(string id)
    {
        var p = id.Split(IdSeparator);
        if (p.Length != 3) throw new MailServiceException("Некорректный идентификатор письма.");
        return (p[0], uint.Parse(p[1], CultureInfo.InvariantCulture), new UniqueId(uint.Parse(p[1], CultureInfo.InvariantCulture), uint.Parse(p[2], CultureInfo.InvariantCulture)));
    }

    private async Task<IMailFolder> FolderAsync(string folderId, CancellationToken ct)
    {
        if (folderId == RootId) return _imap.GetFolder(_imap.PersonalNamespaces[0]);
        if (folderId.Equals("INBOX", StringComparison.OrdinalIgnoreCase)) return _imap.Inbox;
        return await _imap.GetFolderAsync(folderId, ct).ConfigureAwait(false);
    }

    private static async Task<IMailFolder> OpenAsync(IMailFolder folder, FolderAccess access, CancellationToken ct)
    {
        if (!folder.IsOpen || (access == FolderAccess.ReadWrite && folder.Access != FolderAccess.ReadWrite))
            await folder.OpenAsync(access, ct).ConfigureAwait(false);
        return folder;
    }

    // ===================================================================== folders

    private static readonly (WellKnownFolder kind, FolderAttributes attr, string[] names)[] SpecialNames =
    {
        (WellKnownFolder.SentItems, FolderAttributes.Sent, new[] { "Sent", "Sent Items", "Sent Messages", "Отправленные", "Отправленные элементы" }),
        (WellKnownFolder.Drafts, FolderAttributes.Drafts, new[] { "Drafts", "Черновики" }),
        (WellKnownFolder.DeletedItems, FolderAttributes.Trash, new[] { "Trash", "Deleted", "Deleted Items", "Deleted Messages", "Корзина", "Удаленные", "Удалённые", "Удаленные элементы" }),
        (WellKnownFolder.JunkEmail, FolderAttributes.Junk, new[] { "Junk", "Spam", "Junk E-mail", "Спам", "Нежелательная почта" }),
        (WellKnownFolder.Archive, FolderAttributes.Archive, new[] { "Archive", "Архив" }),
        (WellKnownFolder.Outbox, FolderAttributes.None, new[] { "Outbox", "Исходящие" }),
    };

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken ct = default) => RunAsync(async () =>
    {
        var folders = (await _imap.GetFoldersAsync(_imap.PersonalNamespaces[0], StatusItems.None, false, ct).ConfigureAwait(false))
            .Where(f => !f.Attributes.HasFlag(FolderAttributes.NonExistent))
            .ToList();
        if (!folders.Any(f => f.FullName.Equals(_imap.Inbox.FullName, StringComparison.OrdinalIgnoreCase)))
            folders.Insert(0, _imap.Inbox);

        // Well-known folders: SPECIAL-USE attributes first, then common names (top level only).
        var special = new Dictionary<WellKnownFolder, string> { [WellKnownFolder.Inbox] = _imap.Inbox.FullName };
        foreach (var (kind, attr, names) in SpecialNames)
        {
            var match = (attr != FolderAttributes.None ? folders.FirstOrDefault(f => f.Attributes.HasFlag(attr)) : null)
                        ?? folders.FirstOrDefault(f => names.Contains(f.Name, StringComparer.OrdinalIgnoreCase));
            if (match != null && !special.ContainsValue(match.FullName)) special[kind] = match.FullName;
        }
        _special = special;
        var byName = special.ToDictionary(kv => kv.Value, kv => kv.Key);

        var ids = new HashSet<string>(folders.Select(f => f.FullName));
        var result = new List<MailFolder>
        {
            new() { Id = RootId, DisplayName = Account.EffectiveDisplayName, WellKnown = WellKnownFolder.Root, FolderClass = "IPF.Note" },
        };
        foreach (var f in folders)
        {
            int total = 0, unread = 0;
            if (!f.Attributes.HasFlag(FolderAttributes.NoSelect))
            {
                try
                {
                    await f.StatusAsync(StatusItems.Count | StatusItems.Unread, ct).ConfigureAwait(false);
                    total = f.Count;
                    unread = f.Unread;
                }
                catch (ImapCommandException)
                {
                    // Some servers refuse STATUS on special folders - keep zero counters.
                }
            }
            var parent = f.ParentFolder?.FullName;
            result.Add(new MailFolder
            {
                Id = f.FullName,
                ParentId = parent != null && ids.Contains(parent) ? parent : RootId,
                DisplayName = f.FullName.Equals(_imap.Inbox.FullName, StringComparison.OrdinalIgnoreCase) ? "Входящие" : f.Name,
                FolderClass = "IPF.Note",
                TotalCount = total,
                UnreadCount = unread,
                WellKnown = byName.TryGetValue(f.FullName, out var wk) ? wk : WellKnownFolder.None,
            });
        }
        return (IReadOnlyList<MailFolder>)result;
    }, ct);

    private async Task<IMailFolder?> SpecialFolderAsync(WellKnownFolder kind, CancellationToken ct)
    {
        if (_special.Count == 0)
        {
            // Populate the special-folder map without holding the lock twice.
            var folders = await _imap.GetFoldersAsync(_imap.PersonalNamespaces[0], StatusItems.None, false, ct).ConfigureAwait(false);
            var map = new Dictionary<WellKnownFolder, string> { [WellKnownFolder.Inbox] = _imap.Inbox.FullName };
            foreach (var (k, attr, names) in SpecialNames)
            {
                var m = (attr != FolderAttributes.None ? folders.FirstOrDefault(f => f.Attributes.HasFlag(attr)) : null)
                        ?? folders.FirstOrDefault(f => names.Contains(f.Name, StringComparer.OrdinalIgnoreCase));
                if (m != null && !map.ContainsValue(m.FullName)) map[k] = m.FullName;
            }
            _special = map;
        }
        return _special.TryGetValue(kind, out var name) ? await _imap.GetFolderAsync(name, ct).ConfigureAwait(false) : null;
    }

    /// <summary>Returns the special folder, creating it (top level) when the server has none.</summary>
    private async Task<IMailFolder> EnsureSpecialFolderAsync(WellKnownFolder kind, string name, CancellationToken ct)
    {
        var folder = await SpecialFolderAsync(kind, ct).ConfigureAwait(false);
        if (folder != null) return folder;
        var root = _imap.GetFolder(_imap.PersonalNamespaces[0]);
        folder = Created(await root.CreateAsync(name, true, ct).ConfigureAwait(false), name);
        _special[kind] = folder.FullName;
        return folder;
    }

    public Task<MailFolder> CreateFolderAsync(string parentFolderId, string name, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var parent = await FolderAsync(parentFolderId, ct).ConfigureAwait(false);
            var created = Created(await parent.CreateAsync(name, true, ct).ConfigureAwait(false), name);
            return new MailFolder { Id = created.FullName, ParentId = parentFolderId, DisplayName = created.Name, FolderClass = "IPF.Note" };
        }, ct);

    public Task RenameFolderAsync(string folderId, string newName, CancellationToken ct = default) => RunAsync(async () =>
    {
        var f = await FolderAsync(folderId, ct).ConfigureAwait(false);
        if (f.IsOpen) await f.CloseAsync(false, ct).ConfigureAwait(false);
        var parent = f.ParentFolder ?? _imap.GetFolder(_imap.PersonalNamespaces[0]);
        await f.RenameAsync(parent, newName, ct).ConfigureAwait(false);
    }, ct);

    /// <summary>CREATE succeeded but the server did not list the new folder.</summary>
    private static IMailFolder Created(IMailFolder? folder, string name) =>
        folder ?? throw new MailServiceException($"Сервер не подтвердил создание папки «{name}». Обновите список папок.");

    public Task MoveFolderAsync(string folderId, string newParentFolderId, CancellationToken ct = default) => RunAsync(async () =>
    {
        var f = await FolderAsync(folderId, ct).ConfigureAwait(false);
        var parent = await FolderAsync(newParentFolderId, ct).ConfigureAwait(false);
        if (f.IsOpen) await f.CloseAsync(false, ct).ConfigureAwait(false);
        await f.RenameAsync(parent, f.Name, ct).ConfigureAwait(false);
    }, ct);

    public Task DeleteFolderAsync(string folderId, bool permanent, CancellationToken ct = default) => RunAsync(async () =>
    {
        var f = await FolderAsync(folderId, ct).ConfigureAwait(false);
        if (f.IsOpen) await f.CloseAsync(false, ct).ConfigureAwait(false);
        var trash = permanent ? null : await SpecialFolderAsync(WellKnownFolder.DeletedItems, ct).ConfigureAwait(false);
        if (trash != null && trash.FullName != f.FullName && !f.FullName.StartsWith(trash.FullName + trash.DirectorySeparator, StringComparison.Ordinal))
            await f.RenameAsync(trash, f.Name, ct).ConfigureAwait(false);
        else
            await f.DeleteAsync(ct).ConfigureAwait(false);
    }, ct);

    public Task EmptyFolderAsync(string folderId, bool deleteSubFolders, CancellationToken ct = default) => RunAsync(async () =>
    {
        var f = await OpenAsync(await FolderAsync(folderId, ct).ConfigureAwait(false), FolderAccess.ReadWrite, ct).ConfigureAwait(false);
        if (f.Count == 0) return;
        var uids = await f.SearchAsync(SearchQuery.All, ct).ConfigureAwait(false);
        var trash = await SpecialFolderAsync(WellKnownFolder.DeletedItems, ct).ConfigureAwait(false);
        var junk = await SpecialFolderAsync(WellKnownFolder.JunkEmail, ct).ConfigureAwait(false);
        if (trash != null && f.FullName != trash.FullName && f.FullName != junk?.FullName)
            await f.MoveToAsync(uids, trash, ct).ConfigureAwait(false);
        else
            await DeletePermanentlyAsync(f, uids, ct).ConfigureAwait(false);
    }, ct);

    private async Task DeletePermanentlyAsync(IMailFolder f, IList<UniqueId> uids, CancellationToken ct)
    {
        await f.AddFlagsAsync(uids, MessageFlags.Deleted, true, ct).ConfigureAwait(false);
        if (_imap.Capabilities.HasFlag(ImapCapabilities.UidPlus)) await f.ExpungeAsync(uids, ct).ConfigureAwait(false);
        else await f.ExpungeAsync(ct).ConfigureAwait(false);
    }

    // ===================================================================== message lists

    private static readonly HeaderSet ExtraHeaders = new(new[] { "Importance", "X-Priority" });

    private static FetchRequest SummaryRequest() => new(
        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
        MessageSummaryItems.InternalDate | MessageSummaryItems.Size | MessageSummaryItems.BodyStructure |
        MessageSummaryItems.PreviewText)
    {
        Headers = ExtraHeaders,
    };

    private static string Names(InternetAddressList? list) =>
        list == null ? "" : string.Join("; ", list.Mailboxes.Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Address : m.Name));

    private static MessageSummary ToSummary(IMessageSummary s, IMailFolder folder)
    {
        var from = s.Envelope?.From?.Mailboxes.FirstOrDefault();
        var received = s.InternalDate ?? s.Envelope?.Date ?? DateTimeOffset.UtcNow;
        var importance = Importance.Normal;
        var imp = s.Headers?["Importance"] ?? "";
        var prio = s.Headers?["X-Priority"] ?? "";
        if (imp.Contains("high", StringComparison.OrdinalIgnoreCase) || prio.TrimStart().StartsWith('1') || prio.TrimStart().StartsWith('2'))
            importance = Importance.High;
        else if (imp.Contains("low", StringComparison.OrdinalIgnoreCase) || prio.TrimStart().StartsWith('5') || prio.TrimStart().StartsWith('4'))
            importance = Importance.Low;

        return new MessageSummary
        {
            Id = MessageId(folder, s.UniqueId),
            FolderId = folder.FullName,
            Subject = s.Envelope?.Subject ?? "",
            From = from == null ? null : new EmailAddress(from.Name ?? "", from.Address ?? ""),
            DisplayTo = Names(s.Envelope?.To),
            DisplayCc = Names(s.Envelope?.Cc),
            DateReceived = received,
            DateSent = s.Envelope?.Date ?? received,
            IsRead = s.Flags?.HasFlag(MessageFlags.Seen) == true,
            Flag = s.Flags?.HasFlag(MessageFlags.Flagged) == true ? FlagStatus.Flagged : FlagStatus.NotFlagged,
            HasAttachments = s.Attachments?.Any() == true,
            Size = s.Size ?? 0,
            Preview = (s.PreviewText ?? "").Trim(),
            Importance = importance,
            ItemClass = "IPM.Note",
        };
    }

    public Task<MessagePage> GetMessagesAsync(string folderId, int offset, int pageSize, CancellationToken ct = default) => RunAsync(async () =>
    {
        var f = await OpenAsync(await FolderAsync(folderId, ct).ConfigureAwait(false), FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        var count = f.Count;
        if (offset >= count) return new MessagePage { Items = Array.Empty<MessageSummary>(), TotalCount = count };
        int high = count - 1 - offset;
        int low = Math.Max(0, high - pageSize + 1);
        var summaries = await f.FetchAsync(low, high, SummaryRequest(), ct).ConfigureAwait(false);
        var items = summaries.OrderByDescending(s => s.Index).Select(s => ToSummary(s, f)).ToList();
        return new MessagePage { Items = items, TotalCount = count, HasMore = low > 0 };
    }, ct);

    public Task<MessagePage> SearchMessagesAsync(string folderId, string query, int offset, int pageSize, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var f = await OpenAsync(await FolderAsync(folderId, ct).ConfigureAwait(false), FolderAccess.ReadOnly, ct).ConfigureAwait(false);
            var q = SearchQuery.SubjectContains(query).Or(SearchQuery.FromContains(query))
                .Or(SearchQuery.ToContains(query)).Or(SearchQuery.BodyContains(query));
            var uids = (await f.SearchAsync(q, ct).ConfigureAwait(false)).OrderByDescending(u => u.Id).ToList();
            var page = uids.Skip(offset).Take(pageSize).ToList();
            var items = page.Count == 0 ? new List<MessageSummary>()
                : (await f.FetchAsync(page, SummaryRequest(), ct).ConfigureAwait(false))
                    .Select(s => ToSummary(s, f)).OrderByDescending(m => m.DateReceived).ToList();
            return new MessagePage { Items = items, TotalCount = uids.Count, HasMore = offset + page.Count < uids.Count };
        }, ct);

    // ===================================================================== incremental sync

    /// <summary>Sync state: version|uidvalidity|highestmodseq|uidnext|count|known uids|seen uids|flagged uids.</summary>
    private sealed record SyncState(uint Validity, ulong ModSeq, uint UidNext, int Count, HashSet<uint> Known, HashSet<uint> Seen, HashSet<uint> Flagged)
    {
        public override string ToString() =>
            $"v1|{Validity}|{ModSeq}|{UidNext}|{Count}|{Ranges(Known)}|{Ranges(Seen)}|{Ranges(Flagged)}";

        public static SyncState? Parse(string? s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var p = s.Split('|');
            if (p.Length != 8 || p[0] != "v1") return null;
            return new SyncState(uint.Parse(p[1], CultureInfo.InvariantCulture), ulong.Parse(p[2], CultureInfo.InvariantCulture), uint.Parse(p[3], CultureInfo.InvariantCulture), int.Parse(p[4], CultureInfo.InvariantCulture),
                ParseRanges(p[5]), ParseRanges(p[6]), ParseRanges(p[7]));
        }

        private static string Ranges(IEnumerable<uint> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            var parts = new List<string>();
            for (int i = 0; i < sorted.Count;)
            {
                int j = i;
                while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
                parts.Add(i == j ? sorted[i].ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{sorted[i]}:{sorted[j]}"));
                i = j + 1;
            }
            return string.Join(',', parts);
        }

        private static HashSet<uint> ParseRanges(string s)
        {
            var set = new HashSet<uint>();
            foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var r = part.Split(':');
                uint a = uint.Parse(r[0], CultureInfo.InvariantCulture), b = r.Length > 1 ? uint.Parse(r[1], CultureInfo.InvariantCulture) : a;
                for (uint v = a; v <= b && v >= a; v++) set.Add(v);
            }
            return set;
        }
    }

    public Task<FolderSyncResult> SyncFolderItemsAsync(string folderId, string? syncState, int maxChanges, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var f = await OpenAsync(await FolderAsync(folderId, ct).ConfigureAwait(false), FolderAccess.ReadOnly, ct).ConfigureAwait(false);
            var state = SyncState.Parse(syncState);
            if (state != null && state.Validity != f.UidValidity)
                throw new SyncStateInvalidException("UIDVALIDITY папки изменился — требуется полная синхронизация.");

            var uidNext = f.UidNext?.Id ?? 0;
            var result = new FolderSyncResult();

            // Cheap "nothing changed" check (CONDSTORE mod-sequence, UIDNEXT and message count).
            if (state != null && state.ModSeq != 0 && f.HighestModSeq == state.ModSeq && uidNext == state.UidNext && f.Count == state.Count)
            {
                result.SyncState = state.ToString();
                result.IncludesLastItem = true;
                return result;
            }

            var server = f.Count == 0
                ? new List<IMessageSummary>()
                : (await f.FetchAsync(0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct).ConfigureAwait(false)).ToList();
            var serverFlags = server.ToDictionary(s => s.UniqueId.Id, s => s.Flags ?? MessageFlags.None);
            var known = state?.Known ?? new HashSet<uint>();

            foreach (var uid in known.Where(u => !serverFlags.ContainsKey(u)))
                result.Deleted.Add(MessageId(f, new UniqueId(f.UidValidity, uid)));

            var refetch = new List<uint>();
            if (state != null)
            {
                foreach (var uid in known.Where(serverFlags.ContainsKey))
                {
                    bool seen = serverFlags[uid].HasFlag(MessageFlags.Seen);
                    bool flagged = serverFlags[uid].HasFlag(MessageFlags.Flagged);
                    if (flagged != state.Flagged.Contains(uid)) refetch.Add(uid);
                    else if (seen != state.Seen.Contains(uid)) result.ReadFlagChanges[MessageId(f, new UniqueId(f.UidValidity, uid))] = seen;
                }
            }

            // Newest messages first, so a large mailbox becomes usable quickly.
            var fresh = serverFlags.Keys.Where(u => !known.Contains(u)).OrderByDescending(u => u).ToList();
            var batch = fresh.Take(Math.Max(1, maxChanges)).ToList();
            var toFetch = batch.Concat(refetch).Distinct().Select(u => new UniqueId(f.UidValidity, u)).ToList();
            if (toFetch.Count > 0)
            {
                var isNew = new HashSet<uint>(batch);
                foreach (var s in await f.FetchAsync(toFetch, SummaryRequest(), ct).ConfigureAwait(false))
                {
                    var summary = ToSummary(s, f);
                    result.CreatedOrUpdated.Add(summary);
                    if (isNew.Contains(s.UniqueId.Id)) result.Created.Add(summary.Id);
                }
            }

            var newKnown = new HashSet<uint>(known.Where(serverFlags.ContainsKey).Concat(batch));
            result.IncludesLastItem = fresh.Count <= batch.Count;
            result.SyncState = new SyncState(
                f.UidValidity,
                result.IncludesLastItem ? f.HighestModSeq : 0,
                result.IncludesLastItem ? uidNext : 0,
                result.IncludesLastItem ? f.Count : -1,
                newKnown,
                new HashSet<uint>(newKnown.Where(u => serverFlags[u].HasFlag(MessageFlags.Seen))),
                new HashSet<uint>(newKnown.Where(u => serverFlags[u].HasFlag(MessageFlags.Flagged)))).ToString();
            return result;
        }, ct);

    // ===================================================================== single messages

    private async Task<(IMailFolder folder, UniqueId uid, MimeMessage message)> LoadMimeAsync(string itemId, CancellationToken ct)
    {
        var (folderName, validity, uid) = ParseId(itemId);
        var f = await OpenAsync(await FolderAsync(folderName, ct).ConfigureAwait(false), FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        if (f.UidValidity != validity) throw new MailServiceException("Письмо не найдено (папка была изменена на сервере). Обновите папку.", "ErrorItemNotFound");
        try
        {
            return (f, uid, await f.GetMessageAsync(uid, ct).ConfigureAwait(false));
        }
        catch (MessageNotFoundException ex)
        {
            throw new MailServiceException("Письмо не найдено — возможно, оно уже удалено или перемещено.", "ErrorItemNotFound", ex);
        }
    }

    public Task<MailMessage> GetMessageAsync(string itemId, CancellationToken ct = default) => RunAsync(async () =>
    {
        var (f, uid, mime) = await LoadMimeAsync(itemId, ct).ConfigureAwait(false);
        var summary = (await f.FetchAsync(new[] { uid }, SummaryRequest(), ct).ConfigureAwait(false)).FirstOrDefault();
        var m = new MailMessage();
        if (summary != null)
        {
            var s = ToSummary(summary, f);
            foreach (var prop in typeof(MessageSummary).GetProperties().Where(p => p.CanWrite))
                prop.SetValue(m, prop.GetValue(s));
        }
        m.Id = itemId;
        m.FolderId = f.FullName;
        MimeMail.Fill(m, mime, itemId);
        return m;
    }, ct);

    public Task<byte[]> GetMimeContentAsync(string itemId, CancellationToken ct = default) => RunAsync(async () =>
    {
        var (_, _, mime) = await LoadMimeAsync(itemId, ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await mime.WriteToAsync(ms, ct).ConfigureAwait(false);
        return ms.ToArray();
    }, ct);

    public async Task<AttachmentContent> GetAttachmentAsync(string attachmentId, CancellationToken ct = default) =>
        (await GetAttachmentsAsync(new[] { attachmentId }, ct).ConfigureAwait(false))[0];

    public Task<IReadOnlyList<AttachmentContent>> GetAttachmentsAsync(IEnumerable<string> attachmentIds, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var result = new List<AttachmentContent>();
            foreach (var group in attachmentIds.GroupBy(id => MimeMail.SplitPartId(id).itemId))
            {
                var (_, _, mime) = await LoadMimeAsync(group.Key, ct).ConfigureAwait(false);
                foreach (var id in group) result.Add(await MimeMail.ExtractAsync(mime, id, ct).ConfigureAwait(false));
            }
            return (IReadOnlyList<AttachmentContent>)result;
        }, ct);

    // ===================================================================== flags / move / delete

    private static IEnumerable<(string folder, List<UniqueId> uids)> ByFolder(IEnumerable<string> itemIds) =>
        itemIds.Select(ParseId).GroupBy(x => x.folder).Select(g => (g.Key, g.Select(x => x.uid).ToList()));

    public Task SetReadStateAsync(IEnumerable<string> itemIds, bool isRead, CancellationToken ct = default) => RunAsync(async () =>
    {
        foreach (var (folder, uids) in ByFolder(itemIds))
        {
            var f = await OpenAsync(await FolderAsync(folder, ct).ConfigureAwait(false), FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            if (isRead) await f.AddFlagsAsync(uids, MessageFlags.Seen, true, ct).ConfigureAwait(false);
            else await f.RemoveFlagsAsync(uids, MessageFlags.Seen, true, ct).ConfigureAwait(false);
        }
    }, ct);

    public Task SetFlagAsync(IEnumerable<string> itemIds, FlagStatus flag, CancellationToken ct = default) => RunAsync(async () =>
    {
        foreach (var (folder, uids) in ByFolder(itemIds))
        {
            var f = await OpenAsync(await FolderAsync(folder, ct).ConfigureAwait(false), FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            if (flag == FlagStatus.Flagged) await f.AddFlagsAsync(uids, MessageFlags.Flagged, true, ct).ConfigureAwait(false);
            else await f.RemoveFlagsAsync(uids, MessageFlags.Flagged, true, ct).ConfigureAwait(false);
        }
    }, ct);

    public Task SetCategoriesAsync(string itemId, IEnumerable<string> categories, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> MarkAllReadAsync(string folderId, bool isRead, CancellationToken ct = default) => RunAsync(async () =>
    {
        var f = await OpenAsync(await FolderAsync(folderId, ct).ConfigureAwait(false), FolderAccess.ReadWrite, ct).ConfigureAwait(false);
        var uids = await f.SearchAsync(isRead ? SearchQuery.NotSeen : SearchQuery.Seen, ct).ConfigureAwait(false);
        if (uids.Count == 0) return true;
        if (isRead) await f.AddFlagsAsync(uids, MessageFlags.Seen, true, ct).ConfigureAwait(false);
        else await f.RemoveFlagsAsync(uids, MessageFlags.Seen, true, ct).ConfigureAwait(false);
        return true;
    }, ct);

    public async Task<IReadOnlyList<string?>> MarkAsJunkAsync(IEnumerable<string> itemIds, bool isJunk, CancellationToken ct = default)
    {
        var target = await RunAsync(async () => isJunk
            ? (await EnsureSpecialFolderAsync(WellKnownFolder.JunkEmail, "Junk", ct).ConfigureAwait(false)).FullName
            : _imap.Inbox.FullName, ct).ConfigureAwait(false);
        return await MoveItemsAsync(itemIds, target, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<string?>> MoveItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default) =>
        TransferAsync(itemIds, destinationFolderId, move: true, ct);

    public Task<IReadOnlyList<string?>> CopyItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default) =>
        TransferAsync(itemIds, destinationFolderId, move: false, ct);

    private Task<IReadOnlyList<string?>> TransferAsync(IEnumerable<string> itemIds, string destinationFolderId, bool move, CancellationToken ct) =>
        RunAsync(async () =>
        {
            var ids = itemIds.ToList();
            var dest = await FolderAsync(destinationFolderId, ct).ConfigureAwait(false);
            var newIds = new Dictionary<string, string?>();
            foreach (var (folder, uids) in ByFolder(ids))
            {
                var f = await OpenAsync(await FolderAsync(folder, ct).ConfigureAwait(false), FolderAccess.ReadWrite, ct).ConfigureAwait(false);
                var map = move
                    ? await f.MoveToAsync(uids, dest, ct).ConfigureAwait(false)
                    : await f.CopyToAsync(uids, dest, ct).ConfigureAwait(false);
                foreach (var uid in uids)
                {
                    newIds[MessageId(f, uid)] = map != null && map.TryGetValue(uid, out var target)
                        ? $"{dest.FullName}{IdSeparator}{target.Validity}{IdSeparator}{target.Id}"
                        : null;
                }
            }
            return (IReadOnlyList<string?>)ids.Select(id => newIds.TryGetValue(id, out var n) ? n : null).ToList();
        }, ct);

    public Task DeleteItemsAsync(IEnumerable<string> itemIds, bool permanent, CancellationToken ct = default) => RunAsync(async () =>
    {
        var trash = permanent ? null : await SpecialFolderAsync(WellKnownFolder.DeletedItems, ct).ConfigureAwait(false);
        foreach (var (folder, uids) in ByFolder(itemIds))
        {
            var f = await OpenAsync(await FolderAsync(folder, ct).ConfigureAwait(false), FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            if (trash != null && trash.FullName != f.FullName) await f.MoveToAsync(uids, trash, ct).ConfigureAwait(false);
            else await DeletePermanentlyAsync(f, uids, ct).ConfigureAwait(false);
        }
    }, ct);

    // ===================================================================== composing

    private Task<MimeMessage> BuildMimeAsync(OutgoingMessage message, CancellationToken ct) =>
        MimeMail.BuildAsync(message, new MailboxAddress(Account.EffectiveDisplayName, Account.EmailAddress),
            async (id, token) => (await LoadMimeAsync(id, token).ConfigureAwait(false)).message, ct);

    public async Task SendAsync(OutgoingMessage message, CancellationToken ct = default)
    {
        if (!message.AllRecipients.Any()) throw new MailServiceException("Укажите хотя бы одного получателя.");
        var mime = await RunAsync(() => BuildMimeAsync(message, ct), ct).ConfigureAwait(false);

        using (var smtp = CreateSmtp())
        {
            await ConnectAndAuthenticateAsync(smtp, Account.SmtpHost, Account.SmtpPort, Account.SmtpSecurity, "SMTP", ct).ConfigureAwait(false);
            try
            {
                // Recipients come from the message (Bcc included); the transmitted text carries no Bcc header.
                await smtp.SendAsync(MimeMail.TransportFormat, mime, ct).ConfigureAwait(false);
            }
            catch (SmtpCommandException ex)
            {
                var detail = ex.ErrorCode switch
                {
                    SmtpErrorCode.RecipientNotAccepted => $"Сервер не принял адрес получателя {ex.Mailbox?.Address}.",
                    SmtpErrorCode.SenderNotAccepted => $"Сервер не разрешает отправку от имени {ex.Mailbox?.Address}.",
                    _ => "Сервер отклонил письмо.",
                };
                throw new MailServiceException($"{detail} Ответ сервера: {ex.Message}", ex.StatusCode.ToString(), ex);
            }
            catch (Exception ex) when (ex is SmtpProtocolException or IOException or SocketException)
            {
                throw new MailConnectionException($"Соединение с SMTP-сервером прервано: {ex.Message}", ex);
            }
            await smtp.DisconnectAsync(true, ct).ConfigureAwait(false);
        }

        // The message is sent: from here on a failure must not be reported as "not sent" (the user would send it
        // again). The Sent copy, draft removal and the "answered" flag are best effort.
        try
        {
            await RunAsync(async () =>
            {
                if (Account.SaveSentCopy)
                {
                    var sent = await EnsureSpecialFolderAsync(WellKnownFolder.SentItems, "Sent", ct).ConfigureAwait(false);
                    await sent.AppendAsync(MimeMail.SendFormat, mime, MessageFlags.Seen, ct).ConfigureAwait(false);
                }
                if (message.Action == ComposeAction.EditDraft && message.ReferenceItemId != null)
                    await DeleteDraftAsync(message.ReferenceItemId, ct).ConfigureAwait(false);
                if (message.Action is ComposeAction.Reply or ComposeAction.ReplyAll && message.ReferenceItemId != null)
                    await MarkAnsweredAsync(message.ReferenceItemId, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MailServiceException or ImapCommandException or ImapProtocolException or IOException)
        {
            Core.Diagnostics.MailLog.Warn?.Invoke($"Письмо отправлено, но копия в «Отправленные» не сохранена: {ex.Message}");
        }
    }

    private async Task MarkAnsweredAsync(string itemId, CancellationToken ct)
    {
        try
        {
            var (folder, _, uid) = ParseId(itemId);
            var f = await OpenAsync(await FolderAsync(folder, ct).ConfigureAwait(false), FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            await f.AddFlagsAsync(new[] { uid }, MessageFlags.Answered, true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ImapCommandException or MailServiceException)
        {
            // Not critical.
        }
    }

    private async Task DeleteDraftAsync(string draftId, CancellationToken ct)
    {
        var (folder, _, uid) = ParseId(draftId);
        var f = await OpenAsync(await FolderAsync(folder, ct).ConfigureAwait(false), FolderAccess.ReadWrite, ct).ConfigureAwait(false);
        await DeletePermanentlyAsync(f, new[] { uid }, ct).ConfigureAwait(false);
    }

    public Task<string> SaveDraftAsync(OutgoingMessage message, CancellationToken ct = default) => RunAsync(async () =>
    {
        var mime = await BuildMimeAsync(message, ct).ConfigureAwait(false);
        var drafts = await EnsureSpecialFolderAsync(WellKnownFolder.Drafts, "Drafts", ct).ConfigureAwait(false);
        var uid = await drafts.AppendAsync(MimeMail.SendFormat, mime, MessageFlags.Seen | MessageFlags.Draft, ct).ConfigureAwait(false);
        if (message.Action == ComposeAction.EditDraft && message.ReferenceItemId != null)
            await DeleteDraftAsync(message.ReferenceItemId, ct).ConfigureAwait(false);
        if (uid is not { } u) return "";
        if (!drafts.IsOpen) await drafts.StatusAsync(StatusItems.UidValidity, ct).ConfigureAwait(false);
        return $"{drafts.FullName}{IdSeparator}{u.Validity}{IdSeparator}{u.Id}";
    }, ct);

    public Task<string> ImportMimeAsync(string folderId, byte[] mime, CancellationToken ct = default) => RunAsync(async () =>
    {
        var f = await FolderAsync(folderId, ct).ConfigureAwait(false);
        var message = await MimeMessage.LoadAsync(new MemoryStream(mime), ct).ConfigureAwait(false);
        var uid = await f.AppendAsync(message, MessageFlags.Seen, ct).ConfigureAwait(false);
        return uid is { } u ? $"{f.FullName}{IdSeparator}{u.Validity}{IdSeparator}{u.Id}" : "";
    }, ct);

    // ===================================================================== not available over IMAP

    private static MailServiceException NotSupported(string what) =>
        new($"{what} недоступны для учётных записей IMAP. Эти функции работают при подключении к Microsoft Exchange.", "NotSupported");

    public Task<IReadOnlyList<Contact>> ResolveNamesAsync(string text, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Contact>>(Array.Empty<Contact>());

    public Task<IReadOnlyList<Contact>> GetContactsAsync(string? folderId = null, CancellationToken ct = default) => throw NotSupported("Контакты");
    public Task<string> CreateContactAsync(Contact contact, string? folderId = null, CancellationToken ct = default) => throw NotSupported("Контакты");
    public Task UpdateContactAsync(Contact contact, CancellationToken ct = default) => throw NotSupported("Контакты");
    public Task RespondToMeetingAsync(string itemId, MeetingResponse response, string? comment = null, CancellationToken ct = default) => throw NotSupported("Ответы на приглашения");
    public Task<OofSettings> GetOutOfOfficeAsync(CancellationToken ct = default) => throw NotSupported("Автоответы");
    public Task SetOutOfOfficeAsync(OofSettings settings, CancellationToken ct = default) => throw NotSupported("Автоответы");

    // ===================================================================== forwarding rules (Sieve)

    /// <summary>Name of the script created when the server has no active one.</summary>
    private const string SieveScriptName = "mailclient";

    private static readonly TimeSpan SieveTimeout = TimeSpan.FromSeconds(60);

    private Task<ManageSieveClient> ConnectSieveAsync(CancellationToken ct) =>
        ManageSieveClient.ConnectAsync(Account.ImapHost.Trim(), Account.SievePort > 0 ? Account.SievePort : ManageSieveClient.DefaultPort,
            requireTls: Account.ImapSecurity != ConnectionSecurity.None, CertificateTrust.CreateCallback(Account), Credential(), ct);

    public async Task<ForwardingRuleSet> GetForwardingRulesAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SieveTimeout);
        await using var sieve = await ConnectSieveAsync(timeout.Token).ConfigureAwait(false);
        var (_, active) = await sieve.ListScriptsAsync(timeout.Token).ConfigureAwait(false);
        var script = active != null ? await sieve.GetScriptAsync(active, timeout.Token).ConfigureAwait(false) : null;
        var extensions = SieveRules.Extensions.Parse(sieve.Capabilities.GetValueOrDefault("SIEVE"));
        return new ForwardingRuleSet
        {
            Rules = SieveRules.Parse(script),
            // Sieve has only "redirect": the original message goes on unchanged, from its sender.
            SupportedModes = [ForwardingMode.Redirect],
            SupportsBodyConditions = extensions.Has("body"),
            Note = SieveRules.HasOtherContent(script)
                ? $"На сервере есть и другие фильтры (скрипт «{active}»). Они продолжают работать, программа их не изменяет."
                : "",
        };
    }

    public async Task SaveForwardingRulesAsync(IReadOnlyList<ForwardingRule> rules, bool replaceOutlookRules, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SieveTimeout);
        await using var sieve = await ConnectSieveAsync(timeout.Token).ConfigureAwait(false);
        var (names, active) = await sieve.ListScriptsAsync(timeout.Token).ConfigureAwait(false);
        // Rules go into the active script (alongside filters made elsewhere), or into our own script.
        var name = active ?? SieveScriptName;
        var script = active != null || names.Contains(SieveScriptName)
            ? await sieve.GetScriptAsync(name, timeout.Token).ConfigureAwait(false)
            : null;
        if (active == null && rules.Count == 0) return;

        var stored = rules.Select(r =>
        {
            var copy = r.Clone();
            if (copy.Id.Length == 0) copy.Id = Guid.NewGuid().ToString("N")[..12];
            copy.Mode = ForwardingMode.Redirect;
            return copy;
        }).ToList();
        var extensions = SieveRules.Extensions.Parse(sieve.Capabilities.GetValueOrDefault("SIEVE"));
        var updated = SieveRules.Apply(script, stored, extensions);
        if (active != null && updated == script) return;
        await sieve.PutScriptAsync(name, updated, timeout.Token).ConfigureAwait(false);
        if (active == null) await sieve.SetActiveAsync(name, timeout.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            if (_imap.IsConnected) _imap.Disconnect(true);
        }
        catch
        {
            // Best effort.
        }
        _imap.Dispose();
    }
}
