using System.Text;
using Konscious.Security.Cryptography;

namespace AzureBackup.Core.Crypto;

/// <summary>
/// Derives the 32-byte master key from a password via Argon2id.
/// Parameters are stored in the repo <c>config</c>; defaults are interactive-grade
/// and will be reconfirmed in the security review.
/// </summary>
public static class KeyDerivation
{
    public const int MasterKeyBytes = 32;
    public const int SaltBytes = 16;

    public const int DefaultIterations = 3;
    public const int DefaultMemoryKiB = 64 * 1024; // 64 MiB
    public const int DefaultParallelism = 4;

    public static byte[] DeriveMasterKey(string password, byte[] salt, int iterations, int memoryKiB, int parallelism)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Length < 8)
            throw new ArgumentException("salt must be at least 8 bytes", nameof(salt));

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            Iterations = iterations,
            MemorySize = memoryKiB,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(MasterKeyBytes);
    }
}
