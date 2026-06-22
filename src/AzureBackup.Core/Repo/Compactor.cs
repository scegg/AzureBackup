using AzureBackup.Core.Crypto;
using AzureBackup.Core.Model;
using AzureBackup.Core.Pack;
using AzureBackup.Core.Storage;

namespace AzureBackup.Core.Repo;

/// <summary>
/// Compacts a sparse pack: download (rehydrate) → decode → re-pack only the still-live
/// members into a fresh pack → repoint the index → delete the old pack. The index is
/// mutated in place (caller persists it). Dead members' index entries are dropped.
/// </summary>
public static class Compactor
{
    /// <summary>Compacts one pack; returns the new pack id, or null if nothing live to move.</summary>
    public static async Task<string?> CompactAsync(
        IBlobStore store, byte[] masterKey, RepoIndex index, string packId,
        IReadOnlySet<string> liveHashes, string workDir, long volumeSize,
        BlobTier dataTier = BlobTier.Archive, CancellationToken ct = default)
    {
        if (!index.Packs.TryGetValue(packId, out RepoIndex.PackEntry? pack))
            return null;

        var live = pack.Members.Where(liveHashes.Contains).ToList();
        if (live.Count == 0)
            return null; // fully dead — GC deletes it, no compaction needed

        // Request rehydration of the source pack's volumes (real Archive; no-op for Hot/fake).
        for (int i = 0; i < pack.Volumes; i++)
            await store.SetTierAsync(RepoLayout.Volume(packId, i), BlobTier.Hot, ct: ct).ConfigureAwait(false);

        // Decode the old pack to recover live members' bytes.
        byte[] oldKey = ContentKey.Unwrap(masterKey, Convert.FromBase64String(pack.WrappedKeyBase64));
        byte[] plaintext;
        using (var joined = new MemoryStream())
        {
            for (int i = 0; i < pack.Volumes; i++)
            {
                using Stream v = await store.GetAsync(RepoLayout.Volume(packId, i), ct).ConfigureAwait(false);
                await v.CopyToAsync(joined, ct).ConfigureAwait(false);
            }
            joined.Position = 0;
            plaintext = PackReader.Decode(joined, pack.Codec, oldKey);
        }

        var members = new List<PackMember>(live.Count);
        foreach (string hash in live)
        {
            ContentLocation loc = index.ByHash[hash];
            byte[] bytes = plaintext.AsSpan((int)loc.Offset, (int)loc.Size).ToArray();
            members.Add(new PackMember(hash, () => new MemoryStream(bytes)));
        }

        // Build and upload the new (compacted) pack.
        string newId = Guid.NewGuid().ToString("N");
        byte[] newKey = ContentKey.Generate();
        BuiltPack built = new PackBuilder(workDir, volumeSize).Build(newId, pack.Codec, newKey, members);
        for (int i = 0; i < built.VolumePaths.Count; i++)
        {
            using (Stream vs = File.OpenRead(built.VolumePaths[i]))
                await store.PutAsync(RepoLayout.Volume(newId, i), vs, dataTier, overwrite: true, ct).ConfigureAwait(false);
            TryDelete(built.VolumePaths[i]);
        }

        // Repoint live hashes to the new pack, then drop the old pack (and its dead entries).
        string wrapped = Convert.ToBase64String(ContentKey.Wrap(masterKey, newKey));
        index.AddPack(newId, built.VolumePaths.Count, built.CiphertextSize, wrapped, built.Entries, pack.Codec);

        for (int i = 0; i < pack.Volumes; i++)
            await store.DeleteAsync(RepoLayout.Volume(packId, i), ct).ConfigureAwait(false);
        index.RemovePack(packId);

        return newId;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
