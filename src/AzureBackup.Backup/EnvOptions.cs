using System.Globalization;
using AzureBackup.Core.Backup;
using AzureBackup.Core.Notifications;
using AzureBackup.Core.Pack;
using AzureBackup.Core.Repo;
using AzureBackup.Core.Scan;

namespace AzureBackup.Backup;

/// <summary>Builds settings from environment variables, merging per-job overrides.</summary>
internal static class EnvOptions
{
    private const string DefaultNoCompressExt =
        "7z,rar,zip,gz,xz,bz2,mp4,mkv,avi,mov,jpg,jpeg,png,gif,webp,mp3,flac,aac";

    public static string? Get(string name) => Environment.GetEnvironmentVariable(name);

    public static string ResolvePassword()
    {
        string? file = Get("AZBACKUP_PASSWORD_FILE");
        if (!string.IsNullOrEmpty(file))
            return File.ReadAllText(file).Trim();
        return Get("AZBACKUP_PASSWORD")
            ?? throw new InvalidOperationException("AZBACKUP_PASSWORD or AZBACKUP_PASSWORD_FILE is required");
    }

    public static string ResolveConnectionString()
        => Get("AZURE_STORAGE_CONNECTION_STRING")
           ?? throw new InvalidOperationException("AZURE_STORAGE_CONNECTION_STRING is required");

    public static string ResolveContainer(JobSpec? job)
        => job?.Container ?? Get("AZURE_STORAGE_CONTAINER")
           ?? throw new InvalidOperationException("AZURE_STORAGE_CONTAINER (or job.container) is required");

    public static string? Cron() => Get("AZBACKUP_CRON");

    /// <summary>Builds backup options for a job (null = single-job from env only).</summary>
    public static BackupOptions BuildOptions(JobSpec? job)
    {
        string source = job?.Source ?? Get("AZBACKUP_SOURCE_PATH") ?? "/backup/source";
        string spool = Get("AZBACKUP_SPOOL_DIR") ?? Path.Combine(Path.GetTempPath(), "azbackup-spool");

        GitignoreMatcher exclude = LoadRules(job?.ExcludeFile ?? Get("AZBACKUP_EXCLUDE_FILE"));
        var noCompress = new NoCompressPolicy(
            NoCompressPolicy.ParseExtensionList(job?.NoCompressExt ?? Get("AZBACKUP_NOCOMPRESS_EXT") ?? DefaultNoCompressExt),
            LoadRules(job?.NoCompressFile ?? Get("AZBACKUP_NOCOMPRESS_FILE")));

        string? modeStr = job?.RetentionMode ?? Get("AZBACKUP_RETENTION_MODE");

        return new BackupOptions
        {
            SourcePath = source,
            WorkDir = spool,
            VolumeSizeBytes = ParseSize(job?.VolumeSize ?? Get("AZBACKUP_VOLUME_SIZE"), 100L * 1024 * 1024),
            GroupFileMax = ParseSize(job?.GroupFileMax ?? Get("AZBACKUP_GROUP_FILE_MAX"), 1L * 1024 * 1024),
            PackTargetSize = ParseSize(job?.PackTargetSize ?? Get("AZBACKUP_PACK_TARGET_SIZE"), 256L * 1024 * 1024),
            PackMaxFiles = job?.PackMaxFiles ?? ParseInt(Get("AZBACKUP_PACK_MAX_FILES"), 4096),
            Exclude = exclude,
            NoCompress = noCompress,
            ForceHash = job?.ForceHash ?? ParseBool(Get("AZBACKUP_FORCE_HASH")),
            RetentionCount = job?.RetentionCount ?? ParseNullableInt(Get("AZBACKUP_RETENTION_COUNT")),
            RetentionDays = job?.RetentionDays ?? ParseNullableInt(Get("AZBACKUP_RETENTION_DAYS")),
            RetentionMode = string.Equals(modeStr, "or", StringComparison.OrdinalIgnoreCase) ? RetentionMode.Or : RetentionMode.And,
            CompactionThreshold = GarbageCollector.ParseThreshold(job?.PackCompaction ?? Get("AZBACKUP_PACK_COMPACTION") ?? "30%"),
            RunGc = !string.Equals(Get("AZBACKUP_GC_MODE"), "off", StringComparison.OrdinalIgnoreCase),
            DryRun = ParseBool(Get("AZBACKUP_DRY_RUN")),
        };
    }

    public static WebhookConfig? BuildWebhook()
    {
        string? url = Get("AZBACKUP_WEBHOOK_URL");
        if (string.IsNullOrEmpty(url)) return null;
        WebhookKind kind = string.Equals(Get("AZBACKUP_WEBHOOK_KIND"), "generic", StringComparison.OrdinalIgnoreCase)
            ? WebhookKind.Generic : WebhookKind.Bark;
        WebhookMethod method = string.Equals(Get("AZBACKUP_WEBHOOK_METHOD"), "GET", StringComparison.OrdinalIgnoreCase)
            ? WebhookMethod.Get : WebhookMethod.Post;
        WebhookEvents events = (Get("AZBACKUP_WEBHOOK_EVENTS")?.ToLowerInvariant()) switch
        {
            "success" => WebhookEvents.Success,
            "both" => WebhookEvents.Both,
            _ => WebhookEvents.Error,
        };
        return new WebhookConfig(url, kind, method, events);
    }

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
    private static bool ParseBool(string? v)
        => bool.TryParse(v, out bool b) ? b : v is "1" or "yes" or "YES";
}
