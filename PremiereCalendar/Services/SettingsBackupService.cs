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

    /// <returns>User-facing notices, for example an Arr API key that was removed because its URL changed.</returns>
    public async Task<IReadOnlyList<string>> ImportAsync(string backupJson, CancellationToken cancellationToken)
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

        // An empty or partial settings object would otherwise deserialize into defaults and silently reset everything.
        EnsureSettingsSectionsPresent(backupJson);
        EnsureNoNullValues(backup.Settings);
        var importedState = ValidateImportedState(backup.AppState);

        // A redacted backup carries blank secrets, and a full one may still leave some blank. Neither may erase
        // the secrets that are stored today; only a non-blank secret from an envelope that includes secrets replaces one.
        var current = await _settingsStore.GetAsync(cancellationToken);
        var notices = new List<string>();
        var settings = MergeSecrets(backup.Settings, current, useImportedSecrets: backup.IncludeSecrets, notices);

        await _settingsStore.SaveAsync(settings, cancellationToken);
        await _stateStore.ReplaceValuesByPrefixAsync(StatePrefixes, importedState, cancellationToken);
        return notices;
    }

    private static readonly string[] SourceSectionNames =
        ["tmdb", "tvmaze", "trakt", "omdb", "fanart", "theTvdb", "wikimedia", "watchmode", "simkl"];

    private static void EnsureSettingsSectionsPresent(string backupJson)
    {
        try
        {
            using var document = JsonDocument.Parse(backupJson);
            var settings = FindObject(document.RootElement, "settings")
                ?? throw new InvalidOperationException("Backup settings are missing.");
            if (FindObject(settings, "sonarr") is null
                || FindObject(settings, "radarr") is null
                || FindObject(settings, "sources") is not { } sources)
            {
                throw new InvalidOperationException("Backup settings are incomplete: sonarr, radarr and sources sections are required.");
            }

            // Every release that could write backups exported all nine source sections, so a missing or null one
            // means a hand-edited or truncated file that would silently reset that provider to defaults.
            var missing = SourceSectionNames.Where(name => FindObject(sources, name) is null).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Backup settings are incomplete: missing source section(s) {string.Join(", ", missing)}.");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Backup file is invalid.", ex);
        }
    }

    private static void EnsureNoNullValues(object section)
    {
        foreach (var property in section.GetType().GetProperties())
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var value = property.GetValue(section);
            var type = property.PropertyType;
            if (value is null)
            {
                if (type == typeof(string) || type.IsArray || (type.IsClass && type.Namespace == typeof(IntegrationSettings).Namespace))
                {
                    throw new InvalidOperationException($"Backup settings are invalid: '{property.Name}' must not be null.");
                }

                continue;
            }

            if (type.IsClass && type.Namespace == typeof(IntegrationSettings).Namespace)
            {
                EnsureNoNullValues(value);
            }
            else if (value is string[] items && items.Any(item => item is null))
            {
                throw new InvalidOperationException($"Backup settings are invalid: '{property.Name}' contains null entries.");
            }
        }
    }

    private static JsonElement? FindObject(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in parent.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.Object ? property.Value : null;
            }
        }

        return null;
    }

    private static string KeepOrReplace(string? imported, string? current, bool useImportedSecrets)
    {
        return useImportedSecrets && !string.IsNullOrWhiteSpace(imported)
            ? imported
            : current ?? "";
    }

    private static string KeepOrClearForUrl(
        string name,
        string? importedKey,
        string? importedBaseUrl,
        object? currentSettings,
        bool useImportedSecrets,
        List<string> notices)
    {
        var (currentKey, currentBaseUrl) = currentSettings switch
        {
            SonarrIntegrationSettings sonarr => (sonarr.ApiKey, sonarr.BaseUrl),
            RadarrIntegrationSettings radarr => (radarr.ApiKey, radarr.BaseUrl),
            _ => ("", "")
        };
        if (useImportedSecrets && !string.IsNullOrWhiteSpace(importedKey))
        {
            return importedKey;
        }

        // A stored key must never follow an imported URL to a different host.
        if (!string.IsNullOrWhiteSpace(currentKey) && !SecretHostBinding.SameBaseUrl(importedBaseUrl, currentBaseUrl))
        {
            notices.Add($"The imported {name} URL differs from the saved one, so the saved {name} API key was removed. Enter the API key again.");
            return "";
        }

        return currentKey ?? "";
    }

    private static IntegrationSettings MergeSecrets(
        IntegrationSettings imported,
        IntegrationSettings current,
        bool useImportedSecrets,
        List<string> notices)
    {
        var sources = imported.Sources ?? new SourceIntegrationSettings();
        var currentSources = current.Sources ?? new SourceIntegrationSettings();
        var currentTmdb = currentSources.Tmdb ?? new TmdbSourceSettings();
        var currentTrakt = currentSources.Trakt ?? new TraktSourceSettings();
        var currentOmdb = currentSources.Omdb ?? new OmdbSourceSettings();
        var currentFanart = currentSources.Fanart ?? new FanartSourceSettings();
        var currentTvdb = currentSources.TheTvdb ?? new TheTvdbSourceSettings();
        var currentWatchmode = currentSources.Watchmode ?? new WatchmodeSourceSettings();
        var currentSimkl = currentSources.Simkl ?? new SimklSourceSettings();
        var importedSonarr = imported.Sonarr ?? new SonarrIntegrationSettings();
        var importedRadarr = imported.Radarr ?? new RadarrIntegrationSettings();

        return imported with
        {
            Sonarr = importedSonarr with
            {
                ApiKey = KeepOrClearForUrl(
                    "Sonarr", importedSonarr.ApiKey, importedSonarr.BaseUrl, current.Sonarr, useImportedSecrets, notices)
            },
            Radarr = importedRadarr with
            {
                ApiKey = KeepOrClearForUrl(
                    "Radarr", importedRadarr.ApiKey, importedRadarr.BaseUrl, current.Radarr, useImportedSecrets, notices)
            },
            Sources = sources with
            {
                Tmdb = (sources.Tmdb ?? new TmdbSourceSettings()) with
                {
                    BearerToken = KeepOrReplace(sources.Tmdb?.BearerToken, currentTmdb.BearerToken, useImportedSecrets)
                },
                Trakt = (sources.Trakt ?? new TraktSourceSettings()) with
                {
                    ClientId = KeepOrReplace(sources.Trakt?.ClientId, currentTrakt.ClientId, useImportedSecrets)
                },
                Omdb = (sources.Omdb ?? new OmdbSourceSettings()) with
                {
                    ApiKey = KeepOrReplace(sources.Omdb?.ApiKey, currentOmdb.ApiKey, useImportedSecrets)
                },
                Fanart = (sources.Fanart ?? new FanartSourceSettings()) with
                {
                    ApiKey = KeepOrReplace(sources.Fanart?.ApiKey, currentFanart.ApiKey, useImportedSecrets)
                },
                TheTvdb = (sources.TheTvdb ?? new TheTvdbSourceSettings()) with
                {
                    ApiKey = KeepOrReplace(sources.TheTvdb?.ApiKey, currentTvdb.ApiKey, useImportedSecrets)
                },
                Watchmode = (sources.Watchmode ?? new WatchmodeSourceSettings()) with
                {
                    ApiKey = KeepOrReplace(sources.Watchmode?.ApiKey, currentWatchmode.ApiKey, useImportedSecrets)
                },
                Simkl = (sources.Simkl ?? new SimklSourceSettings()) with
                {
                    ClientId = KeepOrReplace(sources.Simkl?.ClientId, currentSimkl.ClientId, useImportedSecrets),
                    ClientSecret = KeepOrReplace(sources.Simkl?.ClientSecret, currentSimkl.ClientSecret, useImportedSecrets),
                    AccessToken = KeepOrReplace(sources.Simkl?.AccessToken, currentSimkl.AccessToken, useImportedSecrets)
                }
            }
        };
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

/// <summary>Decides whether a saved Arr API key may be sent to a (possibly edited) base URL.</summary>
internal static class SecretHostBinding
{
    /// <summary>
    /// True when both URLs address the same endpoint. Compares parsed parts (scheme, IDNA host, effective port, user info,
    /// path, query) so look-alike Unicode hosts and explicit default ports are handled; unparseable URLs are never "the same"
    /// unless they are textually identical.
    /// </summary>
    public static bool SameBaseUrl(string? left, string? right)
    {
        var first = (left ?? "").Trim();
        var second = (right ?? "").Trim();
        if (first.Length == 0 && second.Length == 0)
        {
            return true;
        }

        if (first.Length == 0 || second.Length == 0)
        {
            return false;
        }

        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            return true;
        }

        if (!TryParse(first, out var a) || !TryParse(second, out var b))
        {
            return false;
        }

        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
            && a.Port == b.Port
            && string.Equals(a.UserInfo, b.UserInfo, StringComparison.Ordinal)
            && string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal)
            && string.Equals(a.Query, b.Query, StringComparison.Ordinal);
    }

    private static bool TryParse(string value, out Uri uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
