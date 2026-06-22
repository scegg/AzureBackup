using System.Diagnostics;

namespace AzureBackup.Core.Compression;

/// <summary>
/// Runs an external filter process (e.g. <c>xz</c>), pumping <paramref name="source"/>
/// into its stdin and its stdout into <paramref name="destination"/> concurrently
/// (so a full pipe never deadlocks). Throws if the process exits non-zero.
/// </summary>
internal static class ProcessFilter
{
    public static void Run(string fileName, string arguments, Stream source, Stream destination)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start '{fileName}'");

        // Drain stderr so the child never blocks writing diagnostics.
        string stderr = string.Empty;
        var stderrTask = Task.Run(() => stderr = process.StandardError.ReadToEnd());

        // Feed stdin on a worker; read stdout on this thread.
        var feedTask = Task.Run(() =>
        {
            using Stream stdin = process.StandardInput.BaseStream;
            source.CopyTo(stdin);
        });

        process.StandardOutput.BaseStream.CopyTo(destination);

        feedTask.GetAwaiter().GetResult();
        stderrTask.GetAwaiter().GetResult();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new IOException($"'{fileName} {arguments}' exited {process.ExitCode}: {stderr.Trim()}");
    }

    public static bool IsAvailable(string fileName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
