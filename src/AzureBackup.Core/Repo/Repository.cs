using System.Security.Cryptography;
using AzureBackup.Core.Crypto;
using AzureBackup.Core.Hashing;
using AzureBackup.Core.Model;
using AzureBackup.Core.Serialization;
using AzureBackup.Core.Storage;

namespace AzureBackup.Core.Repo;

/// <summary>Thrown when the supplied password does not match the repository.</summary>
public sealed class InvalidPasswordException() : Exception("incorrect password for this repository");

/// <summary>
/// An opened repository: holds the derived master key and config. The <c>config</c> blob
/// is stored as plaintext JSON (the KDF salt is needed before any key exists); confidentiality
/// of everything else comes from encryption under the master key, and a wrong password is
/// rejected via the encrypted pwCheck token.
/// </summary>
public sealed class Repository
{
    public RepoConfig Config { get; }

    /// <summary>Master key derived from the password — used to encrypt structure and wrap content keys.</summary>
    public byte[] MasterKey { get; }

    private Repository(byte[] masterKey, RepoConfig config)
    {
        MasterKey = masterKey;
        Config = config;
    }

    public static Task<bool> ExistsAsync(IBlobStore store, CancellationToken ct = default)
        => store.ExistsAsync(RepoLayout.Config, ct);

    public static async Task<Repository> InitAsync(IBlobStore store, string password, long volumeSizeBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (await store.ExistsAsync(RepoLayout.Config, ct).ConfigureAwait(false))
            throw new InvalidOperationException("repository already initialized");

        byte[] salt = RandomNumberGenerator.GetBytes(KeyDerivation.SaltBytes);
        int iter = KeyDerivation.DefaultIterations, mem = KeyDerivation.DefaultMemoryKiB, par = KeyDerivation.DefaultParallelism;
        byte[] key = KeyDerivation.DeriveMasterKey(password, salt, iter, mem, par);

        var config = new RepoConfig(
            FormatVersion.Current,
            new KdfParams("argon2id", Convert.ToBase64String(salt), iter, mem, par),
            Convert.ToBase64String(PasswordVerifier.CreateToken(key)),
            ContentHasher.AlgoName,
            volumeSizeBytes);

        await WriteConfigAsync(store, config, ct).ConfigureAwait(false);
        return new Repository(key, config);
    }

    public static async Task<Repository> OpenAsync(IBlobStore store, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (!await store.ExistsAsync(RepoLayout.Config, ct).ConfigureAwait(false))
            throw new InvalidOperationException("repository not initialized");

        RepoConfig config = await ReadConfigAsync(store, ct).ConfigureAwait(false);
        byte[] salt = Convert.FromBase64String(config.Kdf.SaltBase64);
        byte[] key = KeyDerivation.DeriveMasterKey(password, salt, config.Kdf.Iterations, config.Kdf.MemoryKiB, config.Kdf.Parallelism);

        if (!PasswordVerifier.Verify(key, Convert.FromBase64String(config.PwCheckBase64)))
            throw new InvalidPasswordException();

        return new Repository(key, config);
    }

    private static async Task WriteConfigAsync(IBlobStore store, RepoConfig config, CancellationToken ct)
    {
        using var ms = new MemoryStream(RepoJson.Serialize(config));
        await store.PutAsync(RepoLayout.Config, ms, BlobTier.Hot, overwrite: false, ct).ConfigureAwait(false);
    }

    private static async Task<RepoConfig> ReadConfigAsync(IBlobStore store, CancellationToken ct)
    {
        using Stream s = await store.GetAsync(RepoLayout.Config, ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct).ConfigureAwait(false);
        return RepoJson.Deserialize<RepoConfig>(ms.ToArray());
    }
}
