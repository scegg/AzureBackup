// azbackup entry point (single-job). Reads configuration from environment variables,
// runs one backup, prints a report. Jobs-file / cron / webhook orchestration is layered on next.
using AzureBackup.Backup;
using AzureBackup.Core.Backup;
using AzureBackup.Core.Storage;

try
{
    string password = EnvOptions.ResolvePassword();
    (string connectionString, string container) = EnvOptions.ResolveAzure();
    BackupOptions options = EnvOptions.BuildOptions();

    var store = AzureBlobStore.Create(connectionString, container);

    Console.WriteLine($"azbackup: source={options.SourcePath} container={container} dryRun={options.DryRun}");
    BackupReport report = await BackupRunner.RunAsync(store, password, options);

    if (report.Skipped)
    {
        Console.WriteLine("azbackup: skipped — a previous run is still in progress (lock held)");
        return 3;
    }

    Console.WriteLine(Report(report));
    return 0;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"config error: {ex.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"backup failed: {ex.Message}");
    return 1;
}

static string Report(BackupReport r) => string.Join('\n',
[
    "=== backup report ===",
    $"snapshot          : {r.SnapshotId ?? "(dry-run)"}",
    $"files / dirs      : {r.Files} / {r.Directories}",
    $"new or modified   : {r.NewOrModified}",
    $"unchanged         : {r.Unchanged}",
    $"packs created     : {r.PacksCreated}",
    $"volumes uploaded  : {r.VolumesUploaded}",
    $"uploaded bytes    : {r.UploadedBytes}",
    $"snapshots deleted : {r.SnapshotsDeleted}",
    $"packs deleted     : {r.PacksDeleted}",
    $"compaction-eligible: {r.PacksEligibleForCompaction}",
]);
