# Event Ingestion Service

High-throughput ingestion tier for an IoT fleet (.NET 10, C#). Accepts batches of up to 50,000 readings per call, streams them without materialising the body, and keeps exact rolling 5-minute **count / min / max / average** per device, all in memory.

## Requirements
.NET 10 SDK. Nothing else (no external datastore).

## Build, run, test, benchmark
```
dotnet build                                                        # build everything
dotnet test                                                         # 100+ tests: windowing, parser, API, sweeper, concurrency
dotnet run --project src/EventIngestion.Api --launch-profile http   # http://localhost:5084
dotnet run -c Release --project benchmarks/EventIngestion.Benchmarks -- --filter "*"
```

## Using the API
```
curl -X POST http://localhost:5084/api/v1/readings -H "Content-Type: application/json" \
  -d '[{"deviceId":"sensor-0042","timestamp":"2026-10-08T09:00:01Z","value":21.5}]'
curl http://localhost:5084/api/v1/devices/sensor-0042/aggregates
```
(Use current timestamps: readings older than 5 minutes are counted as `stale`.)
Full reference, status codes, and ready-made **Postman / Bruno / .http** collections: [docs/api/API.md](docs/api/API.md).

## Layout
```
src/EventIngestion.Core   pure engine: DeviceWindow (per-device exact window), DeviceStore (registry), models
src/EventIngestion.Api    HTTP shell: streaming parser, endpoints, eviction sweeper
tests/                    xUnit: unit, randomized oracle, parser chunk-boundary, API integration, concurrency
benchmarks/               BenchmarkDotNet: streaming vs naive baseline, query cost
docs/                     DESIGN.md, BENCHMARK_RESULTS.md, PROJECT_PLAN.md, STATUS.md, REPO_RULES.md, api/
```

## Documents
- [Design document](docs/DESIGN.md): data structures, allocation strategy, concurrency model and correctness argument, windowing tradeoffs, scaling beyond one instance.
- [Benchmark results](docs/BENCHMARK_RESULTS.md): streaming ingest ~27% faster and ~3.6x less allocation than a naive deserialize-the-list baseline; queries ~39 ns, zero allocation.
- [Operations guide](docs/OPERATIONS.md): configuration (env vars), Production profile, Docker, probes, metrics, durability, releases, production checklist.
- [CHANGELOG](CHANGELOG.md): version history (current 1.0.0).
- [Plan / decisions](docs/PROJECT_PLAN.md), [status log](docs/STATUS.md).

## Assumptions (where the brief is open)
| Topic | Decision |
|---|---|
| Window | Exact sliding window `(now - 5 min, now]`, evaluated at request time; a reading stamped exactly 5 minutes ago is already expired. |
| "Now" | Server clock (UTC) is the reference; one clock read per batch. |
| Out of order | Late readings inside the window are inserted in timestamp order and included in all aggregates. |
| Stale / future | Older than the window: `stale`. More than 1 minute ahead of server time: `future` (guards against a bad device clock pinning a window). Both are skipped and counted. |
| Bad data | A malformed reading (missing/null/wrong type field, bad date, NaN/Infinity, empty or >256-char id, non-object element) is skipped and counted as `invalid`; it never blocks the rest of the batch. |
| Response | 200 with `accepted`, `rejected{stale,future,invalid}`, `total`. |
| Syntax error / over limit | 400 / 413; readings parsed before the error stay applied and the problem body reports how many. |
| Duplicates | No reading id exists, so identical readings are counted separately (a retried batch is counted twice). |
| deviceId | Case-sensitive, 1-256 chars. Timestamps with offsets are converted to UTC. Property names are exact camelCase; duplicate keys: last wins. |
| Unknown vs empty device | Both 404, with distinct `title` (`Unknown device` / `No readings in window`). |
| Restart / outage | Default: state is in memory, a restart starts empty and readings sent while the service is down are not recorded by it (devices retry; use Idempotency-Key). With the optional journal (Journal:Enabled, on in the Production profile) accepted readings are written ahead to disk and the window is rebuilt on start before traffic is accepted. A durable queue in front for outage capture is discussed in DESIGN.md. |
| Body limit | 32 MiB request cap plus the 50,000-reading cap. |
| Beyond the brief | The brief lists these as out of scope; they were added afterwards as **optional, off-by-default-in-dev** features at the owner's request: API-key auth, rate limiting, durable journal + replay, idempotent retries, Dockerfile/compose. Still not built: UI, CI pipelines, horizontal scaling (discussed in DESIGN.md). |




