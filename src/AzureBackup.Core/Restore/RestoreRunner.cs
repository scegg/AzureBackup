using AzureBackup.Core.Crypto;
using AzureBackup.Core.Hashing;
using AzureBackup.Core.Model;
using AzureBackup.Core.Pack;
using AzureBackup.Core.Repo;
using AzureBackup.Core.Storage;

namespace AzureBackup.Core.Restore;

public sealed record RestoreReport(int FilesRestored, int DirectoriesCreated, int Verified, IReadOnlyList<string> Failures);

/// <summary>
/// Restores files from a snapshot: resolve hash → pack via the index, group by pack so each
/// pack is decoded once, decrypt+decompress, slice members out, write to the target (dirs,
/// mtime, mode). Verifies each restored file's hash. Also offers a read-only local verify.
/// </summary>
public static class RestoreRunner
{
    public static async Task<List<SnapshotInfo>> ListSnapshotsAsync(IBlobStore store, string password, CancellationToken ct = default)
    {
        Repository repo = await Repository.OpenAsync(store, password, ct).ConfigureAwait(false);
        List<SnapshotInfo> list = await SnapshotStore.ReadSnapshotListAsync(store, repo.MasterKey, ct).ConfigureAwait(false);
        return [.. list.OrderByDescending(s => s.CreatedAtUtc)];
    }

    public static async IAsyncEnumerable<SnapshotFile> ListFilesAsync(
        IBlobStore store, string password, string snapshotId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Repository repo = await Repository.OpenAsync(store, password, ct).ConfigureAwait(false);
        SnapshotRoot root = await SnapshotStore.ReadRootAsync(store, repo.MasterKey, snapshotId, ct).ConfigureAwait(false);
        await foreach (SnapshotFile f in SnapshotStore.EnumerateAsync(store, repo.MasterKey, root.RootTree, ct).ConfigureAwait(false))
            yield return f;
    }

