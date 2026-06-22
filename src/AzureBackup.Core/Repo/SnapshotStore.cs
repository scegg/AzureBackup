using AzureBackup.Core.Model;
using AzureBackup.Core.Storage;

namespace AzureBackup.Core.Repo;

/// <summary>A file resolved from a snapshot: its path and recorded metadata.</summary>
public sealed record SnapshotFile(string Path, bool IsDirectory, long Size, DateTimeOffset Mtime, int Mode, string? Hash);

/// <summary>
/// Writes and reads snapshots: content-addressed tree objects, the consolidated index,
/// the snapshot root and the snapshot list — all encrypted (Hot).
/// </summary>
public static class SnapshotStore
{
    public static async Task WriteAsync(
        IBlobStore store, byte[] masterKey, string snapshotId, DateTimeOffset createdAtUtc,
        IEnumerable<SnapshotEntry> entries, RepoIndex index, CancellationToken ct = default)
    {
        (string rootTree, IReadOnlyDictionary<string, TreeObject> objects) = TreeBuilder.Build(entries);

        // Tree objects (skip those already present — unchanged directories reuse).
        foreach (KeyValuePair<string, TreeObject> kv in objects)
        {
            string name = RepoLayout.Struct(kv.Key);
            if (await store.ExistsAsync(name, ct).ConfigureAwait(false)) continue;
            await PutStructAsync(store, masterKey, name, kv.Value, overwrite: false, ct).ConfigureAwait(false);
        }

        // Consolidated index (overwrite).
        await PutStructAsync(store, masterKey, RepoLayout.IndexBlob, index.ToShard(), overwrite: true, ct).ConfigureAwait(false);

        // Snapshot root.
        var root = new SnapshotRoot(FormatVersion.Current, snapshotId, createdAtUtc, rootTree);
        await PutStructAsync(store, masterKey, RepoLayout.Root(snapshotId), root, overwrite: false, ct).ConfigureAwait(false);

        // Append to the snapshot list.
        List<SnapshotInfo> list = await ReadSnapshotListAsync(store, masterKey, ct).ConfigureAwait(false);
        list.Add(new SnapshotInfo(snapshotId, createdAtUtc));
        await PutStructAsync(store, masterKey, RepoLayout.SnapshotsRef, list, overwrite: true, ct).ConfigureAwait(false);
    }

    public static async Task<List<SnapshotInfo>> ReadSnapshotListAsync(IBlobStore store, byte[] masterKey, CancellationToken ct = default)
    {
        if (!await store.ExistsAsync(RepoLayout.SnapshotsRef, ct).ConfigureAwait(false))
            return [];
        return [.. await GetStructAsync<List<SnapshotInfo>>(store, masterKey, RepoLayout.SnapshotsRef, ct).ConfigureAwait(false)];
    }

    public static Task<RepoIndex> ReadIndexAsync(IBlobStore store, byte[] masterKey, CancellationToken ct = default)
        => ReadIndexCoreAsync(store, masterKey, ct);

    public static async Task<SnapshotRoot> ReadRootAsync(IBlobStore store, byte[] masterKey, string snapshotId, CancellationToken ct = default)
        => await GetStructAsync<SnapshotRoot>(store, masterKey, RepoLayout.Root(snapshotId), ct).ConfigureAwait(false);

    /// <summary>Enumerates every file and directory in a snapshot (paths reconstructed from the tree).</summary>
    public static async IAsyncEnumerable<SnapshotFile> EnumerateAsync(
        IBlobStore store, byte[] masterKey, string rootTree,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var stack = new Stack<(string ObjId, string Prefix)>();
        stack.Push((rootTree, ""));
        while (stack.Count > 0)
        {
            (string objId, string prefix) = stack.Pop();
            TreeObject tree = await GetStructAsync<TreeObject>(store, masterKey, RepoLayout.Struct(objId), ct).ConfigureAwait(false);
            foreach (TreeEntry e in tree.Entries)
            {
                string path = prefix.Length == 0 ? e.Name : prefix + "/" + e.Name;
                if (e.Type == TreeEntryType.Dir)
                {
                    yield return new SnapshotFile(path, true, 0, default, 0, null);
                    if (e.Child is not null) stack.Push((e.Child, path));
                }
                else
                {
                    yield return new SnapshotFile(path, false, e.Size ?? 0, e.Mtime ?? default, e.Mode ?? 0, e.Hash);
                }
            }
        }
    }

    /// <summary>The set of content hashes reachable from the given snapshots (for GC/verify).</summary>
    public static async Task<HashSet<string>> ReachableHashesAsync(
        IBlobStore store, byte[] masterKey, IEnumerable<string> snapshotIds, CancellationToken ct = default)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in snapshotIds)
        {
            SnapshotRoot root = await ReadRootAsync(store, masterKey, id, ct).ConfigureAwait(false);
            await foreach (SnapshotFile f in EnumerateAsync(store, masterKey, root.RootTree, ct).ConfigureAwait(false))
                if (!f.IsDirectory && f.Hash is not null)
                    live.Add(f.Hash);
        }
        return live;
    }

    private static async Task<RepoIndex> ReadIndexCoreAsync(IBlobStore store, byte[] masterKey, CancellationToken ct)
    {
        if (!await store.ExistsAsync(RepoLayout.IndexBlob, ct).ConfigureAwait(false))
            return RepoIndex.FromShards([]);
        IndexShard shard = await GetStructAsync<IndexShard>(store, masterKey, RepoLayout.IndexBlob, ct).ConfigureAwait(false);
        return RepoIndex.FromShards([shard]);
    }

    private static async Task PutStructAsync<T>(IBlobStore store, byte[] masterKey, string name, T value, bool overwrite, CancellationToken ct)
    {
        byte[] blob = StructCodec.Encode(masterKey, value);
        using var ms = new MemoryStream(blob);
        await store.PutAsync(name, ms, BlobTier.Hot, overwrite, ct).ConfigureAwait(false);
    }

    private static async Task<T> GetStructAsync<T>(IBlobStore store, byte[] masterKey, string name, CancellationToken ct)
    {
        using Stream s = await store.GetAsync(name, ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct).ConfigureAwait(false);
        return StructCodec.Decode<T>(masterKey, ms.ToArray());
    }
}
