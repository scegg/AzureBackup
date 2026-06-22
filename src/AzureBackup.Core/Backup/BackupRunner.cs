using AzureBackup.Core.Compression;
using AzureBackup.Core.Crypto;
using AzureBackup.Core.Hashing;
using AzureBackup.Core.Pack;
using AzureBackup.Core.Repo;
using AzureBackup.Core.Scan;
using AzureBackup.Core.Storage;

namespace AzureBackup.Core.Backup;

/// <summary>Resolved options for a single backup job.</summary>
public sealed record BackupOptions
{
    public required string SourcePath { get; init; }
    public required string WorkDir { get; init; }
    public long VolumeSizeBytes { get; init; } = 100L * 1024 * 1024;
    public long GroupFileMax { get; init; } = 1L * 1024 * 1024;
    public long PackTargetSize { get; init; } = 256L * 1024 * 1024;
    public int PackMaxFiles { get; init; } = 4096;
    public GitignoreMatcher Exclude { get; init; } = GitignoreMatcher.Empty;
    public NoCompressPolicy NoCompress { get; init; } = new(null, null);
    public bool ForceHash { get; init; }
    public int? RetentionCount { get; init; }
    public int? RetentionDays { get; init; }
    public RetentionMode RetentionMode { get; init; } = RetentionMode.And;
    public double CompactionThreshold { get; init; } = 0.30;
    public bool RunGc { get; init; } = true;
    public bool DryRun { get; init; }
}

/// <summary>Outcome of a backup run (for reporting / webhook).</summary>
public sealed record BackupReport(
    string? SnapshotId,
    int Files,
    int Directories,
    int NewOrModified,
    int Unchanged,
    int PacksCreated,
    int VolumesUploaded,
    long UploadedBytes,
    int SnapshotsDeleted,
    int PacksDeleted,
    int PacksEligibleForCompaction,
    bool DryRun,
    bool Skipped = false);

