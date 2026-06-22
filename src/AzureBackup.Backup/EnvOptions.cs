using System.Globalization;
using AzureBackup.Core.Backup;
using AzureBackup.Core.Pack;
using AzureBackup.Core.Repo;
using AzureBackup.Core.Scan;

namespace AzureBackup.Backup;

/// <summary>Builds <see cref="BackupOptions"/> and connection settings from environment variables.</summary>
internal static class EnvOptions
{
    public static string? Get(string name) => Environment.GetEnvironmentVariable(name);

    public static string ResolvePassword()
    {
        string? file = Get("AZBACKUP_PASSWORD_FILE");
        if (!string.IsNullOrEmpty(file))
            return File.ReadAllText(file).Trim();
        string? pw = Get("AZBACKUP_PASSWORD");
        if (string.IsNullOrEmpty(pw))
            throw new InvalidOperationException("AZBACKUP_PASSWORD or AZBACKUP_PASSWORD_FILE is required");
        return pw;
    }

    public static (string ConnectionString, string Container) ResolveAzure()
    {
        string cs = Get("AZURE_STORAGE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("AZURE_STORAGE_CONNECTION_STRING is required");
        string container = Get("AZURE_STORAGE_CONTAINER")
            ?? throw new InvalidOperationException("AZURE_STORAGE_CONTAINER is required");
        return (cs, container);
    }

    public static BackupOptions BuildOptions()
    {
        string source = Get("AZBACKUP_SOURCE_PATH") ?? "/backup/source";
        string spool = Get("AZBACKUP_SPOOL_DIR") ?? Path.Combine(Path.GetTempPath(), "azbackup-spool");

        GitignoreMatcher exclude = LoadRules(Get("AZBACKUP_EXCLUDE_FILE"));
        var noCompress = new NoCompressPolicy(
            NoCompressPolicy.ParseExtensionList(Get("AZBACKUP_NOCOMPRESS_EXT") ?? DefaultNoCompressExt),
            LoadRules(Get("AZBACKUP_NOCOMPRESS_FILE")));

        return new BackupOptions
        {
            SourcePath = source,
            WorkDir = spool,
            VolumeSizeBytes = ParseSize(Get("AZBACKUP_VOLUME_SIZE"), 100L * 1024 * 1024),
            GroupFileMax = ParseSize(Get("AZBACKUP_GROUP_FILE_MAX"), 1L * 1024 * 1024),
            PackTargetSize = ParseSize(Get("AZBACKUP_PACK_TARGET_SIZE"), 256L * 1024 * 1024),
            PackMaxFiles = ParseInt(Get("AZBACKUP_PACK_MAX_FILES"), 4096),
            Exclude = exclude,
            NoCompress = noCompress,
            ForceHash = ParseBool(Get("AZBACKUP_FORCE_HASH"), false),
            RetentionCount = ParseNullableInt(Get("AZBACKUP_RETENTION_COUNT")),
            RetentionDays = ParseNullableInt(Get("AZBACKUP_RETENTION_DAYS")),
            RetentionMode = string.Equals(Get("AZBACKUP_RETENTION_MODE"), "or", StringComparison.OrdinalIgnoreCase)
                ? RetentionMode.Or : RetentionMode.And,
            CompactionThreshold = GarbageCollector.ParseThreshold(Get("AZBACKUP_PACK_COMPACTION") ?? "30%"),
            RunGc = !string.Equals(Get("AZBACKUP_GC_MODE"), "off", StringComparison.OrdinalIgnoreCase),
            DryRun = ParseBool(Get("AZBACKUP_DRY_RUN"), false),
        };
    }

    private const string DefaultNoCompressExt = "7z,rar,zip,gz,xz,bz2,mp4,mkv,avi,mov,jpg,jpeg,png,gif,webp,mp3,flac,aac";

    private static GitignoreMatcher LoadRules(string? path)
        => string.IsNullOrEmpty(path) || !File.Exists(path)
            ? GitignoreMatcher.Empty
            : GitignoreMatcher.Parse(File.ReadAllLines(path));

    public static long ParseSize(string? value, long fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        value = value.Trim();
        (string suffix, long unit)[] units = [("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10), ("B", 1)];
        foreach ((string suffix, long unit) in units)
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return (long)(double.Parse(value[..^suffix.Length].Trim(), CultureInfo.InvariantCulture) * unit);
        return long.Parse(value, CultureInfo.InvariantCulture);
    }

    private static int ParseInt(string? v, int fallback) => int.TryParse(v, out int n) ? n : fallback;
    private static int? ParseNullableInt(string? v) => int.TryParse(v, out int n) ? n : null;
    private static bool ParseBool(string? v, bool fallback)
        => bool.TryParse(v, out bool b) ? b : (v == "1" || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase) || fallback && v is null);
}
