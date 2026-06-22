// azrestore entry point. Lists snapshots, then restores (or locally verifies) a chosen
// snapshot. Driven by environment variables for non-interactive use; the snapshot defaults
// to the latest when not specified. Config (except the password) comes from the repository.
using AzureBackup.Core.Restore;
using AzureBackup.Core.Storage;

try
{
    string password = Env("AZBACKUP_PASSWORD_FILE") is { Length: > 0 } pf
        ? File.ReadAllText(pf).Trim()
        : Env("AZBACKUP_PASSWORD") ?? throw new InvalidOperationException("AZBACKUP_PASSWORD or AZBACKUP_PASSWORD_FILE required");

    string connectionString = Env("AZURE_STORAGE_CONNECTION_STRING")
        ?? throw new InvalidOperationException("AZURE_STORAGE_CONNECTION_STRING required");
    string container = Env("AZURE_STORAGE_CONTAINER")
        ?? throw new InvalidOperationException("AZURE_STORAGE_CONTAINER required (container = one backup)");

    var store = AzureBlobStore.Create(connectionString, container);

    var snapshots = await RestoreRunner.ListSnapshotsAsync(store, password);
    if (snapshots.Count == 0)
    {
        Console.Error.WriteLine("no snapshots in this container");
        return 1;
    }

    Console.WriteLine($"=== {snapshots.Count} backup(s) in '{container}' ===");
    foreach (var s in snapshots)
        Console.WriteLine($"  {s.CreatedAtUtc:yyyy-MM-dd HH:mm:ss}Z  {s.Id}");

    string snapshotId = Env("AZRESTORE_SNAPSHOT") is { Length: > 0 } id && !id.Equals("latest", StringComparison.OrdinalIgnoreCase)
        ? id
        : snapshots[0].Id; // newest first

    Func<string, bool>? select = BuildSelect(Env("AZRESTORE_SELECT"));

    if (string.Equals(Env("AZRESTORE_VERIFY"), "local", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"local verify of {snapshotId} ...");
        var failures = await RestoreRunner.VerifyLocalAsync(store, password, snapshotId, select);
        if (failures.Count == 0) { Console.WriteLine("verify OK"); return 0; }
        foreach (string f in failures) Console.Error.WriteLine("FAIL " + f);
        return 4;
    }

    string target = Env("AZRESTORE_TARGET_PATH") ?? "/restore/target";
    RehydratePriority priority = string.Equals(Env("AZRESTORE_REHYDRATE_PRIORITY"), "High", StringComparison.OrdinalIgnoreCase)
        ? RehydratePriority.High : RehydratePriority.Standard;

    Console.WriteLine($"restoring {snapshotId} → {target} (rehydrate={priority}) ...");
    RestoreReport report = await RestoreRunner.RestoreAsync(
        store, password, snapshotId, target, select, requestRehydrate: true, priority);

    Console.WriteLine($"restored files={report.FilesRestored} dirs={report.DirectoriesCreated} verified={report.Verified} failures={report.Failures.Count}");
    foreach (string f in report.Failures) Console.Error.WriteLine("FAIL " + f);
    return report.Failures.Count == 0 ? 0 : 4;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"config error: {ex.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"restore failed: {ex.Message}");
    return 1;
}

static string? Env(string name) => Environment.GetEnvironmentVariable(name);

// AZRESTORE_SELECT: "all"/empty → everything; otherwise a path to a newline list of relative paths.
static Func<string, bool>? BuildSelect(string? value)
{
    if (string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase))
        return null;
    if (!File.Exists(value))
        throw new InvalidOperationException($"AZRESTORE_SELECT list file not found: {value}");
    var set = new HashSet<string>(
        File.ReadAllLines(value).Select(l => l.Trim().Replace('\\', '/')).Where(l => l.Length > 0),
        StringComparer.Ordinal);
    return path => set.Contains(path);
}