/// <summary>
/// Runs one backup job: scan → change-detect → pack changed content → upload (Archive)
/// → write snapshot (Hot) → retention + GC. Content dedup is by hash via the index.
/// </summary>
public static class BackupRunner
{
    public static async Task<BackupReport> RunAsync(IBlobStore store, string password, BackupOptions o, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(o);

        Repository repo = await Repository.ExistsAsync(store, ct).ConfigureAwait(false)
            ? await Repository.OpenAsync(store, password, ct).ConfigureAwait(false)
            : await Repository.InitAsync(store, password, o.VolumeSizeBytes, ct).ConfigureAwait(false);
        byte[] key = repo.MasterKey;

        // Distributed lock: skip if a previous run is still in progress.
        ILockHandle? lockHandle = await store.TryAcquireLockAsync(RepoLayout.Lock, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (lockHandle is null)
            return new BackupReport(null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, o.DryRun, Skipped: true);

        using var renewCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task renewLoop = RenewLockAsync(lockHandle, renewCts.Token);
        try
        {
        Dictionary<string, PriorFile> prior = await LoadPriorAsync(store, key, ct).ConfigureAwait(false);
        RepoIndex index = await SnapshotStore.ReadIndexAsync(store, key, ct).ConfigureAwait(false);

        var entries = new List<SnapshotEntry>();
        var toUpload = new List<GroupItem>();
        var openers = new Dictionary<string, Func<Stream>>(StringComparer.Ordinal);
        int files = 0, dirs = 0, changed = 0, unchanged = 0;

        string root = Path.GetFullPath(o.SourcePath);
        var scanner = new FileScanner(o.Exclude);

        foreach (ScannedEntry se in scanner.Scan(root))
        {
            if (se.IsSymlink) continue; // v1: symlink content not backed up

            string full = Path.Combine(root, se.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (se.IsDirectory)
            {
                dirs++;
                entries.Add(new SnapshotEntry(se.RelativePath, true, 0, se.Mtime, GetMode(full), null));
                continue;
            }

            files++;
            prior.TryGetValue(se.RelativePath, out PriorFile? p);
            ChangeDetector.Decision d = ChangeDetector.Detect(p, se.Size, se.Mtime, o.ForceHash,
                () => ContentHasher.ToHex(ContentHasher.HashStream(File.OpenRead(full))));

            if (d.Kind == ChangeKind.Unchanged) unchanged++; else changed++;
            entries.Add(new SnapshotEntry(se.RelativePath, false, se.Size, se.Mtime, GetMode(full), d.Hash));

            if (!index.Contains(d.Hash) && !openers.ContainsKey(d.Hash))
            {
                openers[d.Hash] = () => File.OpenRead(full);
                toUpload.Add(new GroupItem(se.RelativePath, d.Hash, se.Size, o.NoCompress.CodecFor(se.RelativePath)));
            }
        }

        if (o.DryRun)
            return new BackupReport(null, files, dirs, changed, unchanged, 0, 0, 0, 0, 0, 0, DryRun: true);

        int packsCreated = 0, volumesUploaded = 0;
        long uploadedBytes = 0;
        var grouper = new PackGrouper(o.GroupFileMax, o.PackTargetSize, o.PackMaxFiles);
        var builder = new PackBuilder(o.WorkDir, o.VolumeSizeBytes);

        foreach (PackPlan plan in grouper.Group(toUpload))
        {
            string packId = Guid.NewGuid().ToString("N");
            byte[] contentKey = ContentKey.Generate();
            var members = plan.Items.Select(i => new PackMember(i.Hash, openers[i.Hash])).ToList();

            BuiltPack built = builder.Build(packId, plan.Codec, contentKey, members);
            for (int i = 0; i < built.VolumePaths.Count; i++)
            {
                string volName = RepoLayout.Volume(packId, i);
                using (Stream vs = File.OpenRead(built.VolumePaths[i]))
                {
                    uploadedBytes += vs.Length;
                    await store.PutAsync(volName, vs, BlobTier.Archive, overwrite: true, ct).ConfigureAwait(false);
                }
                TryDelete(built.VolumePaths[i]); // spool cleanup after upload
                volumesUploaded++;
            }

            string wrapped = Convert.ToBase64String(ContentKey.Wrap(key, contentKey));
            index.AddPack(packId, built.VolumePaths.Count, built.CiphertextSize, wrapped, built.Entries, plan.Codec);
            packsCreated++;
        }

        string snapshotId = NewSnapshotId();
        await SnapshotStore.WriteAsync(store, key, snapshotId, DateTimeOffset.UtcNow, entries, index, ct).ConfigureAwait(false);

        int snapsDeleted = 0, packsDeleted = 0, eligibleCompaction = 0;
        if (o.RunGc)
            (snapsDeleted, packsDeleted, eligibleCompaction) =
                await RetainAndGcAsync(store, key, index, o, ct).ConfigureAwait(false);

        return new BackupReport(snapshotId, files, dirs, changed, unchanged, packsCreated, volumesUploaded,
            uploadedBytes, snapsDeleted, packsDeleted, eligibleCompaction, DryRun: false);
        }
        finally
        {
            renewCts.Cancel();
            try { await renewLoop.ConfigureAwait(false); } catch { /* ignore */ }
            await lockHandle.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task RenewLockAsync(ILockHandle handle, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                await handle.RenewAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch { /* renew failure: writes will surface the problem */ }
    }

    private static async Task<(int snaps, int packs, int compactable)> RetainAndGcAsync(
        IBlobStore store, byte[] key, RepoIndex index, BackupOptions o, CancellationToken ct)
    {
        List<SnapshotInfo> list = await SnapshotStore.ReadSnapshotListAsync(store, key, ct).ConfigureAwait(false);
        IReadOnlyList<string> doomed = RetentionPolicy.SelectForDeletion(
            list, o.RetentionCount, o.RetentionDays, o.RetentionMode, DateTimeOffset.UtcNow);

        if (doomed.Count > 0)
        {
            var keep = list.Where(s => !doomed.Contains(s.Id)).ToList();
            foreach (string id in doomed)
                await store.DeleteAsync(RepoLayout.Root(id), ct).ConfigureAwait(false);
            await OverwriteSnapshotListAsync(store, key, keep, ct).ConfigureAwait(false);
            list = keep;
        }

        HashSet<string> live = await SnapshotStore.ReachableHashesAsync(store, key, list.Select(s => s.Id), ct).ConfigureAwait(false);
        GcPlan plan = GarbageCollector.Plan(index, live, o.CompactionThreshold);

        foreach (string packId in plan.PacksToDelete)
        {
            if (index.Packs.TryGetValue(packId, out RepoIndex.PackEntry? pe))
                for (int i = 0; i < pe.Volumes; i++)
                    await store.DeleteAsync(RepoLayout.Volume(packId, i), ct).ConfigureAwait(false);
            index.RemovePack(packId);
        }

        if (plan.PacksToDelete.Count > 0)
            await OverwriteIndexAsync(store, key, index, ct).ConfigureAwait(false);

        // Compaction execution is deferred; report how many packs are eligible.
        return (doomed.Count, plan.PacksToDelete.Count, plan.PacksToCompact.Count);
    }

    private static async Task<Dictionary<string, PriorFile>> LoadPriorAsync(IBlobStore store, byte[] key, CancellationToken ct)
    {
        var prior = new Dictionary<string, PriorFile>(StringComparer.Ordinal);
        List<SnapshotInfo> list = await SnapshotStore.ReadSnapshotListAsync(store, key, ct).ConfigureAwait(false);
        if (list.Count == 0) return prior;

        SnapshotInfo latest = list.OrderByDescending(s => s.CreatedAtUtc).First();
        Model.SnapshotRoot root = await SnapshotStore.ReadRootAsync(store, key, latest.Id, ct).ConfigureAwait(false);
        await foreach (SnapshotFile f in SnapshotStore.EnumerateAsync(store, key, root.RootTree, ct).ConfigureAwait(false))
            if (!f.IsDirectory && f.Hash is not null)
                prior[f.Path] = new PriorFile(f.Size, f.Mtime, f.Hash);
        return prior;
    }

    private static async Task OverwriteSnapshotListAsync(IBlobStore store, byte[] key, List<SnapshotInfo> list, CancellationToken ct)
    {
        using var ms = new MemoryStream(StructCodec.Encode(key, list));
        await store.PutAsync(RepoLayout.SnapshotsRef, ms, BlobTier.Hot, overwrite: true, ct).ConfigureAwait(false);
    }

    private static async Task OverwriteIndexAsync(IBlobStore store, byte[] key, RepoIndex index, CancellationToken ct)
    {
        using var ms = new MemoryStream(StructCodec.Encode(key, index.ToShard()));
        await store.PutAsync(RepoLayout.IndexBlob, ms, BlobTier.Hot, overwrite: true, ct).ConfigureAwait(false);
    }

    private static string NewSnapshotId()
        => DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + Guid.NewGuid().ToString("N")[..8];

    private static int GetMode(string path)
    {
        if (OperatingSystem.IsWindows()) return 0; // Unix mode is the production (Linux) target
        try { return (int)File.GetUnixFileMode(path); }
        catch { return 0; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
