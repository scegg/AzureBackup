using System.Security.Cryptography;

namespace AzureBackup.Core.Crypto;

/// <summary>
/// AES-256-GCM authenticated encryption over a single buffer.
/// Output layout: <c>nonce(12) || ciphertext || tag(16)</c>.
/// Used for keys, password-check tokens, and (compressed) structure shards.
/// Large pack data is encrypted in segments by a separate path.
/// </summary>
public static class Aead
{
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int KeyBytes = 32;

    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        if (key.Length != KeyBytes)
            throw new ArgumentException($"key must be {KeyBytes} bytes", nameof(key));

        byte[] output = new byte[NonceBytes + plaintext.Length + TagBytes];
        Span<byte> nonce = output.AsSpan(0, NonceBytes);
        Span<byte> ciphertext = output.AsSpan(NonceBytes, plaintext.Length);
        Span<byte> tag = output.AsSpan(NonceBytes + plaintext.Length, TagBytes);

        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(key, TagBytes);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return output;
    }

    /// <summary>Decrypts a buffer produced by <see cref="Seal"/>.
    /// Throws <see cref="CryptographicException"/> on a wrong key or tampering.</summary>
    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> associatedData = default)
    {
        if (key.Length != KeyBytes)
            throw new ArgumentException($"key must be {KeyBytes} bytes", nameof(key));
        if (payload.Length < NonceBytes + TagBytes)
            throw new ArgumentException("payload too short", nameof(payload));

        ReadOnlySpan<byte> nonce = payload[..NonceBytes];
        int ctLen = payload.Length - NonceBytes - TagBytes;
        ReadOnlySpan<byte> ciphertext = payload.Slice(NonceBytes, ctLen);
        ReadOnlySpan<byte> tag = payload.Slice(NonceBytes + ctLen, TagBytes);

        byte[] plaintext = new byte[ctLen];
        using var gcm = new AesGcm(key, TagBytes);
        gcm.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }
}
