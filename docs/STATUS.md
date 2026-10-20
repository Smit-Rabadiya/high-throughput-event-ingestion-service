# Development Status

Last updated: 2026-10-20

## Where we are
v1.0.0: all 10 plan steps + production hardening + approved out-of-scope extras are done. 108 tests pass. Remaining: build/smoke-test the Docker image in an environment with Docker; load/soak test on target hardware.
Plan: `PROJECT_PLAN.md`. Rules: `../CLAUDE.md`, `REPO_RULES.md`. Requirements PDF is kept locally only (`docs/reference/Practical.pdf`, git-ignored).

## Remote
https://github.com/Smit-Rabadiya/high-throughput-event-ingestion-service (public, only the owner can push). Push after every step. `gh` CLI installed and logged in; git uses it as credential helper.

## Done
- [x] Step 0: plan + decisions, git repo, rules, GitHub repo created and first commits pushed
- [x] Step 1: solution `EventIngestion.slnx` + Core / Api / Tests / Benchmarks, `Directory.Build.props` (nullable, warnings as errors)
- [x] Steps 2-4: `DeviceWindow` (exact sliding window, monotonic min/max queues), `DeviceStore` (span alternate lookup, `SweepIdle`), models, options
- [x] Step 5: `ReadingStreamParser` (PipeReader + resumable Utf8JsonReader, pooled id buffer, 50,000 cap, bad readings counted as invalid, unknown/nested fields skipped by depth tracking)
- [x] Step 6: endpoints (POST /api/v1/readings, GET /api/v1/devices/{id}/aggregates, /health), Problem Details, 16 MiB body cap, integration tests; API docs + Postman / Bruno / .http in docs/api/ (verified live with curl)
- [x] Step 7: EvictionSweeper BackgroundService (30 s) + tests
- [x] Step 8: ConcurrencyTests (16 threads one device: exact count/min/max/avg; 8 threads overlapping devices vs sequential oracle; queries during ingest never torn and count monotonic; sweeper same-instant loses nothing; sweeper retiring everything races writers: no failures, no leaked Retired)
- [x] Step 9: BenchmarkDotNet (enchmarks/), results in BENCHMARK_RESULTS.md: streaming vs naive deserialize-list = ~27% faster, ~3.6x less allocation (warm), query ~39 ns / 0 B
- [x] Step 10: README.md (build/run/test/bench + assumptions), docs/DESIGN.md (all 5 PDF topics), query-string aggregates endpoint for ids with /, API docs/collections synced
- [x] 72 tests passing (window, parser, API integration, sweeper, concurrency); concurrency tests re-run 3x, stable

## Parser behaviour decisions (to put in README)
- Elements failing validation are counted `invalid` and skipped; readings before a syntax error stay applied and are reported with the error.
- Device id: non-empty, max 256 chars. Property names are exact camelCase; duplicate keys: last wins. Content after the closing `]` is ignored.
- One clock read per batch (`now`) so a whole batch sees one consistent window.

## Next
- [ ] Step 6: `POST /api/v1/readings`, `GET /api/v1/devices/{id}/aggregates`, `/health`, Problem Details, body-size limit, integration tests
- [ ] 7: EvictionSweeper (BackgroundService); 8: concurrency tests; 9: benchmarks; 10: README / DESIGN / BENCHMARK_RESULTS, final review vs PDF

## Notes
- Run tests: `dotnet test` from repo root.
- ArrayPool used only for the parser's id buffer; Core uses plain arrays (revisit only if benchmarks show a win).
- Local git identity: SMIT <gencore.ai.labs@gmail.com> (repo-local).

## API notes
- API reference and client collections: docs/api/API.md, docs/api/postman/, docs/api/bruno/, docs/api/requests.http. Keep them in sync when endpoints change.
- A / inside a deviceId cannot be addressed via the URL path (documented assumption).
- Response field is verage (matches the PDF wording).

- Open spec gap: the PDF says nothing about characters in deviceId (only sensor-0042 example). Plan: add GET /api/v1/devices/aggregates?deviceId= alongside the path form so ids containing / are queryable (do in step 10 docs pass).


