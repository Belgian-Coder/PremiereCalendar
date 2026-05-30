using System.Text.Json;
using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed class SettingsBackupService
{
    private static readonly string[] StatePrefixes = ["Calendar."];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IIntegrationSettingsStore _settingsStore;
    private readonly IAppStateStore _stateStore;
    private readonly TimeProvider _timeProvider;

    public SettingsBackupService(
        IIntegrationSettingsStore settingsStore,
        IAppStateStore stateStore,
        TimeProvider timeProvider)
    {
        _settingsStore = settingsStore;
        _stateStore = stateStore;
        _timeProvider = timeProvider;
    }

    public async Task<string> ExportAsync(bool includeSecrets, CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.GetAsync(cancellationToken);
        if (!includeSecrets)
        {
            settings = RedactSecrets(settings);
        }

        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prefix in StatePrefixes)
        {
            foreach (var entry in await _stateStore.GetValuesByPrefixAsync(prefix, cancellationToken))
            {
                state[entry.Key] = entry.Value;
            }
        }

        var backup = new SettingsBackupEnvelope(
            SchemaVersion: 1,
            ExportedUtc: _timeProvider.GetUtcNow(),
            IncludeSecrets: includeSecrets,
            Settings: settings,
            AppState: state.ToDictionary(entry => entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal));
        return JsonSerializer.Serialize(backup, JsonOptions);
    }

    public async Task ImportAsync(string backupJson, CancellationToken cancellationToken)
    {
        SettingsBackupEnvelope backup;
        try
        {
            backup = JsonSerializer.Deserialize<SettingsBackupEnvelope>(backupJson, JsonOptions)
                ?? throw new InvalidOperationException("Backup file is invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Backup file is invalid.", ex);
        }

        if (backup.SchemaVersion != 1)
        {
            throw new InvalidOperationException("Backup schema is not supported.");
        }

        if (backup.Settings is null)
        {
            throw new InvalidOperationException("Backup settings are missing.");
        }

        var importedState = ValidateImportedState(backup.AppState);

        await _settingsStore.SaveAsync(backup.Settings, cancellationToken);
        await _stateStore.ReplaceValuesByPrefixAsync(StatePrefixes, importedState, cancellationToken);
    }

    private static Dictionary<string, string> ValidateImportedState(IReadOnlyDictionary<string, string?>? appState)
    {
        var importedState = new Dictionary<string, string>(StringComparer.Ordinal);
        if (appState is null)
        {
            return importedState;
        }

        foreach (var entry in appState)
        {
            if (!StatePrefixes.Any(prefix => entry.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            if (entry.Value is null)
            {
                throw new InvalidOperationException("Backup state contains empty values.");
            }

            importedState[entry.Key] = entry.Value;
        }

        return importedState;
    }

    private static IntegrationSettings RedactSecrets(IntegrationSettings settings)
    {
        return settings with
        {
            Sonarr = settings.Sonarr with { ApiKey = "" },
            Radarr = settings.Radarr with { ApiKey = "" },
            Sources = settings.Sources with
            {
                Tmdb = settings.Sources.Tmdb with { BearerToken = "" },
                Trakt = settings.Sources.Trakt with { ClientId = "" },
                Omdb = settings.Sources.Omdb with { ApiKey = "" },
                Fanart = settings.Sources.Fanart with { ApiKey = "" },
                TheTvdb = settings.Sources.TheTvdb with { ApiKey = "" },
                Watchmode = settings.Sources.Watchmode with { ApiKey = "" },
                Simkl = settings.Sources.Simkl with
                {
                    ClientId = "",
                    ClientSecret = "",
                    AccessToken = ""
                }
            }
        };
    }

    private sealed record SettingsBackupEnvelope(
        int SchemaVersion,
        DateTimeOffset ExportedUtc,
        bool IncludeSecrets,
        IntegrationSettings? Settings,
        IReadOnlyDictionary<string, string?>? AppState);
}
