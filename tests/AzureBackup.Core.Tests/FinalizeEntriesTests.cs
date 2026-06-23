using AzureBackup.Core.Backup;
using AzureBackup.Core.Repo;
using AzureBackup.Core.Scan;
using Xunit;

namespace AzureBackup.Core.Tests;

public sealed class FinalizeEntriesTests
{
    private static SnapshotEntry File(string path, string hash)
        => new(path, false, 1, DateTimeOffset.UnixEpoch, 0, hash);

    [Fact]
    public void Carries_forward_when_prior_exists()
    {
        var entries = new List<SnapshotEntry> { File("a.txt", "Hnew") };
        var failedContent = new Dictionary<string, SkipReason> { ["Hnew"] = SkipReason.Unreadable };
        var failedPath = new Dictionary<string, SkipReason> { ["a.txt"] = SkipReason.Unreadable };
        var prior = new Dictionary<string, PriorFile> { ["a.txt"] = new(9, DateTimeOffset.UnixEpoch, "Hold", 420 /* 0644 octal */) };
        var miss = new HashSet<string>(); var unr = new HashSet<string>(); var warn = new List<SkipWarning>();

        var result = BackupRunner.FinalizeEntries(entries, failedContent, failedPath, prior, miss, unr, warn);

        Assert.Single(result);
        Assert.Equal("Hold", result[0].Hash);
        Assert.Equal(420 /* 0644 octal */, result[0].Mode);
        Assert.Contains("a.txt", unr);
        Assert.Empty(miss);
        // 打包期 boundPath 打不开也要报错于报告。
        Assert.Contains(warn, w => w.Path == "a.txt" && w.Reason == SkipReason.Unreadable);
    }

    [Fact]
    public void Omits_when_no_prior()
    {
        var entries = new List<SnapshotEntry> { File("a.txt", "Hnew") };
        var failedContent = new Dictionary<string, SkipReason> { ["Hnew"] = SkipReason.Unreadable };
        var failedPath = new Dictionary<string, SkipReason> { ["a.txt"] = SkipReason.Unreadable };
        var prior = new Dictionary<string, PriorFile>();
        var miss = new HashSet<string>(); var unr = new HashSet<string>(); var warn = new List<SkipWarning>();

        var result = BackupRunner.FinalizeEntries(entries, failedContent, failedPath, prior, miss, unr, warn);

        Assert.Empty(result);
        Assert.Contains("a.txt", unr);
    }

    [Fact]
    public void Dedup_sibling_carries_or_omits_and_warns_without_dangling()
    {
        var entries = new List<SnapshotEntry> { File("A.txt", "H"), File("B.txt", "H") };
        var failedContent = new Dictionary<string, SkipReason> { ["H"] = SkipReason.Missing };
        var failedPath = new Dictionary<string, SkipReason> { ["A.txt"] = SkipReason.Missing };
        var prior = new Dictionary<string, PriorFile>();
        var miss = new HashSet<string>(); var unr = new HashSet<string>(); var warn = new List<SkipWarning>();

        var result = BackupRunner.FinalizeEntries(entries, failedContent, failedPath, prior, miss, unr, warn);

        Assert.Empty(result);
        Assert.Contains("A.txt", miss);
        Assert.Contains("B.txt", unr);
        Assert.Contains(warn, w => w.Path == "B.txt" && w.Reason == SkipReason.Unreadable);
        Assert.DoesNotContain(warn, w => w.Path == "A.txt");
    }
}
