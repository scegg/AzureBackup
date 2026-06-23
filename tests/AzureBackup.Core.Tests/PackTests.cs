using System.Text;
using AzureBackup.Core.Compression;
using AzureBackup.Core.Crypto;
using AzureBackup.Core.Pack;
using AzureBackup.Core.Scan;
using Xunit;

namespace AzureBackup.Core.Tests;

public class NoCompressPolicyTests
{
    [Fact]
    public void Extension_list_forces_store()
    {
        var policy = new NoCompressPolicy(["7z", "mp4", "jpg"], GitignoreMatcher.Empty);
        Assert.Equal(CompressionCodec.Store, policy.CodecFor("movies/clip.mp4"));
        Assert.Equal(CompressionCodec.Store, policy.CodecFor("a.JPG")); // case-insensitive
        Assert.Equal(CompressionCodec.Xz, policy.CodecFor("docs/readme.txt"));
    }

    [Fact]
    public void Rules_file_forces_store()
    {
        var policy = new NoCompressPolicy(null, GitignoreMatcher.Parse(["media/**", "*.iso"]));
        Assert.Equal(CompressionCodec.Store, policy.CodecFor("media/a/b.bin"));
        Assert.Equal(CompressionCodec.Store, policy.CodecFor("x.iso"));
        Assert.Equal(CompressionCodec.Xz, policy.CodecFor("src/main.cs"));
    }
}

public class PackGrouperTests
{
    private static GroupItem Item(string path, long size, CompressionCodec codec = CompressionCodec.Xz)
        => new(path, "h:" + path, size, codec);

    [Fact]
    public void Large_files_get_their_own_pack()
    {
        var grouper = new PackGrouper(groupFileMax: 1000, packTargetSize: 10_000, packMaxFiles: 100);
        List<PackPlan> plans = grouper.Group([Item("big.bin", 5000), Item("small.txt", 10)]).ToList();

        Assert.Contains(plans, p => p.Items.Count == 1 && p.Items[0].RelativePath == "big.bin");
    }

    [Fact]
    public void Does_not_mix_codecs_in_one_pack()
    {
        var grouper = new PackGrouper(1000, 10_000, 100);
        List<PackPlan> plans = grouper.Group(
        [
            Item("dir/a.txt", 10, CompressionCodec.Xz),
            Item("dir/b.mp4", 10, CompressionCodec.Store),
        ]).ToList();

        Assert.All(plans, p => Assert.All(p.Items, i => Assert.Equal(p.Codec, i.Codec)));
        Assert.Equal(2, plans.Count); // one xz pack, one store pack
    }

    [Fact]
    public void Flushes_when_target_size_reached()
    {
        var grouper = new PackGrouper(groupFileMax: 1000, packTargetSize: 100, packMaxFiles: 100);
        List<PackPlan> plans = grouper.Group(
        [
            Item("d/a", 60), Item("d/b", 60), // 120 >= 100 → flush
            Item("d/c", 10),
        ]).ToList();

        Assert.Equal(2, plans.Count);
        Assert.Equal(2, plans[0].Items.Count);
        Assert.Single(plans[1].Items);
    }

    [Fact]
    public void Separates_by_directory()
    {
        var grouper = new PackGrouper(1000, 10_000, 100);
        List<PackPlan> plans = grouper.Group([Item("x/a", 10), Item("y/b", 10)]).ToList();
        Assert.Equal(2, plans.Count);
    }
}

public sealed class PackBuilderTests : IDisposable
{
    private readonly string _work;

    public PackBuilderTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "azbk-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch { /* best effort */ }
    }

    private static PackMember Member(string hash, byte[] content) => new(hash, () => new MemoryStream(content));

    [Theory]
    [InlineData(CompressionCodec.Xz)]
    [InlineData(CompressionCodec.Store)]
    public void Build_then_decode_recovers_every_member(CompressionCodec codec)
    {
        if (codec == CompressionCodec.Xz && !XzCompressor.IsAvailable())
            return;

        byte[] a = Encoding.UTF8.GetBytes(new string('a', 500));
        byte[] b = Encoding.UTF8.GetBytes("hello 世界");
        byte[] c = System.Security.Cryptography.RandomNumberGenerator.GetBytes(300);

        byte[] key = ContentKey.Generate();
        var builder = new PackBuilder(_work, volumeSize: 64); // small → multiple volumes

        BuiltPack pack = builder.Build("pack-1", codec, key,
        [
            Member("ha", a), Member("hb", b), Member("hc", c),
        ]);

        Assert.True(pack.VolumePaths.Count >= 1);
        Assert.All(pack.VolumePaths, p => Assert.True(File.Exists(p)));

        using Stream joined = PackReader.OpenJoined(pack.VolumePaths);
        byte[] plaintext = PackReader.Decode(joined, codec, key);

        Assert.Equal(a, Slice(plaintext, pack.Entries["ha"]));
        Assert.Equal(b, Slice(plaintext, pack.Entries["hb"]));
        Assert.Equal(c, Slice(plaintext, pack.Entries["hc"]));
    }

    [Fact]
    public void Wrong_key_fails_to_decode()
    {
        byte[] key = ContentKey.Generate();
        var builder = new PackBuilder(_work, 64);
        BuiltPack pack = builder.Build("pack-x", CompressionCodec.Store, key,
            [Member("h1", Encoding.UTF8.GetBytes("secret"))]);

        using Stream joined = PackReader.OpenJoined(pack.VolumePaths);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => PackReader.Decode(joined, CompressionCodec.Store, ContentKey.Generate()));
    }

    private static byte[] Slice(byte[] data, ContentSpan span)
        => data.AsSpan((int)span.Offset, (int)span.Size).ToArray();

    [Fact]
    public void Build_tolerates_failing_member_and_reports_it()
    {
        if (!XzCompressor.IsAvailable()) return;
        string work = Path.Combine(Path.GetTempPath(), "azbk-pb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            byte[] key = ContentKey.Generate();
            var members = new List<PackMember>
            {
                new("hashGOOD", () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes("alpha"))),
                new("hashBAD",  () => throw new FileNotFoundException("gone")),
                new("hashGOOD2",() => new MemoryStream(System.Text.Encoding.UTF8.GetBytes("beta"))),
            };

            BuiltPack built = new PackBuilder(work, 1024)
                .Build("pid", CompressionCodec.Store, key, members, tolerateMemberFailures: true);

            Assert.Contains("hashGOOD", built.Entries.Keys);
            Assert.Contains("hashGOOD2", built.Entries.Keys);
            Assert.DoesNotContain("hashBAD", built.Entries.Keys);
            Assert.Contains("hashBAD", built.FailedMembers.Select(f => f.Hash));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void Build_without_tolerance_rethrows_member_failure()
    {
        string work = Path.Combine(Path.GetTempPath(), "azbk-pb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var members = new List<PackMember>
            {
                new("h", () => throw new FileNotFoundException("gone")),
            };
            Assert.ThrowsAny<IOException>(() =>
                new PackBuilder(work, 1024).Build("pid", CompressionCodec.Store, ContentKey.Generate(), members));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
