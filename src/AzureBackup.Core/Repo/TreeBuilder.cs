using AzureBackup.Core.Hashing;
using AzureBackup.Core.Model;
using AzureBackup.Core.Serialization;

namespace AzureBackup.Core.Repo;

/// <summary>A file or directory to record in a snapshot (hash already computed for files).</summary>
public sealed record SnapshotEntry(
    string RelativePath,
    bool IsDirectory,
    long Size,
    DateTimeOffset Mtime,
    int Mode,
    string? Hash);

/// <summary>
/// Builds content-addressed directory tree objects from a flat entry list.
/// Each directory becomes a <see cref="TreeObject"/> whose id is the BLAKE3 of its
/// serialized bytes — so unchanged directories produce the same id and are reused.
/// </summary>
public static class TreeBuilder
{
    private sealed class Dir
    {
        public SortedDictionary<string, Dir> Dirs { get; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, SnapshotEntry> Files { get; } = new(StringComparer.Ordinal);
    }

    public static (string RootObjId, IReadOnlyDictionary<string, TreeObject> Objects) Build(IEnumerable<SnapshotEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var root = new Dir();

        foreach (SnapshotEntry e in entries)
        {
            string[] parts = e.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            Dir parent = root;
            int upto = e.IsDirectory ? parts.Length : parts.Length - 1;
            for (int i = 0; i < upto; i++)
                parent = GetOrAdd(parent, parts[i]);

            if (!e.IsDirectory)
                parent.Files[parts[^1]] = e;
        }

        var objects = new Dictionary<string, TreeObject>(StringComparer.Ordinal);
        string rootId = Serialize(root, objects);
        return (rootId, objects);
    }

    private static Dir GetOrAdd(Dir parent, string name)
    {
        if (!parent.Dirs.TryGetValue(name, out Dir? child))
        {
            child = new Dir();
            parent.Dirs[name] = child;
        }
        return child;
    }

    private static string Serialize(Dir dir, Dictionary<string, TreeObject> objects)
    {
        var entries = new List<TreeEntry>();
        foreach ((string name, Dir child) in dir.Dirs)
        {
            string childId = Serialize(child, objects);
            entries.Add(new TreeEntry(name, TreeEntryType.Dir, Child: childId));
        }
        foreach ((string name, SnapshotEntry f) in dir.Files)
            entries.Add(new TreeEntry(name, TreeEntryType.File, Size: f.Size, Mtime: f.Mtime, Mode: f.Mode, Hash: f.Hash));

        var tree = new TreeObject(entries);
        byte[] bytes = RepoJson.Serialize(tree);
        string objId = ContentHasher.ToHex(ContentHasher.Hash(bytes));
        objects[objId] = tree;
        return objId;
    }
}
