# Operations Guide

Version: see `GET /version` and `CHANGELOG.md` (SemVer, set in `Directory.Build.props`).

## Configuration
Sources, lowest to highest priority: `appsettings.json` -> `appsettings.{Environment}.json` -> **environment variables** (`Section__Key`, arrays as `Section__List__0__Field`) -> command line. All values are validated at start-up; a bad value stops the process with a clear message. **Never put secrets in a committed file** (see `REPO_RULES.md`).

| Setting (env var) | Default | Meaning |
|---|---|---|
| `Window__Window` | `00:05:00` | Sliding window length |
| `Window__FutureSkewTolerance` | `00:01:00` | Readings further ahead are `future` |
| `Window__MaxDevices` | `100000` | Cap on tracked devices; extra *new* devices are reported as `rejected.deviceLimit` (memory-exhaustion guard) |
| `Service__SweepInterval` | `00:00:30` | Idle-device / expired-key / old-segment sweep period |
| `Service__MaxRequestBodyBytes` | `33554432` | Kestrel body cap |
| `Idempotency__Enabled` / `Ttl` / `MaxEntries` | `true` / `00:15:00` / `100000` | `Idempotency-Key` memory (see API.md) |
| `Auth__Enabled` | `false` (`true` in Production) | API-key authentication |
| `Auth__Keys__N__Name` / `KeySha256` (or `Key`) / `Scopes__M` | none | Keys. Scopes: `ingest`, `query`. Prefer `KeySha256` so the secret is not in the environment. Min 24 chars for plain keys |
| `RateLimiting__Enabled` | `false` (`true` in Production) | Per-client limits |
| `RateLimiting__IngestPermitsPerWindow` / `QueryPermitsPerWindow` / `Window` | `600` / `6000` / `00:01:00` | Per client (API-key name, else IP) |
| `RateLimiting__MaxConcurrentIngests` / `ConcurrentIngestQueue` | `64` / `16` | Global cap on batches processed at once; excess queues then gets 429 |
| `Proxy__Enabled`, `Proxy__KnownProxies__N`, `Proxy__KnownNetworks__N` | off | Trust `X-Forwarded-For/Proto` only from these proxies (otherwise clients can spoof IPs and dodge rate limits) |
| `Journal__Enabled` | `false` (`true` in Production) | Write-ahead journal + replay on restart |
| `Journal__Directory` | `data/journal` (`/var/lib/ingestion/journal` in Production) | Put on a persistent volume |
| `Journal__Durability` | `Fsync` | `Fsync`: ack means on disk (group-committed). `Buffered`: ack immediately, fsync every `FsyncInterval` (default 1 s), power loss can lose that interval |
| `Journal__SegmentDuration` / `QueueCapacity` | `00:01:00` / `256` | Segment roll period / writer backpressure queue |

## Environments
- **Development / default**: auth, rate limiting and journal **off** (so `curl` just works). Start-up logs a warning for each disabled protection.
- **Production** (`ASPNETCORE_ENVIRONMENT=Production`, set in the Dockerfile): auth, rate limiting and journal **on**, logs are single-line JSON. **Fails closed**: without `Auth__Keys__*` the process refuses to start. Override any default with env vars (e.g. `Journal__Enabled=false` if durability is handled elsewhere).

## Run in a container
Docker is not required for development. Files provided: `Dockerfile` (multi-stage, non-root, locked restore, SemVer + git SHA), `docker-compose.yml` (read-only root FS, dropped capabilities, journal volume, memory limit, 30 s graceful stop), `.env.example`.
```
cp .env.example .env        # fill the two SHA-256 hashes (generation commands are in the file); never commit .env
docker compose up --build -d
curl localhost:8080/health/ready
```
> The Dockerfile/compose were written to the same Production profile that was exercised locally on Kestrel (auth, scopes, idempotent replay, rate limiting, journal recovery after a hard kill), but **no image has been built in this repository's environment** (Docker was not available). Build and smoke-test it in CI before relying on it. Pin base images by digest there.

TLS: terminate at the ingress / reverse proxy (the container speaks HTTP on 8080). Then set `Proxy__Enabled` and `KnownNetworks` to the proxy's network so rate limiting sees real client IPs.

## Probes, metrics, logs
- `GET /health` liveness; `GET /health/ready` readiness (503 until started *and journal recovery finished*, and 503 again as soon as shutdown begins so traffic drains); `GET /version`.
- Metrics (`System.Diagnostics.Metrics`, meter `EventIngestion`): `ingest.readings{outcome}`, `ingest.batches`, `ingest.batches.failed`, `devices.active`, `journal.bytes_written`, `journal.errors`, `journal.recovered_readings`. Updated per batch, never per reading. Scrape with OpenTelemetry or `dotnet-counters monitor EventIngestion`.
- Logs: source-generated structured messages; JSON in Production. Warnings: batches that stop early, disabled protections, journal write failures.
- Suggested alerts: `journal.errors > 0`; 429/503 rates; `devices.active` approaching `Window__MaxDevices`; readiness flapping.

## Durability and restarts
With the journal enabled, every accepted reading is written to `journal-*.log` segments (CRC-framed). On start the service replays segments and restores readings still inside the window **before** accepting traffic (readiness stays 503 meanwhile). A crash can only tear the last frame; it is detected and skipped, earlier data is kept. Readings sent *while the service is down* are not captured by the service: devices retry (use `Idempotency-Key`) or put a durable queue in front (DESIGN.md s5). With one instance, a restart is a brief outage; run behind a load balancer with 2+ instances partitioned by device for zero downtime (DESIGN.md s5). The journal never grows unbounded: segments are deleted once nothing in them can still be inside the window.

## Dependency and release management
- NuGet versions pinned centrally (`Directory.Packages.props`) and **locked** (`packages.lock.json` committed; Docker restores with `--locked-mode`). SDK pinned by `global.json`.
- Releases: bump `Version` in `Directory.Build.props`, update `CHANGELOG.md`, `git tag vX.Y.Z`, push the tag. Breaking API changes bump the major version and the `/api/vN` route.

## Production checklist
| Area | Status |
|---|---|
| Bounded memory (window, idle sweep, device cap, idempotency cap, body cap, journal retention) | In the service |
| Config validation, env-var config, fail-closed security in Production | In the service |
| Auth (scoped API keys, constant-time compare), rate limiting, proxy trust | In the service |
| Readiness/liveness, metrics, structured logs, graceful shutdown | In the service |
| Durability + crash-safe replay | In the service (optional) |
| Versioning, locked dependencies | In the repo |
| Container + compose | Provided, **untested here** (no Docker available) |
| TLS, WAF, secrets manager, CI/CD pipeline, dashboards/alerts | Deployment platform; not in this repo |
| Capacity | Single instance, in memory: RAM ~ devices x readings in window x 16-48 B plus headroom. Benchmarks are from a 2015 laptop CPU: re-run on target hardware, and load/soak test before launch. GC mode (Server/Concurrent) not tuned: measure first |
