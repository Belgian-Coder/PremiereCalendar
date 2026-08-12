using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class SqliteIntegrationSettingsStore : IIntegrationSettingsStore
{
    private readonly AppDatabaseOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration? _configuration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IntegrationSettings? _cachedSettings;
    private bool _initialized;

    public SqliteIntegrationSettingsStore(
        IOptions<AppDatabaseOptions> options,
        IWebHostEnvironment environment,
        IConfiguration? configuration = null)
    {
        _options = options.Value;
        _environment = environment;
        _configuration = configuration;
    }

    public async Task<IntegrationSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            if (_cachedSettings is not null)
            {
                return CloneSettings(_cachedSettings);
            }

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Key, Value
                FROM AppParameters
                WHERE Key LIKE 'Integrations.%'
                   OR Key LIKE 'Sources.%'
                """;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                values[reader.GetString(0)] = reader.GetString(1);
            }

            _cachedSettings = CloneSettings(MapSettings(values));
            return CloneSettings(_cachedSettings);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IntegrationSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var parameters = FlattenSettings(settings);

            foreach (var parameter in parameters)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO AppParameters (Key, Value, UpdatedUtc)
                    VALUES ($key, $value, $updatedUtc)
                    ON CONFLICT(Key) DO UPDATE SET
                        Value = excluded.Value,
                        UpdatedUtc = excluded.UpdatedUtc
                    """;
                command.Parameters.AddWithValue("$key", parameter.Key);
                command.Parameters.AddWithValue("$value", parameter.Value);
                command.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            _cachedSettings = CloneSettings(MapSettings(parameters.ToDictionary(
                parameter => parameter.Key,
                parameter => parameter.Value,
                StringComparer.OrdinalIgnoreCase)));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        var databasePath = ResolveDatabasePath();
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AppParameters (
                Key TEXT NOT NULL PRIMARY KEY,
                Value TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            )
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        _initialized = true;
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = ResolveDatabasePath(),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        return SqliteConnectionFactory.Create(builder.ToString());
    }

    private string ResolveDatabasePath()
    {
        var configuredPath = string.IsNullOrWhiteSpace(_options.Path)
            ? "App_Data/data/premiere-calendar.db"
            : _options.Path.Trim();

        return Path.IsPathFullyQualified(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configuredPath));
    }

    private IntegrationSettings MapSettings(IReadOnlyDictionary<string, string> values)
    {
        return new IntegrationSettings
        {
            Sonarr = new SonarrIntegrationSettings
            {
                Enabled = GetBool(values, "Integrations.Sonarr.Enabled"),
                BaseUrl = GetString(values, "Integrations.Sonarr.BaseUrl"),
                ApiKey = GetString(values, "Integrations.Sonarr.ApiKey"),
                RootFolderPath = GetString(values, "Integrations.Sonarr.RootFolderPath"),
                QualityProfileId = GetInt(values, "Integrations.Sonarr.QualityProfileId"),
                SeriesType = GetString(values, "Integrations.Sonarr.SeriesType", "standard"),
                Monitor = GetString(values, "Integrations.Sonarr.Monitor", "all"),
                SeasonFolder = GetBool(values, "Integrations.Sonarr.SeasonFolder", true),
                SearchOnAdd = GetBool(values, "Integrations.Sonarr.SearchOnAdd", true),
                TagOnAdd = GetStringAllowEmpty(values, "Integrations.Sonarr.TagOnAdd", "import")
            },
            Radarr = new RadarrIntegrationSettings
            {
                Enabled = GetBool(values, "Integrations.Radarr.Enabled"),
                BaseUrl = GetString(values, "Integrations.Radarr.BaseUrl"),
                ApiKey = GetString(values, "Integrations.Radarr.ApiKey"),
                RootFolderPath = GetString(values, "Integrations.Radarr.RootFolderPath"),
                QualityProfileId = GetInt(values, "Integrations.Radarr.QualityProfileId"),
                MinimumAvailability = GetString(values, "Integrations.Radarr.MinimumAvailability", "released"),
                Monitored = GetBool(values, "Integrations.Radarr.Monitored", true),
                SearchOnAdd = GetBool(values, "Integrations.Radarr.SearchOnAdd", true),
                TagOnAdd = GetStringAllowEmpty(values, "Integrations.Radarr.TagOnAdd", "import")
            },
            Sources = new SourceIntegrationSettings
            {
                Tmdb = new TmdbSourceSettings
                {
                    BearerToken = GetStringAllowEmpty(
                        values,
                        "Sources.Tmdb.BearerToken",
                        "")
                },
                Tvmaze = new TvmazeSourceSettings
                {
                    Enabled = GetBool(values, "Sources.Tvmaze.Enabled", GetConfigBool("Tvmaze:Enabled", true)),
                    EnableScheduleDiscovery = GetBool(
                        values,
                        "Sources.Tvmaze.EnableScheduleDiscovery",
                        GetConfigBool("Tvmaze:EnableScheduleDiscovery", true)),
                    ScheduleCountries = GetArray(
                        values,
                        "Sources.Tvmaze.ScheduleCountries",
                        GetConfigArray("Tvmaze:ScheduleCountries"))
                },
                Trakt = new TraktSourceSettings
                {
                    Enabled = GetBool(values, "Sources.Trakt.Enabled", GetConfigBool("Trakt:Enabled", true)),
                    ClientId = GetStringAllowEmpty(values, "Sources.Trakt.ClientId", "")
                },
                Omdb = new OmdbSourceSettings
                {
                    Enabled = GetBool(values, "Sources.Omdb.Enabled", GetConfigBool("Omdb:Enabled")),
                    ApiKey = GetStringAllowEmpty(values, "Sources.Omdb.ApiKey", "")
                },
                Fanart = new FanartSourceSettings
                {
                    Enabled = GetBool(values, "Sources.Fanart.Enabled", GetConfigBool("Fanart:Enabled")),
                    ApiKey = GetStringAllowEmpty(values, "Sources.Fanart.ApiKey", "")
                },
                TheTvdb = new TheTvdbSourceSettings
                {
                    Enabled = GetBool(values, "Sources.TheTvdb.Enabled", GetConfigBool("TheTvdb:Enabled")),
                    ApiKey = GetStringAllowEmpty(values, "Sources.TheTvdb.ApiKey", "")
                },
                Wikimedia = new WikimediaSourceSettings
                {
                    Enabled = GetBool(values, "Sources.Wikimedia.Enabled", GetConfigBool("Wikimedia:Enabled", true))
                },
                Watchmode = new WatchmodeSourceSettings
                {
                    Enabled = GetBool(values, "Sources.Watchmode.Enabled", GetConfigBool("Watchmode:Enabled", true)),
                    ApiKey = GetStringAllowEmpty(values, "Sources.Watchmode.ApiKey", ""),
                    Regions = GetArray(
                        values,
                        "Sources.Watchmode.Regions",
                        GetConfigArray("Watchmode:Regions")),
                    EnableReleaseDiscovery = GetBool(
                        values,
                        "Sources.Watchmode.EnableReleaseDiscovery",
                        GetConfigBool("Watchmode:EnableReleaseDiscovery", false)),
                    EnableAvailabilityEnrichment = GetBool(
                        values,
                        "Sources.Watchmode.EnableAvailabilityEnrichment",
                        GetConfigBool("Watchmode:EnableAvailabilityEnrichment", true)),
                    CacheHours = GetInt(values, "Sources.Watchmode.CacheHours")
                        ?? GetConfigInt("Watchmode:CacheHours")
                },
                Simkl = new SimklSourceSettings
                {
                    Enabled = GetBool(values, "Sources.Simkl.Enabled", GetConfigBool("Simkl:Enabled", true)),
                    ClientId = GetStringAllowEmpty(values, "Sources.Simkl.ClientId", ""),
                    ClientSecret = GetStringAllowEmpty(values, "Sources.Simkl.ClientSecret", ""),
                    AccessToken = GetStringAllowEmpty(values, "Sources.Simkl.AccessToken", ""),
                    MinimumActivityCheckMinutes = GetInt(values, "Sources.Simkl.MinimumActivityCheckMinutes")
                        ?? GetConfigInt("Simkl:MinimumActivityCheckMinutes")
                }
            }
        };
    }

    private static IntegrationSettings CloneSettings(IntegrationSettings settings)
    {
        return new IntegrationSettings
        {
            Sonarr = new SonarrIntegrationSettings
            {
                Enabled = settings.Sonarr.Enabled,
                BaseUrl = settings.Sonarr.BaseUrl,
                ApiKey = settings.Sonarr.ApiKey,
                RootFolderPath = settings.Sonarr.RootFolderPath,
                QualityProfileId = settings.Sonarr.QualityProfileId,
                SeriesType = settings.Sonarr.SeriesType,
                Monitor = settings.Sonarr.Monitor,
                SeasonFolder = settings.Sonarr.SeasonFolder,
                SearchOnAdd = settings.Sonarr.SearchOnAdd,
                TagOnAdd = settings.Sonarr.TagOnAdd
            },
            Radarr = new RadarrIntegrationSettings
            {
                Enabled = settings.Radarr.Enabled,
                BaseUrl = settings.Radarr.BaseUrl,
                ApiKey = settings.Radarr.ApiKey,
                RootFolderPath = settings.Radarr.RootFolderPath,
                QualityProfileId = settings.Radarr.QualityProfileId,
                MinimumAvailability = settings.Radarr.MinimumAvailability,
                Monitored = settings.Radarr.Monitored,
                SearchOnAdd = settings.Radarr.SearchOnAdd,
                TagOnAdd = settings.Radarr.TagOnAdd
            },
            Sources = new SourceIntegrationSettings
            {
                Tmdb = new TmdbSourceSettings
                {
                    BearerToken = settings.Sources.Tmdb.BearerToken
                },
                Tvmaze = new TvmazeSourceSettings
                {
                    Enabled = settings.Sources.Tvmaze.Enabled,
                    EnableScheduleDiscovery = settings.Sources.Tvmaze.EnableScheduleDiscovery,
                    ScheduleCountries = [.. settings.Sources.Tvmaze.ScheduleCountries]
                },
                Trakt = new TraktSourceSettings
                {
                    Enabled = settings.Sources.Trakt.Enabled,
                    ClientId = settings.Sources.Trakt.ClientId
                },
                Omdb = new OmdbSourceSettings
                {
                    Enabled = settings.Sources.Omdb.Enabled,
                    ApiKey = settings.Sources.Omdb.ApiKey
                },
                Fanart = new FanartSourceSettings
                {
                    Enabled = settings.Sources.Fanart.Enabled,
                    ApiKey = settings.Sources.Fanart.ApiKey
                },
                TheTvdb = new TheTvdbSourceSettings
                {
                    Enabled = settings.Sources.TheTvdb.Enabled,
                    ApiKey = settings.Sources.TheTvdb.ApiKey
                },
                Wikimedia = new WikimediaSourceSettings
                {
                    Enabled = settings.Sources.Wikimedia.Enabled
                },
                Watchmode = new WatchmodeSourceSettings
                {
                    Enabled = settings.Sources.Watchmode.Enabled,
                    ApiKey = settings.Sources.Watchmode.ApiKey,
                    Regions = [.. settings.Sources.Watchmode.Regions],
                    EnableReleaseDiscovery = settings.Sources.Watchmode.EnableReleaseDiscovery,
                    EnableAvailabilityEnrichment = settings.Sources.Watchmode.EnableAvailabilityEnrichment,
                    CacheHours = settings.Sources.Watchmode.CacheHours
                },
                Simkl = new SimklSourceSettings
                {
                    Enabled = settings.Sources.Simkl.Enabled,
                    ClientId = settings.Sources.Simkl.ClientId,
                    ClientSecret = settings.Sources.Simkl.ClientSecret,
                    AccessToken = settings.Sources.Simkl.AccessToken,
                    MinimumActivityCheckMinutes = settings.Sources.Simkl.MinimumActivityCheckMinutes
                }
            }
        };
    }

    private static IReadOnlyList<KeyValuePair<string, string>> FlattenSettings(IntegrationSettings settings)
    {
        return
        [
            new("Integrations.Sonarr.Enabled", settings.Sonarr.Enabled.ToString(CultureInfo.InvariantCulture)),
            new("Integrations.Sonarr.BaseUrl", settings.Sonarr.BaseUrl.Trim()),
            new("Integrations.Sonarr.ApiKey", settings.Sonarr.ApiKey.Trim()),
            new("Integrations.Sonarr.RootFolderPath", settings.Sonarr.RootFolderPath.Trim()),
            new("Integrations.Sonarr.QualityProfileId", settings.Sonarr.QualityProfileId?.ToString(CultureInfo.InvariantCulture) ?? ""),
            new("Integrations.Sonarr.SeriesType", settings.Sonarr.SeriesType.Trim()),
            new("Integrations.Sonarr.Monitor", settings.Sonarr.Monitor.Trim()),
            new("Integrations.Sonarr.SeasonFolder", settings.Sonarr.SeasonFolder.ToString(CultureInfo.InvariantCulture)),
            new("Integrations.Sonarr.SearchOnAdd", settings.Sonarr.SearchOnAdd.ToString(CultureInfo.InvariantCulture)),
            new("Integrations.Sonarr.TagOnAdd", settings.Sonarr.TagOnAdd.Trim()),
            new("Integrations.Radarr.Enabled", settings.Radarr.Enabled.ToString(CultureInfo.InvariantCulture)),
            new("Integrations.Radarr.BaseUrl", settings.Radarr.BaseUrl.Trim()),
            new("Integrations.Radarr.ApiKey", settings.Radarr.ApiKey.Trim()),
            new("Integrations.Radarr.RootFolderPath", settings.Radarr.RootFolderPath.Trim()),
            new("Integrations.Radarr.QualityProfileId", settings.Radarr.QualityProfileId?.ToString(CultureInfo.InvariantCulture) ?? ""),
            new("Integrations.Radarr.MinimumAvailability", settings.Radarr.MinimumAvailability.Trim()),
            new("Integrations.Radarr.Monitored", settings.Radarr.Monitored.ToString(CultureInfo.InvariantCulture)),
            new("Integrations.Radarr.SearchOnAdd", settings.Radarr.SearchOnAdd.ToString(CultureInfo.InvariantCulture)),
            new("Integrations.Radarr.TagOnAdd", settings.Radarr.TagOnAdd.Trim()),
            new("Sources.Tmdb.BearerToken", settings.Sources.Tmdb.BearerToken.Trim()),
            new("Sources.Tvmaze.Enabled", settings.Sources.Tvmaze.Enabled.ToString(CultureInfo.InvariantCulture)),
            new("Sources.Tvmaze.EnableScheduleDiscovery", settings.Sources.Tvmaze.EnableScheduleDiscovery.ToString(CultureInfo.InvariantCulture)),
            new("Sources.Tvmaze.ScheduleCountries", string.Join(",", settings.Sources.Tvmaze.ScheduleCountries.Select(value => value.Trim()).Where(value => value.Length > 0))),
            new("Sources.Trakt.Enabled", settings.Sources.Trakt.Enabled.ToString(CultureInfo.InvariantCulture)),
            new("Sources.Trakt.ClientId", settings.Sources.Trakt.ClientId.Trim()),
            new("Sources.Omdb.Enabled", settings.Sources.Omdb.Enabled.ToString(CultureInfo.InvariantCulture)),
            new("Sources.Omdb.ApiKey", settings.Sources.Omdb.ApiKey.Trim()),
            new("Sources.Fanart.Enabled", settings.Sources.Fanart.Enabled.ToString(CultureInfo.InvariantCulture)),
            new("Sources.Fanart.ApiKey", settings.Sources.Fanart.ApiKey.Trim()),
            new("Sources.TheTvdb.Enabled", settings.Sources.TheTvdb.Enabled.ToString(CultureInfo.InvariantCulture)),
            new("Sources.TheTvdb.ApiKey", settings.Sources.TheTvdb.ApiKey.Trim()),
            new("Sources.Wikimedia.Enabled", settings.Sources.Wikimedia.Enabled.ToString(CultureInfo.InvariantCulture)),
            new("Sources.Watchmode.Enabled", settings.Sources.Watchmode.Enabled.ToString(CultureInfo.InvariantCulture)),
            new("Sources.Watchmode.ApiKey", settings.Sources.Watchmode.ApiKey.Trim()),
            new("Sources.Watchmode.Regions", string.Join(",", settings.Sources.Watchmode.Regions.Select(value => value.Trim()).Where(value => value.Length > 0))),
            new("Sources.Watchmode.EnableReleaseDiscovery", settings.Sources.Watchmode.EnableReleaseDiscovery.ToString(CultureInfo.InvariantCulture)),
            new("Sources.Watchmode.EnableAvailabilityEnrichment", settings.Sources.Watchmode.EnableAvailabilityEnrichment.ToString(CultureInfo.InvariantCulture)),
            new("Sources.Watchmode.CacheHours", settings.Sources.Watchmode.CacheHours?.ToString(CultureInfo.InvariantCulture) ?? ""),
            new("Sources.Simkl.Enabled", settings.Sources.Simkl.Enabled.ToString(CultureInfo.InvariantCulture)),
            new("Sources.Simkl.ClientId", settings.Sources.Simkl.ClientId.Trim()),
            new("Sources.Simkl.ClientSecret", settings.Sources.Simkl.ClientSecret.Trim()),
            new("Sources.Simkl.AccessToken", settings.Sources.Simkl.AccessToken.Trim()),
            new("Sources.Simkl.MinimumActivityCheckMinutes", settings.Sources.Simkl.MinimumActivityCheckMinutes?.ToString(CultureInfo.InvariantCulture) ?? "")
        ];
    }

    private static string GetString(
        IReadOnlyDictionary<string, string> values,
        string key,
        string fallback = "")
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;
    }

    private string GetConfigString(string key, string fallback = "")
    {
        return _configuration?[key]?.Trim() ?? fallback;
    }

    private bool GetConfigBool(string key, bool fallback = false)
    {
        return bool.TryParse(_configuration?[key], out var value) ? value : fallback;
    }

    private int? GetConfigInt(string key)
    {
        return int.TryParse(_configuration?[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private string[] GetConfigArray(string key)
    {
        return _configuration?.GetSection(key).Get<string[]>() ?? [];
    }

    private static string[] GetArray(
        IReadOnlyDictionary<string, string> values,
        string key,
        IReadOnlyList<string> fallback)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return fallback
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GetStringAllowEmpty(
        IReadOnlyDictionary<string, string> values,
        string key,
        string fallback)
    {
        return values.TryGetValue(key, out var value)
            ? value.Trim()
            : fallback;
    }

    private static bool GetBool(
        IReadOnlyDictionary<string, string> values,
        string key,
        bool fallback = false)
    {
        return values.TryGetValue(key, out var value)
            && bool.TryParse(value, out var result)
                ? result
                : fallback;
    }

    private static int? GetInt(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : null;
    }
}
