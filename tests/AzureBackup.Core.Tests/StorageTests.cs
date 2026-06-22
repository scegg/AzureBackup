using System.Text;
using AzureBackup.Core.Storage;
using Xunit;

namespace AzureBackup.Core.Tests;

public class RetryPolicyTests
{
    private static RetryPolicy Capture(List<TimeSpan> waits) => RetryPolicy.Default(
        (t, _) => { waits.Add(t); return Task.CompletedTask; });

    [Fact]
    public async Task Succeeds_first_try_without_waiting()
    {
        var waits = new List<TimeSpan>();
        int result = await Capture(waits).ExecuteAsync(_ => Task.FromResult(42), _ => true);

        Assert.Equal(42, result);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task Retries_then_succeeds()
    {
        var waits = new List<TimeSpan>();
        int calls = 0;
        int result = await Capture(waits).ExecuteAsync(_ =>
        {
            if (++calls < 3) throw new IOException("transient");
            return Task.FromResult(7);
        }, _ => true);

        Assert.Equal(7, result);
        Assert.Equal(3, calls);
        Assert.Equal(2, waits.Count);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)], waits);
    }

    [Fact]
    public async Task Non_transient_is_not_retried()
    {
        var waits = new List<TimeSpan>();
        int calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Capture(waits).ExecuteAsync<int>(_ => { calls++; throw new InvalidOperationException(); }, _ => false));

        Assert.Equal(1, calls);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task Gives_up_within_two_hour_budget_with_expected_schedule()
    {
        var waits = new List<TimeSpan>();
        await Assert.ThrowsAsync<IOException>(() =>
            Capture(waits).ExecuteAsync<int>(_ => throw new IOException("always"), _ => true));

        Assert.Equal([5, 30, 90, 300], waits.Take(4).Select(w => w.TotalSeconds));
        Assert.All(waits.Skip(4), w => Assert.Equal(300, w.TotalSeconds));
        Assert.True(waits.Sum(w => w.TotalSeconds) <= 7200);                // within 2h
        Assert.True(waits.Sum(w => w.TotalSeconds) + 300 > 7200);           // and no room for one more
    }
}

public class InMemoryBlobStoreTests
{
    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Put_get_exists_delete_roundtrip()
    {
        var store = new InMemoryBlobStore();
        await store.PutAsync("a/b", Bytes("hi"), BlobTier.Hot, overwrite: true);

        Assert.True(await store.ExistsAsync("a/b"));
        using (Stream s = await store.GetAsync("a/b"))
        {
            using var r = new StreamReader(s);
            Assert.Equal("hi", r.ReadToEnd());
        }

        await store.DeleteAsync("a/b");
        Assert.False(await store.ExistsAsync("a/b"));
    }

    [Fact]
    public async Task No_overwrite_throws_when_exists()
    {
        var store = new InMemoryBlobStore();
        await store.PutAsync("k", Bytes("1"), BlobTier.Hot, overwrite: true);
        await Assert.ThrowsAsync<IOException>(() => store.PutAsync("k", Bytes("2"), BlobTier.Hot, overwrite: false));
    }

    [Fact]
    public async Task List_filters_by_prefix()
    {
        var store = new InMemoryBlobStore();
        await store.PutAsync("packs/1", Bytes("a"), BlobTier.Archive, true);
        await store.PutAsync("packs/2", Bytes("b"), BlobTier.Archive, true);
        await store.PutAsync("root/x", Bytes("c"), BlobTier.Hot, true);

        var packs = new List<string>();
        await foreach (string n in store.ListAsync("packs/"))
            packs.Add(n);

        Assert.Equal(["packs/1", "packs/2"], packs);
    }

    [Fact]
    public async Task SetTier_changes_tier()
    {
        var store = new InMemoryBlobStore();
        await store.PutAsync("p", Bytes("x"), BlobTier.Hot, true);
        await store.SetTierAsync("p", BlobTier.Archive);
        Assert.Equal(BlobTier.Archive, store.TierOf("p"));
    }

    [Fact]
    public async Task Lock_is_mutually_exclusive_until_released()
    {
        var store = new InMemoryBlobStore();
        ILockHandle? first = await store.TryAcquireLockAsync("lock", TimeSpan.FromMinutes(5));
        Assert.NotNull(first);

        ILockHandle? second = await store.TryAcquireLockAsync("lock", TimeSpan.FromMinutes(5));
        Assert.Null(second); // already held → skip

        await first!.DisposeAsync(); // release

        ILockHandle? third = await store.TryAcquireLockAsync("lock", TimeSpan.FromMinutes(5));
        Assert.NotNull(third);
        await third!.DisposeAsync();
    }

    [Fact]
    public async Task Expired_lease_can_be_reacquired()
    {
        var store = new InMemoryBlobStore();
        ILockHandle? first = await store.TryAcquireLockAsync("lock", TimeSpan.Zero); // already expired
        Assert.NotNull(first);

        ILockHandle? second = await store.TryAcquireLockAsync("lock", TimeSpan.FromMinutes(5));
        Assert.NotNull(second); // previous lease expired immediately
        await second!.DisposeAsync();
    }
}
