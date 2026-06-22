using System.Security.Cryptography;
using AzureBackup.Core.Crypto;
using Xunit;

namespace AzureBackup.Core.Tests;

public class AeadTests
{
    private static byte[] Key() => RandomNumberGenerator.GetBytes(Aead.KeyBytes);

    [Fact]
    public void Seal_then_Open_roundtrips()
    {
        byte[] key = Key();
        byte[] plaintext = "hello 世界"u8.ToArray();

        byte[] sealed_ = Aead.Seal(key, plaintext);
        byte[] opened = Aead.Open(key, sealed_);

        Assert.Equal(plaintext, opened);
    }

    [Fact]
    public void Open_with_wrong_key_throws()
    {
        byte[] sealed_ = Aead.Seal(Key(), "secret"u8);
        Assert.ThrowsAny<CryptographicException>(() => Aead.Open(Key(), sealed_));
    }

    [Fact]
    public void Tampered_ciphertext_is_rejected()
    {
        byte[] key = Key();
        byte[] sealed_ = Aead.Seal(key, "payload"u8);
        sealed_[^1] ^= 0xFF; // flip a tag byte

        Assert.ThrowsAny<CryptographicException>(() => Aead.Open(key, sealed_));
    }

    [Fact]
    public void Associated_data_mismatch_is_rejected()
    {
        byte[] key = Key();
        byte[] sealed_ = Aead.Seal(key, "payload"u8, "context-A"u8);
        Assert.ThrowsAny<CryptographicException>(() => Aead.Open(key, sealed_, "context-B"u8));
    }

    [Fact]
    public void Empty_plaintext_roundtrips()
    {
        byte[] key = Key();
        byte[] sealed_ = Aead.Seal(key, ReadOnlySpan<byte>.Empty);
        Assert.Empty(Aead.Open(key, sealed_));
    }
}

public class KeyDerivationTests
{
    private static byte[] Salt() => RandomNumberGenerator.GetBytes(KeyDerivation.SaltBytes);

    [Fact]
    public void Derivation_is_deterministic()
    {
        byte[] salt = Salt();
        byte[] a = KeyDerivation.DeriveMasterKey("pw", salt, 2, 8 * 1024, 1);
        byte[] b = KeyDerivation.DeriveMasterKey("pw", salt, 2, 8 * 1024, 1);

        Assert.Equal(KeyDerivation.MasterKeyBytes, a.Length);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_salt_yields_different_key()
    {
        byte[] a = KeyDerivation.DeriveMasterKey("pw", Salt(), 2, 8 * 1024, 1);
        byte[] b = KeyDerivation.DeriveMasterKey("pw", Salt(), 2, 8 * 1024, 1);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Different_password_yields_different_key()
    {
        byte[] salt = Salt();
        byte[] a = KeyDerivation.DeriveMasterKey("pw1", salt, 2, 8 * 1024, 1);
        byte[] b = KeyDerivation.DeriveMasterKey("pw2", salt, 2, 8 * 1024, 1);
        Assert.NotEqual(a, b);
    }
}

public class PasswordVerifierTests
{
    [Fact]
    public void Correct_key_verifies_and_wrong_key_fails()
    {
        byte[] salt = RandomNumberGenerator.GetBytes(KeyDerivation.SaltBytes);
        byte[] master = KeyDerivation.DeriveMasterKey("correct horse", salt, 2, 8 * 1024, 1);
        byte[] token = PasswordVerifier.CreateToken(master);

        Assert.True(PasswordVerifier.Verify(master, token));

        byte[] wrong = KeyDerivation.DeriveMasterKey("battery staple", salt, 2, 8 * 1024, 1);
        Assert.False(PasswordVerifier.Verify(wrong, token));
    }
}

public class ContentKeyTests
{
    [Fact]
    public void Wrap_then_Unwrap_roundtrips()
    {
        byte[] master = RandomNumberGenerator.GetBytes(Aead.KeyBytes);
        byte[] contentKey = ContentKey.Generate();

        byte[] wrapped = ContentKey.Wrap(master, contentKey);
        byte[] unwrapped = ContentKey.Unwrap(master, wrapped);

        Assert.Equal(Aead.KeyBytes, contentKey.Length);
        Assert.Equal(contentKey, unwrapped);
    }
}
