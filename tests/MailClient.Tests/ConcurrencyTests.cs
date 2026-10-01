using MailClient.Core.Models;
using MailClient.Core.Storage;
using Xunit;

namespace MailClient.Tests;

public class ConcurrencyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mc-conc-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public async Task Cache_tolerates_concurrent_sync_writes_and_ui_reads()
    {
        var cache = new LocalCache(_path);
        cache.ReplaceFolders(new[] { new MailFolder { Id = "F", DisplayName = "Inbox" } });

        var writer = Task.Run(() =>
        {
            for (int batch = 0; batch < 40; batch++)
                cache.UpsertMessages(Enumerable.Range(0, 50).Select(i => new MessageSummary
                {
                    Id = $"{batch}-{i}", FolderId = "F", Subject = $"Письмо {i}", DateReceived = DateTimeOffset.UtcNow,
                }));
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                cache.GetMessages("F", 0, 200);
                cache.GetFolders();
                cache.SetReadState(new[] { "0-1", "0-2" }, i % 2 == 0);
            }
        }));

        await Task.WhenAll(readers.Append(writer));
        Assert.Equal(2000, cache.CountMessages("F"));
    }
}
