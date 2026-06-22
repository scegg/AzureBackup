using AzureBackup.Core.Scan;
using Xunit;

namespace AzureBackup.Core.Tests;

public class ChangeDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_file_is_New_and_hashed()
    {
        int calls = 0;
        var d = ChangeDetector.Detect(prior: null, size: 10, mtime: T0, forceHash: false,
            computeHash: () => { calls++; return "h1"; });

        Assert.Equal(ChangeKind.New, d.Kind);
        Assert.Equal("h1", d.Hash);
        Assert.True(d.Hashed);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Unchanged_mtime_and_size_skips_hashing()
    {
        var prior = new PriorFile(10, T0, "h1");
        int calls = 0;
        var d = ChangeDetector.Detect(prior, size: 10, mtime: T0, forceHash: false,
            computeHash: () => { calls++; return "h2"; });

        Assert.Equal(ChangeKind.Unchanged, d.Kind);
        Assert.Equal("h1", d.Hash);
        Assert.False(d.Hashed);
        Assert.Equal(0, calls); // mtime gate: no read
    }

    [Fact]
    public void Mtime_changed_but_same_content_is_Unchanged_after_hash()
    {
        var prior = new PriorFile(10, T0, "h1");
        var d = ChangeDetector.Detect(prior, size: 10, mtime: T0.AddSeconds(5), forceHash: false,
            computeHash: () => "h1");

        Assert.Equal(ChangeKind.Unchanged, d.Kind);
        Assert.True(d.Hashed); // had to rehash to confirm
        Assert.Equal("h1", d.Hash);
    }

    [Fact]
    public void Mtime_changed_and_different_content_is_Modified()
    {
        var prior = new PriorFile(10, T0, "h1");
        var d = ChangeDetector.Detect(prior, size: 10, mtime: T0.AddSeconds(5), forceHash: false,
            computeHash: () => "h2");

        Assert.Equal(ChangeKind.Modified, d.Kind);
        Assert.Equal("h2", d.Hash);
    }

    [Fact]
    public void Size_changed_with_same_mtime_still_rehashes()
    {
        var prior = new PriorFile(10, T0, "h1");
        int calls = 0;
        var d = ChangeDetector.Detect(prior, size: 20, mtime: T0, forceHash: false,
            computeHash: () => { calls++; return "h2"; });

        Assert.Equal(ChangeKind.Modified, d.Kind);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ForceHash_ignores_mtime_fast_path()
    {
        var prior = new PriorFile(10, T0, "h1");
        int calls = 0;
        var d = ChangeDetector.Detect(prior, size: 10, mtime: T0, forceHash: true,
            computeHash: () => { calls++; return "h1"; });

        Assert.Equal(ChangeKind.Unchanged, d.Kind);
        Assert.True(d.Hashed);
        Assert.Equal(1, calls); // forced rehash even though mtime/size match
    }
}
