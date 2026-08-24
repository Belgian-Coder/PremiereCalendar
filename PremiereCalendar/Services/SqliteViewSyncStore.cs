using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class SqliteViewSyncStore : IViewSyncStore
{
    private readonly AppDatabaseOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SqliteViewSyncStore(
        IOptions<AppDatabaseOptions> options,
        IWebHostEnvironment environment)
    {
        _options = options.Value;
        _environment = environment;
    }

    public async Task<ViewSyncGroup> CreateGroupAsync(
        string name,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var group = new ViewSyncGroup(
            Guid.NewGuid().ToString("N"),
            NormalizeName(name, "My devices"),
            nowUtc);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ViewSyncGroups (GroupId, Name, CreatedUtc)
                VALUES (@groupId, @name, @createdUtc)
                """;
            DatabaseParameters.Add(command, "@groupId", group.GroupId);
            DatabaseParameters.Add(command, "@name", group.Name);
            DatabaseParameters.Add(command, "@createdUtc", FormatDate(group.CreatedUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return group;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ViewSyncGroup>> GetGroupsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT GroupId, Name, CreatedUtc
                FROM ViewSyncGroups
                ORDER BY LOWER(Name), CreatedUtc
                """;

            var groups = new List<ViewSyncGroup>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                groups.Add(ReadGroup(reader));
            }

            return groups;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ViewSyncDevice> RegisterDeviceAsync(
        string deviceId,
        string displayName,
        bool syncEnabled,
        string? groupId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var normalizedDeviceId = NormalizeDeviceId(deviceId);
        var normalizedName = NormalizeName(displayName, "This browser");
        var normalizedGroupId = string.IsNullOrWhiteSpace(groupId) ? null : groupId.Trim();
        var effectiveSyncEnabled = syncEnabled && !string.IsNullOrWhiteSpace(normalizedGroupId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (DbTransaction)await connection.BeginTransactionAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(normalizedGroupId))
            {
                await EnsureGroupExistsAsync(connection, transaction, normalizedGroupId, nowUtc, cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ViewSyncDevices (DeviceId, DisplayName, SyncEnabled, GroupId, LastSeenUtc)
                VALUES (@deviceId, @displayName, @syncEnabled, @groupId, @lastSeenUtc)
                ON CONFLICT(DeviceId) DO UPDATE SET
                    DisplayName = excluded.DisplayName,
                    SyncEnabled = excluded.SyncEnabled,
                    GroupId = excluded.GroupId,
                    LastSeenUtc = excluded.LastSeenUtc
                """;
            DatabaseParameters.Add(command, "@deviceId", normalizedDeviceId);
            DatabaseParameters.Add(command, "@displayName", normalizedName);
            DatabaseParameters.Add(command, "@syncEnabled", effectiveSyncEnabled ? 1 : 0);
            DatabaseParameters.Add(command, "@groupId", (object?)normalizedGroupId ?? DBNull.Value);
            DatabaseParameters.Add(command, "@lastSeenUtc", FormatDate(nowUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new ViewSyncDevice(normalizedDeviceId, normalizedName, effectiveSyncEnabled, normalizedGroupId, nowUtc);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ViewSyncDevice?> GetDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        var normalizedDeviceId = NormalizeDeviceId(deviceId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT DeviceId, DisplayName, SyncEnabled, GroupId, LastSeenUtc
                FROM ViewSyncDevices
                WHERE DeviceId = @deviceId
                """;
            DatabaseParameters.Add(command, "@deviceId", normalizedDeviceId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadDevice(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ViewSyncDevice>> GetGroupDevicesAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT DeviceId, DisplayName, SyncEnabled, GroupId, LastSeenUtc
                FROM ViewSyncDevices
                WHERE GroupId = @groupId
                ORDER BY LOWER(DisplayName), LastSeenUtc DESC
                """;
            DatabaseParameters.Add(command, "@groupId", groupId.Trim());

            var devices = new List<ViewSyncDevice>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                devices.Add(ReadDevice(reader));
            }

            return devices;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UngroupDeviceAsync(
        string deviceId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var normalizedDeviceId = NormalizeDeviceId(deviceId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ViewSyncDevices
                SET SyncEnabled = 0,
                    GroupId = NULL,
                    LastSeenUtc = @lastSeenUtc
                WHERE DeviceId = @deviceId
                """;
            DatabaseParameters.Add(command, "@deviceId", normalizedDeviceId);
            DatabaseParameters.Add(command, "@lastSeenUtc", FormatDate(nowUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ViewSyncPublishResult> PublishUrlAsync(
        string deviceId,
        string relativeUrl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var normalizedDeviceId = NormalizeDeviceId(deviceId);
        if (!ViewSyncUrlPolicy.TryNormalize(relativeUrl, out var normalizedUrl)
            || normalizedUrl is null
            || ViewSyncUrlPolicy.RouteKeyFor(normalizedUrl) is not { } routeKey)
        {
            return new ViewSyncPublishResult(false, null, "URL is not eligible for view sync.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (DbTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var device = await GetDeviceAsync(connection, transaction, normalizedDeviceId, cancellationToken);
            if (device is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new ViewSyncPublishResult(false, null, "Device is not registered.");
            }

            if (!device.SyncEnabled || string.IsNullOrWhiteSpace(device.GroupId))
            {
                await TouchDeviceAsync(connection, transaction, normalizedDeviceId, nowUtc, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ViewSyncPublishResult(false, null, "Device is not grouped for sync.");
            }

            var existing = await GetGroupStateAsync(connection, transaction, device.GroupId, routeKey, cancellationToken);
            if (existing is not null && string.Equals(existing.RelativeUrl, normalizedUrl, StringComparison.Ordinal))
            {
                await TouchDeviceAsync(connection, transaction, normalizedDeviceId, nowUtc, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ViewSyncPublishResult(false, existing);
            }

            var state = new ViewSyncGroupState(
                device.GroupId,
                routeKey,
                normalizedUrl,
                (existing?.Revision ?? 0) + 1,
                nowUtc,
                normalizedDeviceId,
                device.DisplayName);
            await SaveGroupStateAsync(connection, transaction, state, cancellationToken);
            await TouchDeviceAsync(connection, transaction, normalizedDeviceId, nowUtc, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ViewSyncPublishResult(true, state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ViewSyncGroupState?> GetLatestStateForDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        return GetLatestStateForDeviceAsync(deviceId, routeKey: null, cancellationToken);
    }

    public async Task<ViewSyncGroupState?> GetLatestStateForDeviceAsync(
        string deviceId,
        string? routeKey,
        CancellationToken cancellationToken)
    {
        var normalizedDeviceId = NormalizeDeviceId(deviceId);
        var normalizedRouteKey = NormalizeRouteKey(routeKey);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (DbTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var device = await GetDeviceAsync(connection, transaction, normalizedDeviceId, cancellationToken);
            if (device is null || !device.SyncEnabled || string.IsNullOrWhiteSpace(device.GroupId))
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            var state = await GetGroupStateAsync(connection, transaction, device.GroupId, normalizedRouteKey, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ViewSyncGroupState?> GetGroupStateAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        return GetGroupStateAsync(groupId, routeKey: null, cancellationToken);
    }

    public async Task<ViewSyncGroupState?> GetGroupStateAsync(
        string groupId,
        string? routeKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return null;
        }

        var normalizedRouteKey = NormalizeRouteKey(routeKey);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (DbTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var state = await GetGroupStateAsync(connection, transaction, groupId.Trim(), normalizedRouteKey, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ViewSyncGroupState>> GetGroupStatesAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT GroupId, RouteKey, RelativeUrl, Revision, UpdatedUtc, UpdatedByDeviceId, UpdatedByDeviceName
                FROM ViewSyncGroupState
                WHERE GroupId = @groupId
                ORDER BY
                    CASE RouteKey
                        WHEN 'all' THEN 0
                        WHEN 'series' THEN 1
                        WHEN 'movies' THEN 2
                        ELSE 3
                    END
                """;
            DatabaseParameters.Add(command, "@groupId", groupId.Trim());

            var states = new List<ViewSyncGroupState>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                states.Add(ReadState(reader));
            }

            return states;
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

    private DbConnection CreateConnection()
    {
        return DatabaseConnectionFactory.Create(_options, _environment.ContentRootPath);
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

    private static async Task EnsureGroupExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string groupId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ViewSyncGroups (GroupId, Name, CreatedUtc)
            VALUES (@groupId, @name, @createdUtc)
            ON CONFLICT(GroupId) DO NOTHING
            """;
        DatabaseParameters.Add(command, "@groupId", groupId);
        DatabaseParameters.Add(command, "@name", "My devices");
        DatabaseParameters.Add(command, "@createdUtc", FormatDate(nowUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ViewSyncDevice?> GetDeviceAsync(
        DbConnection connection,
        DbTransaction transaction,
        string deviceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DeviceId, DisplayName, SyncEnabled, GroupId, LastSeenUtc
            FROM ViewSyncDevices
            WHERE DeviceId = @deviceId
            """;
        DatabaseParameters.Add(command, "@deviceId", deviceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDevice(reader) : null;
    }

    private static async Task<ViewSyncGroupState?> GetGroupStateAsync(
        DbConnection connection,
        DbTransaction transaction,
        string groupId,
        string? routeKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (routeKey is null)
        {
            command.CommandText = """
                SELECT GroupId, RouteKey, RelativeUrl, Revision, UpdatedUtc, UpdatedByDeviceId, UpdatedByDeviceName
                FROM ViewSyncGroupState
                WHERE GroupId = @groupId
                ORDER BY UpdatedUtc DESC, Revision DESC
                LIMIT 1
                """;
            DatabaseParameters.Add(command, "@groupId", groupId);
        }
        else
        {
            command.CommandText = """
                SELECT GroupId, RouteKey, RelativeUrl, Revision, UpdatedUtc, UpdatedByDeviceId, UpdatedByDeviceName
                FROM ViewSyncGroupState
                WHERE GroupId = @groupId
                  AND RouteKey = @routeKey
                """;
            DatabaseParameters.Add(command, "@groupId", groupId);
            DatabaseParameters.Add(command, "@routeKey", routeKey);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadState(reader) : null;
    }

    private static async Task SaveGroupStateAsync(
        DbConnection connection,
        DbTransaction transaction,
        ViewSyncGroupState state,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ViewSyncGroupState (
                GroupId,
                RouteKey,
                RelativeUrl,
                Revision,
                UpdatedUtc,
                UpdatedByDeviceId,
                UpdatedByDeviceName
            )
            VALUES (
                @groupId,
                @routeKey,
                @relativeUrl,
                @revision,
                @updatedUtc,
                @updatedByDeviceId,
                @updatedByDeviceName
            )
            ON CONFLICT(GroupId, RouteKey) DO UPDATE SET
                RelativeUrl = excluded.RelativeUrl,
                Revision = excluded.Revision,
                UpdatedUtc = excluded.UpdatedUtc,
                UpdatedByDeviceId = excluded.UpdatedByDeviceId,
                UpdatedByDeviceName = excluded.UpdatedByDeviceName
            """;
        DatabaseParameters.Add(command, "@groupId", state.GroupId);
        DatabaseParameters.Add(command, "@routeKey", state.RouteKey);
        DatabaseParameters.Add(command, "@relativeUrl", state.RelativeUrl);
        DatabaseParameters.Add(command, "@revision", state.Revision);
        DatabaseParameters.Add(command, "@updatedUtc", FormatDate(state.UpdatedUtc));
        DatabaseParameters.Add(command, "@updatedByDeviceId", state.UpdatedByDeviceId);
        DatabaseParameters.Add(command, "@updatedByDeviceName", state.UpdatedByDeviceName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TouchDeviceAsync(
        DbConnection connection,
        DbTransaction transaction,
        string deviceId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ViewSyncDevices
            SET LastSeenUtc = @lastSeenUtc
            WHERE DeviceId = @deviceId
            """;
        DatabaseParameters.Add(command, "@deviceId", deviceId);
        DatabaseParameters.Add(command, "@lastSeenUtc", FormatDate(nowUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ViewSyncGroup ReadGroup(DbDataReader reader)
    {
        return new ViewSyncGroup(
            reader.GetString(0),
            reader.GetString(1),
            ParseDate(reader.GetString(2)));
    }

    private static ViewSyncDevice ReadDevice(DbDataReader reader)
    {
        return new ViewSyncDevice(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2) != 0,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            ParseDate(reader.GetString(4)));
    }

    private static ViewSyncGroupState ReadState(DbDataReader reader)
    {
        return new ViewSyncGroupState(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            ParseDate(reader.GetString(4)),
            reader.GetString(5),
            reader.GetString(6));
    }

    private static string NormalizeDeviceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Device id is required.", nameof(value));
        }

        return value.Trim();
    }

    private static string NormalizeName(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string? NormalizeRouteKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "all" => "all",
            "series" => "series",
            "movies" => "movies",
            _ => null
        };
    }

    private static string FormatDate(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseDate(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
