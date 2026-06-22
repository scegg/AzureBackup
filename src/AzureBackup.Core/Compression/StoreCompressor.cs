namespace AzureBackup.Core.Compression;

/// <summary>Identity codec — passes bytes through unchanged (incompressible types).</summary>
public sealed class StoreCompressor : ICompressor
{
    public CompressionCodec Codec => CompressionCodec.Store;

    public void Compress(Stream source, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        source.CopyTo(destination);
    }

    public void Decompress(Stream source, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        source.CopyTo(destination);
    }
}
