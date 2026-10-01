using MailClient.Core.Models;
using MailClient.Core.Rendering;
using MailClient.Core.Storage;
using MailClient.Core.Services;
using Xunit;

namespace MailClient.Tests;

public class EmailAddressTests
{
    [Fact]
    public void ParseList_handles_names_quotes_and_mixed_separators()
    {
        var list = EmailAddress.ParseList("Иван Петров <ivan@contoso.ru>; \"Сидоров, Пётр\" <petr@contoso.ru>, anna@contoso.ru");
        Assert.Equal(3, list.Count);
        Assert.Equal("Иван Петров", list[0].Name);
        Assert.Equal("ivan@contoso.ru", list[0].Address);
        Assert.Equal("Сидоров, Пётр", list[1].Name);
        Assert.Equal("anna@contoso.ru", list[2].Address);
    }

    [Fact]
    public void FormatList_roundtrips_names_with_commas()
    {
        var src = new[] { new EmailAddress("Сидоров, Пётр", "petr@contoso.ru"), new EmailAddress("", "a@b.ru") };
        Assert.Equal(src, EmailAddress.ParseList(EmailAddress.FormatList(src)));
    }

    [Theory]
    [InlineData("user@contoso.ru", true)]
    [InlineData("пользователь@пример.рф", true)]
    [InlineData("user", false)]
    [InlineData("a@@b", false)]
    [InlineData("a b@c.ru", false)]
    public void LooksValid(string address, bool expected) => Assert.Equal(expected, EmailAddress.LooksValid(address));
}

public class HtmlTests
{
    [Fact]
    public void Build_blocks_scripts_and_remote_content_and_embeds_cid_images()
    {
        var msg = new MailMessage
        {
            BodyIsHtml = true,
            Body = "<html><head><style>p{color:red}</style><script>alert(1)</script></head>" +
                   "<body onload=\"evil()\"><p onclick='x()'>Привет</p><img src=\"cid:logo@x\"><a href=\"javascript:alert(1)\">x</a>" +
                   "<iframe src=\"https://evil\"></iframe></body></html>",
        };
        var html = MessageHtmlBuilder.Build(msg,
            new Dictionary<string, (string, byte[])> { ["logo@x"] = ("image/png", new byte[] { 1, 2, 3 }) },
            allowRemoteContent: false, darkMode: false);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data:image/png;base64,AQID", html);
        Assert.Contains("img-src data:;", html);
        Assert.Contains("p{color:red}", html);
        Assert.Contains("Привет", html);
    }

    [Fact]
    public void Plain_text_is_encoded_and_links_are_clickable()
    {
        var html = MessageHtmlBuilder.Build(new MailMessage { Body = "a <b> https://contoso.ru/x?y=1 end" }, null, false, false);
        Assert.Contains("&lt;b&gt;", html);
        Assert.Contains("<a href=\"https://contoso.ru/x?y=1\">", html);
    }

    [Fact]
    public void InlineImageExtractor_converts_data_uris_to_cid_parts()
    {
        var (html, images) = InlineImageExtractor.Extract("<p>x</p><img src=\"data:image/png;base64,AQID\" width=\"10\"><img src='data:image/jpeg;base64,BAUG'>");
        Assert.Equal(2, images.Count);
        Assert.All(images, i => Assert.True(i.IsInline));
        Assert.Equal(new byte[] { 1, 2, 3 }, images[0].Content);
        Assert.Equal("image/jpeg", images[1].ContentType);
        Assert.Contains($"src=\"cid:{images[0].ContentId}\"", html);
        Assert.Contains($"src='cid:{images[1].ContentId}'", html);
        Assert.DoesNotContain("data:", html);
    }
}

