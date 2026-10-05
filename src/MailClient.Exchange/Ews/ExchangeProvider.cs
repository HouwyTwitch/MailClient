using System.Xml.Linq;
using MailClient.Core.Mime;
using MailClient.Core.Models;
using MimeKit;
using MailClient.Core.Services;
using MailClient.Exchange.Http;
using static MailClient.Exchange.Ews.Ews;

namespace MailClient.Exchange.Ews;

/// <summary>
/// <see cref="IMailProvider"/> implementation for Microsoft Exchange Server (2010 SP2 → Subscription Edition)
/// using Exchange Web Services — the same protocol Evolution's evolution-ews backend uses.
/// </summary>
public sealed class ExchangeProvider : IMailProvider
{
    private const int PageSizeMax = 1000;

    private readonly EwsClient _ews;
    private readonly bool _supports2013;
    private readonly Dictionary<WellKnownFolder, string> _wellKnownIds = new();

    public AccountSettings Account { get; }

    public ProviderCapabilities Capabilities => ProviderCapabilities.All;

    public ExchangeProvider(AccountSettings account, ICredentialProvider credentials)
        : this(account, ExchangeHttp.CreateClient(account, credentials)) { }

    /// <summary>Constructor used by tests to inject a fake HTTP pipeline.</summary>
    public ExchangeProvider(AccountSettings account, HttpClient http)
    {
        Account = account;
        if (!Uri.TryCreate(account.EwsUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("https" or "http"))
            throw new ArgumentException("Для учётной записи не указан корректный адрес EWS.", nameof(account));
        _ews = new EwsClient(http, endpoint, VersionString(account.ServerVersion));
        _supports2013 = account.ServerVersion >= ExchangeServerVersion.Exchange2013;
    }

    public string? ServerVersion => _ews.LastServerVersion;

    /// <summary>
    /// Best EWS schema version for a server build reported in ServerVersionInfo ("15.2.1544.4" → Exchange2016),
    /// or null when unknown. Exchange 2013 is 15.0, 2016 is 15.1, 2019 and Subscription Edition are 15.2.
    /// </summary>
    public static ExchangeServerVersion? SuggestVersion(string? serverVersion)
    {
        var parts = (serverVersion ?? "").Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return null;
        return major switch
        {
            < 14 => null,
            14 => ExchangeServerVersion.Exchange2010_SP2,
            15 when minor == 0 => ExchangeServerVersion.Exchange2013_SP1,
            _ => ExchangeServerVersion.Exchange2016,
        };
    }

    private string? SharedMailbox => string.IsNullOrWhiteSpace(Account.SharedMailbox) ? null : Account.SharedMailbox.Trim();

    private static string WindowsTimeZoneId()
    {
        var id = TimeZoneInfo.Local.Id;
        if (OperatingSystem.IsWindows()) return id;
        return TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win) ? win : "UTC";
    }

    // ===================================================================== folder ids

    /// <summary>Builds a FolderId or DistinguishedFolderId element (for well-known names like "inbox").</summary>
    private XElement FolderIdElement(string id)
    {
        if (DistinguishedFolders.ContainsKey(id))
        {
            var e = new XElement(T + "DistinguishedFolderId", new XAttribute("Id", id.ToLowerInvariant()));
            if (SharedMailbox != null)
                e.Add(new XElement(T + "Mailbox", new XElement(T + "EmailAddress", SharedMailbox)));
            return e;
        }
        return new XElement(T + "FolderId", new XAttribute("Id", id));
    }

    private string WellKnownOrName(WellKnownFolder wk, string distinguishedName) =>
        _wellKnownIds.TryGetValue(wk, out var id) ? id : distinguishedName;

    // ===================================================================== shapes

    private IEnumerable<XElement> SummaryProperties()
    {
        yield return FieldUri("item:Subject");
        yield return FieldUri("item:DateTimeReceived");
        yield return FieldUri("item:DateTimeSent");
        yield return FieldUri("item:HasAttachments");
        yield return FieldUri("item:Importance");
        yield return FieldUri("item:Size");
        yield return FieldUri("item:DisplayTo");
        yield return FieldUri("item:DisplayCc");
        yield return FieldUri("item:ItemClass");
        yield return FieldUri("item:Categories");
        yield return FieldUri("item:ConversationId");
        yield return FieldUri("message:From");
        yield return FieldUri("message:IsRead");
        if (_supports2013)
        {
            yield return FieldUri("item:Preview");
            yield return FieldUri("item:Flag");
        }
        else
        {
            yield return ExtendedFieldUri(EwsParser.FlagStatusPropTag, "Integer");
        }
    }

    private XElement SummaryShape(XName name) =>
        new(name,
            new XElement(T + "BaseShape", "IdOnly"),
            new XElement(T + "AdditionalProperties", SummaryProperties()));

    private static IEnumerable<XElement> ItemElements(XElement? items) =>
        items?.Elements().Where(e => e.Element(T + "ItemId") != null) ?? Enumerable.Empty<XElement>();

    // ===================================================================== connect

    /// <summary>
    /// Connectivity check exactly as Thunderbird does it: GetFolder for the mailbox root (IdOnly), sent with
    /// the conservative Exchange2007_SP1 schema version so that any Exchange server accepts it. The server's
    /// real version comes back in the ServerVersionInfo header (see <see cref="SuggestVersion"/>).
    /// </summary>
    public async Task<MailboxInfo> ConnectAsync(CancellationToken ct = default)
    {
        var request = new XElement(M + "GetFolder",
            new XElement(M + "FolderShape", new XElement(T + "BaseShape", "IdOnly")),
            new XElement(M + "FolderIds", FolderIdElement("msgfolderroot")));
        var response = await _ews.SendAsync(request, ct, requestVersion: "Exchange2007_SP1").ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        return new MailboxInfo
        {
            EmailAddress = SharedMailbox ?? Account.EmailAddress,
            DisplayName = Account.EffectiveDisplayName,
            ServerVersion = _ews.LastServerVersion ?? "",
        };
    }

    // ===================================================================== folders

    /// <summary>
    /// Well-known folders resolved first, as Thunderbird does (rust/protocol_shared EXCHANGE_DISTINGUISHED_IDS).
    /// "archive" is left out: it is not in the Exchange 2016 schema and older servers reject the whole request.
    /// </summary>
    private static readonly string[] WellKnownNames =
        { "msgfolderroot", "inbox", "deleteditems", "drafts", "outbox", "sentitems", "junkemail" };

    /// <summary>Microsoft's recommended batch size for GetFolder/GetItem (also used by Thunderbird).</summary>
    private const int BatchSize = 10;

    private string? _hierarchySyncState;
    private readonly Dictionary<string, MailFolder> _folders = new();
    private MailFolder? _root;

