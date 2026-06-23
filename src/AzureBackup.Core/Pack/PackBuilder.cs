using AzureBackup.Core.Compression;
using AzureBackup.Core.Crypto;
using AzureBackup.Core.Scan;
using AzureBackup.Core.Volumes;

namespace AzureBackup.Core.Pack;

/// <summary>A member to place in a pack: its content hash and a way to open its bytes.</summary>
public sealed record PackMember(string Hash, Func<Stream> Open);

/// <summary>Byte range of a member within the pack's decompressed plaintext.</summary>
public readonly record struct ContentSpan(long Offset, long Size);

/// <summary>Result of building a pack: volume files on disk + per-member spans.</summary>
public sealed record BuiltPack(
    string PackId,
    CompressionCodec Codec,
    IReadOnlyList<string> VolumePaths,
    long PlaintextSize,
    long CiphertextSize,
    IReadOnlyDictionary<string, ContentSpan> Entries,
    IReadOnlyList<FailedMember> FailedMembers);

/// <summary>打包时未能读取的成员(仅容错模式下产生)。</summary>
public readonly record struct FailedMember(string Hash, SkipReason Reason, string? Detail);

/// <summary>
/// Builds a pack: concatenate member contents (recording spans) → compress → segmented
/// encrypt → split into volume files in the work dir. Volumes are only "admitted" once
/// the whole pack succeeds (see docs); on failure the caller discards the work dir output.
/// </summary>
public sealed class PackBuilder
{
    private readonly string _workDir;
    private readonly long _volumeSize;
    private readonly int _segmentSize;

    public PackBuilder(string workDir, long volumeSize, int segmentSize = SegmentedCipher.DefaultSegmentSize)
    {
        _workDir = workDir ?? throw new ArgumentNullException(nameof(workDir));
        if (volumeSize <= 0) throw new ArgumentOutOfRangeException(nameof(volumeSize));
        if (segmentSize <= 0) throw new ArgumentOutOfRangeException(nameof(segmentSize));
        _volumeSize = volumeSize;
        _segmentSize = segmentSize;
    }

    public BuiltPack Build(string packId, CompressionCodec codec, byte[] contentKey,
        IReadOnlyList<PackMember> members, bool tolerateMemberFailures = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(packId);
        ArgumentNullException.ThrowIfNull(contentKey);
        ArgumentNullException.ThrowIfNull(members);

        Directory.CreateDirectory(_workDir);
        string plaintextPath = Path.Combine(_workDir, packId + ".plain");
        string compressedPath = Path.Combine(_workDir, packId + ".comp");
        var entries = new Dictionary<string, ContentSpan>(StringComparer.Ordinal);
        var failed = new List<FailedMember>();

        try
        {
            long offset = 0;
            using (var pt = new FileStream(plaintextPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (PackMember m in members)
                {
                    if (entries.ContainsKey(m.Hash))
                        continue; // identical content already placed (dedup within pack)
                    Stream? src = null;
                    try { src = m.Open(); }
                    catch (Exception ex) when (tolerateMemberFailures && IsSkippable(ex))
                    {
                        failed.Add(new FailedMember(m.Hash, Classify(ex), ex.Message));
                        continue;
                    }
                    try
                    {
                        long copied = Copy(src, pt);
                        entries[m.Hash] = new ContentSpan(offset, copied);
                        offset += copied;
                    }
                    catch (Exception ex) when (tolerateMemberFailures && IsSkippable(ex))
                    {
                        failed.Add(new FailedMember(m.Hash, Classify(ex), ex.Message));
                        offset = pt.Position; // 排除脏字节,后续成员 offset 续接
                        continue;
                    }
                    finally { src?.Dispose(); }
                }
            }
            long plaintextSize = offset;

            using (Stream input = File.OpenRead(plaintextPath))
            using (var output = new FileStream(compressedPath, FileMode.Create, FileAccess.Write, FileShare.None))
                Compressors.For(codec).Compress(input, output);

            var writer = new VolumeWriter(_workDir, packId, _volumeSize);
            using (writer)
            using (Stream compressed = File.OpenRead(compressedPath))
                SegmentedCipher.Encrypt(contentKey, compressed, writer, _segmentSize);

            return new BuiltPack(packId, codec, [.. writer.VolumePaths], plaintextSize,
                writer.TotalBytesWritten, entries, failed);
        }
        finally
        {
            TryDelete(plaintextPath);
            TryDelete(compressedPath);
        }
    }

    private static bool IsSkippable(Exception ex)
        => ex is FileNotFoundException or DirectoryNotFoundException
              or UnauthorizedAccessException or IOException;

    private static SkipReason Classify(Exception ex)
        => ex is FileNotFoundException or DirectoryNotFoundException
            ? SkipReason.Missing : SkipReason.Unreadable;

    private static long Copy(Stream src, Stream dst)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            dst.Write(buffer, 0, n);
            total += n;
        }
        return total;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
