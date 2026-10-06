using MailClient.Core.Models;
using MailClient.Core.Storage;
using Xunit;

namespace MailClient.Tests;

public sealed class ConcurrencyTests : IDisposable
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
        }, TestContext.Current.CancellationToken);
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

    /// <summary>Counts overlapping GetFolders calls; everything else is not needed by the folder refresh.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1852", Justification = "DispatchProxy needs an unsealed type")]
    private class SlowFolders : System.Reflection.DispatchProxy
    {
        public int Active, MaxActive, Calls;

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(MailClient.Core.Services.IMailProvider.GetFoldersAsync)) throw new NotSupportedException(method?.Name);
            return GetFoldersAsync();
        }

        private async Task<IReadOnlyList<MailFolder>> GetFoldersAsync()
        {
            var now = Interlocked.Increment(ref Active);
            lock (this) MaxActive = Math.Max(MaxActive, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref Active);
            var call = Interlocked.Increment(ref Calls);
            return new[] { new MailFolder { Id = "P", DisplayName = $"Проекты {call}" } };
        }
    }

    [Fact]
    public async Task Folder_refreshes_do_not_overlap_so_an_older_one_cannot_win()
    {
        var provider = System.Reflection.DispatchProxy.Create<MailClient.Core.Services.IMailProvider, SlowFolders>();
        var engine = new MailClient.Core.Services.SyncEngine(provider, new LocalCache(_path));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => engine.SyncFoldersAsync(TestContext.Current.CancellationToken)));

        var slow = (SlowFolders)(object)provider;
        Assert.Equal(1, slow.MaxActive);
        Assert.Equal("Проекты 4", engine.Cache.GetFolders().Single().DisplayName);
    }
}