## Decisions to carry into README / DESIGN.md (step 10)
- **Restart/outage behaviour:** state is in-memory only (PDF section 6 puts persistence/recovery out of scope). Readings sent while the service is down are not recorded by it; after restart it starts empty. Devices that buffer and retry get their readings accepted if timestamps are within the last 5 minutes; older ones are stale. Retried batches can be double-counted (no reading id to dedupe; assumption: duplicates are counted). Scaling/durability answer for DESIGN.md: durable partitioned log (e.g. Kafka) in front, replay on restart.
- Add query-string form of the aggregates endpoint for ids containing /.
- DESIGN.md must cover the 5 PDF topics: data structures, allocation minimisation, concurrency model + correctness argument, windowing + tradeoffs, scaling beyond one instance.


## Final check against Practical.pdf
- 3.1 ingest (50k cap, streaming, accepted count): done. 3.2 query + unknown/empty device: done. 3.3 continuous window, aging out, out-of-order: done.
- 4.1 allocation-conscious + justified (DESIGN s2, benchmark): done. 4.2 per-device locks, tests: done. 4.3 bounded memory (eviction + sweeper), single process: done.
- 5 deliverables: README, DESIGN.md, BENCHMARK_RESULTS.md + benchmark project, tests: done. 6 out-of-scope items not built. 7 git history + assumptions (README table): done.


## Post-completion audit (2026-10-20)
- Release build clean (warnings as errors). 72 tests pass.
- End-to-end over real Kestrel: 50,000 readings -> 200 accepted=50000 (~0.25-0.45 s, ~75 MB working set after 3 batches); 50,001 -> 413.
- Body cap raised to 32 MiB; global.json pins SDK 10 (rollForward latestFeature); PROJECT_PLAN.md now lists deviations as built.
- **Actual requirements: complete.** Next, if wanted: out-of-scope extras, in this order of value: (1) idempotent retry / dedupe key, (2) optional durable write-ahead log + replay on restart, (3) rate limiting / auth.


## Production hardening (2026-10-21)
- Added: device cap (Window:MaxDevices, ejected.deviceLimit), typed config + fail-fast validation, /health/ready with shutdown draining, System.Diagnostics.Metrics, source-generated logging, central package versions (Directory.Packages.props), docs/OPERATIONS.md. 78 tests pass.
- Next (approved out-of-scope, in order): idempotent retries -> optional durable journal + replay -> rate limiting + API-key auth.


## v1.0.0 release work (2026-10-21)
- Added: idempotent retries (Idempotency-Key), API-key auth with scopes, per-client rate limits + global ingest concurrency cap, trusted-proxy config, durable write-ahead journal with crash-safe replay, /version, SemVer 1.0.0 (+git sha), CHANGELOG.md, packages.lock.json, Dockerfile, docker-compose, .env.example, ppsettings.Production.json (auth/rate-limit/journal on, JSON logs, fail-closed), docs/OPERATIONS.md rewritten.
- Verified on real Kestrel in Production profile: fail-closed without keys; 401/403/200; idempotent replay; 429 after limit; hard-kill then restart restores data from journal; JSON logs; /version shows sha; no Server header.
- **Not verified:** Docker image build (Docker not installed here). Do this first on a machine with Docker / in CI.
- Process fix: a commit once went out with 20 failing tests (4d8d702 fixed it). Rule 8 in CLAUDE.md now requires green tests before every commit.


## Audit vs Practical.pdf + TLS (2026-10-21, committed in 8b69d5a and 2c50047)
- Line-by-line audit of the PDF: every requirement and deliverable present. Found and fixed one real bug: timestamps without Z/offset were parsed as server-local time (new TimestampParsingTests). Added TLS enforcement (Tls:RequireHttps, TLS 1.2/1.3 only) + docs.
- Committed and pushed. Consider releasing 1.0.1 (bump Version, move CHANGELOG [Unreleased], tag).
- Contributor identity: the owner cannot verify the gmail on GitHub, so commits use Smit Rabadiya <76813205+Smit-Rabadiya@users.noreply.github.com> (repo-local git config) and link to the GitHub account. Commits before 8b69d5a still show the gmail address and are not linked to the account (history is not rewritten).
- Done: Swagger/OpenAPI. UI /swagger + spec /openapi/v1.json (dev on, Production off via Docs:Enabled), committed docs/api/openapi.json with drift-guard test, Microsoft.AspNetCore.OpenApi 10.0.13 + Swashbuckle.AspNetCore.SwaggerUI 10.3.1 (pinned). Version stays 1.0.0 for now (owner's decision); release 1.1.0 (UTC fix, TLS, Swagger) only when asked.




