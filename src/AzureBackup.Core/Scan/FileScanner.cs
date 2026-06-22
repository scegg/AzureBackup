namespace AzureBackup.Core.Scan;

/// <summary>A scanned filesystem entry, path relative to the scan root ('/' separators).</summary>
public sealed record ScannedEntry(
    string RelativePath,
    bool IsDirectory,
    long Size,
    DateTimeOffset Mtime,
    bool IsSymlink);

/// <summary>
/// Recursively scans a source root, applying the exclude rules. Ignored directories
/// are pruned (so their contents are never visited). Empty directories are yielded so
/// they can be recreated on restore. Symlinks are reported (not followed).
/// </summary>
public sealed class FileScanner
{
    private readonly GitignoreMatcher _exclude;

    public FileScanner(GitignoreMatcher exclude)
    {
        _exclude = exclude ?? throw new ArgumentNullException(nameof(exclude));
    }

    public IEnumerable<ScannedEntry> Scan(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        string fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException($"source root not found: {fullRoot}");

        foreach (ScannedEntry e in Walk(fullRoot, fullRoot))
            yield return e;
    }

    private IEnumerable<ScannedEntry> Walk(string dir, string root)
    {
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(dir);
        }
        catch (UnauthorizedAccessException)
        {
            yield break; // skip unreadable directories
        }

        foreach (string path in children.OrderBy(p => p, StringComparer.Ordinal))
        {
            var info = new FileInfo(path);
            bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
            bool isSymlink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
            string rel = Rel(root, path);

            if (_exclude.IsIgnored(rel, isDir))
                continue;

            if (isDir && !isSymlink)
            {
                yield return new ScannedEntry(rel, true, 0, ToUtc(info.LastWriteTimeUtc), false);
                foreach (ScannedEntry e in Walk(path, root))
                    yield return e;
            }
            else
            {
                // File, or a symlink (to file or dir) — reported, not followed.
                long size = isSymlink ? 0 : info.Length;
                yield return new ScannedEntry(rel, false, size, ToUtc(info.LastWriteTimeUtc), isSymlink);
            }
        }
    }

    private static string Rel(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static DateTimeOffset ToUtc(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
