# Speed and reliability validation

Validation date: 2026-10-26. Baseline source: `dc50724671aa9165c96d1ab114d912f00a5d1ead`.

## Automated acceptance

Windows Release acceptance passed all 736 tests in one solution invocation with MSBuild project parallelism limited to one (`-m:1`): 392 unit, 158 component, 184 integration, and 2 browser tests. The final one-branch frame-limit classification correction then passed all 185 integration tests, including its new regression; the current test inventory is 737. A final test-only correction to the read-only metadata fixture passed on both Windows and Linux. PostgreSQL tests executed against a dedicated PostgreSQL 18.6 container and created/dropped isolated databases. Four PostgreSQL cases cover concurrent enqueue/claim, stale-owner persistence, and cache publication when the original database backend is terminated. Missing PostgreSQL configuration now produces explicit skips.

Desktop and mobile browser checks retained the existing first-card, day-switch (≤750 ms), rolling-window, mounted-card (≤40), DOM (<2,000 nodes), layout, accessibility, and browser-error budgets. An earlier parallel run passed all 724 non-browser tests and desktop browsing; mobile rolling-window navigation took 4,264 ms against the unchanged 3,000 ms budget. Both an isolated browser rerun and the final sequential-project solution run passed both cases. This timing sensitivity remains visible in the evidence; no assertion was relaxed. No production service or live provider was exercised.

The scheduler tests cover cancellation and provider recovery, cumulative successful-source checkpoints, failed-source exclusion, restart before lease expiry, independent renewal, ownership loss, deferred cooldowns, queued priority changes, slow subscribers, and late replay. Image tests cover shared limits, content hashes, stale fallback, pixel bounds, corrupt metadata, and atomic replacement with open readers. Enrichment tests cover batching, deduplication, preservation, bounded workers, failure isolation, and cancellation.

## Controlled performance workload

The same test workload runs against an archived baseline and the changed source: 2,000 movie rows across seven days, fake provider clients, and both real scheduler workers occupied for 300 ms. Daily requests return only their day's rows. Test-only SQLite triggers count scheduler row mutations; these are not physical disk writes. The outer observation budget is 90 seconds so the slower baseline can finish; product request limits are unchanged. A complete fresh-cache load must create no additional provider job or provider client calls. Timings describe this deterministic Windows Debug fixture, not production latency.

The isolated before/after runs produced these results (milliseconds):

| Cache state | Baseline first result | Changed first result | Baseline completion | Changed completion |
| --- | ---: | ---: | ---: | ---: |
| Fresh | 544.95 | 105.00 | 1,578.37 | 105.30 |
| Stale | 384.88 | 40.89 | 27,642.50 | 11,026.17 |
| Cold | 392.02 | 516.93 | 23,224.12 | 5,134.09 |

Fresh-cache scheduling fell from one added job to zero. Neither version made provider client calls for a fresh cache. Cold and stale runs made seven daily discovery calls and returned all 2,000 rows. Cold first-result latency increased by 125 ms in these individual runs while completion improved; these are controlled observations, not production percentiles.

| Cache state | Allocated bytes before / after | Job row mutations before / after | Lease row mutations before / after | Checkpoint changes before / after |
| --- | ---: | ---: | ---: | ---: |
| Fresh | 74,441,088 / 14,145,936 | 8 / 0 | 7 / 0 | 3 / 0 |
| Stale | 6,684,417,304 / 574,009,384 | 219 / 18 | 218 / 16 | 214 / 11 |
| Cold | 3,417,469,120 / 231,813,856 | 218 / 18 | 217 / 16 | 213 / 11 |

Allocation measurements use the process-wide counter. SQL counters include inserts, updates, and deletes observed until the calendar returns, including the two deliberately occupied jobs when they finish within that interval. Checkpoint counts record changed checkpoint JSON. These figures are fixture evidence, not per-request production memory or physical disk-I/O accounting.

The final APNG, image-error classification and scratch-retention corrections leave the calendar/scheduler paths and measurement fixture unchanged. The final full Release acceptance run also exercised all three workload cases.

The baseline's fresh case intentionally fails the new assertion requiring no added provider job; its timing and 2,000 returned rows were recorded before that assertion. Baseline cold/stale and all three changed cases passed. Source product code in the baseline archive was unchanged.

Adversarial reviews use independent fresh Claude Code Opus 5.5 high and GPT-6 Astra high sessions. Their first passes identified cross-component gaps beyond the initial tests. Fixes add lease-locked calendar publication (including warmup), immutable image generations and atomic metadata pointers, real handler-owned HTTP deadlines, aggregate animation bounds, safe incremental metadata parsing, terminal replay race handling, stale fallback on deferred forced refresh, request-preserving backfill criteria, best-effort watcher cleanup, and timestamped checkpoint source expiry. New regressions passed in the integrated run. The bounded resource check completes 100 jobs with late subscriptions and returns retained broadcasts to zero each time; a separate slow-reader test coalesces 5,000 updates without losing source summaries or the final result.

