namespace AzureBackup.Core.Repo;

public enum RetentionMode
{
    And,
    Or,
}

public sealed record SnapshotInfo(string Id, DateTimeOffset CreatedAtUtc);

/// <summary>
/// Selects snapshots to delete from "keep last X" and/or "keep last Y days".
/// When both are set, <see cref="RetentionMode"/> chooses AND (delete only if over
/// both — keeps more) or OR (delete if over either — keeps fewer).
/// </summary>
public static class RetentionPolicy
{
    public static IReadOnlyList<string> SelectForDeletion(
        IReadOnlyList<SnapshotInfo> snapshots,
        int? keepCount,
        int? keepDays,
        RetentionMode mode,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (keepCount is null && keepDays is null)
            return []; // no policy → keep everything

        // Newest first; rank 0 = newest.
        var ordered = snapshots.OrderByDescending(s => s.CreatedAtUtc).ToList();
        var toDelete = new List<string>();

        for (int rank = 0; rank < ordered.Count; rank++)
        {
            SnapshotInfo s = ordered[rank];
            bool overCount = keepCount.HasValue ? rank >= keepCount.Value : (mode == RetentionMode.And);
            bool overDays = keepDays.HasValue ? (now - s.CreatedAtUtc).TotalDays > keepDays.Value : (mode == RetentionMode.And);

            bool delete = mode == RetentionMode.And ? (overCount && overDays) : (overCount || overDays);
            if (delete)
                toDelete.Add(s.Id);
        }
        return toDelete;
    }
}
