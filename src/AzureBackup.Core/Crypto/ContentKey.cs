using System.Security.Cryptography;

namespace AzureBackup.Core.Crypto;

/// <summary>
/// Per-pack content key handling: a random 32-byte key wraps each pack's data,
/// and is itself wrapped (sealed) under the master key for storage in the index.
/// </summary>
public static class ContentKey
{
    public static byte[] Generate() => RandomNumberGenerator.GetBytes(Aead.KeyBytes);

    public static byte[] Wrap(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> contentKey)
        => Aead.Seal(masterKey, contentKey);

    public static byte[] Unwrap(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> wrapped)
        => Aead.Open(masterKey, wrapped);
}
