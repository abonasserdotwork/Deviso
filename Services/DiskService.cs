using System.Text.Json;
using System.Security.Principal;
using Deviso.Models;

namespace Deviso.Services;

public sealed class DiskService(PowerShellRunner powerShellRunner)
{
    private readonly SmartDataService _smartDataService = new();
    private const string InventoryScript = """
        $ErrorActionPreference = 'Stop'
        if (-not (Get-Command Get-PhysicalDisk -ErrorAction SilentlyContinue)) {
            throw 'The Windows Storage PowerShell module is unavailable.'
        }
        $disks = @(Get-PhysicalDisk -ErrorAction Stop)
        $result = foreach ($disk in $disks) {
            $reliabilityError = $null
            $counter = $null
            try {
                $counter = Get-StorageReliabilityCounter -PhysicalDisk $disk -ErrorAction Stop
            } catch {
                $reliabilityError = $_.Exception.Message
            }
            $volumeRoot = $null
            try {
                $letter = Get-Partition -DiskNumber ([int]$disk.DeviceId) -ErrorAction Stop |
                    Get-Volume -ErrorAction Stop |
                    Where-Object { $_.DriveLetter } |
                    Select-Object -First 1 -ExpandProperty DriveLetter
                if ($letter) { $volumeRoot = "$letter`:\" }
            } catch {
                $volumeRoot = $null
            }
            [pscustomobject]@{
                Name = [string]$disk.FriendlyName
                DeviceId = [string]$disk.DeviceId
                BusType = [string]$disk.BusType
                MediaType = [string]$disk.MediaType
                Size = [long]$disk.Size
                Health = [string]$disk.HealthStatus
                OperationalStatus = (@($disk.OperationalStatus) -join ', ')
                Temperature = if ($null -ne $counter.Temperature) { [int]$counter.Temperature } else { $null }
                TemperatureMax = if ($null -ne $counter.TemperatureMax) { [int]$counter.TemperatureMax } else { $null }
                PowerOnHours = if ($null -ne $counter.PowerOnHours) { [long]$counter.PowerOnHours } else { $null }
                Wear = if ($null -ne $counter.Wear) { [int]$counter.Wear } else { $null }
                ReliabilityError = $reliabilityError
                VolumeRoot = $volumeRoot
            }
        }
        ConvertTo-Json -InputObject @($result) -Compress -Depth 3
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<PhysicalDisk>> GetDisksAsync(CancellationToken cancellationToken = default)
    {
        var json = await powerShellRunner.RunAsync(
            InventoryScript, TimeSpan.FromMinutes(2), cancellationToken);
        var disks = JsonSerializer.Deserialize<List<PhysicalDisk>>(json, JsonOptions)
            ?? throw new InvalidOperationException("Windows returned an invalid disk inventory.");

        using var identity = WindowsIdentity.GetCurrent();
        var isAdministrator = new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
        var enriched = await Task.WhenAll(disks.Select(disk => _smartDataService.EnrichAsync(
            disk with
            {
                LifeRemainingPercentage = disk.Wear is >= 0 and <= 100
                    ? 100 - disk.Wear
                    : null,
                LifeRemainingSource = disk.Wear is >= 0 and <= 100
                    ? "Windows wear counter (100% − wear used)"
                    : null
            },
            cancellationToken)));
        return enriched.Select(disk => disk with
        {
            HealthSource = disk.HealthSource == "SMART overall assessment"
                ? disk.HealthSource
                : isAdministrator ? "Windows · administrator" : "Windows · limited access",
            ReliabilityError = disk.SmartDataSource == "smartctl SMART data"
                ? null
                : !isAdministrator &&
                               disk.Temperature is null &&
                               disk.PowerOnHours is null &&
                               disk.Wear is null
                ? "Windows limited access: click Unlock SMART details (Admin) and approve the UAC prompt."
                : disk.ReliabilityError
        }).ToArray();
    }
}
