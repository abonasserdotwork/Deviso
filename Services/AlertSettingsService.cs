using System.Text.Json;

namespace Deviso.Services;

public sealed class AlertSettingsService
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Deviso", "settings.json");

    public int TemperatureThreshold { get; private set; } = 55;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return;
        }

        await using var stream = File.OpenRead(_settingsPath);
        var settings = await JsonSerializer.DeserializeAsync<Settings>(
            stream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Deviso alert settings are invalid.");
        if (settings.TemperatureThreshold is < 30 or > 100)
        {
            throw new InvalidDataException("The saved temperature threshold must be between 30 and 100 °C.");
        }

        TemperatureThreshold = settings.TemperatureThreshold;
    }

    public async Task SaveTemperatureThresholdAsync(
        int temperatureThreshold,
        CancellationToken cancellationToken = default)
    {
        if (temperatureThreshold is < 30 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(temperatureThreshold), "Choose a temperature threshold between 30 and 100 °C.");
        }

        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("Could not determine the Deviso data directory.");
        Directory.CreateDirectory(directory);
        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(
            stream, new Settings(temperatureThreshold), cancellationToken: cancellationToken);
        TemperatureThreshold = temperatureThreshold;
    }

    private sealed record Settings(int TemperatureThreshold);
}
