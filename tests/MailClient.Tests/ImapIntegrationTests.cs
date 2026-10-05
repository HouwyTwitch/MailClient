using MailClient.Core.Models;
using MailClient.Core.Services;
using MailClient.Imap;
using MimeKit;
using Xunit;

namespace MailClient.Tests;

/// <summary>
/// End-to-end tests against a real IMAP server (Dovecot) and SMTP server.
/// Enabled when MAILCLIENT_IMAP_TEST=1; see tests/imap-test-env.md for the server setup.
/// Environment: IMAP_TEST_HOST, IMAP_TEST_PORT (TLS), IMAP_TEST_CA (root CA PEM file),
/// IMAP_TEST_USER, IMAP_TEST_PASSWORD, SMTP_TEST_PORT (plain, no auth), SMTP_TEST_MAILDIR.
/// </summary>
[Collection("imap")]
public class ImapIntegrationTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("MAILCLIENT_IMAP_TEST") == "1";
    private static string Env(string name, string fallback = "") => Environment.GetEnvironmentVariable(name) ?? fallback;

    private sealed class Creds : ICredentialProvider
    {
        private readonly string _password;
        public Creds(string password) => _password = password;
        public string? GetPassword(Guid accountId) => _password;
        public Task<string> GetAccessTokenAsync(AccountSettings account, bool forceRefresh, CancellationToken ct) => throw new NotSupportedException();
    }

    private static AccountSettings Account(bool withCa = true) => new()
    {
        Protocol = MailProtocol.Imap,
        EmailAddress = Env("IMAP_TEST_USER"),
        DisplayName = "Иванов Иван",
        ImapHost = Env("IMAP_TEST_HOST", "localhost"),
        ImapPort = int.Parse(Env("IMAP_TEST_PORT", "993")),
        ImapSecurity = ConnectionSecurity.SslOnConnect,
        SmtpHost = Env("IMAP_TEST_HOST", "localhost"),
        SmtpPort = int.Parse(Env("SMTP_TEST_PORT", "25")),
        SmtpSecurity = ConnectionSecurity.None,
        TrustedRootCertificatesPem = withCa ? File.ReadAllText(Env("IMAP_TEST_CA")) : "",
    };

    private static ImapProvider Provider(string? password = null, bool withCa = true) =>
        new(Account(withCa), new Creds(password ?? Env("IMAP_TEST_PASSWORD")));

    private static byte[] Eml(string subject, string body, string? attachmentName = null, DateTimeOffset? date = null)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("Петров Пётр", "petrov@test.ru"));
        m.To.Add(new MailboxAddress("Иванов Иван", "ivanov@test.ru"));
        m.Subject = subject;
        m.Date = date ?? DateTimeOffset.Now;
        m.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("test.ru");
        var b = new BodyBuilder { HtmlBody = $"<p>{body}</p>", TextBody = body };
        if (attachmentName != null) b.Attachments.Add(attachmentName, "содержимое файла"u8.ToArray(), new ContentType("text", "plain"));
        m.Body = b.ToMessageBody();
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Full_mailbox_workflow()
    {
        Assert.SkipUnless(Enabled, "Сквозные тесты IMAP/SMTP включаются переменной MAILCLIENT_IMAP_TEST=1 (см. tests/imap-test-env.md).");
        using var p = Provider();

        var info = await p.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Env("IMAP_TEST_USER"), info.EmailAddress);

        var folders = await p.GetFoldersAsync(TestContext.Current.CancellationToken);
        var inbox = folders.Single(f => f.WellKnown == WellKnownFolder.Inbox);
        Assert.Equal("Входящие", inbox.DisplayName);
        Assert.Contains(folders, f => f.WellKnown == WellKnownFolder.SentItems);
        Assert.Contains(folders, f => f.WellKnown == WellKnownFolder.Drafts);
        var trash = folders.Single(f => f.WellKnown == WellKnownFolder.DeletedItems);

        // Clean slate.
        await p.EmptyFolderAsync(inbox.Id, false, TestContext.Current.CancellationToken);
        await p.EmptyFolderAsync(trash.Id, false, TestContext.Current.CancellationToken);

        // Import three messages (Cyrillic subjects, attachment with a Cyrillic file name).
        await p.ImportMimeAsync(inbox.Id, Eml("Отчёт за квартал", "Добрый день, отчёт во вложении", "Отчёт 2026.txt", DateTimeOffset.Now.AddHours(-2)), TestContext.Current.CancellationToken);
        await p.ImportMimeAsync(inbox.Id, Eml("Совещание", "Совещание в 15:00", date: DateTimeOffset.Now.AddHours(-1)), TestContext.Current.CancellationToken);
        await p.ImportMimeAsync(inbox.Id, Eml("Привет", "Как дела?"), TestContext.Current.CancellationToken);

        // Paging, newest first.
        var page = await p.GetMessagesAsync(inbox.Id, 0, 2, TestContext.Current.CancellationToken);
        Assert.Equal(3, page.TotalCount);
        Assert.True(page.HasMore);
        Assert.Equal("Привет", page.Items[0].Subject);
        Assert.Equal("Петров Пётр", page.Items[0].From!.Name);

        // Initial incremental sync in batches of 2: two rounds, then complete.
        var first = await p.SyncFolderItemsAsync(inbox.Id, null, 2, TestContext.Current.CancellationToken);
        Assert.Equal(2, first.CreatedOrUpdated.Count);
        Assert.False(first.IncludesLastItem);
        var second = await p.SyncFolderItemsAsync(inbox.Id, first.SyncState, 2, TestContext.Current.CancellationToken);
        Assert.Single(second.CreatedOrUpdated);
        Assert.True(second.IncludesLastItem);
        var all = first.CreatedOrUpdated.Concat(second.CreatedOrUpdated).ToList();
        var report = all.Single(m => m.Subject == "Отчёт за квартал");
        Assert.True(report.HasAttachments);
        Assert.True(report.IsRead); // imported messages are stored as read

        // Nothing changed -> no changes.
        var idle = await p.SyncFolderItemsAsync(inbox.Id, second.SyncState, 50, TestContext.Current.CancellationToken);
        Assert.Empty(idle.CreatedOrUpdated);
        Assert.Empty(idle.Deleted);
        Assert.Empty(idle.ReadFlagChanges);

        // Read + flag changes are picked up incrementally.
        await p.SetReadStateAsync(new[] { report.Id }, false, TestContext.Current.CancellationToken);
        var meeting = all.Single(m => m.Subject == "Совещание");
        await p.SetFlagAsync(new[] { meeting.Id }, FlagStatus.Flagged, TestContext.Current.CancellationToken);
        var changes = await p.SyncFolderItemsAsync(inbox.Id, idle.SyncState, 50, TestContext.Current.CancellationToken);
        Assert.False(changes.ReadFlagChanges[report.Id]);
        Assert.Equal(FlagStatus.Flagged, changes.CreatedOrUpdated.Single(m => m.Id == meeting.Id).Flag);

        // Full message + attachment download.
        var full = await p.GetMessageAsync(report.Id, TestContext.Current.CancellationToken);
        Assert.True(full.BodyIsHtml);
        Assert.Contains("отчёт во вложении", full.Body);
        Assert.Equal("ivanov@test.ru", full.To.Single().Address);
        var att = Assert.Single(full.Attachments);
        Assert.Equal("Отчёт 2026.txt", att.Name);
        var content = await p.GetAttachmentAsync(att.Id, TestContext.Current.CancellationToken);
        Assert.Equal("содержимое файла", System.Text.Encoding.UTF8.GetString(content.Content));
        Assert.NotEmpty(await p.GetMimeContentAsync(report.Id, TestContext.Current.CancellationToken));

        // Cyrillic server-side search.
        var found = await p.SearchMessagesAsync(inbox.Id, "Совещание", 0, 10, TestContext.Current.CancellationToken);
        Assert.Equal("Совещание", Assert.Single(found.Items).Subject);

        // Folder management with a Cyrillic name (modified UTF-7 on the wire).
        var projects = await p.CreateFolderAsync(ImapProvider.RootId, "Проекты", TestContext.Current.CancellationToken);
        var moved = await p.MoveItemsAsync(new[] { meeting.Id }, projects.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(moved[0]);
        Assert.Equal(1, (await p.GetMessagesAsync(projects.Id, 0, 10, TestContext.Current.CancellationToken)).TotalCount);
        await p.RenameFolderAsync(projects.Id, "Проекты 2026", TestContext.Current.CancellationToken);
        var renamed = (await p.GetFoldersAsync(TestContext.Current.CancellationToken)).Single(f => f.DisplayName == "Проекты 2026");
        Assert.Equal(1, renamed.TotalCount);
        await p.DeleteFolderAsync(renamed.Id, permanent: true, ct: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(await p.GetFoldersAsync(TestContext.Current.CancellationToken), f => f.DisplayName.StartsWith("Проекты"));

        // Deletion goes to Trash and is seen by sync.
        var hello = all.Single(m => m.Subject == "Привет");
        await p.DeleteItemsAsync(new[] { hello.Id }, permanent: false, ct: TestContext.Current.CancellationToken);
        var afterDelete = await p.SyncFolderItemsAsync(inbox.Id, changes.SyncState, 50, TestContext.Current.CancellationToken);
        Assert.Contains(hello.Id, afterDelete.Deleted);
        Assert.Contains(meeting.Id, afterDelete.Deleted);
        Assert.Equal(1, (await p.GetMessagesAsync(trash.Id, 0, 10, TestContext.Current.CancellationToken)).TotalCount);
    }

    [Fact]
    public async Task Send_reply_saves_sent_copy_with_threading_and_quote()
    {
        Assert.SkipUnless(Enabled, "Сквозные тесты IMAP/SMTP включаются переменной MAILCLIENT_IMAP_TEST=1 (см. tests/imap-test-env.md).");
        using var p = Provider();
        var folders = await p.GetFoldersAsync(TestContext.Current.CancellationToken);
        var inbox = folders.Single(f => f.WellKnown == WellKnownFolder.Inbox).Id;
        var sent = folders.Single(f => f.WellKnown == WellKnownFolder.SentItems).Id;
        await p.EmptyFolderAsync(sent, false, TestContext.Current.CancellationToken);
        var originalId = await p.ImportMimeAsync(inbox, Eml("Вопрос по договору", "Подскажите сроки"), TestContext.Current.CancellationToken);

        await p.SendAsync(new OutgoingMessage
        {
            Action = ComposeAction.Reply,
            ReferenceItemId = originalId,
            To = { new EmailAddress("Петров Пётр", "petrov@test.ru") },
            Subject = "RE: Вопрос по договору",
            Body = "<p>Сроки — до пятницы.</p>",
            Importance = Importance.High,
            Attachments = { new OutgoingAttachment { Name = "Договор №5.pdf", ContentType = "application/pdf", Content = new byte[] { 37, 80, 68, 70 } } },
        }, TestContext.Current.CancellationToken);

        var sentPage = await p.GetMessagesAsync(sent, 0, 10, TestContext.Current.CancellationToken);
        var copy = Assert.Single(sentPage.Items);
        Assert.Equal("RE: Вопрос по договору", copy.Subject);
        Assert.Equal(Importance.High, copy.Importance);
        Assert.True(copy.IsRead);

        var mime = MimeMessage.Load(new MemoryStream(await p.GetMimeContentAsync(copy.Id, TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken);
        var original = MimeMessage.Load(new MemoryStream(await p.GetMimeContentAsync(originalId, TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken);
        Assert.Equal(original.MessageId, mime.InReplyTo);
        Assert.Contains("Подскажите сроки", mime.HtmlBody);
        Assert.Contains("Исходное сообщение", mime.HtmlBody);
        Assert.Equal("Договор №5.pdf", mime.Attachments.OfType<MimePart>().Single().FileName);

        // The SMTP server received it too.
        var maildir = Path.Combine(Env("SMTP_TEST_MAILDIR"), "new");
        var messageId = Assert.IsType<string>(mime.MessageId);
        Assert.Contains(Directory.GetFiles(maildir), f => File.ReadAllText(f).Contains(messageId));
    }

    [Fact]
    public async Task Draft_save_and_replace()
    {
        Assert.SkipUnless(Enabled, "Сквозные тесты IMAP/SMTP включаются переменной MAILCLIENT_IMAP_TEST=1 (см. tests/imap-test-env.md).");
        using var p = Provider();
        var drafts = (await p.GetFoldersAsync(TestContext.Current.CancellationToken)).Single(f => f.WellKnown == WellKnownFolder.Drafts).Id;
        await p.EmptyFolderAsync(drafts, false, TestContext.Current.CancellationToken);

        var id1 = await p.SaveDraftAsync(new OutgoingMessage { Subject = "Черновик", Body = "<p>v1</p>", To = { new EmailAddress("", "a@test.ru") } }, TestContext.Current.CancellationToken);
        Assert.NotEmpty(id1);
        var id2 = await p.SaveDraftAsync(new OutgoingMessage { Action = ComposeAction.EditDraft, ReferenceItemId = id1, Subject = "Черновик", Body = "<p>v2</p>" }, TestContext.Current.CancellationToken);
        var page = await p.GetMessagesAsync(drafts, 0, 10, TestContext.Current.CancellationToken);
        Assert.Equal(id2, Assert.Single(page.Items).Id);
        Assert.Contains("v2", (await p.GetMessageAsync(id2, TestContext.Current.CancellationToken)).Body);
    }

    [Fact]
    public async Task Sync_engine_keeps_offline_cache_in_step_with_server()
    {
        Assert.SkipUnless(Enabled, "Сквозные тесты IMAP/SMTP включаются переменной MAILCLIENT_IMAP_TEST=1 (см. tests/imap-test-env.md).");
        var db = Path.Combine(Path.GetTempPath(), $"mc-imap-{Guid.NewGuid():N}.db");
        try
        {
            using var p = Provider();
            var cache = new MailClient.Core.Storage.LocalCache(db);
            var engine = new SyncEngine(p, cache);
            var folders = await engine.SyncFoldersAsync(TestContext.Current.CancellationToken);
            var inbox = folders.Single(f => f.WellKnown == WellKnownFolder.Inbox).Id;
            await p.EmptyFolderAsync(inbox, false, TestContext.Current.CancellationToken);
            for (int i = 0; i < 5; i++) await p.ImportMimeAsync(inbox, Eml($"Письмо {i}", "текст", date: DateTimeOffset.Now.AddMinutes(i)), TestContext.Current.CancellationToken);

            await engine.PrimeFolderAsync(inbox, 3, TestContext.Current.CancellationToken);
            Assert.Equal(3, cache.CountMessages(inbox));
            await engine.SyncFolderAsync(inbox, TestContext.Current.CancellationToken);
            Assert.Equal(5, cache.CountMessages(inbox));
            Assert.Equal("Письмо 4", cache.GetMessages(inbox, 0, 10)[0].Subject);

            var arrived = new List<MessageSummary>();
            engine.NewMessagesArrived += (_, list) => arrived.AddRange(list);
            var victim = cache.GetMessages(inbox, 0, 10).Last();
            await p.SetReadStateAsync(new[] { cache.GetMessages(inbox, 0, 1)[0].Id }, false, TestContext.Current.CancellationToken);
            await p.DeleteItemsAsync(new[] { victim.Id }, permanent: true, ct: TestContext.Current.CancellationToken);
            var newId = await p.ImportMimeAsync(inbox, Eml("Новое письмо", "текст"), TestContext.Current.CancellationToken);
            await p.SetReadStateAsync(new[] { newId }, false, TestContext.Current.CancellationToken);
            await engine.SyncFolderAsync(inbox, TestContext.Current.CancellationToken);

            var cached = cache.GetMessages(inbox, 0, 10);
            Assert.Equal(5, cached.Count);
            Assert.DoesNotContain(cached, m => m.Id == victim.Id);
            Assert.Contains(cached, m => m.Subject == "Новое письмо" && !m.IsRead);
            Assert.Contains(arrived, m => m.Subject == "Новое письмо");
            Assert.Equal(2, cache.GetFolders().Single(f => f.Id == inbox).UnreadCount);

            var body = await engine.GetMessageAsync(newId, TestContext.Current.CancellationToken);
            Assert.Contains("текст", body.Body);
            Assert.NotNull(cache.GetCachedMessage(newId)); // available offline afterwards
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { db, db + "-wal", db + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task Wrong_password_is_reported_as_authentication_error()
    {
        Assert.SkipUnless(Enabled, "Сквозные тесты IMAP/SMTP включаются переменной MAILCLIENT_IMAP_TEST=1 (см. tests/imap-test-env.md).");
        using var p = Provider(password: "неверный");
        var ex = await Assert.ThrowsAsync<MailAuthenticationException>(() => p.GetFoldersAsync(TestContext.Current.CancellationToken));
        Assert.Contains("пароль приложения", ex.Message);
    }

    [Fact]
    public async Task Untrusted_certificate_explains_root_ca_import()
    {
        Assert.SkipUnless(Enabled, "Сквозные тесты IMAP/SMTP включаются переменной MAILCLIENT_IMAP_TEST=1 (см. tests/imap-test-env.md).");
        using var p = Provider(withCa: false);
        var ex = await Assert.ThrowsAsync<MailConnectionException>(() => p.GetFoldersAsync(TestContext.Current.CancellationToken));
        Assert.Contains("корневой сертификат", ex.Message);
    }
}
