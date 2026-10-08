using System.Globalization;
using System.Text.Json;
using MailClient.Core.Models;
using Microsoft.Data.Sqlite;

namespace MailClient.Core.Storage;

/// <summary>
/// Per-account offline cache (SQLite). Holds the folder tree, message summaries, downloaded
/// message bodies and the EWS sync states, so the UI starts instantly and works offline.
/// </summary>
public sealed class LocalCache
{
    private const int SchemaVersion = 1;

    /// <summary>
    /// Version of the message decoding (MIME, charsets). Cached bodies are decoded copies: when decoding improves,
    /// bump this so bodies opened with an older version (e.g. with mojibake) are dropped and re-read from the
    /// server. Folders, the message list and sync states are kept.
    /// </summary>
    internal const int BodyFormatVersion = 2;

    /// <summary>Downloaded message bodies not opened for this long are dropped (re-read from the server when needed).</summary>
    internal static readonly TimeSpan BodyRetention = TimeSpan.FromDays(60);
    private readonly string _connectionString;
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = false };

    public LocalCache(string databasePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Private cache + WAL: readers never block the background sync writer, and lock waits use the
            // busy timeout (shared-cache mode would fail immediately with SQLITE_LOCKED instead).
            Cache = SqliteCacheMode.Default,
            DefaultTimeout = 30,
        }.ToString();
        Initialize();
    }

    /// <summary>Closes pooled connections so database files can be deleted (Windows keeps open files locked).</summary>
    public static void ReleaseFiles() => SqliteConnection.ClearAllPools();

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        // SQLite's LIKE/lower() only fold ASCII; Cyrillic needs a Unicode-aware lower-case.
        c.CreateFunction("ulower", (string? s) => s?.ToLowerInvariant(), isDeterministic: true);
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        long version = (long)(new SqliteCommand("PRAGMA user_version;", c).ExecuteScalar() ?? 0L);
        if (version != SchemaVersion)
        {
            Exec(c, """
                DROP TABLE IF EXISTS folders;
                DROP TABLE IF EXISTS messages;
                DROP TABLE IF EXISTS bodies;
                DROP TABLE IF EXISTS pending_read;
                CREATE TABLE folders(
                    id TEXT PRIMARY KEY, change_key TEXT, parent_id TEXT, name TEXT NOT NULL,
                    folder_class TEXT, total INTEGER, unread INTEGER, child_count INTEGER,
                    well_known INTEGER, sync_state TEXT);
                CREATE TABLE messages(
                    id TEXT PRIMARY KEY, folder_id TEXT NOT NULL, change_key TEXT, subject TEXT,
                    from_name TEXT, from_addr TEXT, display_to TEXT, display_cc TEXT,
                    received INTEGER, sent INTEGER, is_read INTEGER, has_att INTEGER,
                    importance INTEGER, flag INTEGER, size INTEGER, preview TEXT,
                    item_class TEXT, conversation_id TEXT, categories TEXT);
                CREATE INDEX ix_messages_folder ON messages(folder_id, received DESC);
                CREATE TABLE bodies(id TEXT PRIMARY KEY, json TEXT NOT NULL, cached_at INTEGER);
                """);
            Exec(c, $"PRAGMA user_version={SchemaVersion};");
        }
        // Read states set while the server could not be reached, sent with the next synchronization.
        Exec(c, "CREATE TABLE IF NOT EXISTS pending_read(id TEXT PRIMARY KEY, is_read INTEGER NOT NULL);");
        long bodyFormat = (long)(new SqliteCommand("PRAGMA application_id;", c).ExecuteScalar() ?? 0L);
        if (bodyFormat != BodyFormatVersion)
        {
            Exec(c, "DELETE FROM bodies;");
            Exec(c, $"PRAGMA application_id={BodyFormatVersion};");
        }
        // Keep the cache bounded: every opened message would otherwise stay here forever.
        using var prune = c.CreateCommand();
        prune.CommandText = "DELETE FROM bodies WHERE cached_at < $t";
        prune.Parameters.AddWithValue("$t", (DateTimeOffset.UtcNow - BodyRetention).ToUnixTimeSeconds());
        prune.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ folders

    /// <summary>
    /// Replaces the cached folder tree, keeping sync states of folders that still exist. A folder that has been
    /// synchronized keeps the counters of its cached messages, which is what the user sees and can change:
    /// the server's own counter may include items the list never shows, or lag behind a "mark as read"
    /// that is still on its way. Returns the synchronized folders whose server counters differ from the cache,
    /// so they can be synchronized to settle the difference.
    /// </summary>
    public IReadOnlyList<string> ReplaceFolders(IEnumerable<MailFolder> folders)
    {
        var outOfStep = new List<string>();
        using var c = Open();
        using var tx = c.BeginTransaction();
        var states = new Dictionary<string, string?>();
        using (var read = c.CreateCommand())
        {
            read.CommandText = "SELECT id, sync_state FROM folders";
            using var r = read.ExecuteReader();
            while (r.Read()) states[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
        }
        var list = folders.ToList();
        var keep = new HashSet<string>(list.Select(f => f.Id));
        var cachedCounts = new Dictionary<string, (int Total, int Unread)>();
        using (var read = c.CreateCommand())
        {
            read.CommandText = "SELECT folder_id, COUNT(*), SUM(CASE WHEN is_read=0 THEN 1 ELSE 0 END) FROM messages GROUP BY folder_id";
            using var r = read.ExecuteReader();
            while (r.Read()) cachedCounts[r.GetString(0)] = (r.GetInt32(1), r.IsDBNull(2) ? 0 : r.GetInt32(2));
        }
        Exec(c, "DELETE FROM folders");
        foreach (var f in list)
        {
            int total = f.TotalCount, unread = f.UnreadCount;
            if (states.TryGetValue(f.Id, out var synced) && synced != null)
            {
                var cached = cachedCounts.GetValueOrDefault(f.Id);
                if (cached.Unread != f.UnreadCount || cached.Total != f.TotalCount) outOfStep.Add(f.Id);
                (total, unread) = cached;
            }
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO folders(id, change_key, parent_id, name, folder_class, total, unread, child_count, well_known, sync_state)
                VALUES($id,$ck,$p,$n,$fc,$t,$u,$cc,$wk,$ss)
                """;
            cmd.Parameters.AddWithValue("$id", f.Id);
            cmd.Parameters.AddWithValue("$ck", f.ChangeKey);
            cmd.Parameters.AddWithValue("$p", (object?)f.ParentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$n", f.DisplayName);
            cmd.Parameters.AddWithValue("$fc", f.FolderClass);
            cmd.Parameters.AddWithValue("$t", total);
            cmd.Parameters.AddWithValue("$u", unread);
            cmd.Parameters.AddWithValue("$cc", f.ChildFolderCount);
            cmd.Parameters.AddWithValue("$wk", (int)f.WellKnown);
            cmd.Parameters.AddWithValue("$ss", states.TryGetValue(f.Id, out var s) && s != null ? s : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        // Drop messages of folders that disappeared.
        using (var del = c.CreateCommand())
        {
            del.CommandText = "DELETE FROM messages WHERE folder_id NOT IN (SELECT id FROM folders)";
            del.ExecuteNonQuery();
        }
        tx.Commit();
        return outOfStep;
    }

    public List<MailFolder> GetFolders()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, change_key, parent_id, name, folder_class, total, unread, child_count, well_known FROM folders";
        using var r = cmd.ExecuteReader();
        var list = new List<MailFolder>();
        while (r.Read())
        {
            list.Add(new MailFolder
            {
                Id = r.GetString(0),
                ChangeKey = r.IsDBNull(1) ? "" : r.GetString(1),
                ParentId = r.IsDBNull(2) ? null : r.GetString(2),
                DisplayName = r.GetString(3),
                FolderClass = r.IsDBNull(4) ? "" : r.GetString(4),
                TotalCount = r.GetInt32(5),
                UnreadCount = r.GetInt32(6),
                ChildFolderCount = r.GetInt32(7),
                WellKnown = (WellKnownFolder)r.GetInt32(8),
            });
        }
        return list;
    }

    /// <summary>Stores a folder's new name (after a rename on the server, before the next folder refresh).</summary>
    public void RenameFolder(string folderId, string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE folders SET name=$n WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.ExecuteNonQuery();
    }

    public string? GetSyncState(string folderId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT sync_state FROM folders WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", folderId);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSyncState(string folderId, string? state)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE folders SET sync_state=$s WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.Parameters.AddWithValue("$s", (object?)state ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Recomputes the cached total/unread counters of a synchronized folder from its cached messages (a folder
    /// that was never synchronized holds only part of its messages and keeps the server's counters).
    /// </summary>
    public (int total, int unread) RefreshFolderCounts(string folderId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE folders SET
              total  = (SELECT COUNT(*) FROM messages WHERE folder_id=$id),
              unread = (SELECT COUNT(*) FROM messages WHERE folder_id=$id AND is_read=0)
            WHERE id=$id AND sync_state IS NOT NULL;
            SELECT total, unread FROM folders WHERE id=$id;
            """;
        cmd.Parameters.AddWithValue("$id", folderId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetInt32(1)) : (0, 0);
    }

    // ------------------------------------------------------------------ messages

    public void UpsertMessages(IEnumerable<MessageSummary> messages)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var m in messages)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO messages(id, folder_id, change_key, subject, from_name, from_addr, display_to, display_cc,
                    received, sent, is_read, has_att, importance, flag, size, preview, item_class, conversation_id, categories)
                VALUES($id,$f,$ck,$s,$fn,$fa,$dt,$dc,$r,$se,COALESCE((SELECT is_read FROM pending_read WHERE id=$id),$ir),$ha,$im,$fl,$sz,$pv,$ic,$cv,$cat)
                ON CONFLICT(id) DO UPDATE SET folder_id=$f, change_key=$ck, subject=$s, from_name=$fn, from_addr=$fa,
                    display_to=$dt, display_cc=$dc, received=$r, sent=$se,
                    is_read=COALESCE((SELECT is_read FROM pending_read WHERE id=$id),$ir), has_att=$ha, importance=$im,
                    flag=$fl, size=$sz, preview=$pv, item_class=$ic, conversation_id=$cv, categories=$cat
                """;
            cmd.Parameters.AddWithValue("$id", m.Id);
            cmd.Parameters.AddWithValue("$f", m.FolderId);
            cmd.Parameters.AddWithValue("$ck", m.ChangeKey);
            cmd.Parameters.AddWithValue("$s", m.Subject);
            cmd.Parameters.AddWithValue("$fn", m.From?.Name ?? "");
            cmd.Parameters.AddWithValue("$fa", m.From?.Address ?? "");
            cmd.Parameters.AddWithValue("$dt", m.DisplayTo);
            cmd.Parameters.AddWithValue("$dc", m.DisplayCc);
            cmd.Parameters.AddWithValue("$r", m.DateReceived.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$se", m.DateSent.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$ir", m.IsRead ? 1 : 0);
            cmd.Parameters.AddWithValue("$ha", m.HasAttachments ? 1 : 0);
            cmd.Parameters.AddWithValue("$im", (int)m.Importance);
            cmd.Parameters.AddWithValue("$fl", (int)m.Flag);
            cmd.Parameters.AddWithValue("$sz", m.Size);
            cmd.Parameters.AddWithValue("$pv", m.Preview);
            cmd.Parameters.AddWithValue("$ic", m.ItemClass);
            cmd.Parameters.AddWithValue("$cv", m.ConversationId);
            cmd.Parameters.AddWithValue("$cat", string.Join("\u001f", m.Categories));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void DeleteMessages(IEnumerable<string> ids)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var id in ids)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM messages WHERE id=$id; DELETE FROM bodies WHERE id=$id; DELETE FROM pending_read WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void ClearFolderMessages(string folderId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM bodies WHERE id IN (SELECT id FROM messages WHERE folder_id=$f); DELETE FROM messages WHERE folder_id=$f;";
        cmd.Parameters.AddWithValue("$f", folderId);
        cmd.ExecuteNonQuery();
    }

    public void SetReadState(IEnumerable<string> ids, bool isRead) =>
        UpdateColumn(ids, "is_read", isRead ? 1 : 0);

    /// <summary>A read state reported by the server; messages with a change of their own still on its way keep it.</summary>
    public void ApplyServerReadState(IEnumerable<string> ids, bool isRead)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var id in ids)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE messages SET is_read=$v WHERE id=$id AND NOT EXISTS (SELECT 1 FROM pending_read WHERE id=$id)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$v", isRead ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// Remembers read states that still have to reach the server. Until then synchronization does not overwrite
    /// them, so a message read without a connection does not turn unread again.
    /// </summary>
    public void QueueReadState(IEnumerable<string> ids, bool isRead)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var id in ids)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO pending_read(id, is_read) VALUES($id,$v) ON CONFLICT(id) DO UPDATE SET is_read=$v";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$v", isRead ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<(string Id, bool IsRead)> GetPendingReadStates()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, is_read FROM pending_read";
        using var r = cmd.ExecuteReader();
        var list = new List<(string, bool)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1) != 0));
        return list;
    }

    /// <summary>Forgets queued read states that reached the server (unless the user changed them again meanwhile).</summary>
    public void CompletePendingReadStates(IEnumerable<string> ids, bool isRead)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var id in ids)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM pending_read WHERE id=$id AND is_read=$v";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$v", isRead ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void SetFlag(IEnumerable<string> ids, FlagStatus flag) =>
        UpdateColumn(ids, "flag", (int)flag);

    private void UpdateColumn(IEnumerable<string> ids, string column, int value)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var id in ids)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"UPDATE messages SET {column}=$v WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<MessageSummary> GetMessages(string folderId, int offset, int limit, string? search = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = "folder_id=$f";
        if (!string.IsNullOrWhiteSpace(search))
        {
            where += " AND (ulower(subject) LIKE $q ESCAPE '\\' OR ulower(from_name) LIKE $q ESCAPE '\\' OR ulower(from_addr) LIKE $q ESCAPE '\\' OR ulower(display_to) LIKE $q ESCAPE '\\' OR ulower(preview) LIKE $q ESCAPE '\\')";
            var escaped = search.ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            cmd.Parameters.AddWithValue("$q", $"%{escaped}%");
        }
        cmd.CommandText = $"""
            SELECT id, folder_id, change_key, subject, from_name, from_addr, display_to, display_cc, received, sent,
                   is_read, has_att, importance, flag, size, preview, item_class, conversation_id, categories
            FROM messages WHERE {where} ORDER BY received DESC LIMIT $l OFFSET $o
            """;
        cmd.Parameters.AddWithValue("$f", folderId);
        cmd.Parameters.AddWithValue("$l", limit);
        cmd.Parameters.AddWithValue("$o", offset);
        using var r = cmd.ExecuteReader();
        var list = new List<MessageSummary>();
        while (r.Read())
        {
            var fromName = r.GetString(4);
            var fromAddr = r.GetString(5);
            var cats = r.IsDBNull(18) ? "" : r.GetString(18);
            list.Add(new MessageSummary
            {
                Id = r.GetString(0),
                FolderId = r.GetString(1),
                ChangeKey = r.IsDBNull(2) ? "" : r.GetString(2),
                Subject = r.IsDBNull(3) ? "" : r.GetString(3),
                From = fromName.Length == 0 && fromAddr.Length == 0 ? null : new EmailAddress(fromName, fromAddr),
                DisplayTo = r.IsDBNull(6) ? "" : r.GetString(6),
                DisplayCc = r.IsDBNull(7) ? "" : r.GetString(7),
                DateReceived = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8)),
                DateSent = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9)),
                IsRead = r.GetInt32(10) != 0,
                HasAttachments = r.GetInt32(11) != 0,
                Importance = (Importance)r.GetInt32(12),
                Flag = (FlagStatus)r.GetInt32(13),
                Size = r.GetInt64(14),
                Preview = r.IsDBNull(15) ? "" : r.GetString(15),
                ItemClass = r.IsDBNull(16) ? "IPM.Note" : r.GetString(16),
                ConversationId = r.IsDBNull(17) ? "" : r.GetString(17),
                Categories = cats.Length == 0 ? new() : cats.Split('\u001f').ToList(),
            });
        }
        return list;
    }

    /// <summary>Distinct senders matching <paramref name="text"/>, most recent first (address autocomplete).</summary>
    public List<EmailAddress> SuggestAddresses(string text, int limit)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT from_name, from_addr, MAX(received) AS last FROM messages
            WHERE from_addr <> '' AND (ulower(from_name) LIKE $q ESCAPE '\' OR ulower(from_addr) LIKE $q ESCAPE '\')
            GROUP BY lower(from_addr) ORDER BY last DESC LIMIT $l
            """;
        var escaped = text.Trim().ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        cmd.Parameters.AddWithValue("$q", $"%{escaped}%");
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<EmailAddress>();
        while (r.Read()) list.Add(new EmailAddress(r.GetString(0), r.GetString(1)));
        return list;
    }

    public int CountMessages(string folderId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE folder_id=$f";
        cmd.Parameters.AddWithValue("$f", folderId);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ bodies

    public MailMessage? GetCachedMessage(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        // Reading marks the body as recently used, so messages people keep opening are not pruned.
        cmd.CommandText = "UPDATE bodies SET cached_at=$t WHERE id=$id RETURNING json";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<MailMessage>(json, JsonOptions) : null;
    }

    public void PutCachedMessage(MailMessage message)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO bodies(id, json, cached_at) VALUES($id,$j,$t)";
        cmd.Parameters.AddWithValue("$id", message.Id);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(message, JsonOptions));
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    public void InvalidateCachedMessage(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM bodies WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}
