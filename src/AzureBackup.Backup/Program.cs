// azbackup entry point. Single-job or multi-job (jobs file), optional cron loop, webhook
// notification. Jobs run sequentially; one job's failure does not block the others.
using System.Text;
using AzureBackup.Backup;
using AzureBackup.Core.Backup;
using AzureBackup.Core.Notifications;
using AzureBackup.Core.Storage;
using NCrontab;

using var http = new HttpClient();

try
{
    // 凭据 per-job 解析(见 RunBatchAsync);此处只构建全局汇总通知。
    WebhookConfig? webhook = EnvOptions.BuildWebhook();

    string? jobsFile = EnvOptions.Get("AZBACKUP_JOBS_FILE");
    IReadOnlyList<JobSpec?> jobs = string.IsNullOrEmpty(jobsFile)
        ? [null]                                   // single-job from env
        : [.. JobsFile.Parse(jobsFile)];

    string? cron = EnvOptions.Cron();
    if (string.IsNullOrEmpty(cron))
        return await RunBatchAsync(jobs, webhook, http);

    CrontabSchedule schedule = CrontabSchedule.Parse(cron);
    Console.WriteLine($"azbackup: cron '{cron}' — waiting for scheduled runs (Ctrl+C to stop)");
    while (true)
    {
        DateTime next = schedule.GetNextOccurrence(DateTime.Now);
        TimeSpan wait = next - DateTime.Now;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        Console.WriteLine($"--- run @ {DateTime.Now:u} ---");
        await RunBatchAsync(jobs, webhook, http);
    }
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"config error: {ex.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"fatal: {ex.Message}");
    return 1;
}

static async Task<int> RunBatchAsync(IReadOnlyList<JobSpec?> jobs, WebhookConfig? webhook, HttpClient http)
{
    var summary = new StringBuilder();
    bool anyFailure = false;

    foreach (JobSpec? job in jobs)
    {
        string name = job?.Name ?? job?.Container ?? "default";
        bool jobFailed = false;
        string line;
        try
        {
            string connectionString = EnvOptions.ResolveConnectionString(job);
            string password = EnvOptions.ResolvePassword(job);
            string container = EnvOptions.ResolveContainer(job);
            BackupOptions options = EnvOptions.BuildOptions(job);
            var store = AzureBlobStore.Create(connectionString, container);

            BackupReport r = await BackupRunner.RunAsync(store, password, options);
            line = r.Skipped
                ? $"[{name}] skipped (lock held)"
                : $"[{name}] snapshot={r.SnapshotId} new/mod={r.NewOrModified} packs={r.PacksCreated} vols={r.VolumesUploaded} bytes={r.UploadedBytes} delSnaps={r.SnapshotsDeleted} delPacks={r.PacksDeleted} compacted={r.PacksCompacted}";
            Console.WriteLine(line);
        }
        catch (Exception ex)
        {
            jobFailed = true;
            anyFailure = true;
            line = $"[{name}] FAILED: {ex.Message}";
            Console.Error.WriteLine(line);
        }
        summary.AppendLine(line);

        // per-job 即时通知(job 设了 webhookUrl 才发)。
        WebhookConfig? jobHook = EnvOptions.BuildJobWebhook(job);
        if (jobHook is not null && WebhookNotifier.ShouldFire(jobHook.Events, success: !jobFailed))
        {
            string title = $"AzureBackup [{name}] {(jobFailed ? "FAILED" : "OK")}";
            try { await WebhookNotifier.SendAsync(http, jobHook, title, line); }
            catch (Exception ex) { Console.Error.WriteLine($"[{name}] webhook failed: {ex.Message}"); }
        }
    }

    if (webhook is not null && WebhookNotifier.ShouldFire(webhook.Events, success: !anyFailure))
    {
        string title = $"AzureBackup {(anyFailure ? "FAILED" : "OK")}";
        try { await WebhookNotifier.SendAsync(http, webhook, title, summary.ToString().Trim()); }
        catch (Exception ex) { Console.Error.WriteLine($"webhook failed: {ex.Message}"); }
    }

    return anyFailure ? 1 : 0;
}
