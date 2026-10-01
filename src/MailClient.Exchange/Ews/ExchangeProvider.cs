using System.Xml.Linq;
using MailClient.Core.Models;
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

    public async Task<MailboxInfo> ConnectAsync(CancellationToken ct = default)
    {
        var request = new XElement(M + "GetFolder",
            new XElement(M + "FolderShape", new XElement(T + "BaseShape", "IdOnly")),
            new XElement(M + "FolderIds", FolderIdElement("inbox")));
        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        return new MailboxInfo
        {
            EmailAddress = SharedMailbox ?? Account.EmailAddress,
            DisplayName = Account.EffectiveDisplayName,
            ServerVersion = _ews.LastServerVersion ?? "",
        };
    }

    // ===================================================================== folders

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken ct = default)
    {
        // 1) Resolve the ids of the well-known folders (one request, one response message each).
        var names = DistinguishedFolders.Keys.ToList();
        var getFolder = new XElement(M + "GetFolder",
            new XElement(M + "FolderShape", new XElement(T + "BaseShape", "AllProperties")),
            new XElement(M + "FolderIds", names.Select(FolderIdElement)));
        var gfResponse = await _ews.SendAsync(getFolder, ct).ConfigureAwait(false);
        var messages = EwsClient.ResponseMessages(gfResponse).ToList();

        var result = new List<MailFolder>();
        MailFolder? root = null;
        for (int i = 0; i < messages.Count && i < names.Count; i++)
        {
            if ((string?)messages[i].Attribute("ResponseClass") == "Error")
            {
                if (names[i] == "msgfolderroot") EwsClient.ThrowIfError(messages[i]);
                continue; // e.g. "notes" may not exist
            }
            var folderEl = messages[i].Element(M + "Folders")?.Elements().FirstOrDefault();
            if (folderEl == null) continue;
            var wk = DistinguishedFolders[names[i]];
            var f = EwsParser.ParseFolder(folderEl);
            _wellKnownIds[wk] = f.Id;
            if (wk == WellKnownFolder.Root)
            {
                f.WellKnown = WellKnownFolder.Root;
                f.ParentId = null;
                f.DisplayName = string.IsNullOrEmpty(f.DisplayName) ? (SharedMailbox ?? Account.EmailAddress) : f.DisplayName;
                root = f;
            }
        }
        if (root == null) throw new MailServiceException("Не удалось открыть корневую папку почтового ящика.");
        result.Add(root);

        // 2) Enumerate the whole tree below the root (deep traversal, paged).
        var wellKnownById = _wellKnownIds.ToDictionary(kv => kv.Value, kv => kv.Key);
        int offset = 0;
        while (true)
        {
            var findFolder = new XElement(M + "FindFolder", new XAttribute("Traversal", "Deep"),
                new XElement(M + "FolderShape",
                    new XElement(T + "BaseShape", "AllProperties"),
                    new XElement(T + "AdditionalProperties", ExtendedFieldUri(EwsParser.HiddenPropTag, "Boolean"))),
                new XElement(M + "IndexedPageFolderView",
                    new XAttribute("MaxEntriesReturned", PageSizeMax),
                    new XAttribute("Offset", offset),
                    new XAttribute("BasePoint", "Beginning")),
                new XElement(M + "ParentFolderIds", new XElement(T + "FolderId", new XAttribute("Id", root.Id))));
            var response = await _ews.SendAsync(findFolder, ct).ConfigureAwait(false);
            EwsClient.ThrowOnError(response);
            var rootFolder = response.Descendants(M + "RootFolder").FirstOrDefault();
            var folders = rootFolder?.Element(T + "Folders")?.Elements().Where(EwsParser.IsFolderElement).ToList() ?? new();
            foreach (var fe in folders)
            {
                if (EwsParser.IsHidden(fe)) continue;
                var f = EwsParser.ParseFolder(fe);
                f.WellKnown = wellKnownById.TryGetValue(f.Id, out var wk) ? wk : WellKnownFolder.None;
                result.Add(f);
            }
            offset += folders.Count;
            if (folders.Count == 0 || ParseBool((string?)rootFolder?.Attribute("IncludesLastItemInRange"))) break;
        }

        // Drop folders whose parent was hidden (they would be orphans in the tree).
        var ids = new HashSet<string>(result.Select(f => f.Id));
        bool removed;
        do
        {
            removed = result.RemoveAll(f => f.ParentId != null && !ids.Contains(f.ParentId)) > 0;
            if (removed) ids = new HashSet<string>(result.Select(f => f.Id));
        } while (removed);
        return result;
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

    public async Task DeleteFolderAsync(string folderId, bool permanent, CancellationToken ct = default)
    {
        var request = new XElement(M + "DeleteFolder",
            new XAttribute("DeleteType", permanent ? "HardDelete" : "MoveToDeletedItems"),
            new XElement(M + "FolderIds", FolderIdElement(folderId)));
        EwsClient.ThrowOnError(await _ews.SendAsync(request, ct).ConfigureAwait(false));
    }

    public async Task EmptyFolderAsync(string folderId, bool deleteSubFolders, CancellationToken ct = default)
    {
        bool isTrash = _wellKnownIds.TryGetValue(WellKnownFolder.DeletedItems, out var del) && del == folderId
                       || _wellKnownIds.TryGetValue(WellKnownFolder.JunkEmail, out var junk) && junk == folderId
                       || folderId.Equals("deleteditems", StringComparison.OrdinalIgnoreCase);
        var request = new XElement(M + "EmptyFolder",
            new XAttribute("DeleteType", isTrash ? "HardDelete" : "MoveToDeletedItems"),
            new XAttribute("DeleteSubFolders", deleteSubFolders ? "true" : "false"),
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

    private async Task<MessagePage> FindMessagesAsync(string folderId, XElement? filter, int offset, int pageSize, CancellationToken ct)
    {
        var request = new XElement(M + "FindItem", new XAttribute("Traversal", "Shallow"),
            SummaryShape(M + "ItemShape"),
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
        var items = ItemElements(root?.Element(T + "Items")).Select(e =>
        {
            var s = EwsParser.ParseSummary(e);
            s.FolderId = folderId;
            return s;
        }).ToList();
        return new MessagePage
        {
            Items = items,
            TotalCount = ParseInt((string?)root?.Attribute("TotalItemsInView")),
            HasMore = !ParseBool((string?)root?.Attribute("IncludesLastItemInRange")),
        };
    }

    public async Task<FolderSyncResult> SyncFolderItemsAsync(string folderId, string? syncState, int maxChanges, CancellationToken ct = default)
    {
        var request = new XElement(M + "SyncFolderItems",
            SummaryShape(M + "ItemShape"),
            new XElement(M + "SyncFolderId", FolderIdElement(folderId)));
        if (!string.IsNullOrEmpty(syncState)) request.Add(new XElement(M + "SyncState", syncState));
        request.Add(new XElement(M + "MaxChangesReturned", Math.Clamp(maxChanges, 1, 512)));
        request.Add(new XElement(M + "SyncScope", "NormalItems"));

        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        var msg = EwsClient.ResponseMessages(response).First();
        var result = new FolderSyncResult
        {
            SyncState = msg.Element(M + "SyncState")?.Value ?? "",
            IncludesLastItem = ParseBool(msg.Element(M + "IncludesLastItemInRange")?.Value),
        };
        foreach (var change in msg.Element(M + "Changes")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            switch (change.Name.LocalName)
            {
                case "Create":
                case "Update":
                    foreach (var item in ItemElements(change))
                    {
                        var s = EwsParser.ParseSummary(item);
                        s.FolderId = folderId;
                        result.CreatedOrUpdated.Add(s);
                    }
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
        return result;
    }

    // ===================================================================== single items

    public async Task<MailMessage> GetMessageAsync(string itemId, CancellationToken ct = default)
    {
        var additional = new List<XElement>
        {
            FieldUri("item:Body"),
            FieldUri("item:Attachments"),
            FieldUri("message:ToRecipients"),
            FieldUri("message:CcRecipients"),
            FieldUri("message:BccRecipients"),
            FieldUri("message:ReplyTo"),
            FieldUri("message:Sender"),
            FieldUri("message:InternetMessageId"),
            FieldUri("message:IsReadReceiptRequested"),
        };
        additional.AddRange(SummaryProperties());
        var request = new XElement(M + "GetItem",
            new XElement(M + "ItemShape",
                new XElement(T + "BaseShape", "AllProperties"),
                new XElement(T + "BodyType", "Best"),
                new XElement(T + "AdditionalProperties", additional)),
            new XElement(M + "ItemIds", ItemId(itemId)));
        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        var item = ItemElements(response.Descendants(M + "Items").FirstOrDefault()).FirstOrDefault()
                   ?? throw new MailServiceException("Сервер не вернул запрошенное письмо (возможно, оно удалено).", "ErrorItemNotFound");
        return EwsParser.ParseMessage(item);
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
        var request = new XElement(M + "GetItem",
            new XElement(M + "ItemShape",
                new XElement(T + "BaseShape", "IdOnly"),
                new XElement(T + "IncludeMimeContent", "true")),
            new XElement(M + "ItemIds", ItemId(itemId)));
        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        var mime = response.Descendants(T + "MimeContent").FirstOrDefault()
                   ?? throw new MailServiceException("Сервер не вернул содержимое письма в формате MIME.");
        return Convert.FromBase64String(mime.Value);
    }

    public async Task<AttachmentContent> GetAttachmentAsync(string attachmentId, CancellationToken ct = default) =>
        (await GetAttachmentsAsync(new[] { attachmentId }, ct).ConfigureAwait(false))[0];

    public async Task<IReadOnlyList<AttachmentContent>> GetAttachmentsAsync(IEnumerable<string> attachmentIds, CancellationToken ct = default)
    {
        var ids = attachmentIds.ToList();
        if (ids.Count == 0) return Array.Empty<AttachmentContent>();
        var request = new XElement(M + "GetAttachment",
            new XElement(M + "AttachmentShape", new XElement(T + "IncludeMimeContent", "true")),
            new XElement(M + "AttachmentIds", ids.Select(id => new XElement(T + "AttachmentId", new XAttribute("Id", id)))));
        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);

        var result = new List<AttachmentContent>();
        foreach (var msg in EwsClient.ResponseMessages(response))
        {
            foreach (var a in msg.Element(M + "Attachments")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                var info = EwsParser.ParseAttachments(new XElement(T + "Attachments", a)).First();
                byte[] content;
                if (a.Name.LocalName == "FileAttachment")
                {
                    content = Convert.FromBase64String(a.Element(T + "Content")?.Value ?? "");
                }
                else
                {
                    // Item attachment (e.g. forwarded e-mail): expose as .eml.
                    var mime = a.Descendants(T + "MimeContent").FirstOrDefault();
                    content = mime != null ? Convert.FromBase64String(mime.Value) : Array.Empty<byte>();
                    info.ContentType = "message/rfc822";
                }
                info.Size = content.Length;
                result.Add(new AttachmentContent { Info = info, Content = content });
            }
        }
        return result;
    }

    // ===================================================================== item updates

    private async Task UpdateItemsAsync(IEnumerable<string> itemIds, Func<XElement[]> updates, bool isMessage, CancellationToken ct)
    {
        foreach (var batch in itemIds.Chunk(100))
        {
            var request = new XElement(M + "UpdateItem",
                new XAttribute("ConflictResolution", "AutoResolve"),
                new XElement(M + "ItemChanges", batch.Select(id =>
                    new XElement(T + "ItemChange", ItemId(id), new XElement(T + "Updates", updates())))));
            if (isMessage) request.Add(new XAttribute("MessageDisposition", "SaveOnly"));
            EwsClient.ThrowOnError(await _ews.SendAsync(request, ct).ConfigureAwait(false));
        }
    }

    public Task SetReadStateAsync(IEnumerable<string> itemIds, bool isRead, CancellationToken ct = default) =>
        UpdateItemsAsync(itemIds, () => new[]
        {
            new XElement(T + "SetItemField", FieldUri("message:IsRead"),
                new XElement(T + "Message", Bool(T + "IsRead", isRead))),
        }, true, ct);

    public Task SetFlagAsync(IEnumerable<string> itemIds, FlagStatus flag, CancellationToken ct = default)
    {
        if (_supports2013)
        {
            return UpdateItemsAsync(itemIds, () => new[]
            {
                new XElement(T + "SetItemField", FieldUri("item:Flag"),
                    new XElement(T + "Message",
                        new XElement(T + "Flag", new XElement(T + "FlagStatus", flag.ToString())))),
            }, true, ct);
        }
        // Exchange 2010: write PR_FLAG_STATUS directly.
        return UpdateItemsAsync(itemIds, () => flag == FlagStatus.NotFlagged
            ? new[] { new XElement(T + "DeleteItemField", ExtendedFieldUri(EwsParser.FlagStatusPropTag, "Integer")) }
            : new[]
            {
                new XElement(T + "SetItemField", ExtendedFieldUri(EwsParser.FlagStatusPropTag, "Integer"),
                    new XElement(T + "Message",
                        new XElement(T + "ExtendedProperty",
                            ExtendedFieldUri(EwsParser.FlagStatusPropTag, "Integer"),
                            new XElement(T + "Value", flag == FlagStatus.Complete ? "1" : "2")))),
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

    public Task<IReadOnlyList<string?>> MoveItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default) =>
        MoveOrCopyAsync("MoveItem", itemIds, destinationFolderId, ct);

    public Task<IReadOnlyList<string?>> CopyItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default) =>
        MoveOrCopyAsync("CopyItem", itemIds, destinationFolderId, ct);

    private async Task<IReadOnlyList<string?>> MoveOrCopyAsync(string op, IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct)
    {
        var result = new List<string?>();
        foreach (var batch in itemIds.Chunk(100))
        {
            var request = new XElement(M + op,
                new XElement(M + "ToFolderId", FolderIdElement(destinationFolderId)),
                ItemIds(batch));
            var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
            EwsClient.ThrowOnError(response);
            foreach (var msg in EwsClient.ResponseMessages(response))
                result.Add((string?)msg.Descendants(T + "ItemId").FirstOrDefault()?.Attribute("Id"));
        }
        return result;
    }

    public async Task DeleteItemsAsync(IEnumerable<string> itemIds, bool permanent, CancellationToken ct = default)
    {
        foreach (var batch in itemIds.Chunk(100))
        {
            var request = new XElement(M + "DeleteItem",
                new XAttribute("DeleteType", permanent ? "HardDelete" : "MoveToDeletedItems"),
                new XAttribute("SendMeetingCancellations", "SendToNone"),
                new XAttribute("AffectedTaskOccurrences", "AllOccurrences"),
                ItemIds(batch));
            // Already-deleted items are not an error for the user.
            EwsClient.ThrowOnError(await _ews.SendAsync(request, ct).ConfigureAwait(false), "ErrorItemNotFound");
        }
    }

    // ===================================================================== sending

    private static XElement? Recipients(XName name, IReadOnlyCollection<EmailAddress> list) =>
        list.Count == 0 ? null : new XElement(name, list.Select(a => Mailbox(a.Name, a.Address)));

    private XElement BuildMessageElement(OutgoingMessage m)
    {
        var msg = new XElement(T + "Message",
            new XElement(T + "Subject", m.Subject),
            new XElement(T + "Body", new XAttribute("BodyType", m.BodyIsHtml ? "HTML" : "Text"), m.Body),
            new XElement(T + "Importance", m.Importance.ToString()),
            Recipients(T + "ToRecipients", m.To),
            Recipients(T + "CcRecipients", m.Cc),
            Recipients(T + "BccRecipients", m.Bcc),
            Bool(T + "IsReadReceiptRequested", m.RequestReadReceipt),
            Bool(T + "IsDeliveryReceiptRequested", m.RequestDeliveryReceipt));
        if (SharedMailbox != null)
            msg.Add(new XElement(T + "From", Mailbox(null, SharedMailbox)));
        return msg;
    }

    private XElement BuildResponseObject(OutgoingMessage m, string changeKey)
    {
        var name = m.Action switch
        {
            ComposeAction.Reply => "ReplyToItem",
            ComposeAction.ReplyAll => "ReplyAllToItem",
            ComposeAction.Forward => "ForwardItem",
            _ => throw new InvalidOperationException("Not a response action."),
        };
        var e = new XElement(T + name,
            new XElement(T + "Subject", m.Subject),
            Recipients(T + "ToRecipients", m.To),
            Recipients(T + "CcRecipients", m.Cc),
            Recipients(T + "BccRecipients", m.Bcc),
            Bool(T + "IsReadReceiptRequested", m.RequestReadReceipt),
            Bool(T + "IsDeliveryReceiptRequested", m.RequestDeliveryReceipt));
        if (SharedMailbox != null) e.Add(new XElement(T + "From", Mailbox(null, SharedMailbox)));
        e.Add(ItemId(m.ReferenceItemId!, changeKey).WithName(T + "ReferenceItemId"));
        // The server appends the quoted original message (and, for forwards, the original attachments).
        e.Add(new XElement(T + "NewBodyContent", new XAttribute("BodyType", m.BodyIsHtml ? "HTML" : "Text"), m.Body));
        return e;
    }

    private bool IsResponse(OutgoingMessage m) =>
        m.Action is ComposeAction.Reply or ComposeAction.ReplyAll or ComposeAction.Forward && !string.IsNullOrEmpty(m.ReferenceItemId);

    public async Task SendAsync(OutgoingMessage message, CancellationToken ct = default)
    {
        if (!message.AllRecipients.Any())
            throw new MailServiceException("Укажите хотя бы одного получателя.");

        if (message.Attachments.Count == 0)
        {
            // Single round-trip: create and send, saving a copy in Sent Items.
            var item = IsResponse(message)
                ? BuildResponseObject(message, await GetChangeKeyAsync(message.ReferenceItemId!, ct).ConfigureAwait(false))
                : BuildMessageElement(message);
            var request = new XElement(M + "CreateItem",
                new XAttribute("MessageDisposition", "SendAndSaveCopy"),
                new XElement(M + "SavedItemFolderId", FolderIdElement("sentitems")),
                new XElement(M + "Items", item));
            EwsClient.ThrowOnError(await _ews.SendAsync(request, ct).ConfigureAwait(false));
        }
        else
        {
            // Large/many attachments: save draft, upload attachments one by one, then send.
            var (id, changeKey) = await CreateDraftAsync(message, ct).ConfigureAwait(false);
            changeKey = await UploadAttachmentsAsync(id, changeKey, message.Attachments, ct).ConfigureAwait(false);
            var send = new XElement(M + "SendItem",
                new XAttribute("SaveItemToFolder", "true"),
                new XElement(M + "ItemIds", ItemId(id, changeKey)),
                new XElement(M + "SavedItemFolderId", FolderIdElement("sentitems")));
            EwsClient.ThrowOnError(await _ews.SendAsync(send, ct).ConfigureAwait(false));
        }

        if (message.Action == ComposeAction.EditDraft && !string.IsNullOrEmpty(message.ReferenceItemId))
            await DeleteItemsAsync(new[] { message.ReferenceItemId }, true, ct).ConfigureAwait(false);
    }

    public async Task<string> SaveDraftAsync(OutgoingMessage message, CancellationToken ct = default)
    {
        var (id, changeKey) = await CreateDraftAsync(message, ct).ConfigureAwait(false);
        await UploadAttachmentsAsync(id, changeKey, message.Attachments, ct).ConfigureAwait(false);
        if (message.Action == ComposeAction.EditDraft && !string.IsNullOrEmpty(message.ReferenceItemId))
            await DeleteItemsAsync(new[] { message.ReferenceItemId }, true, ct).ConfigureAwait(false);
        return id;
    }

    private async Task<(string id, string changeKey)> CreateDraftAsync(OutgoingMessage message, CancellationToken ct)
    {
        var item = IsResponse(message)
            ? BuildResponseObject(message, await GetChangeKeyAsync(message.ReferenceItemId!, ct).ConfigureAwait(false))
            : BuildMessageElement(message);
        var request = new XElement(M + "CreateItem",
            new XAttribute("MessageDisposition", "SaveOnly"),
            new XElement(M + "SavedItemFolderId", FolderIdElement("drafts")),
            new XElement(M + "Items", item));
        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        var id = response.Descendants(T + "ItemId").FirstOrDefault()
                 ?? throw new MailServiceException("Сервер не вернул идентификатор черновика.");
        return ((string)id.Attribute("Id")!, (string?)id.Attribute("ChangeKey") ?? "");
    }

    private async Task<string> UploadAttachmentsAsync(string itemId, string changeKey, IEnumerable<OutgoingAttachment> attachments, CancellationToken ct)
    {
        foreach (var a in attachments)
        {
            var file = new XElement(T + "FileAttachment",
                new XElement(T + "Name", a.Name),
                new XElement(T + "ContentType", a.ContentType));
            if (!string.IsNullOrEmpty(a.ContentId)) file.Add(new XElement(T + "ContentId", a.ContentId));
            file.Add(Bool(T + "IsInline", a.IsInline));
            file.Add(new XElement(T + "Content", Convert.ToBase64String(a.Content)));

            var parent = new XElement(M + "ParentItemId", new XAttribute("Id", itemId));
            if (!string.IsNullOrEmpty(changeKey)) parent.Add(new XAttribute("ChangeKey", changeKey));
            var request = new XElement(M + "CreateAttachment", parent, new XElement(M + "Attachments", file));
            var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
            EwsClient.ThrowOnError(response);
            changeKey = (string?)response.Descendants(T + "AttachmentId").FirstOrDefault()?.Attribute("RootItemChangeKey") ?? changeKey;
        }
        return changeKey;
    }

    public async Task<string> ImportMimeAsync(string folderId, byte[] mime, CancellationToken ct = default)
    {
        var request = new XElement(M + "CreateItem",
            new XAttribute("MessageDisposition", "SaveOnly"),
            new XElement(M + "SavedItemFolderId", FolderIdElement(folderId)),
            new XElement(M + "Items",
                new XElement(T + "Message",
                    new XElement(T + "MimeContent", Convert.ToBase64String(mime)),
                    // PR_MESSAGE_FLAGS = MSGFLAG_READ: import as a regular (non-draft) read message.
                    new XElement(T + "ExtendedProperty",
                        ExtendedFieldUri("0x0E07", "Integer"),
                        new XElement(T + "Value", "1")))));
        var response = await _ews.SendAsync(request, ct).ConfigureAwait(false);
        EwsClient.ThrowOnError(response);
        return (string?)response.Descendants(T + "ItemId").FirstOrDefault()?.Attribute("Id") ?? "";
    }

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
