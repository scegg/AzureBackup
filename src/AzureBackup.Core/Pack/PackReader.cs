using AzureBackup.Core.Compression;
using AzureBackup.Core.Crypto;

namespace AzureBackup.Core.Pack;

/// <summary>
/// Reverses <see cref="PackBuilder"/>: join volumes → segmented-decrypt → decompress
/// → pack plaintext, from which member byte ranges are sliced.
/// </summary>
public static class PackReader
{
    /// <summary>Decodes a pack's joined ciphertext back to its plaintext.</summary>
    public static byte[] Decode(Stream ciphertext, CompressionCodec codec, byte[] contentKey)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(contentKey);

        using var compressed = new MemoryStream();
        SegmentedCipher.Decrypt(contentKey, ciphertext, compressed);
        compressed.Position = 0;

        using var plaintext = new MemoryStream();
        Compressors.For(codec).Decompress(compressed, plaintext);
        return plaintext.ToArray();
    }

    /// <summary>Joins volume files (in order) into a single readable stream.</summary>
    public static Stream OpenJoined(IEnumerable<string> volumePaths)
    {
        ArgumentNullException.ThrowIfNull(volumePaths);
        var ms = new MemoryStream();
        foreach (string path in volumePaths)
        {
            using Stream v = File.OpenRead(path);
            v.CopyTo(ms);
        }
        ms.Position = 0;
        return ms;
    }
}
