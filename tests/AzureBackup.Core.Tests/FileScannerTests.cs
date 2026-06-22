using AzureBackup.Core.Scan;
using Xunit;

namespace AzureBackup.Core.Tests;

public sealed class FileScannerTests : IDisposable
{
    private readonly string _root;

    public FileScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "azbk-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Touch(string rel, string content = "x")
    {
        string full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private HashSet<string> ScanPaths(GitignoreMatcher exclude)
        => new FileScanner(exclude).Scan(_root).Select(e => e.RelativePath).ToHashSet();

    [Fact]
    public void Scans_files_and_dirs_recursively()
    {
        Touch("a.txt");
        Touch("sub/b.txt");
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

        HashSet<string> paths = ScanPaths(GitignoreMatcher.Empty);

        Assert.Contains("a.txt", paths);
        Assert.Contains("sub", paths);
        Assert.Contains("sub/b.txt", paths);
        Assert.Contains("empty", paths); // empty directory preserved
    }

    [Fact]
    public void Prunes_excluded_directories()
    {
        Touch("keep.txt");
        Touch("node_modules/pkg/index.js");

        var exclude = GitignoreMatcher.Parse(["node_modules/"]);
        HashSet<string> paths = ScanPaths(exclude);

        Assert.Contains("keep.txt", paths);
        Assert.DoesNotContain("node_modules", paths);
        Assert.DoesNotContain("node_modules/pkg/index.js", paths);
    }

    [Fact]
    public void Applies_file_exclusions_and_reinclude()
    {
        Touch("app.log");
        Touch("important/keep.log");
        Touch("important/other.log");

        var exclude = GitignoreMatcher.Parse(["*.log", "!important/keep.log"]);
        HashSet<string> paths = ScanPaths(exclude);

        Assert.DoesNotContain("app.log", paths);
        Assert.DoesNotContain("important/other.log", paths);
        Assert.Contains("important/keep.log", paths);
        Assert.Contains("important", paths); // dir itself kept
    }

    [Fact]
    public void Records_size_and_directory_flag()
    {
        Touch("file.bin", "12345");
        ScannedEntry entry = new FileScanner(GitignoreMatcher.Empty)
            .Scan(_root).Single(e => e.RelativePath == "file.bin");

        Assert.False(entry.IsDirectory);
        Assert.Equal(5, entry.Size);
    }
}
