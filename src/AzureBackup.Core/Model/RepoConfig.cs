namespace AzureBackup.Core.Model;

/// <summary>Argon2id parameters, stored (with the salt) in the repo config.</summary>
public sealed record KdfParams(
    string Algo,
    string SaltBase64,
    int Iterations,
    int MemoryKiB,
    int Parallelism);

/// <summary>
/// Repository <c>config</c> blob (Hot, encrypted). Holds format version, KDF params,
/// the password-check token, the fixed hash algorithm and the volume size.
/// </summary>
public sealed record RepoConfig(
    int FormatVersion,
    KdfParams Kdf,
    string PwCheckBase64,
    string HashAlgo,
    long VolumeSizeBytes,
    string? RepoLabel = null);
