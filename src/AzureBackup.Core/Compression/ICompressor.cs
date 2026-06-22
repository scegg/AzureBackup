namespace AzureBackup.Core.Compression;

/// <summary>Compression codec id, recorded per pack so restore decompresses correctly.</summary>
public enum CompressionCodec
{
    /// <summary>No compression (used for incompressible types — still encrypted).</summary>
    Store = 0,

    /// <summary>LZMA2 via <c>xz -9e</c> (same algorithm family as 7-Zip, max compression).</summary>
    Xz = 1,
}

/// <summary>Stream codec applied to a pack's bytes before encryption.</summary>
public interface ICompressor
{
    CompressionCodec Codec { get; }

    void Compress(Stream source, Stream destination);

    void Decompress(Stream source, Stream destination);
}

public static class Compressors
{
    public static ICompressor For(CompressionCodec codec) => codec switch
    {
        CompressionCodec.Store => new StoreCompressor(),
        CompressionCodec.Xz => new XzCompressor(),
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "unknown codec"),
    };
}
