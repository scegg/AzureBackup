namespace AzureBackup.Core;

/// <summary>
/// Version of the on-blob repository format (manifests, index, pack layout,
/// crypto envelope). Writer (azbackup) and reader (azrestore) must agree on
/// this. Bump on any incompatible format change. See docs/format-spec.md.
/// </summary>
public static class FormatVersion
{
    public const int Current = 1;
}
