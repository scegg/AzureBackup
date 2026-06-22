using AzureBackup.Core.Storage;

namespace AzureBackup.Core.Repo;

/// <summary>Per-pack liveness, and the GC/compaction actions implied by it.</summary>
public sealed record GcPlan(
    IReadOnlyList<string> PacksToDelete,     // liveCount == 0 → delete whole pack
    IReadOnlyList<string> PacksToCompact,    // 0 < deadRatio ≥ threshold → repack survivors
    IReadOnlyList<string> DeadHashes);       // index entries no longer referenced

/// <summary>
/// Reference-counting GC + compaction decisions over the index, given the set of
/// hashes still reachable from retained snapshots.
/// </summary>
public static class GarbageCollector
{
    public static GcPlan Plan(RepoIndex index, IReadOnlySet<string> liveHashes, double compactionThreshold)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(liveHashes);

        var toDelete = new List<string>();
        var toCompact = new List<string>();

        foreach (KeyValuePair<string, RepoIndex.PackEntry> kv in index.Packs)
        {
            RepoIndex.PackEntry pack = kv.Value;
            int total = pack.Members.Count;
            int live = total == 0 ? 0 : pack.Members.Count(liveHashes.Contains);

            if (live == 0)
            {
                toDelete.Add(kv.Key);
                continue;
            }
            double deadRatio = total == 0 ? 0 : 1.0 - (double)live / total;
            if (compactionThreshold > 0 && deadRatio >= compactionThreshold)
                toCompact.Add(kv.Key);
        }

        var deadHashes = index.ByHash.Keys.Where(h => !liveHashes.Contains(h)).ToList();
        return new GcPlan(toDelete, toCompact, deadHashes);
    }

    /// <summary>Parses a compaction setting: "off" → 0, "30%"/"0.3" → 0.30.</summary>
    public static double ParseThreshold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("off", StringComparison.OrdinalIgnoreCase))
            return 0;
        value = value.Trim();
        if (value.EndsWith('%'))
            return double.Parse(value[..^1], System.Globalization.CultureInfo.InvariantCulture) / 100.0;
        return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>Checks that every volume referenced by retained snapshots exists (remote verify).</summary>
public static class RemoteVerifier
{
    public static async Task<IReadOnlyList<string>> FindMissingVolumesAsync(
        IBlobStore store, RepoIndex index, IReadOnlySet<string> liveHashes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(index);

        var livePacks = new HashSet<string>(StringComparer.Ordinal);
        foreach (string hash in liveHashes)
            if (index.TryResolve(hash, out var loc))
                livePacks.Add(loc.Pack);

        var missing = new List<string>();
        foreach (string packId in livePacks)
        {
            if (!index.Packs.TryGetValue(packId, out RepoIndex.PackEntry? pack)) continue;
            for (int i = 0; i < pack.Volumes; i++)
            {
                string vol = RepoLayout.Volume(packId, i);
                if (!await store.ExistsAsync(vol, ct).ConfigureAwait(false))
                    missing.Add(vol);
            }
        }
        return missing;
    }
}
