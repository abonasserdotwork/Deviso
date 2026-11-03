using System.Text.Json.Serialization;

namespace Deviso.Models;

public sealed record PhysicalDisk
{
    [JsonPropertyName("Name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("DeviceId")]
    public string DeviceId { get; init; } = "";

    [JsonPropertyName("BusType")]
    public string BusType { get; init; } = "";

    [JsonPropertyName("MediaType")]
    public string MediaType { get; init; } = "";

    [JsonPropertyName("Size")]
    public long Size { get; init; }

    [JsonPropertyName("Health")]
    public string Health { get; init; } = "Unknown";

    [JsonPropertyName("OperationalStatus")]
    public string OperationalStatus { get; init; } = "Unknown";

    [JsonPropertyName("Temperature")]
    public int? Temperature { get; init; }

    [JsonPropertyName("TemperatureMax")]
    public int? TemperatureMax { get; init; }

    [JsonPropertyName("PowerOnHours")]
    public long? PowerOnHours { get; init; }

    [JsonPropertyName("Wear")]
    public int? Wear { get; init; }

    [JsonPropertyName("LifeRemainingPercentage")]
    public int? LifeRemainingPercentage { get; init; }

    [JsonPropertyName("LifeRemainingSource")]
    public string? LifeRemainingSource { get; init; }

    [JsonPropertyName("HealthSource")]
    public string HealthSource { get; init; } = "Windows";

    [JsonPropertyName("SmartDataSource")]
    public string? SmartDataSource { get; init; }

    [JsonPropertyName("ReliabilityError")]
    public string? ReliabilityError { get; init; }

    [JsonPropertyName("VolumeRoot")]
    public string? VolumeRoot { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Disk {DeviceId}" : Name;
    public string Capacity => Size <= 0 ? "—" : $"{Size / 1_000_000_000d:0.##} GB";
    public string TemperatureDisplay => Temperature is null ? "Not reported" : $"{Temperature} °C";
    public string PowerOnDisplay => PowerOnHours is null ? "Not reported" : $"{PowerOnHours:N0} h";
    public string WearDisplay => Wear is null ? "Not reported" : $"{Wear}% used";
    public string LifeRemainingDisplay =>
        LifeRemainingPercentage is null ? "Not reported" : $"{LifeRemainingPercentage}%";
    public string LifeRemainingCaption =>
        LifeRemainingPercentage is null ? "Not reported by this drive/controller" : LifeRemainingSource ?? "Drive-reported wear indicator";
    public string DataSourceDisplay =>
        string.IsNullOrWhiteSpace(SmartDataSource) ? HealthSource : $"{SmartDataSource} · {HealthSource}";
    public string VolumeDisplay => VolumeRoot ?? "No mounted volume";
}
