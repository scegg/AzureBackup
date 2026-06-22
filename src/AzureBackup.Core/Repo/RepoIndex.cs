using AzureBackup.Core.Model;
using AzureBackup.Core.Pack;

namespace AzureBackup.Core.Repo;

/// <summary>
/// In-memory global index: <c>hash → physical location</c> plus per-pack info.
/// Built from index shards (latest-wins) and serialized back to a shard. Relocation
/// (compaction) only updates entries here — snapshots never change.
/// </summary>
public sealed class RepoIndex
{
    public sealed class PackEntry
    {
        public int Volumes { get; set; }
        public long TotalSize { get; set; }
        public string WrappedKeyBase64 { get; set; } = "";
        public HashSet<string> Members { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, ContentLocation> _byHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PackEntry> _packs = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ContentLocation> ByHash => _byHash;
    public IReadOnlyDictionary<string, PackEntry> Packs => _packs;

    public bool Contains(string hash) => _byHash.ContainsKey(hash);

    public bool TryResolve(string hash, out ContentLocation location)
        => _byHash.TryGetValue(hash, out location!);

    public void AddPack(string packId, int volumes, long totalSize, string wrappedKeyBase64,
        IReadOnlyDictionary<string, ContentSpan> entries)
    {
        var entry = new PackEntry { Volumes = volumes, TotalSize = totalSize, WrappedKeyBase64 = wrappedKeyBase64 };
        foreach (KeyValuePair<string, ContentSpan> kv in entries)
        {
            _byHash[kv.Key] = new ContentLocation(packId, kv.Value.Offset, kv.Value.Size);
            entry.Members.Add(kv.Key);
        }
        _packs[packId] = entry;
    }

    /// <summary>Removes a pack and all index entries that still point at it.</summary>
    public void RemovePack(string packId)
    {
        if (!_packs.Remove(packId, out PackEntry? entry)) return;
        foreach (string hash in entry.Members)
            if (_byHash.TryGetValue(hash, out ContentLocation? loc) && loc.Pack == packId)
                _byHash.Remove(hash);
    }

    public IndexShard ToShard()
    {
        var byHash = _byHash.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var packs = _packs.ToDictionary(
            kv => kv.Key,
            kv => new PackInfo(kv.Value.Volumes, kv.Value.TotalSize, kv.Value.WrappedKeyBase64,
                [.. kv.Value.Members], LiveCount: 0),
            StringComparer.Ordinal);
        return new IndexShard(byHash, packs);
    }

    public static RepoIndex FromShards(IEnumerable<IndexShard> shards)
    {
        var index = new RepoIndex();
        foreach (IndexShard shard in shards) // later shards win
        {
            foreach (KeyValuePair<string, PackInfo> p in shard.Packs)
            {
                var entry = new PackEntry { Volumes = p.Value.Volumes, TotalSize = p.Value.TotalSize, WrappedKeyBase64 = p.Value.WrappedKeyBase64 };
                foreach (string m in p.Value.Members) entry.Members.Add(m);
                index._packs[p.Key] = entry;
            }
            foreach (KeyValuePair<string, ContentLocation> h in shard.ByHash)
                index._byHash[h.Key] = h.Value;
        }
        return index;
    }
}
