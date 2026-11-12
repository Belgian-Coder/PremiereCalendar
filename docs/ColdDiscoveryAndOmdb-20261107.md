# Cold discovery and OMDb recovery

Cold discovery previously held a source slot while optional ratings, artwork and
availability finished. In the investigated week, the SIMKL feed bodies completed
in about 0.47 seconds, but two already week-filtered batches spent 18.99 and
51.27 seconds mapping. TMDb later-page requests also waited behind optional
mapping; the final day/language source started about 68.6 seconds after the first.
These overlapping durations must not be added into a total elapsed time.

The implementation separates verified core discovery from optional enrichment.
All selected sources, pages, release occurrences and provenance remain required.
Readiness is distinct from final completion, and successful durable source
checkpoints remain behind complete enrichment. Profiles whose membership needs
optional fields retain the complete mapping path. Forced full-provider refreshes
retain their existing semantics.

OMDb returned HTTP 200 with `Response=False` for the 24 reported items: 18
`Error getting data.` and six `Incorrect IMDb ID.` errors. Each stored IMDb ID
agrees exactly with the cached TMDb details for the same identity. That agreement
does not prove the IMDb page exists; no identity replacement is justified.

The two errors now have separate failure reasons and per-ID retry deadlines:
one hour for retrieval failure and 24 hours for rejected IDs. Prior verified
metadata and its freshness timestamps are preserved. Deadlines persist across
restart, are partitioned by endpoint and credential hash, and do not open a
provider-wide circuit. Explicit force can retry earlier. Successful force clears
the deadline, and a late older request cannot republish it. Upstream data may
remain unavailable; this change does not fabricate ratings or treat these errors
as verified title absence.

Overlapping loads that shared core work can reuse the same completed enrichment
calculation. This is a bounded, session-owned proposal; every consumer still
passes the existing generation, revision and accepted-value checks before
publishing it. Sharing ends when the last session detaches. Cancelled callers
do not cancel another caller, and failed persistence does not leak these owners.

Review fixes preserve partial results when readiness validation detects a newer
generation, and exclude the presentation milestone from provider diagnostics.
OMDb recovery clears both memory and durable failures using a scoped successful
observation, including tied clocks and failed clear writes. A different credential
or endpoint cannot supersede the current scope. Old good-cache JSON stays readable.

Local validation: 1,905 passed and one expected Linux file-lock skip: 1,118 unit,
195 component and 592 integration. PostgreSQL cases executed in isolated
databases. The initial integration container lacked a writable development
bootstrap log directory; all 592 integration cases passed after adding a tmpfs
and rerunning without rebuilding. Original failures and DLL hashes are retained.

Matched local fixtures compare installed-source baseline `8dcbcbb` with the
validated candidate. All caches start empty; identical fake provider responses,
delays and concurrency pools run sequentially with network access disabled.
Build and restore time are excluded. Each scenario has one measured iteration.

| Fixture | Calendar ready before / after | Final before / after | Rows |
|---|---:|---:|---:|
| Movies | 4.322 / 1.881 s (2.30x) | 4.324 / 2.431 s (43.8% less time) | 28 |
| Movies + new Series | 5.686 / 2.908 s (1.96x) | 5.723 / 4.359 s (23.8% less time) | 42 |

Complete core coverage improved from 4.132 to 1.878 seconds for movies and 5.415
to 2.886 seconds for mixed premieres. First verified rows were 175/164 ms for
movies and 741/820 ms for mixed premieres. This is not a percentile study.

Provider calls and normalized final business projections match byte for byte.
Movies make 28 discovery and 28 details requests, plus 28 calls to each optional
fixture provider. Mixed premieres make 32 discovery and 56 details requests,
plus 42 calls per optional provider. No results or providers were removed.
The comparison normalizes observation clocks and internal enrichment versions,
and orders rows by identity and set-like collections by value; no business
values are removed. It does not prove identical collection ordering.

These fixture results do not establish that the historical live 77-second load
now takes a particular duration. Independent reviews, exact-container and
installed acceptance remain separate pending checks. Builds, publication and
installation run locally without GitHub Actions.
