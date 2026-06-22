using System.IO.Compression;
using AzureBackup.Core.Crypto;
using AzureBackup.Core.Serialization;

namespace AzureBackup.Core.Repo;

/// <summary>
/// Encodes/decodes structure objects: JSON → gzip → AES-256-GCM (under the master key).
/// Used for config, root, refs and struct shards (all Hot).
/// </summary>
public static class StructCodec
{
    public static byte[] Encode<T>(ReadOnlySpan<byte> masterKey, T value)
    {
        byte[] json = RepoJson.Serialize(value);
        using var compressed = new MemoryStream();
        using (var gz = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            gz.Write(json, 0, json.Length);
        return Aead.Seal(masterKey, compressed.ToArray());
    }

    public static T Decode<T>(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> blob)
    {
        byte[] compressed = Aead.Open(masterKey, blob); // throws on wrong key / tamper
        using var input = new MemoryStream(compressed);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var json = new MemoryStream();
        gz.CopyTo(json);
        return RepoJson.Deserialize<T>(json.ToArray());
    }
}
