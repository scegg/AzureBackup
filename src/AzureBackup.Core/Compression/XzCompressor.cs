namespace AzureBackup.Core.Compression;

/// <summary>
/// LZMA2 compression via the <c>xz</c> binary (same algorithm family as 7-Zip).
/// <c>-9e</c> = level 9 "extreme" (max compression). Requires <c>xz</c> on PATH
/// (provided by the Docker image).
/// </summary>
public sealed class XzCompressor : ICompressor
{
    private const string Exe = "xz";

    public CompressionCodec Codec => CompressionCodec.Xz;

    public static bool IsAvailable() => ProcessFilter.IsAvailable(Exe);

    public void Compress(Stream source, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        // -9e: max compression; -T1: deterministic single-threaded output; -c: stdout.
        ProcessFilter.Run(Exe, "-9e -T1 -c", source, destination);
    }

    public void Decompress(Stream source, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ProcessFilter.Run(Exe, "-d -c", source, destination);
    }
}