public class LocalCacheTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mc-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void Messages_roundtrip_sorted_newest_first_with_search()
    {
        var cache = new LocalCache(_path);
        cache.ReplaceFolders(new[] { new MailFolder { Id = "F", DisplayName = "Входящие", WellKnown = WellKnownFolder.Inbox } });
        cache.UpsertMessages(new[]
        {
            new MessageSummary { Id = "1", FolderId = "F", Subject = "Старое", DateReceived = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), From = new EmailAddress("Иван", "ivan@x.ru") },
            new MessageSummary { Id = "2", FolderId = "F", Subject = "Новое 100%", DateReceived = DateTimeOffset.Parse("2026-09-01T00:00:00Z"), IsRead = true, Categories = { "Важно" } },
        });

        var list = cache.GetMessages("F", 0, 10);
        Assert.Equal(new[] { "2", "1" }, list.Select(m => m.Id));
        Assert.Equal("Иван", list[1].From!.Name);
        Assert.Equal(new[] { "Важно" }, list[0].Categories);
        Assert.Single(cache.GetMessages("F", 0, 10, "иван"));
        Assert.Single(cache.GetMessages("F", 0, 10, "100%"));

        cache.SetReadState(new[] { "1" }, true);
        Assert.Equal((2, 0), cache.RefreshFolderCounts("F"));
        cache.DeleteMessages(new[] { "1" });
        Assert.Equal(1, cache.CountMessages("F"));
    }

    [Fact]
    public void ReplaceFolders_keeps_sync_state_and_drops_removed_folders_messages()
    {
        var cache = new LocalCache(_path);
        cache.ReplaceFolders(new[] { new MailFolder { Id = "A", DisplayName = "A" }, new MailFolder { Id = "B", DisplayName = "B" } });
        cache.SetSyncState("A", "S1");
        cache.UpsertMessages(new[] { new MessageSummary { Id = "m", FolderId = "B" } });

        cache.ReplaceFolders(new[] { new MailFolder { Id = "A", DisplayName = "A2" } });

        Assert.Equal("S1", cache.GetSyncState("A"));
        Assert.Equal(0, cache.CountMessages("B"));
        Assert.Equal("A2", cache.GetFolders().Single().DisplayName);
    }

    [Fact]
    public void Message_body_cache_roundtrips()
    {
        var cache = new LocalCache(_path);
        cache.PutCachedMessage(new MailMessage { Id = "x", Subject = "Тема", Body = "<p>Текст</p>", BodyIsHtml = true, To = { new EmailAddress("A", "a@b.ru") } });
        var m = cache.GetCachedMessage("x")!;
        Assert.Equal("Тема", m.Subject);
        Assert.Equal("a@b.ru", m.To.Single().Address);
        cache.InvalidateCachedMessage("x");
        Assert.Null(cache.GetCachedMessage("x"));
    }
}

