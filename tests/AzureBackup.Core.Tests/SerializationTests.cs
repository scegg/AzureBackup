using AzureBackup.Core.Model;
using AzureBackup.Core.Serialization;
using Xunit;

namespace AzureBackup.Core.Tests;

public class SerializationTests
{
    [Fact]
    public void RepoConfig_roundtrips()
    {
        var config = new RepoConfig(
            FormatVersion: 1,
            Kdf: new KdfParams("argon2id", "c2FsdA==", 3, 65536, 4),
            PwCheckBase64: "dG9rZW4=",
            HashAlgo: "blake3",
            VolumeSizeBytes: 100 * 1024 * 1024,
            RepoLabel: "web");

        byte[] json = RepoJson.Serialize(config);
        RepoConfig back = RepoJson.Deserialize<RepoConfig>(json);

        Assert.Equal(config, back);
    }

    [Fact]
    public void TreeObject_roundtrips_with_enum_as_string()
    {
        var tree = new TreeObject(
        [
            new TreeEntry("sub", TreeEntryType.Dir, Child: "obj-123"),
            new TreeEntry("a.txt", TreeEntryType.File, Size: 42,
                Mtime: new DateTimeOffset(2026, 6, 22, 1, 2, 3, TimeSpan.Zero),
                Mode: 0x1A4, Hash: "deadbeef"),
        ]);

        byte[] json = RepoJson.Serialize(tree);
        string text = System.Text.Encoding.UTF8.GetString(json);
        Assert.Contains("\"dir\"", text);   // enum serialized as camelCase string
        Assert.Contains("\"file\"", text);

        TreeObject back = RepoJson.Deserialize<TreeObject>(json);
        // Records with collection members use reference equality, so compare canonical JSON.
        Assert.Equal(json, RepoJson.Serialize(back));
    }

    [Fact]
    public void IndexShard_roundtrips()
    {
        var shard = new IndexShard(
            ByHash: new Dictionary<string, ContentLocation>
            {
                ["deadbeef"] = new ContentLocation("pack-1", 0, 42),
            },
            Packs: new Dictionary<string, PackInfo>
            {
                ["pack-1"] = new PackInfo(2, 1024, "d3JhcA==", ["deadbeef"], 1),
            });

        byte[] json = RepoJson.Serialize(shard);
        IndexShard back = RepoJson.Deserialize<IndexShard>(json);

        Assert.Equal(json, RepoJson.Serialize(back));
    }

    [Fact]
    public void Null_optionals_are_omitted()
    {
        var entry = new TreeObject([new TreeEntry("x", TreeEntryType.File, Hash: "h")]);
        string text = System.Text.Encoding.UTF8.GetString(RepoJson.Serialize(entry));

        Assert.DoesNotContain("child", text);   // null Child omitted
        Assert.DoesNotContain("next", text);    // null Next omitted
    }
}
