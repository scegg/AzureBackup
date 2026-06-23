namespace AzureBackup.Core.Scan;

public enum ChangeKind
{
    Unchanged,
    New,
    Modified,
}

/// <summary>A file's metadata as recorded by the previous snapshot.</summary>
public sealed record PriorFile(long Size, DateTimeOffset Mtime, string Hash, int Mode);

/// <summary>
/// Decides whether a file changed since the last snapshot, using mtime as the gate
/// (see docs/requirements §2.4):
///  - new file → New (hash computed);
///  - mtime &amp; size unchanged → Unchanged (no read, no hash);
///  - otherwise → recompute hash: equal → Unchanged (metadata only), else Modified.
/// <c>forceHash</c> ignores the mtime fast-path and always rehashes.
/// </summary>
public static class ChangeDetector
{
    public readonly record struct Decision(ChangeKind Kind, string Hash, bool Hashed);

    public static Decision Detect(
        PriorFile? prior,
        long size,
        DateTimeOffset mtime,
        bool forceHash,
        Func<string> computeHash)
    {
        ArgumentNullException.ThrowIfNull(computeHash);

        if (prior is null)
            return new Decision(ChangeKind.New, computeHash(), Hashed: true);

        if (!forceHash && size == prior.Size && mtime == prior.Mtime)
            return new Decision(ChangeKind.Unchanged, prior.Hash, Hashed: false);

        string current = computeHash();
        return current == prior.Hash
            ? new Decision(ChangeKind.Unchanged, current, Hashed: true)
            : new Decision(ChangeKind.Modified, current, Hashed: true);
    }
}
