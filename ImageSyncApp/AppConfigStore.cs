using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageSyncApp;

public sealed class AppConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly string _configPath;

    public AppConfigStore(string? configPath = null)
    {
        _configPath = configPath ?? Path.Combine(AppContext.BaseDirectory, "config.json");
    }

    public async Task<ConfigLoadResult> ReadAsync()
    {
        if (!File.Exists(_configPath))
        {
            return new ConfigLoadResult(AppConfig.Default);
        }

        try
        {
            var json = await File.ReadAllTextAsync(_configPath).ConfigureAwait(false);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (config?.Source is not null && config.Version >= AppConfig.CurrentVersion)
            {
                return new ConfigLoadResult(config);
            }

            var legacy = JsonSerializer.Deserialize<LegacyAppConfig>(json, JsonOptions);
            if (legacy is not null)
            {
                var kind = Enum.TryParse<SourceKind>(legacy.SourceKind, ignoreCase: true, out var parsedKind)
                    ? parsedKind
                    : SourceKind.FileSystem;
                return new ConfigLoadResult(
                    new AppConfig(AppConfig.CurrentVersion, new SavedSource(kind, legacy.SourceDirectory ?? ""), legacy.DestinationDirectory ?? ""));
            }

            return new ConfigLoadResult(AppConfig.Default, "Saved settings were empty; using defaults.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ConfigLoadResult(AppConfig.Default, $"Could not read saved settings: {ex.Message}");
        }
    }

    public async Task SaveAsync(AppConfig config)
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        var temporaryPath = $"{_configPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, config, JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            File.Move(temporaryPath, _configPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            finally
            {
                _saveLock.Release();
            }
        }
    }

    private sealed record LegacyAppConfig(string? SourceDirectory, string? DestinationDirectory, string? SourceKind);
}