    public static async Task<RestoreReport> RestoreAsync(
        IBlobStore store, string password, string snapshotId, string targetDir,
        Func<string, bool>? select = null, bool requestRehydrate = false,
        RehydratePriority priority = RehydratePriority.Standard, CancellationToken ct = default)
    {
        Repository repo = await Repository.OpenAsync(store, password, ct).ConfigureAwait(false);
        byte[] key = repo.MasterKey;
        RepoIndex index = await SnapshotStore.ReadIndexAsync(store, key, ct).ConfigureAwait(false);
        SnapshotRoot root = await SnapshotStore.ReadRootAsync(store, key, snapshotId, ct).ConfigureAwait(false);

        (List<SnapshotFile> files, List<string> dirs) = await CollectAsync(store, key, root.RootTree, select, ct).ConfigureAwait(false);

        int dirCount = 0;
        foreach (string d in dirs)
        {
            Directory.CreateDirectory(ToLocal(targetDir, d));
            dirCount++;
        }

        if (requestRehydrate)
            await RehydrateAsync(store, index, files, priority, ct).ConfigureAwait(false);

        var failures = new List<string>();
        int restored = 0, verified = 0;

        foreach ((string packId, List<SnapshotFile> members) in GroupByPack(index, files, failures))
        {
            byte[] plaintext;
            try
            {
                plaintext = await DecodePackAsync(store, key, index, packId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add($"{packId}: decode failed: {ex.Message}");
                continue;
            }

            foreach (SnapshotFile f in members)
            {
                index.TryResolve(f.Hash!, out ContentLocation loc);
                byte[] data = plaintext.AsSpan((int)loc.Offset, (int)loc.Size).ToArray();

                string outPath = ToLocal(targetDir, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                await File.WriteAllBytesAsync(outPath, data, ct).ConfigureAwait(false);
                ApplyMetadata(outPath, f);
                restored++;

                if (ContentHasher.ToHex(ContentHasher.Hash(data)) == f.Hash) verified++;
                else failures.Add($"{f.Path}: hash mismatch after restore");
            }
        }

        return new RestoreReport(restored, dirCount, verified, failures);
    }

    public static async Task<IReadOnlyList<string>> VerifyLocalAsync(
        IBlobStore store, string password, string snapshotId, Func<string, bool>? select = null, CancellationToken ct = default)
    {
        Repository repo = await Repository.OpenAsync(store, password, ct).ConfigureAwait(false);
        byte[] key = repo.MasterKey;
        RepoIndex index = await SnapshotStore.ReadIndexAsync(store, key, ct).ConfigureAwait(false);
        SnapshotRoot root = await SnapshotStore.ReadRootAsync(store, key, snapshotId, ct).ConfigureAwait(false);

        (List<SnapshotFile> files, _) = await CollectAsync(store, key, root.RootTree, select, ct).ConfigureAwait(false);
        var failures = new List<string>();

        foreach ((string packId, List<SnapshotFile> members) in GroupByPack(index, files, failures))
        {
            byte[] plaintext;
            try { plaintext = await DecodePackAsync(store, key, index, packId, ct).ConfigureAwait(false); }
            catch (Exception ex) { failures.Add($"{packId}: decode failed: {ex.Message}"); continue; }

            foreach (SnapshotFile f in members)
            {
                index.TryResolve(f.Hash!, out ContentLocation loc);
                byte[] data = plaintext.AsSpan((int)loc.Offset, (int)loc.Size).ToArray();
                if (ContentHasher.ToHex(ContentHasher.Hash(data)) != f.Hash)
                    failures.Add($"{f.Path}: hash mismatch");
            }
        }
        return failures;
    }

    private static async Task<(List<SnapshotFile> Files, List<string> Dirs)> CollectAsync(
        IBlobStore store, byte[] key, string rootTree, Func<string, bool>? select, CancellationToken ct)
    {
        var files = new List<SnapshotFile>();
        var dirs = new List<string>();
        await foreach (SnapshotFile f in SnapshotStore.EnumerateAsync(store, key, rootTree, ct).ConfigureAwait(false))
        {
            if (f.IsDirectory)
            {
                if (select is null || select(f.Path)) dirs.Add(f.Path);
            }
            else if (f.Hash is not null && (select is null || select(f.Path)))
            {
                files.Add(f);
            }
        }
        return (files, dirs);
    }

    private static IEnumerable<(string PackId, List<SnapshotFile> Members)> GroupByPack(
        RepoIndex index, List<SnapshotFile> files, List<string> failures)
    {
        var byPack = new Dictionary<string, List<SnapshotFile>>(StringComparer.Ordinal);
        foreach (SnapshotFile f in files)
        {
            if (!index.TryResolve(f.Hash!, out ContentLocation loc))
            {
                failures.Add($"{f.Path}: content {f.Hash} not in index");
                continue;
            }
            if (!byPack.TryGetValue(loc.Pack, out List<SnapshotFile>? list))
                byPack[loc.Pack] = list = [];
            list.Add(f);
        }
        return byPack.Select(kv => (kv.Key, kv.Value));
    }

    private static async Task<byte[]> DecodePackAsync(IBlobStore store, byte[] key, RepoIndex index, string packId, CancellationToken ct)
    {
        RepoIndex.PackEntry pack = index.Packs[packId];
        byte[] contentKey = ContentKey.Unwrap(key, Convert.FromBase64String(pack.WrappedKeyBase64));

        using var joined = new MemoryStream();
        for (int i = 0; i < pack.Volumes; i++)
        {
            using Stream v = await store.GetAsync(RepoLayout.Volume(packId, i), ct).ConfigureAwait(false);
            await v.CopyToAsync(joined, ct).ConfigureAwait(false);
        }
        joined.Position = 0;
        return PackReader.Decode(joined, pack.Codec, contentKey);
    }

    private static async Task RehydrateAsync(IBlobStore store, RepoIndex index, List<SnapshotFile> files, RehydratePriority priority, CancellationToken ct)
    {
        var packs = new HashSet<string>(StringComparer.Ordinal);
        foreach (SnapshotFile f in files)
            if (index.TryResolve(f.Hash!, out ContentLocation loc)) packs.Add(loc.Pack);

        foreach (string packId in packs)
        {
            if (!index.Packs.TryGetValue(packId, out RepoIndex.PackEntry? pe)) continue;
            for (int i = 0; i < pe.Volumes; i++)
                await store.SetTierAsync(RepoLayout.Volume(packId, i), BlobTier.Hot, priority, ct).ConfigureAwait(false);
        }
    }

    private static string ToLocal(string targetDir, string relPath)
        => Path.Combine(targetDir, relPath.Replace('/', Path.DirectorySeparatorChar));

    private static void ApplyMetadata(string path, SnapshotFile f)
    {
        try { if (f.Mtime != default) File.SetLastWriteTimeUtc(path, f.Mtime.UtcDateTime); } catch { /* best effort */ }
        if (!OperatingSystem.IsWindows() && f.Mode != 0)
        {
            try { File.SetUnixFileMode(path, (UnixFileMode)f.Mode); } catch { /* best effort */ }
        }
    }
}
