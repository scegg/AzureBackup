using AzureBackup.Core.Compression;
using AzureBackup.Core.Scan;

namespace AzureBackup.Core.Pack;

/// <summary>
/// Decides whether a file is compressed (LZMA2) or stored (no compression).
/// Two sources, merged: an extension list (env) and a gitignore-style rules file.
/// A match on either → <see cref="CompressionCodec.Store"/>.
/// </summary>
public sealed class NoCompressPolicy
{
    private readonly HashSet<string> _extensions;
    private readonly GitignoreMatcher _rules;

    public NoCompressPolicy(IEnumerable<string>? extensions, GitignoreMatcher? rules)
    {
        _extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (extensions is not null)
            foreach (string e in extensions)
            {
                string ext = e.Trim().TrimStart('.');
                if (ext.Length > 0) _extensions.Add(ext);
            }
        _rules = rules ?? GitignoreMatcher.Empty;
    }

    public CompressionCodec CodecFor(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string ext = Path.GetExtension(relativePath).TrimStart('.');
        if (ext.Length > 0 && _extensions.Contains(ext))
            return CompressionCodec.Store;
        if (_rules.IsIgnored(relativePath, isDirectory: false))
            return CompressionCodec.Store;
        return CompressionCodec.Xz;
    }

    public static IEnumerable<string> ParseExtensionList(string csv)
        => (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
