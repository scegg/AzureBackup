using System.Text;
using AzureBackup.Core.Backup;
using AzureBackup.Core.Compression;
using AzureBackup.Core.Repo;
using AzureBackup.Core.Restore;
using AzureBackup.Core.Storage;
using Xunit;

namespace AzureBackup.Core.Tests;

public sealed class RestoreRunnerTests : IDisposable
{
    private readonly string _src;
    private readonly string _work;
    private readonly string _target;
    private const string Password = "pw-restore-test";

    public RestoreRunnerTests()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "azbk-rs-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(baseDir, "src");
        _work = Path.Combine(baseDir, "work");
        _target = Path.Combine(baseDir, "target");
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
        VolumeSizeBytes = 64,
        GroupFileMax = 1024,
        PackTargetSize = 4096,
        PackMaxFiles = 100,
    };

    private async Task<(InMemoryBlobStore Store, string SnapshotId)> BackupAsync()
    {
        var store = new InMemoryBlobStore();
        BackupReport r = await BackupRunner.RunAsync(store, Password, Options());
        return (store, r.SnapshotId!);
    }

    [Fact]
    public async Task Full_restore_recreates_files_and_dirs()
    {
        if (!XzCompressor.IsAvailable()) return;

        Write("a.txt", "alpha");
        Write("sub/b.txt", new string('b', 3000));
        Write("docs/c.md", "# 标题");
        Directory.CreateDirectory(Path.Combine(_src, "empty"));

        (InMemoryBlobStore store, string snap) = await BackupAsync();

        RestoreReport report = await RestoreRunner.RestoreAsync(store, Password, snap, _target);

        Assert.Empty(report.Failures);
        Assert.Equal(3, report.FilesRestored);
        Assert.Equal(report.FilesRestored, report.Verified);

        Assert.Equal("alpha", File.ReadAllText(Path.Combine(_target, "a.txt")));
        Assert.Equal(new string('b', 3000), File.ReadAllText(Path.Combine(_target, "sub", "b.txt")));
        Assert.Equal("# 标题", File.ReadAllText(Path.Combine(_target, "docs", "c.md")));
        Assert.True(Directory.Exists(Path.Combine(_target, "empty"))); // empty dir recreated
    }

    [Fact]
    public async Task Selective_restore_only_writes_selected_files()
    {
        if (!XzCompressor.IsAvailable()) return;

        Write("keep.txt", "keep me");
        Write("skip.txt", "skip me");
        (InMemoryBlobStore store, string snap) = await BackupAsync();

        RestoreReport report = await RestoreRunner.RestoreAsync(
            store, Password, snap, _target, select: path => path == "keep.txt");

        Assert.Equal(1, report.FilesRestored);
        Assert.True(File.Exists(Path.Combine(_target, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(_target, "skip.txt")));
    }

    [Fact]
    public async Task Local_verify_passes_for_intact_backup()
    {
        if (!XzCompressor.IsAvailable()) return;

        Write("a.txt", "data");
        (InMemoryBlobStore store, string snap) = await BackupAsync();

        IReadOnlyList<string> failures = await RestoreRunner.VerifyLocalAsync(store, Password, snap);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Local_verify_detects_corruption()
    {
        if (!XzCompressor.IsAvailable()) return;

        Write("a.txt", "data");
        (InMemoryBlobStore store, string snap) = await BackupAsync();

        // Corrupt the first volume of the (only) pack.
        string? vol = null;
        await foreach (string n in store.ListAsync(RepoLayout.PacksPrefix))
        {
            vol = n;
            break;
        }
        Assert.NotNull(vol);
        await store.PutAsync(vol!, new MemoryStream(Encoding.UTF8.GetBytes("corrupted")), BlobTier.Archive, overwrite: true);

        IReadOnlyList<string> failures = await RestoreRunner.VerifyLocalAsync(store, Password, snap);
        Assert.NotEmpty(failures);
    }

    [Fact]
    public async Task ListSnapshots_returns_written_snapshot()
    {
        if (!XzCompressor.IsAvailable()) return;

        Write("a.txt", "x");
        (InMemoryBlobStore store, string snap) = await BackupAsync();

        var snaps = await RestoreRunner.ListSnapshotsAsync(store, Password);
        Assert.Contains(snaps, s => s.Id == snap);
    }
}
