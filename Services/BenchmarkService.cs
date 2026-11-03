using System.Diagnostics;
using System.Security.Cryptography;
using Deviso.Models;

namespace Deviso.Services;

public sealed class BenchmarkService
{
    private const int TestSizeBytes = 64 * 1024 * 1024;
    private const int BufferSizeBytes = 1024 * 1024;

    public async Task<string> RunAsync(PhysicalDisk disk, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(disk.VolumeRoot) || !Directory.Exists(disk.VolumeRoot))
        {
            throw new InvalidOperationException("This disk has no accessible mounted volume to benchmark.");
        }

        var drive = new DriveInfo(disk.VolumeRoot);
        if (!drive.IsReady || drive.AvailableFreeSpace < TestSizeBytes * 2L)
        {
            throw new InvalidOperationException("The selected volume needs at least 128 MB of free space.");
        }

        var temporaryRoot = Path.GetTempPath();
        var testDirectory = string.Equals(
            Path.GetPathRoot(temporaryRoot),
            Path.GetPathRoot(disk.VolumeRoot),
            StringComparison.OrdinalIgnoreCase)
            ? temporaryRoot
            : disk.VolumeRoot;

        return await Task.Run(() => RunCore(testDirectory, cancellationToken), cancellationToken);
    }

    private static string RunCore(string testDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(testDirectory, $".deviso-benchmark-{Guid.NewGuid():N}.tmp");
        var buffer = new byte[BufferSizeBytes];
        RandomNumberGenerator.Fill(buffer);

        try
        {
            var writeTimer = Stopwatch.StartNew();
            using (var stream = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSizeBytes, FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                for (var written = 0; written < TestSizeBytes; written += buffer.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stream.Write(buffer);
                }

                stream.Flush(flushToDisk: true);
            }

            writeTimer.Stop();

            var readTimer = Stopwatch.StartNew();
            using (var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSizeBytes, FileOptions.SequentialScan))
            {
                while (stream.Read(buffer, 0, buffer.Length) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            readTimer.Stop();
            var writeRate = TestSizeBytes / 1024d / 1024d / writeTimer.Elapsed.TotalSeconds;
            var readRate = TestSizeBytes / 1024d / 1024d / readTimer.Elapsed.TotalSeconds;
            return $"64 MB file test — write {writeRate:0.0} MB/s, read {readRate:0.0} MB/s. " +
                   "Results measure this mounted volume and are not a raw-device benchmark.";
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
