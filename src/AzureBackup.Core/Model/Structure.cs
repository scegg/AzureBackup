using AzureBackup.Core.Compression;

namespace AzureBackup.Core.Model;

// Two-level, content-addressed structure (see docs/format-spec.md).
// Snapshots/trees reference content ONLY by hash; the global index maps
// hash -> physical location. Relocation/compaction touches only the index.

public enum TreeEntryType
{
    Dir,
    File,
}

/// <summary>One entry in a directory's tree object. Dir → child tree objid;
/// File → content hash (physical location resolved via the index).</summary>
public sealed record TreeEntry(
    string Name,
    TreeEntryType Type,
    string? Child = null,
    long? Size = null,
    DateTimeOffset? Mtime = null,
    int? Mode = null,
    string? Hash = null);

/// <summary>One directory, content-addressed. <c>Next</c> chains overflow shards
/// for very large directories.</summary>
public sealed record TreeObject(
    IReadOnlyList<TreeEntry> Entries,
    string? Next = null);

/// <summary>Top-level snapshot root — small, immutable, no physical locations.</summary>
public sealed record SnapshotRoot(
    int FormatVersion,
    string SnapshotId,
    DateTimeOffset CreatedAtUtc,
    string RootTree,
    IReadOnlyDictionary<string, string>? ConfigSnapshot = null);

/// <summary>Where a content hash currently lives: a byte range within the pack's
/// decompressed plaintext.</summary>
public sealed record ContentLocation(string Pack, long Offset, long Size);

/// <summary>Per-pack info for GC/compaction and decryption (wrapped content key + codec).</summary>
public sealed record PackInfo(
    int Volumes,
    long TotalSize,
    string WrappedKeyBase64,
    IReadOnlyList<string> Members,
    int LiveCount,
    CompressionCodec Codec = CompressionCodec.Xz);

/// <summary>A shard of the global index: hash → location, plus per-pack info.</summary>
public sealed record IndexShard(
    IReadOnlyDictionary<string, ContentLocation> ByHash,
    IReadOnlyDictionary<string, PackInfo> Packs);
