using AzureBackup.Core.Pack;
using AzureBackup.Core.Repo;
using AzureBackup.Core.Storage;
using Xunit;

namespace AzureBackup.Core.Tests;

public class StructCodecTests
{
    [Fact]
    public void Encode_then_decode_roundtrips()
    {
        byte[] key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var value = new SnapshotInfo("snap-1", new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero));

        byte[] blob = StructCodec.Encode(key, value);
        SnapshotInfo back = StructCodec.Decode<SnapshotInfo>(key, blob);

        Assert.Equal(value, back);
    }

    [Fact]
    public void Decode_with_wrong_key_throws()
    {
        byte[] key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        byte[] blob = StructCodec.Encode(key, new SnapshotInfo("s", DateTimeOffset.UtcNow));
        byte[] wrong = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => StructCodec.Decode<SnapshotInfo>(wrong, blob));
    }
}

public class RepositoryTests
{
    [Fact]
    public async Task Init_then_open_with_correct_password()
    {
        var store = new InMemoryBlobStore();
        Repository created = await Repository.InitAsync(store, "pw", 100 * 1024 * 1024);
        Assert.Equal("blake3", created.Config.HashAlgo);

        Repository opened = await Repository.OpenAsync(store, "pw");
        Assert.Equal(created.MasterKey, opened.MasterKey);
    }

    [Fact]
    public async Task Open_with_wrong_password_is_rejected()
    {
        var store = new InMemoryBlobStore();
        await Repository.InitAsync(store, "correct", 1024);
        await Assert.ThrowsAsync<InvalidPasswordException>(() => Repository.OpenAsync(store, "wrong"));
    }

    [Fact]
    public async Task Init_twice_fails()
    {
        var store = new InMemoryBlobStore();
        await Repository.InitAsync(store, "pw", 1024);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repository.InitAsync(store, "pw", 1024));
    }

    [Fact]
    public async Task Open_uninitialized_fails()
    {
        var store = new InMemoryBlobStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repository.OpenAsync(store, "pw"));
    }
}

public class RetentionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);

    private static List<SnapshotInfo> Snapshots(params int[] daysAgo)
        => daysAgo.Select((d, i) => new SnapshotInfo($"s{i}", Now.AddDays(-d))).ToList();

    [Fact]
    public void No_policy_keeps_all()
    {
        var snaps = Snapshots(0, 1, 2);
        Assert.Empty(RetentionPolicy.SelectForDeletion(snaps, null, null, RetentionMode.And, Now));
    }

    [Fact]
    public void Keep_count_only()
    {
        var snaps = Snapshots(0, 1, 2, 3); // 4 snapshots
        var del = RetentionPolicy.SelectForDeletion(snaps, keepCount: 2, keepDays: null, RetentionMode.And, Now);
        Assert.Equal(2, del.Count); // the 2 oldest deleted
        Assert.Contains("s2", del);
        Assert.Contains("s3", del);
    }

    [Fact]
    public void Keep_days_only()
    {
        var snaps = Snapshots(0, 5, 40);
        var del = RetentionPolicy.SelectForDeletion(snaps, keepCount: null, keepDays: 30, RetentionMode.And, Now);
        Assert.Equal(["s2"], del); // only the 40-day-old one
    }

    [Fact]
    public void And_keeps_more_or_keeps_fewer()
    {
        // 5 snapshots at 0,10,20,40,50 days; keepCount=2, keepDays=30
        var snaps = Snapshots(0, 10, 20, 40, 50);

        var and = RetentionPolicy.SelectForDeletion(snaps, 2, 30, RetentionMode.And, Now);
        // over count: ranks >=2 → 20,40,50 days. over days: >30 → 40,50. AND → 40,50
        Assert.Equal(new HashSet<string> { "s3", "s4" }, and.ToHashSet());

        var or = RetentionPolicy.SelectForDeletion(snaps, 2, 30, RetentionMode.Or, Now);
        // OR → over count(20,40,50) ∪ over days(40,50) = 20,40,50
        Assert.Equal(new HashSet<string> { "s2", "s3", "s4" }, or.ToHashSet());
    }
}

public class GarbageCollectorTests
{
    private static RepoIndex BuildIndex()
    {
        var index = new RepoIndex();
        index.AddPack("p1", volumes: 2, totalSize: 100, "k1", new Dictionary<string, ContentSpan>
        {
            ["a"] = new(0, 10), ["b"] = new(10, 10),
        });
        index.AddPack("p2", volumes: 1, totalSize: 50, "k2", new Dictionary<string, ContentSpan>
        {
            ["c"] = new(0, 10), ["d"] = new(10, 10), ["e"] = new(20, 10), ["f"] = new(30, 10),
        });
        return index;
    }

    [Fact]
    public void Pack_with_no_live_members_is_deleted()
    {
        RepoIndex index = BuildIndex();
        var live = new HashSet<string> { "c", "d", "e", "f" }; // p1 fully dead

        GcPlan plan = GarbageCollector.Plan(index, live, compactionThreshold: 0.30);

        Assert.Contains("p1", plan.PacksToDelete);
        Assert.DoesNotContain("p1", plan.PacksToCompact);
        Assert.Contains("a", plan.DeadHashes);
        Assert.Contains("b", plan.DeadHashes);
    }

    [Fact]
    public void Pack_over_dead_threshold_is_compacted()
    {
        RepoIndex index = BuildIndex();
        // p2: keep only c → 1/4 live → deadRatio 0.75 ≥ 0.30 → compact
        var live = new HashSet<string> { "a", "b", "c" };

        GcPlan plan = GarbageCollector.Plan(index, live, compactionThreshold: 0.30);

        Assert.Contains("p2", plan.PacksToCompact);
        Assert.DoesNotContain("p2", plan.PacksToDelete);
        Assert.Empty(plan.PacksToDelete); // p1 fully live here
    }

    [Fact]
    public void Compaction_off_never_compacts()
    {
        RepoIndex index = BuildIndex();
        var live = new HashSet<string> { "a", "b", "c" };
        GcPlan plan = GarbageCollector.Plan(index, live, compactionThreshold: 0);
        Assert.Empty(plan.PacksToCompact);
    }

    [Theory]
    [InlineData("off", 0)]
    [InlineData("30%", 0.30)]
    [InlineData("0.5", 0.50)]
    public void ParseThreshold_parses(string input, double expected)
        => Assert.Equal(expected, GarbageCollector.ParseThreshold(input), 3);

    [Fact]
    public async Task RemoteVerifier_finds_missing_volumes()
    {
        var store = new InMemoryBlobStore();
        RepoIndex index = BuildIndex();
        // p1 has 2 volumes; upload only volume 0
        await store.PutAsync(RepoLayout.Volume("p1", 0), new MemoryStream([1]), BlobTier.Archive, true);

        var live = new HashSet<string> { "a" }; // references p1
        IReadOnlyList<string> missing = await RemoteVerifier.FindMissingVolumesAsync(store, index, live);

        Assert.Contains(RepoLayout.Volume("p1", 1), missing);     // volume 1 missing
        Assert.DoesNotContain(RepoLayout.Volume("p1", 0), missing);
    }
}
