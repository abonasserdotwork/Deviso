using System.Diagnostics;
using System.Text.RegularExpressions;
using Deviso.Models;

namespace Deviso.Services;

public sealed class SmartTestService
{
    private static readonly Regex TestStartedPattern = new(
        @"(please wait|test has begun|testing has begun|test will complete)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public async Task<string> StartAsync(
        PhysicalDisk disk,
        bool extended,
        CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(disk.DeviceId, out var deviceNumber) || deviceNumber < 0)
        {
            throw new InvalidOperationException("Windows did not provide a valid physical disk number.");
        }

        var smartctl = FindSmartctl()
            ?? throw new FileNotFoundException(
                "smartctl was not found. Install smartmontools and ensure smartctl.exe is available in PATH or its default installation folder.");
        var devicePath = $@"\\.\PhysicalDrive{deviceNumber}";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = smartctl,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-t");
        process.StartInfo.ArgumentList.Add(extended ? "long" : "short");
        process.StartInfo.ArgumentList.Add(devicePath);

        if (!process.Start())
        {
            throw new InvalidOperationException("smartctl could not be started.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            if (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("smartctl did not respond within 30 seconds.");
            }

            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        var combinedOutput = string.Join(Environment.NewLine, new[] { output.Trim(), error.Trim() }
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        if (process.ExitCode != 0 && !TestStartedPattern.IsMatch(combinedOutput))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(combinedOutput)
                ? $"smartctl failed with exit code {process.ExitCode}. Run Deviso as administrator if the device requires elevated access."
                : combinedOutput);
        }

        if (!TestStartedPattern.IsMatch(combinedOutput))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(combinedOutput)
                    ? "smartctl did not confirm that the self-test started."
                    : combinedOutput);
        }

        return combinedOutput;
    }

    private static string? FindSmartctl()
    {
        var executable = OperatingSystem.IsWindows() ? "smartctl.exe" : "smartctl";
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var onPath = path.Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, executable))
            .FirstOrDefault(File.Exists);
        if (onPath is not null)
        {
            return onPath;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "smartmontools", "bin", "smartctl.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "smartmontools", "bin", "smartctl.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
