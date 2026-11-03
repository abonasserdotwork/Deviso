using System.Diagnostics;
using System.Text.Json;
using Deviso.Models;

namespace Deviso.Services;

public sealed class SmartDataService
{
    public async Task<PhysicalDisk> EnrichAsync(
        PhysicalDisk disk,
        CancellationToken cancellationToken = default)
    {
        var executable = FindSmartctl();
        if (executable is null || !int.TryParse(disk.DeviceId, out var deviceNumber))
        {
            return disk with
            {
                LifeRemainingPercentage = ReadWindowsLifeRemaining(disk),
                SmartDataSource = executable is null
                    ? "Windows reliability counters · smartmontools not installed"
                    : "Windows reliability counters · invalid physical disk number"
            };
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-a");
        process.StartInfo.ArgumentList.Add("-j");
        process.StartInfo.ArgumentList.Add($"/dev/pd{deviceNumber}");

        try
        {
            if (!process.Start())
            {
                return disk with
                {
                    LifeRemainingPercentage = ReadWindowsLifeRemaining(disk),
                    SmartDataSource = "smartctl could not start; using Windows data"
                };
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return disk with
            {
                LifeRemainingPercentage = ReadWindowsLifeRemaining(disk),
                SmartDataSource = $"smartctl could not start ({exception.Message}); using Windows data"
            };
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return disk with
            {
                LifeRemainingPercentage = ReadWindowsLifeRemaining(disk),
                SmartDataSource = "smartctl timed out; using Windows data"
            };
        }

        var output = await outputTask;
        var error = await errorTask;
        if (string.IsNullOrWhiteSpace(output))
        {
            return disk with
            {
                LifeRemainingPercentage = ReadWindowsLifeRemaining(disk),
                SmartDataSource = string.IsNullOrWhiteSpace(error)
                    ? $"smartctl returned no data (exit {process.ExitCode}); using Windows data"
                    : $"smartctl could not read this drive; using Windows data: {error.Trim()}"
            };
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var smartAvailable = root.TryGetProperty("smart_support", out var smartSupport) &&
                                 smartSupport.TryGetProperty("available", out var available) &&
                                 available.ValueKind == JsonValueKind.True;
            if (!smartAvailable)
            {
                return disk with
                {
                    LifeRemainingPercentage = ReadWindowsLifeRemaining(disk),
                    SmartDataSource = "SMART is unsupported/disabled; using Windows data"
                };
            }

            var temperature = ReadTemperature(root);
            var powerOnHours = ReadPowerOnHours(root);
            var lifeRemaining = ReadLifeRemaining(root);
            var health = ReadSmartHealth(root);
            var wear = ReadWearUsed(root);
            return disk with
            {
                Temperature = temperature ?? disk.Temperature,
                PowerOnHours = powerOnHours ?? disk.PowerOnHours,
                Wear = wear ?? disk.Wear,
                LifeRemainingPercentage = lifeRemaining ?? ReadWindowsLifeRemaining(disk),
                LifeRemainingSource = lifeRemaining is null
                    ? disk.LifeRemainingSource
                    : ReadLifeRemainingSource(root),
                Health = health ?? disk.Health,
                HealthSource = health is null ? disk.HealthSource : "SMART overall assessment",
                SmartDataSource = "smartctl SMART data"
            };
        }
        catch (JsonException)
        {
            return disk with
            {
                LifeRemainingPercentage = ReadWindowsLifeRemaining(disk),
                SmartDataSource = $"smartctl returned invalid JSON (exit {process.ExitCode}); using Windows data"
            };
        }
    }

    private static int? ReadWindowsLifeRemaining(PhysicalDisk disk) =>
        disk.Wear is >= 0 and <= 100 ? 100 - disk.Wear : null;

    private static int? ReadWearUsed(JsonElement root)
    {
        if (root.TryGetProperty("nvme_smart_health_information_log", out var nvme) &&
            nvme.TryGetProperty("percentage_used", out var percentageUsed) &&
            percentageUsed.ValueKind == JsonValueKind.Number &&
            percentageUsed.TryGetInt32(out var used))
        {
            return Math.Clamp(used, 0, 100);
        }

        if (root.TryGetProperty("ata_smart_attributes", out var attributes) &&
            attributes.TryGetProperty("table", out var table) &&
            table.ValueKind == JsonValueKind.Array)
        {
            foreach (var attribute in table.EnumerateArray())
            {
                if (ReadAttributeName(attribute) is (
                    "Percent_Lifetime_Remain" or "SSD_Life_Left" or "Media_Wearout_Indicator") &&
                    attribute.TryGetProperty("value", out var normalized) &&
                    normalized.TryGetInt32(out var remaining) &&
                    remaining is >= 0 and <= 100)
                {
                    if (ReadE100LifeRemaining(root, attribute) is { } e100Remaining)
                    {
                        return 100 - e100Remaining;
                    }

                    return 100 - remaining;
                }
            }
        }

        return null;
    }

    private static int? ReadE100LifeRemaining(JsonElement root, JsonElement attribute)
    {
        if (!root.TryGetProperty("model_name", out var model) ||
            !string.Equals(model.GetString(), "HS-SSD-E100 128G", StringComparison.OrdinalIgnoreCase) ||
            ReadAttributeName(attribute) != "Media_Wearout_Indicator" ||
            !attribute.TryGetProperty("raw", out var raw) ||
            !raw.TryGetProperty("value", out var rawValue) ||
            !rawValue.TryGetInt64(out var rawLifeRemaining) ||
            rawLifeRemaining is < 0 or > 100_000)
        {
            return null;
        }

        return (int)Math.Round(rawLifeRemaining / 1_000d, MidpointRounding.AwayFromZero);
    }

    private static string? ReadLifeRemainingSource(JsonElement root)
    {
        if (root.TryGetProperty("nvme_smart_health_information_log", out var nvme) &&
            nvme.TryGetProperty("percentage_used", out var percentageUsed) &&
            percentageUsed.ValueKind == JsonValueKind.Number &&
            percentageUsed.TryGetInt32(out _))
        {
            return "NVMe SMART (100% − percentage used)";
        }

        if (root.TryGetProperty("model_name", out var model) &&
            string.Equals(model.GetString(), "HS-SSD-E100 128G", StringComparison.OrdinalIgnoreCase))
        {
            return "SMART 233 vendor raw ÷ 1,000";
        }

        return "SMART SSD lifetime attribute";
    }

    private static int? ReadTemperature(JsonElement root)
    {
        if (root.TryGetProperty("temperature", out var temperature) &&
            temperature.TryGetProperty("current", out var current) &&
            current.TryGetInt32(out var value) && value is > 0 and < 150)
        {
            return value;
        }

        if (!root.TryGetProperty("ata_smart_attributes", out var attributes) ||
            !attributes.TryGetProperty("table", out var table) ||
            table.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var attribute in table.EnumerateArray())
        {
            var name = ReadAttributeName(attribute);
            if (name is not ("Temperature_Celsius" or "Airflow_Temperature_Cel" or
                "Temperature_Internal" or "Current_Temperature"))
            {
                continue;
            }

            if (attribute.TryGetProperty("raw", out var raw) &&
                raw.TryGetProperty("string", out var rawString) &&
                rawString.GetString() is { } displayValue &&
                int.TryParse(displayValue.Split(' ', '(', StringSplitOptions.RemoveEmptyEntries)[0],
                    out var displayedTemperature) &&
                displayedTemperature is > 0 and < 150)
            {
                return displayedTemperature;
            }

            if (attribute.TryGetProperty("raw", out raw) &&
                raw.TryGetProperty("value", out var rawValue) &&
                rawValue.TryGetInt64(out var rawTemperature) &&
                rawTemperature is > 0 and < 150)
            {
                return (int)rawTemperature;
            }
        }

        return null;
    }

    private static long? ReadPowerOnHours(JsonElement root)
    {
        if (root.TryGetProperty("power_on_time", out var powerOn) &&
            powerOn.TryGetProperty("hours", out var hours) &&
            hours.TryGetInt64(out var result) && result >= 0)
        {
            return result;
        }

        if (!root.TryGetProperty("ata_smart_attributes", out var attributes) ||
            !attributes.TryGetProperty("table", out var table) ||
            table.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var attribute in table.EnumerateArray())
        {
            if (ReadAttributeName(attribute) is not ("Power_On_Hours" or "Power_On_Hours_and_Msec"))
            {
                continue;
            }

            if (attribute.TryGetProperty("raw", out var raw) &&
                raw.TryGetProperty("value", out var rawValue) &&
                rawValue.TryGetInt64(out var rawHours) && rawHours >= 0)
            {
                return rawHours;
            }
        }

        return null;
    }

    private static int? ReadLifeRemaining(JsonElement root)
    {
        if (root.TryGetProperty("nvme_smart_health_information_log", out var nvme) &&
            nvme.TryGetProperty("percentage_used", out var used) &&
            used.TryGetInt32(out var percentageUsed) && percentageUsed >= 0)
        {
            return Math.Clamp(100 - percentageUsed, 0, 100);
        }

        if (!root.TryGetProperty("ata_smart_attributes", out var attributes) ||
            !attributes.TryGetProperty("table", out var table) ||
            table.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var attribute in table.EnumerateArray())
        {
            if (ReadAttributeName(attribute) is not (
                "Percent_Lifetime_Remain" or "SSD_Life_Left" or "Media_Wearout_Indicator"))
            {
                continue;
            }

            if (ReadE100LifeRemaining(root, attribute) is { } e100Remaining)
            {
                return e100Remaining;
            }

            if (attribute.TryGetProperty("value", out var normalized) &&
                normalized.TryGetInt32(out var percentage) &&
                percentage is >= 0 and <= 100)
            {
                return percentage;
            }
        }

        return null;
    }

    private static string? ReadSmartHealth(JsonElement root)
    {
        if (root.TryGetProperty("smart_status", out var status) &&
            status.TryGetProperty("passed", out var passed) &&
            passed.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return passed.GetBoolean() ? "Healthy" : "Warning";
        }

        return null;
    }

    private static string ReadAttributeName(JsonElement attribute) =>
        attribute.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";

    private static string? FindSmartctl()
    {
        var executable = OperatingSystem.IsWindows() ? "smartctl.exe" : "smartctl";
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, executable))
            .Append(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "smartmontools", "bin", "smartctl.exe"))
            .Append(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "smartmontools", "bin", "smartctl.exe"))
            .FirstOrDefault(File.Exists);
    }
}
