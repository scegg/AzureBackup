namespace AzureBackup.Core.Storage;

/// <summary>Blob access tier. Data packs use Archive; structure files use Hot.</summary>
public enum BlobTier
{
    Hot,
    Cool,
    Cold,
    Archive,
}

/// <summary>Archive rehydration priority (restore-time).</summary>
public enum RehydratePriority
{
    Standard,
    High,
}

/// <summary>A held distributed lock (blob lease). Dispose releases it.</summary>
public interface ILockHandle : IAsyncDisposable
{
    Task RenewAsync(CancellationToken ct = default);
}

/// <summary>
/// Blob storage abstraction (one instance = one repository/container). The Azure
/// implementation backs production; an in-memory implementation backs tests.
/// </summary>
public interface IBlobStore
{
    Task PutAsync(string name, Stream content, BlobTier tier, bool overwrite, CancellationToken ct = default);

    Task<Stream> GetAsync(string name, CancellationToken ct = default);

    Task<bool> ExistsAsync(string name, CancellationToken ct = default);

    Task DeleteAsync(string name, CancellationToken ct = default);

    IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct = default);

    Task SetTierAsync(string name, BlobTier tier, RehydratePriority priority = RehydratePriority.Standard, CancellationToken ct = default);

    /// <summary>Tries to acquire the named lock; returns null if already held.</summary>
    Task<ILockHandle?> TryAcquireLockAsync(string name, TimeSpan ttl, CancellationToken ct = default);
}
