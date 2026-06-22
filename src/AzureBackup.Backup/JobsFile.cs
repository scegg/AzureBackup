using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AzureBackup.Backup;

/// <summary>One job in the jobs file (B = identity, C = optional overrides of env defaults).</summary>
public sealed class JobSpec
{
    public string? Name { get; set; }
    public string? Source { get; set; }
    public string? Container { get; set; }
    public string? ExcludeFile { get; set; }
    public string? NoCompressFile { get; set; }

    // per-job 凭据:引用环境变量名(密钥不入 jobs 文件);未设则回退全局 env。
    public string? ConnectionStringEnv { get; set; }
    public string? PasswordEnv { get; set; }

    // per-job 工作目录与即时通知(全局汇总通知仍独立保留)。
    public string? SpoolDir { get; set; }
    public string? WebhookUrl { get; set; }
    public string? WebhookKind { get; set; }
    public string? WebhookMethod { get; set; }
    public string? WebhookEvents { get; set; }

    public string? VolumeSize { get; set; }
    public string? GroupFileMax { get; set; }
    public string? PackTargetSize { get; set; }
    public int? PackMaxFiles { get; set; }
    public bool? ForceHash { get; set; }
    public int? RetentionCount { get; set; }
    public int? RetentionDays { get; set; }
    public string? RetentionMode { get; set; }
    public string? NoCompressExt { get; set; }
    public string? PackCompaction { get; set; }
    public string? DataTier { get; set; }
}

internal sealed class JobsDocument
{
    public List<JobSpec> Jobs { get; set; } = [];
}

internal static class JobsFile
{
    public static IReadOnlyList<JobSpec> Parse(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        JobsDocument doc = deserializer.Deserialize<JobsDocument>(File.ReadAllText(path)) ?? new JobsDocument();
        foreach (JobSpec j in doc.Jobs)
        {
            if (string.IsNullOrWhiteSpace(j.Source) || string.IsNullOrWhiteSpace(j.Container))
                throw new InvalidOperationException($"jobs file: job '{j.Name}' must have source and container");
        }
        return doc.Jobs;
    }
}