Additional targeted review covers the separate IMDb download/import budgets and the image-library replacement. The final source/test patch SHA-256 is `494430e70d9c3af968e30134aa43cdcb7f1a0083faf0bedda3df0e4e9582b171`; the product-only patch SHA-256 is `025dff8b61d5d968863205ac983f94f7cd179a9493410e8b1fd60486a2499d4a`. Astra confirmed and reproduced APNG frame loss in the first replacement candidate; the correction preserves original APNG bytes with bounded validation. Further fixes classify native-capacity failures as transient, prove native policy enforcement, and clean old owned IMDb scratch files after Linux hard termination. A real 1,001-frame GIF reproduced Claude's final hard-limit classification finding; that deterministic failure remains a permanent input error. Astra's final limited reviews found no actionable introduced defects and verified the exact APNG fixture with ImageSharp (505 bytes, two frames, 100×100 canvas).

Executable before/after comparisons rejected two other review hypotheses as baseline regressions: both libraries already write multi-page TIFF as multi-frame WebP, and ImageSharp 3.1.12 did not support ICO. The policy-rewrite hypothesis was rejected against the exact Magick.NET 15.0.0 source, which skips existing configuration files. No unrelated format behavior was changed for those hypotheses.

Final limited re-reviews are clean from both GPT-6 Astra high and fresh, read-only Claude CLI sessions with `--model claude-opus-5-5 --effort high`. Claude's recorded model usage confirms `claude-opus-5-5`. The reviews cover the classifier/native GIF regression and the final test-only platform correction; preceding reviews cover the APNG, policy, capacity and IMDb corrections. Review artifacts are `claude-free-image-review.json`, `claude-final-candidate-review.json`, `claude-delivery-candidate-review.json`, `claude-platform-fixture-review.json`, and corresponding Astra reports in the local validation directory.

## Image dependency and container acceptance

ImageSharp is removed. The app pins `Magick.NET-Q8-AnyCPU` to exactly `15.0.0`, using its Apache-2.0 licensed wrapper and bundled ImageMagick runtime. This dependency requires no license key or licensing fee. The published app includes the upstream licenses and notices in `ThirdPartyNotices/`. [Upstream license](https://github.com/dlemstra/Magick.NET/blob/main/License.txt), [cross-platform documentation](https://github.com/dlemstra/Magick.NET/blob/main/docs/CrossPlatform.md).

The current NuGet audit reports no known vulnerable packages in the app's direct or transitive dependency graph; audit warnings were not suppressed. The image checks cover TIFF, basic ICO, TGA, JPEG, PNG, WebP and portable raster formats, animation timing and repetition, mislabeled MIME types, malformed/nonraster input, and native policy enforcement. APNG retains its bounded original PNG bytes after structural and default-image validation, preserving animation while skipping server resizing. Native capacity exhaustion falls back to stale images or a retryable 503. SkiaSharp was rejected because it lacks TIFF decoding for original Wikimedia URLs.

The original Dockerfile built the Release candidate `premierecalendar:reliability-release-ef654b28a730`, image ID `sha256:25601ddfc171d9e75656572877f2a4d8aebd37c1f7e2f7d600326c0cabd11cd8`. The image label and `/health/version` report source snapshot `ef654b28a730594df5215b0ebcf7f45c6e360d42a1469cb6fe9efb11e9ccc807`, build ID `ef654b28a730`, and version `0.0.0-validation`. Product patch bytes were compared directly and are identical to the final tree; only the platform-sensitive test changed after that image build.

The task-owned app ran with UID/GID 1654, a read-only root filesystem, writable application data, PostgreSQL schema 3, and background provider work disabled. Readiness, version, root HTML and Blazor JavaScript returned HTTP 200; the JavaScript body was 235,927 bytes. The published image contained its native musl library, raster policy and all three notice files. `ldd` found no missing dependencies.

Linux Release validation ran as UID 1654 from an immutable source archive, with writable nested build artifacts and isolated log/data mounts. Combined final evidence is **734 passed, one expected Windows-only file-lock skip, no unresolved failures**: 391 unit passes, 158 component passes, and 185 integration passes. Earlier runs exposed test-harness root/log path permissions and an incorrect Windows-only read-only-file assumption. The corrected fixture retains Windows failed-commit assertions and verifies Unix atomic replacement, old-body immutability and metadata/hash consistency; its Linux rerun passed. No application behavior was changed to satisfy those harness issues.

Local raw evidence is retained under `PremiereCalendar.Build.RootValidation` beside the checkout and the task temporary validation directory. Key Linux logs are `linux-release-unit-ef654b28a730.log`, `linux-release-component-ef654b28a730.log`, `linux-release-integration-ef654b28a730.log`, and `linux-platform-fixture-494430e70d9c.log`.

Temporary task containers, volumes, network, intermediate images and the PostgreSQL password file were removed after acceptance. The final Release image, logs and source snapshots remain available. Unrelated resources were unchanged.

This is container and automated-fixture acceptance on Windows/Linux x64. Production latency, load and provider outage behavior have not been measured in production. Production deployment is outside this change.
