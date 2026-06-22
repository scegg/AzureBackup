using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace AzureBackup.Core.Storage;

/// <summary>
/// In-memory <see cref="IBlobStore"/> for tests (and local dry runs). Models tiers and
/// a TTL-based lease lock with auto-expiry. Not for production.
/// </summary>
public sealed class InMemoryBlobStore : IBlobStore
{
    private sealed record Entry(byte[] Data, BlobTier Tier);
    private sealed record Lease(string Id, DateTimeOffset Expiry);

    private readonly ConcurrentDictionary<string, Entry> _blobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private readonly object _lockGate = new();

    public Task PutAsync(string name, Stream content, BlobTier tier, bool overwrite, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!overwrite && _blobs.ContainsKey(name))
            throw new IOException($"blob already exists: {name}");
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        _blobs[name] = new Entry(ms.ToArray(), tier);
        return Task.CompletedTask;
    }

    public Task<Stream> GetAsync(string name, CancellationToken ct = default)
    {
        if (!_blobs.TryGetValue(name, out Entry? e))
            throw new FileNotFoundException($"blob not found: {name}");
        return Task.FromResult<Stream>(new MemoryStream(e.Data, writable: false));
    }

    public Task<bool> ExistsAsync(string name, CancellationToken ct = default)
        => Task.FromResult(_blobs.ContainsKey(name));

    public Task DeleteAsync(string name, CancellationToken ct = default)
    {
        _blobs.TryRemove(name, out _);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (string name in _blobs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            yield return name;
        }
        await Task.CompletedTask;
    }

    public Task SetTierAsync(string name, BlobTier tier, RehydratePriority priority = RehydratePriority.Standard, CancellationToken ct = default)
    {
        if (_blobs.TryGetValue(name, out Entry? e))
            _blobs[name] = e with { Tier = tier };
        return Task.CompletedTask;
    }

    public Task<ILockHandle?> TryAcquireLockAsync(string name, TimeSpan ttl, CancellationToken ct = default)
    {
        lock (_lockGate)
        {
            if (_leases.TryGetValue(name, out Lease? existing) && existing.Expiry > DateTimeOffset.UtcNow)
                return Task.FromResult<ILockHandle?>(null);

            var lease = new Lease(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow + ttl);
            _leases[name] = lease;
            return Task.FromResult<ILockHandle?>(new Handle(this, name, lease.Id, ttl));
        }
    }

    // Test helper.
    public BlobTier TierOf(string name) => _blobs.TryGetValue(name, out Entry? e) ? e.Tier : throw new FileNotFoundException(name);

    private void Renew(string name, string id, TimeSpan ttl)
    {
        lock (_lockGate)
        {
            if (_leases.TryGetValue(name, out Lease? l) && l.Id == id)
                _leases[name] = l with { Expiry = DateTimeOffset.UtcNow + ttl };
        }
    }

    private void Release(string name, string id)
    {
        lock (_lockGate)
        {
            if (_leases.TryGetValue(name, out Lease? l) && l.Id == id)
                _leases.TryRemove(name, out _);
        }
    }

    private sealed class Handle(InMemoryBlobStore store, string name, string id, TimeSpan ttl) : ILockHandle
    {
        public Task RenewAsync(CancellationToken ct = default)
        {
            store.Renew(name, id, ttl);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            store.Release(name, id);
            return ValueTask.CompletedTask;
        }
    }
}
