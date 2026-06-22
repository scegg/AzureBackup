using System.Security.Cryptography;
using AzureBackup.Core.Model;
using AzureBackup.Core.Pack;
using AzureBackup.Core.Repo;
using AzureBackup.Core.Storage;
using Xunit;

namespace AzureBackup.Core.Tests;

public class TreeBuilderTests
{
    private static SnapshotEntry File(string path, string hash)
        => new(path, false, 10, DateTimeOffset.UnixEpoch, 0x1A4, hash);
    private static SnapshotEntry Dir(string path)
        => new(path, true, 0, DateTimeOffset.UnixEpoch, 0, null);

    [Fact]
    public void Identical_subtrees_share_object_id()
    {
        // two dirs with identical content → same child tree object id (dedup)
        var (_, objects) = TreeBuilder.Build(
        [
            File("x/a.txt", "h1"),
            File("y/a.txt", "h1"),
        ]);
        // x and y trees are identical (same entry name+meta) → only one distinct child object
        // objects contains: root + one shared child  = 2
        Assert.Equal(2, objects.Count);
    }

    [Fact]
    public void Changing_a_file_changes_root_id_only_along_path()
    {
        var (root1, _) = TreeBuilder.Build([File("a/b.txt", "h1"), File("c/d.txt", "h2")]);
        var (root2, _) = TreeBuilder.Build([File("a/b.txt", "hX"), File("c/d.txt", "h2")]);
        Assert.NotEqual(root1, root2); // root changes when content changes
    }
}

public class SnapshotStoreTests
{
    private static byte[] Key() => RandomNumberGenerator.GetBytes(32);

    private static RepoIndex IndexWith(params string[] hashes)
    {
        var index = new RepoIndex();
        int i = 0;
        foreach (string h in hashes)
            index.AddPack("p" + i++, 1, 10, "k", new Dictionary<string, ContentSpan> { [h] = new(0, 10) });
        return index;
    }

    [Fact]
    public async Task Write_then_read_roundtrips_files_and_empty_dirs()
    {
        var store = new InMemoryBlobStore();
        byte[] key = Key();
        var entries = new[]
        {
            new SnapshotEntry("a.txt", false, 3, DateTimeOffset.UnixEpoch, 0x1A4, "ha"),
            new SnapshotEntry("sub/b.txt", false, 4, DateTimeOffset.UnixEpoch, 0x1A4, "hb"),
            new SnapshotEntry("empty", true, 0, DateTimeOffset.UnixEpoch, 0, null),
        };

        await SnapshotStore.WriteAsync(store, key, "snap-1", DateTimeOffset.UnixEpoch, entries, IndexWith("ha", "hb"));

        List<SnapshotInfo> list = await SnapshotStore.ReadSnapshotListAsync(store, key);
        Assert.Single(list);
        Assert.Equal("snap-1", list[0].Id);

        SnapshotRoot root = await SnapshotStore.ReadRootAsync(store, key, "snap-1");
        var files = new List<SnapshotFile>();
        await foreach (SnapshotFile f in SnapshotStore.EnumerateAsync(store, key, root.RootTree))
            files.Add(f);

        Assert.Contains(files, f => f.Path == "a.txt" && !f.IsDirectory && f.Hash == "ha");
        Assert.Contains(files, f => f.Path == "sub/b.txt" && f.Hash == "hb");
        Assert.Contains(files, f => f.Path == "empty" && f.IsDirectory);
        Assert.Contains(files, f => f.Path == "sub" && f.IsDirectory);
    }

    [Fact]
    public async Task ReachableHashes_collects_file_hashes()
    {
        var store = new InMemoryBlobStore();
        byte[] key = Key();
        var entries = new[]
        {
            new SnapshotEntry("a.txt", false, 3, DateTimeOffset.UnixEpoch, 0x1A4, "ha"),
            new SnapshotEntry("sub/b.txt", false, 4, DateTimeOffset.UnixEpoch, 0x1A4, "hb"),
        };
        await SnapshotStore.WriteAsync(store, key, "snap-1", DateTimeOffset.UnixEpoch, entries, IndexWith("ha", "hb"));

        HashSet<string> live = await SnapshotStore.ReachableHashesAsync(store, key, ["snap-1"]);
        Assert.Equal(new HashSet<string> { "ha", "hb" }, live);
    }

    [Fact]
    public async Task Index_roundtrips_through_store()
    {
        var store = new InMemoryBlobStore();
        byte[] key = Key();
        await SnapshotStore.WriteAsync(store, key, "s", DateTimeOffset.UnixEpoch,
            [new SnapshotEntry("a", false, 1, DateTimeOffset.UnixEpoch, 0, "ha")], IndexWith("ha"));

        RepoIndex index = await SnapshotStore.ReadIndexAsync(store, key);
        Assert.True(index.TryResolve("ha", out var loc));
        Assert.Equal("p0", loc.Pack);
    }
}
