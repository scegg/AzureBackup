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

    public IEnumerable<ScannedEntry> Scan(string root, ICollection<SkipWarning>? warnings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        string fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException($"source root not found: {fullRoot}");

        foreach (ScannedEntry e in Walk(fullRoot, fullRoot, warnings))
            yield return e;
    }

    private IEnumerable<ScannedEntry> Walk(string dir, string root, ICollection<SkipWarning>? warnings)
    {
        IEnumerable<string> children;
        try
        {
            // 立即物化,把枚举期异常在此捕获(而非延迟到 foreach)。
            children = Directory.EnumerateFileSystemEntries(dir).ToList();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            yield break; // Missing:静默跳过该子树
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            warnings?.Add(new SkipWarning(Rel(root, dir), SkipReason.Unreadable, ex.Message));
            yield break;
        }

        foreach (string path in children.OrderBy(p => p, StringComparer.Ordinal))
        {
            ScannedEntry? entry;
            bool isDir;
            try
            {
                var info = new FileInfo(path);
                isDir = (info.Attributes & FileAttributes.Directory) != 0;
                bool isSymlink = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                string rel = Rel(root, path);

                if (_exclude.IsIgnored(rel, isDir)) continue;

                if (isDir && !isSymlink)
                    entry = new ScannedEntry(rel, true, 0, ToUtc(info.LastWriteTimeUtc), false);
                else
                {
                    long size = isSymlink ? 0 : info.Length;
                    entry = new ScannedEntry(rel, false, size, ToUtc(info.LastWriteTimeUtc), isSymlink);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue; // Missing:该项消失,静默跳过
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                warnings?.Add(new SkipWarning(Rel(root, path), SkipReason.Unreadable, ex.Message));
                continue;
            }

            yield return entry;
            if (entry.IsDirectory)
                foreach (ScannedEntry e in Walk(path, root, warnings))
                    yield return e;
        }
    }

    private static string Rel(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static DateTimeOffset ToUtc(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
