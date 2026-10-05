using MailClient.Core.Models;

namespace MailClient.Core.Services;

[Flags]
public enum ProviderCapabilities
{
    None = 0,
    Contacts = 2,
    OutOfOffice = 8,
    /// <summary>Organization address book (Global Address List).</summary>
    Directory = 16,
    /// <summary>Accept/decline meeting invitations received by mail.</summary>
    MeetingResponses = 32,
    /// <summary>Server-side rules that forward or redirect incoming mail.</summary>
    ForwardingRules = 64,
    All = Contacts | OutOfOffice | Directory | MeetingResponses | ForwardingRules,
}

/// <summary>
/// Server protocol abstraction. The Exchange (EWS) implementation lives in MailClient.Exchange;
/// other back-ends (e.g. Microsoft Graph) can be added by implementing this interface.
/// </summary>
public interface IMailProvider : IDisposable
{
    AccountSettings Account { get; }

    /// <summary>Optional features this back-end supports; the UI hides the rest.</summary>
    ProviderCapabilities Capabilities { get; }

    /// <summary>Verifies connectivity/credentials and returns basic mailbox information.</summary>
    Task<MailboxInfo> ConnectAsync(CancellationToken ct = default);

    // ---- Folders ----
    Task<IReadOnlyList<MailFolder>> GetFoldersAsync(CancellationToken ct = default);
    Task<MailFolder> CreateFolderAsync(string parentFolderId, string name, CancellationToken ct = default);
    Task RenameFolderAsync(string folderId, string newName, CancellationToken ct = default);
    Task MoveFolderAsync(string folderId, string newParentFolderId, CancellationToken ct = default);
    Task DeleteFolderAsync(string folderId, bool permanent, CancellationToken ct = default);
    Task EmptyFolderAsync(string folderId, bool deleteSubFolders, CancellationToken ct = default);

    // ---- Messages ----
    Task<MessagePage> GetMessagesAsync(string folderId, int offset, int pageSize, CancellationToken ct = default);
    Task<MessagePage> SearchMessagesAsync(string folderId, string query, int offset, int pageSize, CancellationToken ct = default);
    Task<FolderSyncResult> SyncFolderItemsAsync(string folderId, string? syncState, int maxChanges, CancellationToken ct = default);
    Task<MailMessage> GetMessageAsync(string itemId, CancellationToken ct = default);
    /// <summary>Returns the raw RFC 822 (MIME) content of an item, e.g. for "Save as .eml".</summary>
    Task<byte[]> GetMimeContentAsync(string itemId, CancellationToken ct = default);
    Task<AttachmentContent> GetAttachmentAsync(string attachmentId, CancellationToken ct = default);
    Task<IReadOnlyList<AttachmentContent>> GetAttachmentsAsync(IEnumerable<string> attachmentIds, CancellationToken ct = default);

    Task SetReadStateAsync(IEnumerable<string> itemIds, bool isRead, CancellationToken ct = default);
    Task SetFlagAsync(IEnumerable<string> itemIds, FlagStatus flag, CancellationToken ct = default);
    Task SetCategoriesAsync(string itemId, IEnumerable<string> categories, CancellationToken ct = default);
    /// <summary>Moves items and returns their new ids (same order; null when the server did not return one).</summary>
    Task<IReadOnlyList<string?>> MoveItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default);
    Task<IReadOnlyList<string?>> CopyItemsAsync(IEnumerable<string> itemIds, string destinationFolderId, CancellationToken ct = default);
    /// <summary>Marks every message of a folder read/unread on the server. Returns false when not supported.</summary>
    Task<bool> MarkAllReadAsync(string folderId, bool isRead, CancellationToken ct = default);

    /// <summary>Moves items to (or out of) the Junk folder, letting the server learn the sender. Returns new ids.</summary>
    Task<IReadOnlyList<string?>> MarkAsJunkAsync(IEnumerable<string> itemIds, bool isJunk, CancellationToken ct = default);

    /// <summary>Deletes items. When <paramref name="permanent"/> is false they go to Deleted Items.</summary>
    Task DeleteItemsAsync(IEnumerable<string> itemIds, bool permanent, CancellationToken ct = default);

    /// <summary>Sends a message (new, reply, reply-all, forward or existing draft). A copy is saved to Sent Items.</summary>
    Task SendAsync(OutgoingMessage message, CancellationToken ct = default);
    /// <summary>Saves a message to Drafts and returns the draft's item id.</summary>
    Task<string> SaveDraftAsync(OutgoingMessage message, CancellationToken ct = default);
    /// <summary>Uploads an .eml file into a folder (import).</summary>
    Task<string> ImportMimeAsync(string folderId, byte[] mime, CancellationToken ct = default);

    // ---- Directory ----
    Task<IReadOnlyList<Contact>> ResolveNamesAsync(string text, CancellationToken ct = default);

    // ---- Contacts ----
    Task<IReadOnlyList<Contact>> GetContactsAsync(string? folderId = null, CancellationToken ct = default);
    Task<string> CreateContactAsync(Contact contact, string? folderId = null, CancellationToken ct = default);
    Task UpdateContactAsync(Contact contact, CancellationToken ct = default);

    // ---- Meeting invitations (received by mail) ----
    Task RespondToMeetingAsync(string itemId, MeetingResponse response, string? comment = null, CancellationToken ct = default);

    // ---- Forwarding rules ----
    Task<ForwardingRuleSet> GetForwardingRulesAsync(CancellationToken ct = default);
    /// <summary>
    /// Makes the server's rules match <paramref name="rules"/> (in this order): new ones (empty id) are created,
    /// changed ones updated, missing ones deleted.
    /// </summary>
    /// <param name="rules">All rules of the mailbox in the order they apply.</param>
    /// <param name="replaceOutlookRules">Exchange: allowed to replace Outlook's copy of the rules (see <see cref="ForwardingRuleSet.OutlookRulesPresent"/>).</param>
    /// <param name="ct">Cancels the request.</param>
    Task SaveForwardingRulesAsync(IReadOnlyList<ForwardingRule> rules, bool replaceOutlookRules, CancellationToken ct = default);

    // ---- Automatic replies ----
    Task<OofSettings> GetOutOfOfficeAsync(CancellationToken ct = default);
    Task SetOutOfOfficeAsync(OofSettings settings, CancellationToken ct = default);
}
