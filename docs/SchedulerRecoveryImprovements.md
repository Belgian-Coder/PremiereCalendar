# Scheduler and recovery behavior

PostgreSQL startup retries network, timeout and explicitly transient database errors.
Authentication, unsupported schemas, migration failures and other non-transient errors
fail startup visibly. Recovery runs as a supervised hosted service with a bounded
attempt timeout and exponential backoff; host shutdown cancels the attempt and wait.
The work scheduler waits for a successful initialization signal, so a temporary DNS
or PostgreSQL outage no longer leaves it permanently stopped.
Prefetch, delta sync, warmup, IMDb refresh and the one-time cache backfill wait for
the same signal before using database tables. Their existing startup delays remain
in effect, and shutdown cancels a producer waiting for initialization. An unexpected
best-effort prefetch failure is logged without stopping foreground hosting.

| Configuration | Default | Purpose |
| --- | --- | --- |
| `AppDatabase:RecoveryRetrySeconds` | 5 | Initial recovery delay |
| `AppDatabase:RecoveryMaximumRetrySeconds` | 60 | Maximum recovery delay |
| `AppDatabase:RecoveryAttemptTimeoutSeconds` | 60 | Recovery attempt budget |
| `ProviderScheduler:IdlePollSeconds` | 60 | Fallback scan for externally queued work |
| `ProviderScheduler:CountRefreshMilliseconds` | 1000 | Minimum interval between count queries |
| `ProviderScheduler:ForegroundInterestTimeoutSeconds` | 5 | Maximum watcher cleanup duration |

Local enqueue wakes both workers through a shared generation signal. A waiting
worker also schedules its next wake against the earliest persisted retry deadline.
Expired leases are still recovered every five seconds and wake workers immediately
when recovery changes queue state. The same pass also detects due work inserted by
another process, preserving the existing five-second cross-process detection bound.
Count refreshes share one query and are throttled;
they remain diagnostic and cannot prevent a durable job transition.

Adjacent prefetch replaces pending local requests when navigation changes. The
durable path retires obsolete queued or retrying adjacent jobs, checks the exact
range and criteria for a complete fresh cache snapshot before enqueue, and retains
running jobs and promoted foreground demand. Filters are cloned before queuing.
Completed provider results and owned cache publication retain their existing lease
and generation fences.

A later non-forced calendar job can inherit fresh successful source coverage from
the newest terminal partial attempt with exactly the same kind, key and serialized
request payload. Provider completion timestamps remain unchanged, failed source
summaries are excluded, and the old final projection replays as a non-final stale
cache observation. Changed criteria, forced refresh, expired/incompatible checkpoints
and a newer full success prevent reuse. At equal priority, retrying windows rejoin
the ready queue at their retry time, allowing already pending windows to run first.

Detaching or making an observation passive demotes demand without cancelling a
running foreground calendar job. Its existing provider requests can finish and
publish their checkpoint. Observation scopes release only their own subscription;
other active browsers retain foreground priority. An already passive scope never
reacquires foreground priority while attaching. A fresh active enqueue or watcher restores foreground
priority. Interest reconciliation has a timeout, host cancellation and a three-pass
limit, so continual watcher churn cannot hold cleanup indefinitely.
If a different foreground request arrives, running work whose current demand has
become passive can be checkpointed and preempted to free a worker. Its historical
job kind does not protect it from that handoff; another active watcher still does.
This also applies when the new request was queued before the previous observation
became passive: releasing the final active interest rechecks due foreground demand
under the dispatch gate, with the maintenance pass as a bounded recovery fallback.

Delta synchronization uses independent successful-check cadences for movie, series
and TVmaze feeds. A failing media feed does not prevent the other feeds from running.
Each provider/media cycle has a time budget. TMDb also limits history items per
cycle and saves successful history lookups in a bounded batch session in the
existing provider state table. Retries reuse that immutable feed window and its
successful lookups; the global watermark advances only after all history checks
and invalidations finish. No database migration is required.

| `ProviderDeltaSync` option | Default |
| --- | --- |
| `TmdbIntervalMinutes` | 60 |
| `TvmazeIntervalMinutes` | 60 |
| `ProviderBudgetSeconds` | 90 |
| `ContinuationDelaySeconds` | 60 |
| `HistoryMaximumItemsPerCycle` | 500 |
| `HistoryBatchSize` | 100 |
| `ChangeHistoryConcurrency` | 4 |

Reaching a history item budget, or a time budget after successful progress, schedules
a persisted continuation deadline without consuming failure attempts or recording
a provider failure. Calls before that deadline do no additional history work.
Malformed, incompatible or expired history sessions are logged and rebuilt from
the feed; they cannot stop the other media/provider feeds from running.

TMDb history is skipped only when every local partition is already dirty; the
current feed observation still invalidates the generation so a concurrent repair
cannot erase the newer observation. TVmaze item markers and targeted invalidations
are restricted to locally known show IDs; global feed count and watermark retain
worldwide coverage information. Unknown show reservations keep their separate fence.
Unexpected programming/schema errors are not reported as ordinary provider outages.

Focused tests cover recovery signalling, connection-refusal recovery against an
isolated PostgreSQL database, unsupported schemas, shutdown, idle enqueue/retry wakes,
watcher churn, passive observations, stale prefetch retirement, cache checks, history
resume, cycle budgets and locally relevant TVmaze markers. These are implementation
checks; production performance and live outage recovery require separate acceptance.
