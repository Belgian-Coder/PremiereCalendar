using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class SqliteCalendarFilterUsageStore : ICalendarFilterUsageStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly AppDatabaseOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SqliteCalendarFilterUsageStore(
        IOptions<AppDatabaseOptions> options,
        IWebHostEnvironment environment)
    {
        _options = options.Value;
        _environment = environment;
    }

    public async Task RecordUseAsync(
        CalendarPageMode pageMode,
        CalendarFilters filters,
        int itemCount,
        DateTimeOffset usedAtUtc,
        CancellationToken cancellationToken)
    {
        var template = CreateTemplate(filters, pageMode);
        var cacheKey = PremiereDiscoveryCriteria.FromFilters(template).CacheKey();
        var profileKey = UsedProfileKey(pageMode, cacheKey);
        var filterJson = JsonSerializer.Serialize(template, JsonOptions);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO CalendarFilterUsage (
                    ProfileKey,
                    PageMode,
                    CacheKey,
                    FilterJson,
                    UseCount,
                    LastUsedUtc,
                    LastWarmedUtc,
                    LastItemCount,
                    LastFailure,
                    IsDefault
                )
                VALUES (
                    $profileKey,
                    $pageMode,
                    $cacheKey,
                    $filterJson,
                    1,
                    $lastUsedUtc,
                    NULL,
                    $lastItemCount,
                    NULL,
                    0
                )
                ON CONFLICT(ProfileKey) DO UPDATE SET
                    PageMode = excluded.PageMode,
                    CacheKey = excluded.CacheKey,
                    FilterJson = excluded.FilterJson,
                    UseCount = CalendarFilterUsage.UseCount + 1,
                    LastUsedUtc = excluded.LastUsedUtc,
                    LastItemCount = excluded.LastItemCount,
                    LastFailure = NULL,
                    IsDefault = 0
                """;
            AddProfileParameters(command, profileKey, pageMode, cacheKey, filterJson);
            command.Parameters.AddWithValue("$lastUsedUtc", FormatTimestamp(usedAtUtc));
            command.Parameters.AddWithValue("$lastItemCount", itemCount);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CalendarFilterUsageProfile?> GetProfileAsync(
        string profileKey,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ProfileKey, PageMode, CacheKey, FilterJson, UseCount, LastUsedUtc, LastWarmedUtc, LastItemCount, LastFailure, IsDefault
                FROM CalendarFilterUsage
                WHERE ProfileKey = $profileKey
                """;
            command.Parameters.AddWithValue("$profileKey", profileKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadProfile(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CalendarFilterUsageProfile>> GetTopProfilesAsync(
        int count,
        DateTimeOffset nowUtc,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return [];
        }

        var cutoffUtc = nowUtc - retention;
        var profiles = new List<CalendarFilterUsageProfile>();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ProfileKey, PageMode, CacheKey, FilterJson, UseCount, LastUsedUtc, LastWarmedUtc, LastItemCount, LastFailure, IsDefault
                FROM CalendarFilterUsage
                WHERE IsDefault = 0
                  AND UseCount > 0
                  AND LastUsedUtc >= $cutoffUtc
                """;
            command.Parameters.AddWithValue("$cutoffUtc", FormatTimestamp(cutoffUtc));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var profile = ReadProfile(reader);
                if (profile is not null)
                {
                    profiles.Add(profile);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return profiles
            .OrderByDescending(profile => DecayedUsageScore(profile, nowUtc))
            .ThenByDescending(profile => profile.LastUsedUtc)
            .Take(count)
            .ToArray();
    }

    public async Task MarkWarmedAsync(
        string profileKey,
        CalendarPageMode pageMode,
        CalendarFilters filters,
        bool isDefault,
        int itemCount,
        DateTimeOffset warmedAtUtc,
        CancellationToken cancellationToken)
    {
        var template = CreateTemplate(filters, pageMode);
        var cacheKey = PremiereDiscoveryCriteria.FromFilters(template).CacheKey();
        var filterJson = JsonSerializer.Serialize(template, JsonOptions);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO CalendarFilterUsage (
                    ProfileKey,
                    PageMode,
                    CacheKey,
                    FilterJson,
                    UseCount,
                    LastUsedUtc,
                    LastWarmedUtc,
                    LastItemCount,
                    LastFailure,
                    IsDefault
                )
                VALUES (
                    $profileKey,
                    $pageMode,
                    $cacheKey,
                    $filterJson,
                    0,
                    $lastUsedUtc,
                    $lastWarmedUtc,
                    $lastItemCount,
                    NULL,
                    $isDefault
                )
                ON CONFLICT(ProfileKey) DO UPDATE SET
                    PageMode = excluded.PageMode,
                    CacheKey = excluded.CacheKey,
                    FilterJson = excluded.FilterJson,
                    LastWarmedUtc = excluded.LastWarmedUtc,
                    LastItemCount = excluded.LastItemCount,
                    LastFailure = NULL,
                    IsDefault = excluded.IsDefault
                """;
            AddProfileParameters(command, profileKey, pageMode, cacheKey, filterJson);
            command.Parameters.AddWithValue("$lastUsedUtc", FormatTimestamp(warmedAtUtc));
            command.Parameters.AddWithValue("$lastWarmedUtc", FormatTimestamp(warmedAtUtc));
            command.Parameters.AddWithValue("$lastItemCount", itemCount);
            command.Parameters.AddWithValue("$isDefault", isDefault ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkWarmFailedAsync(
        string profileKey,
        CalendarPageMode pageMode,
        CalendarFilters filters,
        bool isDefault,
        string failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken)
    {
        var template = CreateTemplate(filters, pageMode);
        var cacheKey = PremiereDiscoveryCriteria.FromFilters(template).CacheKey();
        var filterJson = JsonSerializer.Serialize(template, JsonOptions);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO CalendarFilterUsage (
                    ProfileKey,
                    PageMode,
                    CacheKey,
                    FilterJson,
                    UseCount,
                    LastUsedUtc,
                    LastWarmedUtc,
                    LastItemCount,
                    LastFailure,
                    IsDefault
                )
                VALUES (
                    $profileKey,
                    $pageMode,
                    $cacheKey,
                    $filterJson,
                    0,
                    $lastUsedUtc,
                    NULL,
                    NULL,
                    $lastFailure,
                    $isDefault
                )
                ON CONFLICT(ProfileKey) DO UPDATE SET
                    PageMode = excluded.PageMode,
                    CacheKey = excluded.CacheKey,
                    FilterJson = excluded.FilterJson,
                    LastFailure = excluded.LastFailure,
                    IsDefault = excluded.IsDefault
                """;
            AddProfileParameters(command, profileKey, pageMode, cacheKey, filterJson);
            command.Parameters.AddWithValue("$lastUsedUtc", FormatTimestamp(failedAtUtc));
            command.Parameters.AddWithValue("$lastFailure", failure.Length > 500 ? failure[..500] : failure);
            command.Parameters.AddWithValue("$isDefault", isDefault ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> CleanupAsync(
        DateTimeOffset cutoffUtc,
        IReadOnlySet<string> retainedProfileKeys,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            var retainedKeys = retainedProfileKeys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (retainedKeys.Length == 0)
            {
                command.CommandText = """
                    DELETE FROM CalendarFilterUsage
                    WHERE IsDefault = 0
                      AND LastUsedUtc < $cutoffUtc
                    """;
            }
            else
            {
                var parameterNames = retainedKeys
                    .Select((_, index) => $"$retained{index}")
                    .ToArray();
                command.CommandText = $"""
                    DELETE FROM CalendarFilterUsage
                    WHERE IsDefault = 0
                      AND LastUsedUtc < $cutoffUtc
                      AND ProfileKey NOT IN ({string.Join(", ", parameterNames)})
                    """;

                for (var index = 0; index < retainedKeys.Length; index++)
                {
                    command.Parameters.AddWithValue(parameterNames[index], retainedKeys[index]);
                }
            }

            command.Parameters.AddWithValue("$cutoffUtc", FormatTimestamp(cutoffUtc));
            return await command.ExecuteNonQueryAsync(cancellationToken);
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

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await DatabaseSchema.AssertCurrentAsync(connection, cancellationToken);
        _initialized = true;
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = ResolveDatabasePath(),
            Mode = SqliteOpenMode.ReadWrite,
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

    private static void AddProfileParameters(
        SqliteCommand command,
        string profileKey,
        CalendarPageMode pageMode,
        string cacheKey,
        string filterJson)
    {
        command.Parameters.AddWithValue("$profileKey", profileKey);
        command.Parameters.AddWithValue("$pageMode", pageMode.ToString());
        command.Parameters.AddWithValue("$cacheKey", cacheKey);
        command.Parameters.AddWithValue("$filterJson", filterJson);
    }

    private static CalendarFilterUsageProfile? ReadProfile(SqliteDataReader reader)
    {
        try
        {
            var filters = JsonSerializer.Deserialize<CalendarFilters>(reader.GetString(3), JsonOptions);
            if (filters is null)
            {
                return null;
            }

            if (!Enum.TryParse<CalendarPageMode>(reader.GetString(1), ignoreCase: true, out var pageMode))
            {
                pageMode = CalendarPageMode.All;
            }

            return new CalendarFilterUsageProfile(
                reader.GetString(0),
                pageMode,
                reader.GetString(2),
                filters,
                reader.GetInt32(4),
                ParseTimestamp(reader.GetString(5)),
                reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                !reader.IsDBNull(9) && reader.GetInt32(9) != 0);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static CalendarFilters CreateTemplate(CalendarFilters filters, CalendarPageMode pageMode)
    {
        var template = CalendarFilterState.Clone(filters);
        CalendarFilterState.ApplyPageMode(template, pageMode);
        CalendarFilterState.Normalize(template);
        template.WeekStart = DateOnly.MinValue;
        template.PriorityDate = null;
        return template;
    }

    private static double DecayedUsageScore(CalendarFilterUsageProfile profile, DateTimeOffset nowUtc)
    {
        var ageDays = Math.Max(0, (nowUtc - profile.LastUsedUtc).TotalDays);
        return profile.UseCount / (1.0 + ageDays / 14.0);
    }

    private static string UsedProfileKey(CalendarPageMode pageMode, string cacheKey)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"used:{pageMode.ToString().ToLowerInvariant()}:{cacheKey}");
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }
}