public class SyncEngineTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mc-sync-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    private static string Item(string id, bool read) =>
        $"<t:Message><t:ItemId Id=\"{id}\"/><t:Subject>{id}</t:Subject><t:DateTimeReceived>{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</t:DateTimeReceived><t:IsRead>{(read ? "true" : "false")}</t:IsRead></t:Message>";

    [Fact]
    public async Task Sync_pages_until_last_item_then_reports_new_mail_incrementally()
    {
        var fake = new FakeEws()
            .On("SyncFolderItems", FakeEws.Response("SyncFolderItems", FakeEws.Success("SyncFolderItems",
                $"<m:SyncState>S1</m:SyncState><m:IncludesLastItemInRange>false</m:IncludesLastItemInRange><m:Changes><t:Create>{Item("a", true)}</t:Create></m:Changes>")))
            .On("SyncFolderItems", FakeEws.Response("SyncFolderItems", FakeEws.Success("SyncFolderItems",
                $"<m:SyncState>S2</m:SyncState><m:IncludesLastItemInRange>true</m:IncludesLastItemInRange><m:Changes><t:Create>{Item("b", false)}</t:Create></m:Changes>")))
            .On("SyncFolderItems", FakeEws.Response("SyncFolderItems", FakeEws.Success("SyncFolderItems",
                $"<m:SyncState>S3</m:SyncState><m:IncludesLastItemInRange>true</m:IncludesLastItemInRange><m:Changes><t:Create>{Item("c", false)}</t:Create><t:Delete><t:ItemId Id=\"a\"/></t:Delete></m:Changes>")));
        using var provider = fake.CreateProvider();
        var cache = new LocalCache(_path);
        cache.ReplaceFolders(new[] { new MailFolder { Id = "F", DisplayName = "Inbox" } });
        var engine = new SyncEngine(provider, cache);
        var arrived = new List<MessageSummary>();
        engine.NewMessagesArrived += (_, list) => arrived.AddRange(list);

        await engine.SyncFolderAsync("F");          // initial: 2 pages, no notifications
        Assert.Equal(2, cache.CountMessages("F"));
        Assert.Equal("S2", cache.GetSyncState("F"));
        Assert.Empty(arrived);

        await engine.SyncFolderAsync("F");          // incremental: new unread "c", "a" deleted
        Assert.Equal(new[] { "c" }, arrived.Select(m => m.Id));
        Assert.Equal(new[] { "b", "c" }, cache.GetMessages("F", 0, 10).Select(m => m.Id).OrderBy(x => x));
        Assert.Equal("S2", fake.Last("SyncFolderItems").Element(FakeEws.M + "SyncState")!.Value);
        Assert.Empty(fake.ValidationErrors);
    }

    [Fact]
    public async Task Invalid_sync_state_triggers_full_resync()
    {
        var fake = new FakeEws()
            .On("SyncFolderItems", FakeEws.Response("SyncFolderItems", FakeEws.Error("SyncFolderItems", "ErrorInvalidSyncStateData")))
            .On("SyncFolderItems", FakeEws.Response("SyncFolderItems", FakeEws.Success("SyncFolderItems",
                $"<m:SyncState>NEW</m:SyncState><m:IncludesLastItemInRange>true</m:IncludesLastItemInRange><m:Changes><t:Create>{Item("z", true)}</t:Create></m:Changes>")));
        using var provider = fake.CreateProvider();
        var cache = new LocalCache(_path);
        cache.ReplaceFolders(new[] { new MailFolder { Id = "F", DisplayName = "Inbox" } });
        cache.SetSyncState("F", "STALE");
        cache.UpsertMessages(new[] { new MessageSummary { Id = "old", FolderId = "F" } });

        await new SyncEngine(provider, cache).SyncFolderAsync("F");

        Assert.Equal("NEW", cache.GetSyncState("F"));
        Assert.Equal(new[] { "z" }, cache.GetMessages("F", 0, 10).Select(m => m.Id));
        Assert.Null(fake.Last("SyncFolderItems").Element(FakeEws.M + "SyncState"));
    }
}

public class SuggestTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mc-sugg-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void Suggests_distinct_correspondents_case_insensitively_in_cyrillic()
    {
        var cache = new LocalCache(_path);
        cache.ReplaceFolders(new[] { new MailFolder { Id = "F", DisplayName = "Inbox" } });
        cache.UpsertMessages(new[]
        {
            new MessageSummary { Id = "1", FolderId = "F", From = new EmailAddress("Сидорова Анна", "anna@x.ru"), DateReceived = DateTimeOffset.Parse("2026-01-01T00:00:00Z") },
            new MessageSummary { Id = "2", FolderId = "F", From = new EmailAddress("Сидорова Анна", "ANNA@x.ru"), DateReceived = DateTimeOffset.Parse("2026-02-01T00:00:00Z") },
            new MessageSummary { Id = "3", FolderId = "F", From = new EmailAddress("Сидоров Олег", "oleg@x.ru"), DateReceived = DateTimeOffset.Parse("2026-03-01T00:00:00Z") },
        });
        var s = cache.SuggestAddresses("сидоров", 10);
        Assert.Equal(2, s.Count);
        Assert.Equal("oleg@x.ru", s[0].Address);
        Assert.Single(cache.SuggestAddresses("anna", 10));
    }
}
