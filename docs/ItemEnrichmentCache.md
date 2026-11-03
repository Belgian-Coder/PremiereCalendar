# Shared item enrichment cache

The item enrichment cache stores provider-enriched fields in the database by media type, TMDb ID, and effective provider-settings partition. A verified movie or series can reuse its metadata and ratings when it appears in another week or calendar profile. Calendar week files remain separate snapshots with their own freshness and cleanup rules.

## Freshness and retention

Defaults in `ItemEnrichmentCache` are:

| Setting | Default | Meaning |
|---|---:|---|
| `Enabled` | `true` | Enables shared item records. When disabled, calendar requests use the existing per-load enrichment path; week files remain independent. |
| `MetadataRefreshDays` | 7 | Age limit for metadata, including identifiers, runtime, and artwork fields. |
| `RatingsRefreshHours` | 24 | Age limit for external ratings. Ratings are also tied to the metadata identity they describe. |
| `RetentionMonths` | 3 | Removes item records whose `LastSeenUtc` is older than three calendar months. |
| `MetadataFailureRetryMinutes` | 5 | Cooldown before retrying a failed metadata/availability refresh. |
| `RatingsFailureRetryMinutes` | 5 | Independent cooldown before retrying failed external scores. |

Metadata and ratings have separate successful-fetch timestamps. A failed provider request does not advance its component's timestamp. Successful empty results, such as a confirmed not-found response or a valid empty list, remain distinct from rate limits, invalid payloads, and transient failures.

Failure cooldowns are bounded in-memory deadlines, separate from successful-fetch timestamps. A completed discovery can still be displayed during an optional provider outage. Explicit provider refresh bypasses these cooldowns; restart permits a new attempt. Provider rate limits and circuit breakers remain in effect.

The file-based calendar cache defaults to 30 hours of freshness. That setting controls whether a saved week snapshot is fresh; it is not the database item-record retention period. Expired week files can be rebuilt while their shared item metadata remains available for reuse.

## Selective change tracking and concurrency

The background delta service checks TMDb movie and TV change feeds and TVmaze show updates. It invalidates known cached metadata affected by those feeds rather than treating every weekly rediscovery as a change. TVmaze uses its day, week, or month update feed according to the age of its last successful watermark. Feed or history failures do not advance that watermark; ordinary reads still refresh metadata when its freshness window expires.

Fetch timestamps are recorded in UTC when provider work is reserved, so a slow request does not make an older response appear freshly fetched at completion. `LastSeenUtc` only moves forward. Delta invalidation increments a record generation, and writes must match the generation they read; an older in-flight refresh cannot overwrite a newer invalidation. Ratings are reused only while they still match the current metadata identity.

## Storage and background work

Database schema 4 adds the normalized item enrichment store. Startup imports recent verified premiere items from existing calendar snapshots with bounded work; ordinary calendar reads can import additional items lazily. Database maintenance removes inactive item records outside the configured calendar-month retention window.

At 02:00 Europe/Brussels, nightly warmup prepares the current and next calendar month for the default Series and Movies profiles. It creates durable week jobs, uses normal provider/cache freshness rules, and records job outcomes, row counts, and durations. The nightly snapshots and the shared database cache serve different lifetimes: a refreshed or expired week file does not require discarding still-retained item enrichments.

## Upgrade and fallback

Schema 4 adds the item table and indexes while preserving existing tables and migration history. Verify a pre-upgrade database backup and an isolated migration before deployment. A schema-4-capable image can use `ItemEnrichmentCache__Enabled=false` to fall back to the existing per-load enrichment path while retaining the stored records. The former schema-3-only image requires a separately planned database recovery; disabling the feature does not undo the migration. Never restore over live data automatically.

Manual score backfill and external-ID repair update the shared item projection after their week-file changes are accepted. These narrow repairs preserve unrelated metadata and the original component success clocks. Cache prepare/read, item hit/due decisions, writes, reconciliation, import, provider checks and job durations are recorded in the existing operational/performance logs.
