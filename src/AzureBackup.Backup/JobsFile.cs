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
