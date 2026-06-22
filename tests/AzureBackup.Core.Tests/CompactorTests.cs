using System.Text;
using AzureBackup.Core.Compression;
using AzureBackup.Core.Crypto;
using AzureBackup.Core.Pack;
using AzureBackup.Core.Repo;
using AzureBackup.Core.Storage;
using Xunit;

namespace AzureBackup.Core.Tests;

public sealed class CompactorTests : IDisposable
{
    private readonly string _work;
    private readonly byte[] _master = RandomNumberGeneratorKey();

    public CompactorTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "azbk-compact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch { /* best effort */ }
    }

    private static byte[] RandomNumberGeneratorKey()
        => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    private async Task<(InMemoryBlobStore Store, RepoIndex Index, Dictionary<string, byte[]> Contents)> SeedPackAsync()
    {
        var store = new InMemoryBlobStore();
        var index = new RepoIndex();
        var contents = new Dictionary<string, byte[]>
        {
            ["h1"] = Encoding.UTF8.GetBytes("first member content"),
            ["h2"] = Encoding.UTF8.GetBytes(new string('2', 400)),
            ["h3"] = Encoding.UTF8.GetBytes("third"),
            ["h4"] = Encoding.UTF8.GetBytes("fourth member"),
        };

        byte[] contentKey = ContentKey.Generate();
        var members = contents.Select(kv => new PackMember(kv.Key, () => new MemoryStream(kv.Value))).ToList();
        BuiltPack built = new PackBuilder(_work, volumeSize: 64).Build("oldpack", CompressionCodec.Store, contentKey, members);

        for (int i = 0; i < built.VolumePaths.Count; i++)
            using (Stream vs = File.OpenRead(built.VolumePaths[i]))
                await store.PutAsync(RepoLayout.Volume("oldpack", i), vs, BlobTier.Archive, true);

        string wrapped = Convert.ToBase64String(ContentKey.Wrap(_master, contentKey));
        index.AddPack("oldpack", built.VolumePaths.Count, built.CiphertextSize, wrapped, built.Entries, CompressionCodec.Store);
        return (store, index, contents);
    }

    [Fact]
    public async Task Compacts_survivors_into_new_pack_and_deletes_old()
    {
        (InMemoryBlobStore store, RepoIndex index, Dictionary<string, byte[]> contents) = await SeedPackAsync();
        var live = new HashSet<string> { "h1", "h2" }; // h3, h4 are dead

        string? newId = await Compactor.CompactAsync(store, _master, index, "oldpack", live, _work, volumeSize: 64);

        Assert.NotNull(newId);
        Assert.False(index.Packs.ContainsKey("oldpack"));
        Assert.True(index.Packs.ContainsKey(newId!));

        // Old volumes gone.
        Assert.False(await store.ExistsAsync(RepoLayout.Volume("oldpack", 0)));

        // Dead hashes dropped; live hashes repointed to the new pack.
        Assert.False(index.Contains("h3"));
        Assert.False(index.Contains("h4"));
        Assert.True(index.TryResolve("h1", out var l1) && l1.Pack == newId);
        Assert.True(index.TryResolve("h2", out var l2) && l2.Pack == newId);

        // New pack decodes and survivors' content is intact.
        RepoIndex.PackEntry np = index.Packs[newId!];
        byte[] key = ContentKey.Unwrap(_master, Convert.FromBase64String(np.WrappedKeyBase64));
        using var joined = new MemoryStream();
        for (int i = 0; i < np.Volumes; i++)
        {
            using Stream v = await store.GetAsync(RepoLayout.Volume(newId!, i));
            v.CopyTo(joined);
        }
        joined.Position = 0;
        byte[] plain = PackReader.Decode(joined, np.Codec, key);

        foreach (string h in live)
        {
            var loc = index.ByHash[h];
            byte[] got = plain.AsSpan((int)loc.Offset, (int)loc.Size).ToArray();
            Assert.Equal(contents[h], got);
        }
    }

    [Fact]
    public async Task Fully_dead_pack_is_not_compacted()
    {
        (InMemoryBlobStore store, RepoIndex index, _) = await SeedPackAsync();
        string? newId = await Compactor.CompactAsync(store, _master, index, "oldpack", new HashSet<string>(), _work, 64);
        Assert.Null(newId); // nothing live → GC deletes it instead
    }
}
