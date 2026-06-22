namespace AzureBackup.Core.Repo;

/// <summary>Blob name layout within a repository container (see docs/format-spec.md §1).</summary>
public static class RepoLayout
{
    public const string Config = "config";
    public const string Lock = "lock";
    public const string SnapshotsRef = "refs/snapshots";
    public const string IndexRef = "refs/index";

    public const string RootPrefix = "root/";
    public const string StructPrefix = "struct/";
    public const string PacksPrefix = "packs/";

    /// <summary>Consolidated global index blob (v1: single, chunk-uploaded; sharding is a future optimization).</summary>
    public const string IndexBlob = StructPrefix + "index";

    public static string Root(string snapshotId) => RootPrefix + snapshotId;
    public static string Struct(string objId) => StructPrefix + objId;
    public static string Volume(string packId, int index) => $"{PacksPrefix}{packId}.{index}";

    public static bool IsStructured(string name) =>
        name == Config || name == Lock || name == SnapshotsRef || name == IndexRef
        || name.StartsWith(RootPrefix, StringComparison.Ordinal)
        || name.StartsWith(StructPrefix, StringComparison.Ordinal);
}
