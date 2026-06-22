using AzureBackup.Core.Scan;
using Xunit;

namespace AzureBackup.Core.Tests;

public class GitignoreMatcherTests
{
    private static GitignoreMatcher Parse(params string[] lines) => GitignoreMatcher.Parse(lines);

    [Fact]
    public void Basename_pattern_matches_at_any_depth()
    {
        var m = Parse("*.log");
        Assert.True(m.IsIgnored("a.log", false));
        Assert.True(m.IsIgnored("dir/sub/b.log", false));
        Assert.False(m.IsIgnored("a.txt", false));
    }

    [Fact]
    public void Negation_reincludes_last_match_wins()
    {
        var m = Parse("*.log", "!important/keep.log");
        Assert.True(m.IsIgnored("important/other.log", false));
        Assert.False(m.IsIgnored("important/keep.log", false));
    }

    [Fact]
    public void Directory_only_matches_directories_only()
    {
        var m = Parse("build/");
        Assert.True(m.IsIgnored("build", true));
        Assert.False(m.IsIgnored("build", false)); // a file literally named build is not matched
    }

    [Fact]
    public void Anchored_pattern_matches_from_root_only()
    {
        var m = Parse("/secret.txt");
        Assert.True(m.IsIgnored("secret.txt", false));
        Assert.False(m.IsIgnored("sub/secret.txt", false));
    }

    [Fact]
    public void Unanchored_with_no_slash_matches_anywhere()
    {
        var m = Parse("cache");
        Assert.True(m.IsIgnored("cache", true));
        Assert.True(m.IsIgnored("a/b/cache", true));
    }

    [Fact]
    public void Double_star_leading_matches_zero_or_more_dirs()
    {
        var m = Parse("**/foo");
        Assert.True(m.IsIgnored("foo", false));
        Assert.True(m.IsIgnored("a/foo", false));
        Assert.True(m.IsIgnored("a/b/foo", false));
    }

    [Fact]
    public void Double_star_middle_matches_intermediate_dirs()
    {
        var m = Parse("a/**/b");
        Assert.True(m.IsIgnored("a/b", false));
        Assert.True(m.IsIgnored("a/x/b", false));
        Assert.True(m.IsIgnored("a/x/y/b", false));
        Assert.False(m.IsIgnored("x/a/b", false)); // anchored at root
    }

    [Fact]
    public void Trailing_double_star_matches_everything_under()
    {
        var m = Parse("logs/**");
        Assert.True(m.IsIgnored("logs/a", false));
        Assert.True(m.IsIgnored("logs/a/b.txt", false));
    }

    [Fact]
    public void Single_star_does_not_cross_slash()
    {
        var m = Parse("a/*.txt");
        Assert.True(m.IsIgnored("a/x.txt", false));
        Assert.False(m.IsIgnored("a/b/x.txt", false));
    }

    [Fact]
    public void Question_mark_matches_single_non_slash()
    {
        var m = Parse("file?.txt");
        Assert.True(m.IsIgnored("file1.txt", false));
        Assert.False(m.IsIgnored("file12.txt", false));
        Assert.False(m.IsIgnored("file.txt", false));
    }

    [Fact]
    public void Comments_and_blanks_are_ignored()
    {
        var m = Parse("# a comment", "", "   ", "*.tmp");
        Assert.True(m.IsIgnored("x.tmp", false));
        Assert.False(m.IsIgnored("x.txt", false));
    }

    [Fact]
    public void Empty_matcher_ignores_nothing()
    {
        Assert.False(GitignoreMatcher.Empty.IsIgnored("anything", false));
    }

    [Fact]
    public void Later_rule_can_re_exclude()
    {
        var m = Parse("*.log", "!app.log", "secret/app.log");
        Assert.False(m.IsIgnored("other/app.log", false)); // re-included by !app.log
        Assert.True(m.IsIgnored("secret/app.log", false)); // re-excluded by anchored last rule
    }
}
