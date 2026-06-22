using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace AzureBackup.Core.Storage;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IBlobStore"/> over one container.
/// Uploads use chunked block-blob transfer; transient failures retry per <see cref="RetryPolicy"/>.
/// The lock is a blob lease (auto-renewed by the caller; auto-expires ~&lt;1min after a crash).
/// </summary>
public sealed class AzureBlobStore : IBlobStore
{
    // Azure blob lease fixed durations: 15..60s, renewed periodically.
    private static readonly TimeSpan MinLease = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxLease = TimeSpan.FromSeconds(60);

    private readonly BlobContainerClient _container;
    private readonly RetryPolicy _retry;

    public AzureBlobStore(BlobContainerClient container, RetryPolicy? retry = null)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _retry = retry ?? RetryPolicy.Default();
    }

    public static AzureBlobStore Create(string connectionString, string container, RetryPolicy? retry = null)
        => new(new BlobContainerClient(connectionString, container), retry);

    public async Task PutAsync(string name, Stream content, BlobTier tier, bool overwrite, CancellationToken ct = default)
    {
        BlobClient blob = _container.GetBlobClient(name);
        var options = new BlobUploadOptions
        {
            AccessTier = MapTier(tier),
            TransferOptions = new Azure.Storage.StorageTransferOptions
            {
                MaximumConcurrency = 4,
                MaximumTransferSize = 8 * 1024 * 1024,
            },
        };
        if (!overwrite)
            options.Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };

        await _retry.ExecuteAsync(async c =>
        {
            content.Position = content.CanSeek ? 0 : content.Position;
            await blob.UploadAsync(content, options, c).ConfigureAwait(false);
        }, IsTransient, ct).ConfigureAwait(false);
    }

    public async Task<Stream> GetAsync(string name, CancellationToken ct = default)
        => await _retry.ExecuteAsync(
            async c => (Stream)await _container.GetBlobClient(name).OpenReadAsync(cancellationToken: c).ConfigureAwait(false),
            IsTransient, ct).ConfigureAwait(false);

    public async Task<bool> ExistsAsync(string name, CancellationToken ct = default)
        => await _retry.ExecuteAsync(
            async c => (await _container.GetBlobClient(name).ExistsAsync(c).ConfigureAwait(false)).Value,
            IsTransient, ct).ConfigureAwait(false);

    public async Task DeleteAsync(string name, CancellationToken ct = default)
        => await _retry.ExecuteAsync(
            async c => { await _container.GetBlobClient(name).DeleteIfExistsAsync(cancellationToken: c).ConfigureAwait(false); },
            IsTransient, ct).ConfigureAwait(false);

    public async IAsyncEnumerable<string> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (BlobItem item in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct).ConfigureAwait(false))
            yield return item.Name;
    }

    public Task SetTierAsync(string name, BlobTier tier, RehydratePriority priority = RehydratePriority.Standard, CancellationToken ct = default)
        => _retry.ExecuteAsync(async c =>
        {
            await _container.GetBlobClient(name)
                .SetAccessTierAsync(MapTier(tier), rehydratePriority: MapPriority(priority), cancellationToken: c)
                .ConfigureAwait(false);
        }, IsTransient, ct);

    public async Task<ILockHandle?> TryAcquireLockAsync(string name, TimeSpan ttl, CancellationToken ct = default)
    {
        BlobClient blob = _container.GetBlobClient(name);

        // Ensure the lock blob exists (ignore "already exists").
        try
        {
            await blob.UploadAsync(BinaryData.FromBytes(ReadOnlyMemory<byte>.Empty),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, ct)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // already exists
        }

        TimeSpan duration = Clamp(ttl, MinLease, MaxLease);
        BlobLeaseClient lease = blob.GetBlobLeaseClient();
        try
        {
            await lease.AcquireAsync(duration, cancellationToken: ct).ConfigureAwait(false);
            return new AzureLock(lease);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return null; // already leased → another run is active
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        RequestFailedException rfe => rfe.Status is 0 or 408 or 429 or >= 500,
        IOException => true,
        TimeoutException => true,
        _ => false,
    };

    private static AccessTier MapTier(BlobTier tier) => tier switch
    {
        BlobTier.Hot => AccessTier.Hot,
        BlobTier.Cool => AccessTier.Cool,
        BlobTier.Cold => AccessTier.Cold,
        BlobTier.Archive => AccessTier.Archive,
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };

    private static Azure.Storage.Blobs.Models.RehydratePriority MapPriority(RehydratePriority p) => p switch
    {
        RehydratePriority.High => Azure.Storage.Blobs.Models.RehydratePriority.High,
        _ => Azure.Storage.Blobs.Models.RehydratePriority.Standard,
    };

    private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max)
        => v < min ? min : (v > max ? max : v);

    private sealed class AzureLock(BlobLeaseClient lease) : ILockHandle
    {
        public async Task RenewAsync(CancellationToken ct = default)
            => await lease.RenewAsync(cancellationToken: ct).ConfigureAwait(false);

        public async ValueTask DisposeAsync()
        {
            try { await lease.ReleaseAsync().ConfigureAwait(false); }
            catch { /* lease may have expired; ignore */ }
        }
    }
}
