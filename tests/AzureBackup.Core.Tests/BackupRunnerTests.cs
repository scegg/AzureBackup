using System.Text;
using AzureBackup.Core.Backup;
using AzureBackup.Core.Compression;
using AzureBackup.Core.Crypto;
using AzureBackup.Core.Pack;
using AzureBackup.Core.Repo;
using AzureBackup.Core.Scan;
using AzureBackup.Core.Storage;
using Xunit;

namespace AzureBackup.Core.Tests;

public sealed class BackupRunnerTests : IDisposable
{
    private readonly string _src;
    private readonly string _work;
    private const string Password = "correct horse battery staple";

    public BackupRunnerTests()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "azbk-bk-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(baseDir, "src");
        _work = Path.Combine(baseDir, "work");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_src)!, true); } catch { /* best effort */ }
    }

    private void Write(string rel, string content)
    {
        string full = Path.Combine(_src, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private BackupOptions Options() => new()
    {
        SourcePath = _src,
        WorkDir = _work,
        VolumeSizeBytes = 64,         // tiny → exercise multi-volume
        GroupFileMax = 1024,
        PackTargetSize = 4096,
        PackMaxFiles = 100,
        CompactionThreshold = 0.30,
    };

    [Fact]
    public async Task Backup_then_recover_every_file()
    {
        if (!XzCompressor.IsAvailable()) return;

        Write("a.txt", "hello world");
        Write("sub/b.txt", new string('b', 5000)); // compressible, multi-volume
        Write("docs/c.md", "# title 内容");
        Directory.CreateDirectory(Path.Combine(_src, "empty"));

        var store = new InMemoryBlobStore();
        BackupReport report = await BackupRunner.RunAsync(store, Password, Options());

        Assert.NotNull(report.SnapshotId);
        Assert.Equal(3, report.Files);
        Assert.Equal(3, report.NewOrModified);
        Assert.True(report.PacksCreated >= 1);

        await AssertRecovers(store, "a.txt", "hello world");
        await AssertRecovers(store, "sub/b.txt", new string('b', 5000));
        await AssertRecovers(store, "docs/c.md", "# title 内容");
        await AssertHasDirectory(store, "empty");
    }

    [Fact]
    public async Task Second_unchanged_run_uploads_nothing()
    {
        if (!XzCompressor.IsAvailable()) return;

        Write("a.txt", "data");
        var store = new InMemoryBlobStore();

        BackupReport first = await BackupRunner.RunAsync(store, Password, Options());
        Assert.Equal(1, first.NewOrModified);
        Assert.True(first.PacksCreated >= 1);

        BackupReport second = await BackupRunner.RunAsync(store, Password, Options());
        Assert.Equal(0, second.NewOrModified);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(0, second.PacksCreated);   // dedup: nothing new
        Assert.Equal(0, second.VolumesUploaded);
    }

    [Fact]
    public async Task Modified_file_creates_new_pack_and_recovers_new_content()
    {
        if (!XzCompressor.IsAvailable()) return;

        Write("a.txt", "version one");
        var store = new InMemoryBlobStore();
        await BackupRunner.RunAsync(store, Password, Options());

        // Modify content (and bump mtime to trigger the gate).
        System.Threading.Thread.Sleep(10);
        Write("a.txt", "version two is different");
        File.SetLastWriteTimeUtc(Path.Combine(_src, "a.txt"), DateTime.UtcNow.AddSeconds(5));

        BackupReport second = await BackupRunner.RunAsync(store, Password, Options());
        Assert.Equal(1, second.NewOrModified);
        Assert.True(second.PacksCreated >= 1);

        await AssertRecovers(store, "a.txt", "version two is different");
    }

    [Fact]
    public async Task DryRun_writes_nothing()
    {
        Write("a.txt", "x");
        var store = new InMemoryBlobStore();
        BackupOptions opts = Options() with { DryRun = true };

        BackupReport report = await BackupRunner.RunAsync(store, Password, opts);

        Assert.True(report.DryRun);
        Assert.Null(report.SnapshotId);
        Assert.Equal(0, report.VolumesUploaded);
        // Only the config blob exists (repo init); no snapshot/packs.
        Assert.False(await store.ExistsAsync(RepoLayout.SnapshotsRef));
    }

    [Fact]
    public async Task Unreadable_changed_file_carries_forward_prior_with_warning()
    {
        if (!XzCompressor.IsAvailable() || OperatingSystem.IsWindows()) return;

        Write("a.txt", "v1");
        var store = new InMemoryBlobStore();
        await BackupRunner.RunAsync(store, Password, Options());

        System.Threading.Thread.Sleep(10);
        Write("a.txt", "v2 changed");
        string full = Path.Combine(_src, "a.txt");
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddSeconds(5));
        File.SetUnixFileMode(full, UnixFileMode.None);
        try
        {
            BackupReport r = await BackupRunner.RunAsync(store, Password, Options());

            Assert.NotNull(r.SnapshotId);
            Assert.Equal(1, r.SkippedUnreadable);
            Assert.NotNull(r.Warnings);
            Assert.Contains(r.Warnings!, w => w.Path.Contains("a.txt"));
            await AssertRecovers(store, "a.txt", "v1");
        }
        finally
        {
            File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task Unreadable_new_file_is_omitted_with_warning()
    {
        if (!XzCompressor.IsAvailable() || OperatingSystem.IsWindows()) return;

        Write("keep.txt", "ok");
        Write("locked.txt", "secret");
        string locked = Path.Combine(_src, "locked.txt");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var store = new InMemoryBlobStore();
            BackupReport r = await BackupRunner.RunAsync(store, Password, Options());

            Assert.NotNull(r.SnapshotId);
            Assert.True(r.SkippedUnreadable >= 1);
            Assert.Contains(r.Warnings!, w => w.Path.Contains("locked.txt"));
            await AssertRecovers(store, "keep.txt", "ok");
            await AssertMissing(store, "locked.txt");
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task Skips_when_lock_is_held()
    {
        Write("a.txt", "x");
        var store = new InMemoryBlobStore();
        await BackupRunner.RunAsync(store, Password, Options()); // init repo + create lock blob

        // Hold the lock as if a previous run were still active.
        ILockHandle? held = await store.TryAcquireLockAsync(RepoLayout.Lock, TimeSpan.FromMinutes(5));
        Assert.NotNull(held);

        BackupReport report = await BackupRunner.RunAsync(store, Password, Options());
        Assert.True(report.Skipped);

        await held!.DisposeAsync();
    }

    // ---- recovery helpers (pre-figure the restore tool) ----

    private static async Task AssertRecovers(InMemoryBlobStore store, string path, string expected)
    {
        byte[] actual = await RecoverFile(store, path);
        Assert.Equal(expected, Encoding.UTF8.GetString(actual));
    }

    private static async Task AssertHasDirectory(InMemoryBlobStore store, string path)
    {
        Repository repo = await Repository.OpenAsync(store, Password);
        var list = await SnapshotStore.ReadSnapshotListAsync(store, repo.MasterKey);
        var root = await SnapshotStore.ReadRootAsync(store, repo.MasterKey, list[^1].Id);
        bool found = false;
        await foreach (SnapshotFile f in SnapshotStore.EnumerateAsync(store, repo.MasterKey, root.RootTree))
            if (f.Path == path && f.IsDirectory) found = true;
        Assert.True(found, $"directory {path} not found in snapshot");
    }

    private static async Task AssertMissing(InMemoryBlobStore store, string path)
    {
        Repository repo = await Repository.OpenAsync(store, Password);
        var list = await SnapshotStore.ReadSnapshotListAsync(store, repo.MasterKey);
        var root = await SnapshotStore.ReadRootAsync(store, repo.MasterKey, list.OrderByDescending(s => s.CreatedAtUtc).First().Id);
        await foreach (SnapshotFile f in SnapshotStore.EnumerateAsync(store, repo.MasterKey, root.RootTree))
            Assert.False(f.Path == path && !f.IsDirectory, $"{path} should be omitted");
    }

    private static async Task<byte[]> RecoverFile(InMemoryBlobStore store, string path)
    {
        Repository repo = await Repository.OpenAsync(store, Password);
        byte[] key = repo.MasterKey;

        var list = await SnapshotStore.ReadSnapshotListAsync(store, key);
        var root = await SnapshotStore.ReadRootAsync(store, key, list.OrderByDescending(s => s.CreatedAtUtc).First().Id);

        string? hash = null;
        await foreach (SnapshotFile f in SnapshotStore.EnumerateAsync(store, key, root.RootTree))
            if (f.Path == path && !f.IsDirectory) hash = f.Hash;
        Assert.NotNull(hash);

        RepoIndex index = await SnapshotStore.ReadIndexAsync(store, key);
        Assert.True(index.TryResolve(hash!, out var loc));
        RepoIndex.PackEntry pack = index.Packs[loc.Pack];

        byte[] contentKey = ContentKey.Unwrap(key, Convert.FromBase64String(pack.WrappedKeyBase64));

        using var joined = new MemoryStream();
        for (int i = 0; i < pack.Volumes; i++)
        {
            using Stream v = await store.GetAsync(RepoLayout.Volume(loc.Pack, i));
            v.CopyTo(joined);
        }
        joined.Position = 0;

        byte[] plaintext = PackReader.Decode(joined, pack.Codec, contentKey);
        return plaintext.AsSpan((int)loc.Offset, (int)loc.Size).ToArray();
    }
}
