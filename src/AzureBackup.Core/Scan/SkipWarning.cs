namespace AzureBackup.Core.Scan;

/// <summary>为什么一个文件/目录被跳过。Missing 静默;Unreadable 进报告。</summary>
public enum SkipReason
{
    /// <summary>文件/目录已删除(FileNotFound/DirectoryNotFound)——静默跳过。</summary>
    Missing,
    /// <summary>打不开(权限/锁定/IO)——跳过但报警告。</summary>
    Unreadable,
}

/// <summary>一条跳过警告(仅 Unreadable 产生),用于最终报告。</summary>
public sealed record SkipWarning(string Path, SkipReason Reason, string? Detail);