    /// <summary>
    /// Folder tree, synchronized the way Thunderbird does it (ews_xpcom sync_folder_hierarchy.rs):
    /// 1) GetFolder IdOnly for the well-known folders; 2) SyncFolderHierarchy (IdOnly) from msgfolderroot with
    /// the previous sync state, so later calls only transfer changes; 3) GetFolder AllProperties for created or
    /// updated folders in batches of 10. Only plain (mail) folders are kept, as in Thunderbird.
    /// </summary>
    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken ct = default)
    {
        if (_root == null || _hierarchySyncState == null)
            await LoadWellKnownFoldersAsync(ct).ConfigureAwait(false);

        var created = new List<string>();
        var updated = new HashSet<string>();
        var deleted = new HashSet<string>();
        while (true)
        {
            var request = new XElement(M + "SyncFolderHierarchy",
                new XElement(M + "FolderShape", new XElement(T + "BaseShape", "IdOnly")),
                new XElement(M + "SyncFolderId", FolderIdElement("msgfolderroot")));
            if (_hierarchySyncState != null) request.Add(new XElement(M + "SyncState", _hierarchySyncState));
            XElement response;
            try
            {
                response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
                EwsClient.ThrowOnError(response);
            }
            catch (SyncStateInvalidException)
            {
                _hierarchySyncState = null;
                _folders.Clear();
                continue;
            }
            var msg = EwsClient.ResponseMessages(response).First();
            foreach (var change in msg.Element(M + "Changes")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (change.Name.LocalName == "Delete")
                {
                    if ((string?)change.Element(T + "FolderId")?.Attribute("Id") is { } gone) deleted.Add(gone);
                    continue;
                }
                // Like Thunderbird, only plain folders (mail); calendar/contacts/tasks/search folders are skipped.
                var folder = change.Element(T + "Folder");
                if ((string?)folder?.Element(T + "FolderId")?.Attribute("Id") is not { } id) continue;
                if (change.Name.LocalName == "Create") created.Add(id);
                else updated.Add(id);
            }
            _hierarchySyncState = msg.Element(M + "SyncState")?.Value;
            if (ParseBool(msg.Element(M + "IncludesLastFolderInRange")?.Value) || _hierarchySyncState == null) break;
        }

        foreach (var id in deleted) _folders.Remove(id);
        var toFetch = created.Concat(updated).Where(id => !deleted.Contains(id)).Distinct().ToList();
        var wellKnownById = _wellKnownIds.ToDictionary(kv => kv.Value, kv => kv.Key);
        foreach (var batch in toFetch.Chunk(BatchSize))
        {
            var request = new XElement(M + "GetFolder",
                new XElement(M + "FolderShape",
                    new XElement(T + "BaseShape", "AllProperties"),
                    new XElement(T + "AdditionalProperties", ExtendedFieldUri(EwsParser.HiddenPropTag, "Boolean"))),
                new XElement(M + "FolderIds", batch.Select(id => new XElement(T + "FolderId", new XAttribute("Id", id)))));
            var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
            foreach (var message in EwsClient.ResponseMessages(response))
            {
                // A folder deleted between the two calls is simply skipped.
                if ((string?)message.Attribute("ResponseClass") == "Error") continue;
                var fe = message.Element(M + "Folders")?.Elements().FirstOrDefault(EwsParser.IsFolderElement);
                if (fe == null) continue;
                var f = EwsParser.ParseFolder(fe);
                if (EwsParser.IsHidden(fe))
                {
                    _folders.Remove(f.Id);
                    continue;
                }
                f.WellKnown = wellKnownById.TryGetValue(f.Id, out var wk) ? wk : WellKnownFolder.None;
                _folders[f.Id] = f;
            }
        }

        var result = new List<MailFolder> { _root! };
        result.AddRange(_folders.Values);
        // Drop folders whose parent is hidden or unknown (they would be orphans in the tree).
        var ids = new HashSet<string>(result.Select(f => f.Id));
        bool removed;
        do
        {
            removed = result.RemoveAll(f => f.ParentId != null && !ids.Contains(f.ParentId)) > 0;
            if (removed) ids = new HashSet<string>(result.Select(f => f.Id));
        } while (removed);
        return result;
    }

    private async Task LoadWellKnownFoldersAsync(CancellationToken ct)
    {
        var request = new XElement(M + "GetFolder",
            new XElement(M + "FolderShape", new XElement(T + "BaseShape", "IdOnly")),
            new XElement(M + "FolderIds", WellKnownNames.Select(FolderIdElement)));
        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        var messages = EwsClient.ResponseMessages(response).ToList();
        if (messages.Count != WellKnownNames.Length)
            throw new MailServiceException("Сервер вернул неожиданное число ответов на запрос стандартных папок.");
        // Any error on the root folder is fatal (Thunderbird does the same); others are optional.
        EwsClient.ThrowIfError(messages[0]);
        for (int i = 0; i < messages.Count; i++)
        {
            if ((string?)messages[i].Attribute("ResponseClass") == "Error") continue;
            var fe = messages[i].Element(M + "Folders")?.Elements().FirstOrDefault();
            if ((string?)fe?.Element(T + "FolderId")?.Attribute("Id") is { } id)
                _wellKnownIds[DistinguishedFolders[WellKnownNames[i]]] = id;
        }
        _root = new MailFolder
        {
            Id = _wellKnownIds[WellKnownFolder.Root],
            WellKnown = WellKnownFolder.Root,
            DisplayName = SharedMailbox ?? Account.EmailAddress,
            FolderClass = "IPF.Note",
        };
        _hierarchySyncState = null;
        _folders.Clear();
    }

    public async Task<MailFolder> CreateFolderAsync(string parentFolderId, string name, FolderKind kind = FolderKind.Mail, CancellationToken ct = default)
    {
        var folderClass = kind switch
        {
            FolderKind.Calendar => "IPF.Appointment",
            FolderKind.Contacts => "IPF.Contact",
            FolderKind.Tasks => "IPF.Task",
            FolderKind.Notes => "IPF.StickyNote",
            _ => "IPF.Note",
        };
        var request = new XElement(M + "CreateFolder",
            new XElement(M + "ParentFolderId", FolderIdElement(parentFolderId)),
            new XElement(M + "Folders",
                new XElement(T + "Folder",
                    new XElement(T + "FolderClass", folderClass),
                    new XElement(T + "DisplayName", name))));
        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        var id = response.Descendants(T + "FolderId").First();
        return new MailFolder
        {
            Id = (string)id.Attribute("Id")!,
            ChangeKey = (string?)id.Attribute("ChangeKey") ?? "",
            ParentId = parentFolderId,
            DisplayName = name,
            FolderClass = folderClass,
        };
    }

    public async Task RenameFolderAsync(string folderId, string newName, CancellationToken ct = default)
    {
        var request = new XElement(M + "UpdateFolder",
            new XElement(M + "FolderChanges",
                new XElement(T + "FolderChange",
                    FolderIdElement(folderId),
                    new XElement(T + "Updates",
                        new XElement(T + "SetFolderField",
                            FieldUri("folder:DisplayName"),
                            new XElement(T + "Folder", new XElement(T + "DisplayName", newName)))))));
        EwsClient.ThrowOnError(await _ews.SendAsync(request, ct).ConfigureAwait(false));
    }

    public async Task MoveFolderAsync(string folderId, string newParentFolderId, CancellationToken ct = default)
    {
        var request = new XElement(M + "MoveFolder",
            new XElement(M + "ToFolderId", FolderIdElement(newParentFolderId)),
            new XElement(M + "FolderIds", FolderIdElement(folderId)));
        EwsClient.ThrowOnError(await _ews.SendAsync(request, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Deleting a folder moves it to Deleted Items (MoveFolder); deleting it from there is permanent
    /// (erase_folder.rs: DeleteFolder with HardDelete).
    /// </summary>
    public async Task DeleteFolderAsync(string folderId, bool permanent, CancellationToken ct = default)
    {
        if (!permanent)
        {
            await MoveFolderAsync(folderId, WellKnownOrName(WellKnownFolder.DeletedItems, "deleteditems"), ct).ConfigureAwait(false);
            return;
        }
        var request = new XElement(M + "DeleteFolder",
            new XAttribute("DeleteType", "HardDelete"),
            new XElement(M + "FolderIds", FolderIdElement(folderId)));
        EwsClient.ThrowOnError(await _ews.SendAsync(request, ct).ConfigureAwait(false), "ErrorItemNotFound", "ErrorFolderNotFound");
    }

    /// <summary>
    /// Emptying Deleted Items / Junk is permanent (erase_folder.rs: EmptyFolder HardDelete with subfolders);
    /// emptying any other folder moves its messages to Deleted Items.
    /// </summary>
    public async Task EmptyFolderAsync(string folderId, bool deleteSubFolders, CancellationToken ct = default)
    {
        bool isTrash = _wellKnownIds.TryGetValue(WellKnownFolder.DeletedItems, out var del) && del == folderId
                       || _wellKnownIds.TryGetValue(WellKnownFolder.JunkEmail, out var junk) && junk == folderId
                       || folderId.Equals("deleteditems", StringComparison.OrdinalIgnoreCase);
        var request = new XElement(M + "EmptyFolder",
            new XAttribute("DeleteType", isTrash ? "HardDelete" : "MoveToDeletedItems"),
            new XAttribute("DeleteSubFolders", isTrash || deleteSubFolders ? "true" : "false"),
            new XElement(M + "FolderIds", FolderIdElement(folderId)));
        EwsClient.ThrowOnError(await _ews.SendAsync(request, ct).ConfigureAwait(false));
    }

    // ===================================================================== message lists

    public Task<MessagePage> GetMessagesAsync(string folderId, int offset, int pageSize, CancellationToken ct = default) =>
        FindMessagesAsync(folderId, null, offset, pageSize, ct);

    public async Task<MessagePage> SearchMessagesAsync(string folderId, string query, int offset, int pageSize, CancellationToken ct = default)
    {
        try
        {
            // AQS search (uses the server's content index: subject, body, people, attachments).
            return await FindMessagesAsync(folderId, new XElement(M + "QueryString", query), offset, pageSize, ct).ConfigureAwait(false);
        }
        catch (EwsResponseException)
        {
            // Fall back to a restriction search when content indexing is unavailable.
            var restriction = new XElement(M + "Restriction",
                new XElement(T + "Or",
                    Contains("item:Subject", query),
                    Contains("item:Body", query),
                    Contains("item:DisplayTo", query),
                    Contains("item:DisplayCc", query)));
            return await FindMessagesAsync(folderId, restriction, offset, pageSize, ct).ConfigureAwait(false);
        }

        static XElement Contains(string field, string value) =>
            new(T + "Contains",
                new XAttribute("ContainmentMode", "Substring"),
                new XAttribute("ContainmentComparison", "IgnoreCase"),
                FieldUri(field),
                new XElement(T + "Constant", new XAttribute("Value", value)));
    }

    /// <summary>
    /// Message headers for the given ids, fetched like Thunderbird (ews_xpcom client.rs get_items): GetItem with
    /// IdOnly + explicit AdditionalProperties, 10 ids per request. Items deleted in the meantime are skipped.
    /// </summary>
    private async Task<List<MessageSummary>> GetSummariesAsync(IEnumerable<string> itemIds, string folderId, CancellationToken ct)
    {
        var result = new List<MessageSummary>();
        foreach (var batch in itemIds.Distinct().Chunk(BatchSize))
        {
            var request = new XElement(M + "GetItem",
                SummaryShape(M + "ItemShape"),
                new XElement(M + "ItemIds", batch.Select(id => ItemId(id))));
            var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
            foreach (var message in EwsClient.ResponseMessages(response))
            {
                if ((string?)message.Attribute("ResponseClass") == "Error")
                {
                    var code = message.Element(M + "ResponseCode")?.Value;
                    if (code is "ErrorItemNotFound" or "ErrorMessageDisposalNotFound") continue;
                    EwsClient.ThrowIfError(message);
                }
                foreach (var item in ItemElements(message.Element(M + "Items")))
                {
                    var summary = EwsParser.ParseSummary(item);
                    summary.FolderId = folderId;
                    result.Add(summary);
                }
            }
        }
        return result;
    }

    /// <summary>FindItem returning ids only (newest first); details come from <see cref="GetSummariesAsync"/>.</summary>
    private async Task<MessagePage> FindMessagesAsync(string folderId, XElement? filter, int offset, int pageSize, CancellationToken ct)
    {
        var request = new XElement(M + "FindItem", new XAttribute("Traversal", "Shallow"),
            new XElement(M + "ItemShape", new XElement(T + "BaseShape", "IdOnly")),
            new XElement(M + "IndexedPageItemView",
                new XAttribute("MaxEntriesReturned", Math.Clamp(pageSize, 1, PageSizeMax)),
                new XAttribute("Offset", offset),
                new XAttribute("BasePoint", "Beginning")));
        if (filter?.Name == M + "Restriction") request.Add(filter);
        request.Add(new XElement(M + "SortOrder",
            new XElement(T + "FieldOrder", new XAttribute("Order", "Descending"), FieldUri("item:DateTimeReceived"))));
        request.Add(new XElement(M + "ParentFolderIds", FolderIdElement(folderId)));
        if (filter?.Name == M + "QueryString") request.Add(filter);

        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        var root = response.Descendants(M + "RootFolder").FirstOrDefault();
        var ids = ItemElements(root?.Element(T + "Items"))
            .Select(e => (string?)e.Element(T + "ItemId")?.Attribute("Id"))
            .OfType<string>().ToList();
        var details = (await GetSummariesAsync(ids, folderId, ct).ConfigureAwait(false)).ToDictionary(m => m.Id);
        return new MessagePage
        {
            // Keep the server's (date) order.
            Items = ids.Where(details.ContainsKey).Select(id => details[id]).ToList(),
            TotalCount = ParseInt((string?)root?.Attribute("TotalItemsInView")),
            HasMore = !ParseBool((string?)root?.Attribute("IncludesLastItemInRange")),
        };
    }

    /// <summary>
    /// Incremental item sync as in Thunderbird (ews_xpcom sync_messages_for_folder.rs): SyncFolderItems with
    /// IdOnly — Microsoft's guidance, and the server silently drops some properties in sync responses — then the
    /// headers of created/updated items via GetItem in batches of 10.
    /// </summary>
    public async Task<FolderSyncResult> SyncFolderItemsAsync(string folderId, string? syncState, int maxChanges, CancellationToken ct = default)
    {
        var request = new XElement(M + "SyncFolderItems",
            new XElement(M + "ItemShape", new XElement(T + "BaseShape", "IdOnly")),
            new XElement(M + "SyncFolderId", FolderIdElement(folderId)));
        if (!string.IsNullOrEmpty(syncState)) request.Add(new XElement(M + "SyncState", syncState));
        request.Add(new XElement(M + "MaxChangesReturned", Math.Clamp(maxChanges, 1, 512)));

        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        var msg = EwsClient.ResponseMessages(response).First();
        var result = new FolderSyncResult
        {
            SyncState = msg.Element(M + "SyncState")?.Value ?? "",
            IncludesLastItem = ParseBool(msg.Element(M + "IncludesLastItemInRange")?.Value),
        };
        var toFetch = new List<string>();
        foreach (var change in msg.Element(M + "Changes")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            switch (change.Name.LocalName)
            {
                case "Create":
                case "Update":
                    foreach (var item in ItemElements(change))
                        if ((string?)item.Element(T + "ItemId")?.Attribute("Id") is { } id) toFetch.Add(id);
                    break;
                case "Delete":
                    if (change.Element(T + "ItemId")?.Attribute("Id")?.Value is { } deletedId)
                        result.Deleted.Add(deletedId);
                    break;
                case "ReadFlagChange":
                    if (change.Element(T + "ItemId")?.Attribute("Id")?.Value is { } readId)
                        result.ReadFlagChanges[readId] = ParseBool(change.Element(T + "IsRead")?.Value);
                    break;
            }
        }
        var deleted = new HashSet<string>(result.Deleted);
        result.CreatedOrUpdated.AddRange(await GetSummariesAsync(toFetch.Where(id => !deleted.Contains(id)), folderId, ct).ConfigureAwait(false));
        return result;
    }

    // ===================================================================== single items
    // Ported from Thunderbird (comm-central rust/ews_xpcom/src/client/*.rs). Messages travel as MIME: read with
    // GetItem + IncludeMimeContent and parsed locally (get_message.rs); composed locally and sent with
    // CreateItem SendOnly (send_message.rs); drafts/imports stored with CreateItem SaveOnly + PR_MESSAGE_FLAGS
    // (create_message.rs).

    /// <summary>Small MIME cache: opening a message, showing inline images and saving attachments reuse one download.</summary>
    private readonly LinkedList<(string id, byte[] mime)> _mimeCache = new();
    private const int MimeCacheSize = 8;

    private void RememberMime(string id, byte[] mime)
    {
        lock (_mimeCache)
        {
            var existing = _mimeCache.FirstOrDefault(e => e.id == id);
            if (existing.id != null) _mimeCache.Remove(existing);
            _mimeCache.AddFirst((id, mime));
            while (_mimeCache.Count > MimeCacheSize) _mimeCache.RemoveLast();
        }
    }

    private byte[]? CachedMime(string id)
    {
        lock (_mimeCache) return _mimeCache.FirstOrDefault(e => e.id == id).mime;
    }

    private async Task<MimeMessage> LoadMimeMessageAsync(string itemId, CancellationToken ct) =>
        MimeMail.Parse(await GetMimeContentAsync(itemId, ct).ConfigureAwait(false));

    /// <summary>
    /// Thunderbird's get_message.rs: GetItem IdOnly with IncludeMimeContent. We also request the list properties
    /// (read state, flag, item class…) in the same call, so the reading pane shows the server's current state.
    /// </summary>
    public async Task<MailMessage> GetMessageAsync(string itemId, CancellationToken ct = default)
    {
        var request = new XElement(M + "GetItem",
            new XElement(M + "ItemShape",
                new XElement(T + "BaseShape", "IdOnly"),
                new XElement(T + "IncludeMimeContent", "true"),
                new XElement(T + "AdditionalProperties", SummaryProperties())),
            new XElement(M + "ItemIds", ItemId(itemId)));
        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        var item = ItemElements(response.Descendants(M + "Items").FirstOrDefault()).FirstOrDefault()
                   ?? throw new MailServiceException("Сервер не вернул запрошенное письмо (возможно, оно удалено).", "ErrorItemNotFound");

        var message = new MailMessage();
        EwsParser.FillSummary(message, item);
        var mimeText = item.Element(T + "MimeContent")?.Value;
        if (string.IsNullOrEmpty(mimeText))
            throw new MailServiceException("Сервер не вернул содержимое письма в формате MIME.");
        var mime = Convert.FromBase64String(mimeText);
        RememberMime(itemId, mime);
        MimeMail.Fill(message, MimeMail.Parse(mime), itemId);
        message.Id = itemId;

        // Meeting requests: the calendar data (time, place) is read from the server, not from the MIME body.
        if (message.IsMeetingRequest || message.IsMeetingCancellation)
        {
            try
            {
                var meeting = new XElement(M + "GetItem",
                    new XElement(M + "ItemShape", new XElement(T + "BaseShape", "AllProperties")),
                    new XElement(M + "ItemIds", ItemId(itemId)));
                var meetingResponse = await _ews.SendAsync(meeting, ct, WindowsTimeZoneId()).ConfigureAwait(false);
                EwsClient.ThrowOnError(meetingResponse);
                if (ItemElements(meetingResponse.Descendants(M + "Items").FirstOrDefault()).FirstOrDefault() is { } mi)
                {
                    message.Meeting = EwsParser.ParseCalendarItem(mi);
                    message.Meeting.Organizer ??= message.From;
                }
            }
            catch (MailServiceException ex)
            {
                Core.Diagnostics.MailLog.Warn?.Invoke($"Данные приглашения не получены: {ex.Message}");
            }
        }
        return message;
    }

    private async Task<string> GetChangeKeyAsync(string itemId, CancellationToken ct)
    {
        var request = new XElement(M + "GetItem",
            new XElement(M + "ItemShape", new XElement(T + "BaseShape", "IdOnly")),
            new XElement(M + "ItemIds", ItemId(itemId)));
        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        return (string?)response.Descendants(T + "ItemId").FirstOrDefault()?.Attribute("ChangeKey") ?? "";
    }

    public async Task<byte[]> GetMimeContentAsync(string itemId, CancellationToken ct = default)
    {
        if (CachedMime(itemId) is { } cached) return cached;
        var request = new XElement(M + "GetItem",
            new XElement(M + "ItemShape",
                new XElement(T + "BaseShape", "IdOnly"),
                new XElement(T + "IncludeMimeContent", "true")),
            new XElement(M + "ItemIds", ItemId(itemId)));
        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        var mime = response.Descendants(T + "MimeContent").FirstOrDefault()
                   ?? throw new MailServiceException("Сервер не вернул содержимое письма в формате MIME.");
        var bytes = Convert.FromBase64String(mime.Value);
        RememberMime(itemId, bytes);
        return bytes;
    }

    public async Task<AttachmentContent> GetAttachmentAsync(string attachmentId, CancellationToken ct = default) =>
        (await GetAttachmentsAsync(new[] { attachmentId }, ct).ConfigureAwait(false))[0];

    /// <summary>
    /// Attachments are MIME parts of the message (as in Thunderbird). Ids of the older EWS-attachment form,
    /// still present in locally cached messages, are served with GetAttachment.
    /// </summary>
    public async Task<IReadOnlyList<AttachmentContent>> GetAttachmentsAsync(IEnumerable<string> attachmentIds, CancellationToken ct = default)
    {
        var ids = attachmentIds.ToList();
        var result = new Dictionary<string, AttachmentContent>();
        foreach (var group in ids.Where(MimeMail.IsPartId).GroupBy(id => MimeMail.SplitPartId(id).itemId))
        {
            var mime = await LoadMimeMessageAsync(group.Key, ct).ConfigureAwait(false);
            foreach (var id in group) result[id] = await MimeMail.ExtractAsync(mime, id, ct).ConfigureAwait(false);
        }
        var legacy = ids.Where(id => !MimeMail.IsPartId(id)).ToList();
        if (legacy.Count > 0)
        {
            var request = new XElement(M + "GetAttachment",
                new XElement(M + "AttachmentShape", new XElement(T + "IncludeMimeContent", "true")),
                new XElement(M + "AttachmentIds", legacy.Select(id => new XElement(T + "AttachmentId", new XAttribute("Id", id)))));
            var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
            EwsClient.ThrowOnError(response);
            foreach (var a in EwsClient.ResponseMessages(response).SelectMany(m => m.Element(M + "Attachments")?.Elements() ?? Enumerable.Empty<XElement>()))
            {
                var info = EwsParser.ParseAttachments(new XElement(T + "Attachments", a)).First();
                var content = a.Name.LocalName == "FileAttachment"
                    ? Convert.FromBase64String(a.Element(T + "Content")?.Value ?? "")
                    : a.Descendants(T + "MimeContent").FirstOrDefault() is { } m ? Convert.FromBase64String(m.Value) : Array.Empty<byte>();
                info.Size = content.Length;
                result[info.Id] = new AttachmentContent { Info = info, Content = content };
            }
        }
        return ids.Where(result.ContainsKey).Select(id => result[id]).ToList();
    }

    // ===================================================================== item updates

    /// <summary>
    /// UpdateItem as Thunderbird sends it (change_read_status.rs, change_flag_status.rs): no ChangeKey and
    /// ConflictResolution=AlwaysOverwrite. AutoResolve without a ChangeKey is rejected by Exchange
    /// (ErrorChangeKeyRequiredForWriteOperations) — the "cannot mark as read" error.
    /// </summary>
    private async Task UpdateItemsAsync(IEnumerable<string> itemIds, Func<XElement[]> updates, bool isMessage, CancellationToken ct)
    {
        foreach (var batch in itemIds.Chunk(100))
        {
            var request = new XElement(M + "UpdateItem",
                new XAttribute("ConflictResolution", "AlwaysOverwrite"),
                new XElement(M + "ItemChanges", batch.Select(id =>
                    new XElement(T + "ItemChange", ItemId(id), new XElement(T + "Updates", updates())))));
            if (isMessage) request.Add(new XAttribute("MessageDisposition", "SaveOnly"));
            var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
            // Items deleted meanwhile are not an error for the user (Thunderbird logs and continues).
            EwsClient.ThrowOnError(response, "ErrorItemNotFound");
        }
    }

    public Task SetReadStateAsync(IEnumerable<string> itemIds, bool isRead, CancellationToken ct = default) =>
        UpdateItemsAsync(itemIds, () => new[]
        {
            new XElement(T + "SetItemField", FieldUri("message:IsRead"),
                new XElement(T + "Message", Bool(T + "IsRead", isRead))),
        }, true, ct);

    /// <summary>
    /// change_flag_status.rs: sets both item:Flag and PR_FLAG_STATUS (2 = flagged, 0 = not flagged) in one change,
    /// so Outlook and older clients agree. Exchange 2010 has no item:Flag, only the MAPI property.
    /// </summary>
    public Task SetFlagAsync(IEnumerable<string> itemIds, FlagStatus flag, CancellationToken ct = default)
    {
        var pidValue = flag switch { FlagStatus.Flagged => "2", FlagStatus.Complete => "1", _ => "0" };
        return UpdateItemsAsync(itemIds, () =>
        {
            var list = new List<XElement>();
            if (_supports2013)
                list.Add(new XElement(T + "SetItemField", FieldUri("item:Flag"),
                    new XElement(T + "Message", new XElement(T + "Flag", new XElement(T + "FlagStatus", flag.ToString())))));
            list.Add(new XElement(T + "SetItemField", ExtendedFieldUri(EwsParser.FlagStatusPropTag, "Integer"),
                new XElement(T + "Message",
                    new XElement(T + "ExtendedProperty",
                        ExtendedFieldUri(EwsParser.FlagStatusPropTag, "Integer"),
                        new XElement(T + "Value", pidValue)))));
            return list.ToArray();
        }, true, ct);
    }

    public Task SetCategoriesAsync(string itemId, IEnumerable<string> categories, CancellationToken ct = default)
    {
        var list = categories.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        return UpdateItemsAsync(new[] { itemId }, () => list.Count == 0
            ? new[] { new XElement(T + "DeleteItemField", FieldUri("item:Categories")) }
            : new[]
            {
                new XElement(T + "SetItemField", FieldUri("item:Categories"),
                    new XElement(T + "Message",
                        new XElement(T + "Categories", list.Select(c => new XElement(T + "String", c))))),
            }, true, ct);
    }

    /// <summary>
    /// change_read_status_all.rs: MarkAllItemsAsRead (Exchange 2013+) with SuppressReadReceipts.
    /// Returns false on older servers so the caller marks items one by one.
    /// </summary>
    public async Task<bool> MarkAllReadAsync(string folderId, bool isRead, CancellationToken ct = default)
    {
        if (!_supports2013) return false;
        var request = new XElement(M + "MarkAllItemsAsRead",
            Bool(M + "ReadFlag", isRead),
            Bool(M + "SuppressReadReceipts", true),
            new XElement(M + "FolderIds", FolderIdElement(folderId)));
        EwsClient.ThrowOnError(await _ews.SendAsync(request, ct).ConfigureAwait(false));
        return true;
    }

    /// <summary>mark_as_junk.rs: MarkAsJunk with MoveItem (Exchange 2013+), otherwise a plain move.</summary>
    public async Task<IReadOnlyList<string?>> MarkAsJunkAsync(IEnumerable<string> itemIds, bool isJunk, CancellationToken ct = default)
    {
        var ids = itemIds.ToList();
        if (!_supports2013)
            return await MoveItemsAsync(ids, isJunk ? WellKnownOrName(WellKnownFolder.JunkEmail, "junkemail") : WellKnownOrName(WellKnownFolder.Inbox, "inbox"), ct)
                .ConfigureAwait(false);
        var request = new XElement(M + "MarkAsJunk",
            new XAttribute("IsJunk", isJunk ? "true" : "false"),
            new XAttribute("MoveItem", "true"),
            ItemIds(ids));
        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        return EwsClient.ResponseMessages(response)
            .Select(m => (string?)m.Element(M + "MovedItemId")?.Attribute("Id"))
            .ToList();
    }

    public Task<IReadOnlyList<string?>> MoveItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default) =>
        MoveOrCopyAsync("MoveItem", itemIds, destinationFolderId, ct);

    public Task<IReadOnlyList<string?>> CopyItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default) =>
        MoveOrCopyAsync("CopyItem", itemIds, destinationFolderId, ct);

    /// <summary>copy_move_item.rs: ReturnNewItemIds on servers newer than Exchange 2010.</summary>
    private async Task<IReadOnlyList<string?>> MoveOrCopyAsync(string op, IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct)
    {
        var result = new List<string?>();
        foreach (var batch in itemIds.Chunk(100))
        {
            var request = new XElement(M + op,
                new XElement(M + "ToFolderId", FolderIdElement(destinationFolderId)),
                ItemIds(batch),
                Bool(M + "ReturnNewItemIds", true));
            var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
            EwsClient.ThrowOnError(response, "ErrorItemNotFound");
            foreach (var msg in EwsClient.ResponseMessages(response))
                result.Add((string?)msg.Descendants(T + "ItemId").FirstOrDefault()?.Attribute("Id"));
        }
        return result;
    }

    /// <summary>
    /// Thunderbird moves deleted messages to Deleted Items with MoveItem and only hard-deletes from there
    /// (delete_messages.rs: HardDelete in batches of 1000, ErrorItemNotFound ignored).
    /// </summary>
    public async Task DeleteItemsAsync(IEnumerable<string> itemIds, bool permanent, CancellationToken ct = default)
    {
        var ids = itemIds.ToList();
        if (!permanent)
        {
            await MoveItemsAsync(ids, WellKnownOrName(WellKnownFolder.DeletedItems, "deleteditems"), ct).ConfigureAwait(false);
            return;
        }
        foreach (var batch in ids.Chunk(1000))
        {
            var request = new XElement(M + "DeleteItem",
                new XAttribute("DeleteType", "HardDelete"),
                new XAttribute("SendMeetingCancellations", "SendToNone"),
                new XAttribute("AffectedTaskOccurrences", "AllOccurrences"),
                ItemIds(batch));
            EwsClient.ThrowOnError(await _ews.SendAsync(request, ct).ConfigureAwait(false), "ErrorItemNotFound");
        }
    }

    // ===================================================================== sending

    private MailboxAddress Author => new(Account.EffectiveDisplayName, SharedMailbox ?? Account.EmailAddress);

    private Task<MimeMessage> BuildMimeAsync(OutgoingMessage message, CancellationToken ct) =>
        MimeMail.BuildAsync(message, Author, LoadMimeMessageAsync, ct);

    /// <summary>
    /// send_message.rs: CreateItem with the MIME content and MessageDisposition=SendOnly; Bcc recipients are
    /// passed as BccRecipients (they are not in the transmitted MIME). Like Thunderbird's "copy to Sent",
    /// the message is then stored in Sent Items with CreateItem SaveOnly.
    /// </summary>
    public async Task SendAsync(OutgoingMessage message, CancellationToken ct = default)
    {
        if (!message.AllRecipients.Any())
            throw new MailServiceException("Укажите хотя бы одного получателя.");
        var mime = await BuildMimeAsync(message, ct).ConfigureAwait(false);

        var item = new XElement(T + "Message",
            new XElement(T + "MimeContent", Convert.ToBase64String(MimeMail.ToBytes(mime, MimeMail.TransportFormat))));
        if (message.Bcc.Count > 0)
            item.Add(new XElement(T + "BccRecipients", message.Bcc.Select(a => Mailbox(a.Name, a.Address))));
        item.Add(Bool(T + "IsDeliveryReceiptRequested", message.RequestDeliveryReceipt));
        item.Add(new XElement(T + "InternetMessageId", $"<{mime.MessageId}>"));
        var request = new XElement(M + "CreateItem",
            new XAttribute("MessageDisposition", "SendOnly"),
            new XElement(M + "Items", item));
        EwsClient.ThrowOnError(await _ews.SendAsync(request, ct).ConfigureAwait(false));

        // The message is sent; failures from here on must not report "not sent".
        if (Account.SaveSentCopy)
        {
            try
            {
                await CreateMimeItemAsync("sentitems", MimeMail.ToBytes(mime, MimeMail.SendFormat),
                    MimeMail.MsgFlagRead | MimeMail.MsgFlagUnmodified, isRead: true, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is MailServiceException)
            {
                Core.Diagnostics.MailLog.Warn?.Invoke($"Письмо отправлено, но копия в «Отправленные» не сохранена: {ex.Message}");
            }
        }
        if (message.Action == ComposeAction.EditDraft && !string.IsNullOrEmpty(message.ReferenceItemId))
        {
            try { await DeleteItemsAsync(new[] { message.ReferenceItemId }, true, ct).ConfigureAwait(false); }
            catch (MailServiceException ex) { Core.Diagnostics.MailLog.Warn?.Invoke($"Черновик после отправки не удалён: {ex.Message}"); }
        }
    }

    /// <summary>create_message.rs: CreateItem SaveOnly with MIME, IsRead and PR_MESSAGE_FLAGS.</summary>
    private async Task<string> CreateMimeItemAsync(string folderId, byte[] mime, int mapiFlags, bool isRead, CancellationToken ct)
    {
        var request = new XElement(M + "CreateItem",
            new XAttribute("MessageDisposition", "SaveOnly"),
            new XElement(M + "SavedItemFolderId", FolderIdElement(folderId)),
            new XElement(M + "Items",
                new XElement(T + "Message",
                    new XElement(T + "MimeContent", Convert.ToBase64String(mime)),
                    new XElement(T + "ExtendedProperty",
                        ExtendedFieldUri("0x0E07", "Integer"),
                        new XElement(T + "Value", mapiFlags.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                    Bool(T + "IsRead", isRead))));
        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        return (string?)response.Descendants(T + "ItemId").FirstOrDefault()?.Attribute("Id") ?? "";
    }

    /// <summary>Drafts: MIME in Drafts with READ|UNSENT (create_message.rs with is_draft).</summary>
    public async Task<string> SaveDraftAsync(OutgoingMessage message, CancellationToken ct = default)
    {
        var mime = await BuildMimeAsync(message, ct).ConfigureAwait(false);
        var id = await CreateMimeItemAsync("drafts", MimeMail.ToBytes(mime, MimeMail.SendFormat),
            MimeMail.MsgFlagRead | MimeMail.MsgFlagUnsent, isRead: true, ct).ConfigureAwait(false);
        if (message.Action == ComposeAction.EditDraft && !string.IsNullOrEmpty(message.ReferenceItemId))
            await DeleteItemsAsync(new[] { message.ReferenceItemId }, true, ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>Import (.eml): stored as a regular read message (READ|UNMODIFIED), as Thunderbird does.</summary>
    public Task<string> ImportMimeAsync(string folderId, byte[] mime, CancellationToken ct = default) =>
        CreateMimeItemAsync(folderId, mime, MimeMail.MsgFlagRead | MimeMail.MsgFlagUnmodified, isRead: true, ct);

    // ===================================================================== directory

    public async Task<IReadOnlyList<Contact>> ResolveNamesAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<Contact>();
        var request = new XElement(M + "ResolveNames",
            new XAttribute("ReturnFullContactData", "true"),
            new XAttribute("SearchScope", "ActiveDirectoryContacts"),
            new XElement(M + "UnresolvedEntry", text.Trim()));
        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response, "ErrorNameResolutionNoResults", "ErrorNameResolutionMultipleResults");
        return response.Descendants(T + "Resolution").Select(EwsParser.ParseResolution).ToList();
    }

    // ===================================================================== contacts

    public async Task<IReadOnlyList<Contact>> GetContactsAsync(string? folderId = null, CancellationToken ct = default)
    {
        var all = new List<Contact>();
        int offset = 0;
        while (true)
        {
            var request = new XElement(M + "FindItem", new XAttribute("Traversal", "Shallow"),
                new XElement(M + "ItemShape", new XElement(T + "BaseShape", "AllProperties")),
                new XElement(M + "IndexedPageItemView",
                    new XAttribute("MaxEntriesReturned", 500),
                    new XAttribute("Offset", offset),
                    new XAttribute("BasePoint", "Beginning")),
                new XElement(M + "SortOrder",
                    new XElement(T + "FieldOrder", new XAttribute("Order", "Ascending"), FieldUri("contacts:DisplayName"))),
                new XElement(M + "ParentFolderIds", FolderIdElement(folderId ?? WellKnownOrName(WellKnownFolder.Contacts, "contacts"))));
            var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
            EwsClient.ThrowOnError(response);
            var root = response.Descendants(M + "RootFolder").FirstOrDefault();
            var items = ItemElements(root?.Element(T + "Items")).ToList();
            all.AddRange(items.Select(EwsParser.ParseContact));
            offset += items.Count;
            if (items.Count == 0 || ParseBool((string?)root?.Attribute("IncludesLastItemInRange"))) break;
        }
        return all;
    }

    private static IEnumerable<XElement> ContactFieldElements(Contact c)
    {
        if (!string.IsNullOrWhiteSpace(c.Notes))
            yield return new XElement(T + "Body", new XAttribute("BodyType", "Text"), c.Notes);
        var display = string.IsNullOrWhiteSpace(c.DisplayName) ? $"{c.GivenName} {c.Surname}".Trim() : c.DisplayName;
        if (display.Length > 0) yield return new XElement(T + "DisplayName", display);
        if (!string.IsNullOrWhiteSpace(c.GivenName)) yield return new XElement(T + "GivenName", c.GivenName);
        if (!string.IsNullOrWhiteSpace(c.CompanyName)) yield return new XElement(T + "CompanyName", c.CompanyName);
        var emails = c.EmailAddresses.Where(e => !string.IsNullOrWhiteSpace(e)).Take(3).ToList();
        if (emails.Count > 0)
            yield return new XElement(T + "EmailAddresses",
                emails.Select((e, i) => new XElement(T + "Entry", new XAttribute("Key", $"EmailAddress{i + 1}"), e)));
        var phones = new[] { ("BusinessPhone", c.BusinessPhone), ("HomePhone", c.HomePhone), ("MobilePhone", c.MobilePhone) }
            .Where(p => !string.IsNullOrWhiteSpace(p.Item2)).ToList();
        if (phones.Count > 0)
            yield return new XElement(T + "PhoneNumbers",
                phones.Select(p => new XElement(T + "Entry", new XAttribute("Key", p.Item1), p.Item2)));
        if (!string.IsNullOrWhiteSpace(c.Department)) yield return new XElement(T + "Department", c.Department);
        if (!string.IsNullOrWhiteSpace(c.JobTitle)) yield return new XElement(T + "JobTitle", c.JobTitle);
        if (!string.IsNullOrWhiteSpace(c.Surname)) yield return new XElement(T + "Surname", c.Surname);
    }

    public async Task<string> CreateContactAsync(Contact contact, string? folderId = null, CancellationToken ct = default)
    {
        var request = new XElement(M + "CreateItem",
            new XElement(M + "SavedItemFolderId", FolderIdElement(folderId ?? WellKnownOrName(WellKnownFolder.Contacts, "contacts"))),
            new XElement(M + "Items", new XElement(T + "Contact", ContactFieldElements(contact))));
        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        return (string?)response.Descendants(T + "ItemId").FirstOrDefault()?.Attribute("Id") ?? "";
    }

    public async Task UpdateContactAsync(Contact contact, CancellationToken ct = default)
    {
        var updates = new List<XElement>();
        void Simple(string uri, string element, string value)
        {
            updates.Add(string.IsNullOrWhiteSpace(value)
                ? new XElement(T + "DeleteItemField", FieldUri(uri))
                : new XElement(T + "SetItemField", FieldUri(uri), new XElement(T + "Contact", new XElement(T + element, value))));
        }
        void Indexed(string uri, string container, string key, string value)
        {
            updates.Add(string.IsNullOrWhiteSpace(value)
                ? new XElement(T + "DeleteItemField", IndexedFieldUri(uri, key))
                : new XElement(T + "SetItemField", IndexedFieldUri(uri, key),
                    new XElement(T + "Contact", new XElement(T + container,
                        new XElement(T + "Entry", new XAttribute("Key", key), value)))));
        }

        Simple("contacts:DisplayName", "DisplayName", contact.DisplayName);
        Simple("contacts:GivenName", "GivenName", contact.GivenName);
        Simple("contacts:Surname", "Surname", contact.Surname);
        Simple("contacts:CompanyName", "CompanyName", contact.CompanyName);
        Simple("contacts:JobTitle", "JobTitle", contact.JobTitle);
        Simple("contacts:Department", "Department", contact.Department);
        for (int i = 0; i < 3; i++)
            Indexed("contacts:EmailAddress", "EmailAddresses", $"EmailAddress{i + 1}",
                i < contact.EmailAddresses.Count ? contact.EmailAddresses[i] : "");
        Indexed("contacts:PhoneNumber", "PhoneNumbers", "BusinessPhone", contact.BusinessPhone);
        Indexed("contacts:PhoneNumber", "PhoneNumbers", "MobilePhone", contact.MobilePhone);
        Indexed("contacts:PhoneNumber", "PhoneNumbers", "HomePhone", contact.HomePhone);
        updates.Add(new XElement(T + "SetItemField", FieldUri("item:Body"),
            new XElement(T + "Contact", new XElement(T + "Body", new XAttribute("BodyType", "Text"), contact.Notes ?? ""))));

        var request = new XElement(M + "UpdateItem",
            new XAttribute("ConflictResolution", "AlwaysOverwrite"),
            new XElement(M + "ItemChanges",
                new XElement(T + "ItemChange", ItemId(contact.Id), new XElement(T + "Updates", updates))));
        // Deleting a field that is not set yields a harmless warning/error on some versions.
        EwsClient.ThrowOnError(await _ews.SendAsync(request, ct).ConfigureAwait(false), "ErrorInvalidPropertyDelete");
    }

    // ===================================================================== calendar

    public async Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(DateTimeOffset start, DateTimeOffset end, string? folderId = null, CancellationToken ct = default)
    {
        var request = new XElement(M + "FindItem", new XAttribute("Traversal", "Shallow"),
            new XElement(M + "ItemShape", new XElement(T + "BaseShape", "AllProperties")),
            new XElement(M + "CalendarView",
                new XAttribute("MaxEntriesReturned", 1000),
                new XAttribute("StartDate", Date(start)),
                new XAttribute("EndDate", Date(end))),
            new XElement(M + "ParentFolderIds", FolderIdElement(folderId ?? WellKnownOrName(WellKnownFolder.Calendar, "calendar"))));
        var response = await _ews.SendAsync(request, ct, WindowsTimeZoneId()).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        return ItemElements(response.Descendants(T + "Items").FirstOrDefault())
            .Select(EwsParser.ParseCalendarItem)
            .OrderBy(e => e.Start)
            .ToList();
    }

    public async Task<string> CreateEventAsync(CalendarEvent evt, string? folderId = null, CancellationToken ct = default)
    {
        bool hasAttendees = evt.RequiredAttendees.Count + evt.OptionalAttendees.Count > 0;
        var item = new XElement(T + "CalendarItem",
            new XElement(T + "Subject", evt.Subject),
            new XElement(T + "Body", new XAttribute("BodyType", "HTML"), evt.Body ?? ""),
            Bool(T + "ReminderIsSet", evt.ReminderSet),
            new XElement(T + "ReminderMinutesBeforeStart", evt.ReminderMinutes),
            new XElement(T + "Start", Date(evt.Start)),
            new XElement(T + "End", Date(evt.End)),
            Bool(T + "IsAllDayEvent", evt.IsAllDay),
            new XElement(T + "LegacyFreeBusyStatus", evt.FreeBusy.ToString()),
            new XElement(T + "Location", evt.Location ?? ""));
        if (evt.RequiredAttendees.Count > 0)
            item.Add(new XElement(T + "RequiredAttendees", evt.RequiredAttendees.Select(a => new XElement(T + "Attendee", Mailbox(a.Name, a.Address)))));
        if (evt.OptionalAttendees.Count > 0)
            item.Add(new XElement(T + "OptionalAttendees", evt.OptionalAttendees.Select(a => new XElement(T + "Attendee", Mailbox(a.Name, a.Address)))));

        var request = new XElement(M + "CreateItem",
            new XAttribute("SendMeetingInvitations", hasAttendees ? "SendToAllAndSaveCopy" : "SendToNone"),
            new XElement(M + "SavedItemFolderId", FolderIdElement(folderId ?? WellKnownOrName(WellKnownFolder.Calendar, "calendar"))),
            new XElement(M + "Items", item));
        var response = await _ews.SendAsync(request, ct, WindowsTimeZoneId()).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        return (string?)response.Descendants(T + "ItemId").FirstOrDefault()?.Attribute("Id") ?? "";
    }

    public async Task CancelOrDeleteEventAsync(string itemId, bool isOrganizerOfMeeting, CancellationToken ct = default)
    {
        var request = new XElement(M + "DeleteItem",
            new XAttribute("DeleteType", "MoveToDeletedItems"),
            new XAttribute("SendMeetingCancellations", isOrganizerOfMeeting ? "SendToAllAndSaveCopy" : "SendToNone"),
            ItemIds(new[] { itemId }));
        EwsClient.ThrowOnError(await _ews.SendAsync(request, ct).ConfigureAwait(false));
    }

    public async Task RespondToMeetingAsync(string itemId, MeetingResponse response, string? comment = null, CancellationToken ct = default)
    {
        var name = response switch
        {
            MeetingResponse.Accept => "AcceptItem",
            MeetingResponse.Tentative => "TentativelyAcceptItem",
            _ => "DeclineItem",
        };
        var changeKey = await GetChangeKeyAsync(itemId, ct).ConfigureAwait(false);
        var item = new XElement(T + name);
        if (!string.IsNullOrWhiteSpace(comment))
            item.Add(new XElement(T + "Body", new XAttribute("BodyType", "Text"), comment));
        item.Add(ItemId(itemId, changeKey).WithName(T + "ReferenceItemId"));
        var request = new XElement(M + "CreateItem",
            new XAttribute("MessageDisposition", "SendAndSaveCopy"),
            new XElement(M + "Items", item));
        EwsClient.ThrowOnError(await _ews.SendAsync(request, ct).ConfigureAwait(false));
    }

    // ===================================================================== tasks

    public async Task<IReadOnlyList<TaskItem>> GetTasksAsync(string? folderId = null, CancellationToken ct = default)
    {
        var request = new XElement(M + "FindItem", new XAttribute("Traversal", "Shallow"),
            new XElement(M + "ItemShape", new XElement(T + "BaseShape", "AllProperties")),
            new XElement(M + "IndexedPageItemView",
                new XAttribute("MaxEntriesReturned", PageSizeMax),
                new XAttribute("Offset", 0),
                new XAttribute("BasePoint", "Beginning")),
            new XElement(M + "ParentFolderIds", FolderIdElement(folderId ?? WellKnownOrName(WellKnownFolder.Tasks, "tasks"))));
        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        return ItemElements(response.Descendants(T + "Items").FirstOrDefault())
            .Where(e => e.Name.LocalName == "Task")
            .Select(EwsParser.ParseTask)
            .OrderBy(t => t.IsComplete)
            .ThenBy(t => t.DueDate ?? DateTimeOffset.MaxValue)
            .ToList();
    }

    public async Task<string> CreateTaskAsync(TaskItem task, string? folderId = null, CancellationToken ct = default)
    {
        var item = new XElement(T + "Task",
            new XElement(T + "Subject", task.Subject),
            new XElement(T + "Body", new XAttribute("BodyType", "Text"), task.Body ?? ""),
            new XElement(T + "Importance", task.Importance.ToString()));
        if (task.DueDate is { } due) item.Add(new XElement(T + "DueDate", Date(due)));
        if (task.StartDate is { } start) item.Add(new XElement(T + "StartDate", Date(start)));
        item.Add(new XElement(T + "Status", task.Status.ToString()));
        var request = new XElement(M + "CreateItem",
            new XElement(M + "SavedItemFolderId", FolderIdElement(folderId ?? WellKnownOrName(WellKnownFolder.Tasks, "tasks"))),
            new XElement(M + "Items", item));
        var response = await _ews.SendAsync(request, ct, WindowsTimeZoneId()).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        return (string?)response.Descendants(T + "ItemId").FirstOrDefault()?.Attribute("Id") ?? "";
    }

    public Task SetTaskCompleteAsync(string itemId, bool complete, CancellationToken ct = default) =>
        UpdateItemsAsync(new[] { itemId }, () => new[]
        {
            new XElement(T + "SetItemField", FieldUri("task:Status"),
                new XElement(T + "Task", new XElement(T + "Status", complete ? "Completed" : "NotStarted"))),
        }, false, ct);

    // ===================================================================== out of office

    private string OofMailbox => SharedMailbox ?? Account.EmailAddress;

    public async Task<OofSettings> GetOutOfOfficeAsync(CancellationToken ct = default)
    {
        var request = new XElement(M + "GetUserOofSettingsRequest",
            new XElement(T + "Mailbox", new XElement(T + "Address", OofMailbox)));
        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        var s = response.Descendants(T + "OofSettings").FirstOrDefault()
                ?? throw new MailServiceException("Сервер не вернул настройки автоответа.");
        var duration = s.Element(T + "Duration");
        return new OofSettings
        {
            State = Enum.TryParse<OofState>(s.Val("OofState"), out var st) ? st : OofState.Disabled,
            ExternalAudience = Enum.TryParse<OofExternalAudience>(s.Val("ExternalAudience"), out var ea) ? ea : OofExternalAudience.All,
            StartTime = ParseDate(duration.Val("StartTime")) is var a && a != default ? a.ToLocalTime() : DateTimeOffset.Now,
            EndTime = ParseDate(duration.Val("EndTime")) is var b && b != default ? b.ToLocalTime() : DateTimeOffset.Now.AddDays(1),
            InternalReply = s.Element(T + "InternalReply").Val("Message") ?? "",
            ExternalReply = s.Element(T + "ExternalReply").Val("Message") ?? "",
        };
    }

    public async Task SetOutOfOfficeAsync(OofSettings settings, CancellationToken ct = default)
    {
        var request = new XElement(M + "SetUserOofSettingsRequest",
            new XElement(T + "Mailbox", new XElement(T + "Address", OofMailbox)),
            new XElement(T + "UserOofSettings",
                new XElement(T + "OofState", settings.State.ToString()),
                new XElement(T + "ExternalAudience", settings.ExternalAudience.ToString()),
                new XElement(T + "Duration",
                    new XElement(T + "StartTime", Date(settings.StartTime)),
                    new XElement(T + "EndTime", Date(settings.EndTime))),
                new XElement(T + "InternalReply", new XElement(T + "Message", settings.InternalReply ?? "")),
                new XElement(T + "ExternalReply", new XElement(T + "Message", settings.ExternalReply ?? ""))));
        EwsClient.ThrowOnError(await _ews.SendAsync(request, ct).ConfigureAwait(false));
    }

    public void Dispose() => _ews.Dispose();
}

internal static class XElementExtensions
{
    public static XElement WithName(this XElement e, XName name)
    {
        e.Name = name;
        return e;
    }
}
