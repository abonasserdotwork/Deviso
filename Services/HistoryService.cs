using System.Text.Json;
using Deviso.Models;

namespace Deviso.Services;

public sealed class HistoryService
{
    private readonly string _historyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Deviso", "drive-history.jsonl");

    public async Task AppendAsync(
        IEnumerable<PhysicalDisk> disks,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_historyPath)
            ?? throw new InvalidOperationException("Could not determine the Deviso data directory.");
        Directory.CreateDirectory(directory);

        await using var stream = new FileStream(
            _historyPath, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.Asynchronous);
        await using var writer = new StreamWriter(stream);
        foreach (var disk in disks)
        {
            var entry = new
            {
                Timestamp = DateTimeOffset.UtcNow,
                disk.DeviceId,
                disk.Name,
                disk.Health,
                disk.Temperature,
                disk.PowerOnHours,
                disk.Wear,
                disk.LifeRemainingPercentage,
                disk.LifeRemainingSource,
                disk.HealthSource,
                disk.SmartDataSource
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(entry).AsMemory(), cancellationToken);
        }
    }

    public async Task<IReadOnlyList<TemperatureReading>> ReadTemperatureHistoryAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        var readings = new List<TemperatureReading>();
        await using var stream = new FileStream(
            _historyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        var lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("DeviceId", out var id) ||
                !string.Equals(id.GetString(), deviceId, StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("Temperature", out var temperature) ||
                temperature.ValueKind != JsonValueKind.Number ||
                !root.TryGetProperty("Timestamp", out var timestamp))
            {
                continue;
            }

            if (!timestamp.TryGetDateTimeOffset(out var recordedAt))
            {
                throw new InvalidDataException(
                    $"Invalid timestamp in drive history at line {lineNumber}.");
            }

            readings.Add(new TemperatureReading(recordedAt, temperature.GetInt32()));
        }

        return readings.OrderBy(reading => reading.Timestamp).TakeLast(60).ToArray();
    }

    public string HistoryPath => _historyPath;
}

public sealed record TemperatureReading(DateTimeOffset Timestamp, int Temperature);
