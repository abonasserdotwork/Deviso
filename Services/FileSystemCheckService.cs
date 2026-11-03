using System.Diagnostics;
using Deviso.Models;

namespace Deviso.Services;

public sealed class FileSystemCheckService
{
    public async Task<string> RunAsync(PhysicalDisk disk, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(disk.VolumeRoot) || disk.VolumeRoot.Length < 2)
        {
            throw new InvalidOperationException("This disk has no mounted drive letter to check.");
        }

        var driveLetter = disk.VolumeRoot[..1];
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "chkdsk.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add($"{driveLetter}:");

        if (!process.Start())
        {
            throw new InvalidOperationException("Windows could not start the filesystem check.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
        }

        return string.IsNullOrWhiteSpace(output) ? "Filesystem check completed." : output.Trim();
    }
}
