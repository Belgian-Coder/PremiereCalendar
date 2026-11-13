using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed record ProviderWorkCheckpointMaintenanceResult(
    bool Success, int ConvertedCheckpoints, long PreviousBytes, long LegacyBytes);

/// <summary>Converts checkpoint storage for an offline application downgrade without changing job state.</summary>
public sealed class ProviderWorkCheckpointMaintenance(
    IOptions<AppDatabaseOptions> options,
    TimeProvider timeProvider)
{
    public async Task<ProviderWorkCheckpointMaintenanceResult> PrepareLegacyCheckpointsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionFactory.Create(options.Value);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var tableLock = connection.CreateCommand())
        {
            tableLock.Transaction = transaction;
            // This also blocks SELECT FOR UPDATE claims. Existing claims finish
            // before the lock is granted and are then caught by the lease check.
            tableLock.CommandText = "LOCK TABLE ProviderWorkJobs, ProviderWorkLeases IN EXCLUSIVE MODE";
            await tableLock.ExecuteNonQueryAsync(cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        await using (var leases = connection.CreateCommand())
        {
            leases.Transaction = transaction;
            leases.CommandText = "SELECT LeaseExpiresUtc FROM ProviderWorkLeases";
            await using var reader = await leases.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(0) || !DateTimeOffset.TryParse(reader.GetString(0), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var deadline) || deadline > now)
                    throw new InvalidOperationException("Stop provider workers and wait for active leases to expire before preparing legacy checkpoints.");
            }
        }

        var converted = 0;
        long previousBytes = 0, legacyBytes = 0;
        string? previousJobId = null;
        while (true)
        {
            string jobId, checkpoint;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                // One payload at a time keeps memory bounded even with a large
                // retained job history. The transaction protects the whole rewrite.
                select.CommandText = $"""
                    SELECT JobId, CheckpointJson FROM ProviderWorkJobs
                    WHERE CheckpointJson IS NOT NULL {(previousJobId is null ? string.Empty : "AND JobId > @after")}
                    ORDER BY JobId LIMIT 1
                    """;
                if (previousJobId is not null) DatabaseParameters.Add(select, "@after", previousJobId);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) break;
                jobId = reader.GetString(0);
                checkpoint = reader.GetString(1);
            }
            previousJobId = jobId;
            if (string.IsNullOrWhiteSpace(checkpoint)) continue;
            using var outer = JsonDocument.Parse(checkpoint);
            if (outer.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("A checkpoint is malformed; no checkpoints were converted.");
            if (!outer.RootElement.TryGetProperty("Version", out var version)) continue;
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number is < 1 or > 4)
                throw new InvalidOperationException("A checkpoint version is unsupported; no checkpoints were converted.");
            if (number != 4) continue;
            using var inner = ProviderWorkCheckpointCodec.Decode(checkpoint);
            // Preserve every inner JSON property, including properties unknown to
            // the current model; deserializing and serializing would discard them.
            var legacy = inner.RootElement.GetRawText();
            if (ProviderWorkScheduler.TryReadCheckpoint(legacy) is null)
                throw new InvalidOperationException("A compressed checkpoint is malformed; no checkpoints were converted.");
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE ProviderWorkJobs SET CheckpointJson = @legacy
                WHERE JobId = @id AND CheckpointJson = @previous
                """;
            DatabaseParameters.Add(update, "@legacy", legacy);
            DatabaseParameters.Add(update, "@id", jobId);
            DatabaseParameters.Add(update, "@previous", checkpoint);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Checkpoint ownership changed; no checkpoints were converted.");
            converted++;
            previousBytes += Encoding.UTF8.GetByteCount(checkpoint);
            legacyBytes += Encoding.UTF8.GetByteCount(legacy);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(true, converted, previousBytes, legacyBytes);
    }
}
