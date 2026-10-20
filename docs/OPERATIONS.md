# Operations Guide

## Configuration
Bound from `appsettings.json`, then environment variables (`__` for nesting, e.g. `Window__MaxDevices=50000`), then command line. Invalid values stop start-up with a clear error. **Never put secrets in `appsettings.json`**: use environment variables / a secret store (see `REPO_RULES.md`).

| Key | Default | Meaning |
|---|---|---|
| `Window:Window` | `00:05:00` | Sliding window length |
| `Window:FutureSkewTolerance` | `00:01:00` | Readings further ahead than this are `future` |
| `Window:MaxDevices` | `100000` | Cap on tracked devices; extra new devices are reported under `rejected.deviceLimit` (memory-exhaustion guard; brief expects ~10,000) |
| `Service:SweepInterval` | `00:00:30` | Idle-device sweep period |
| `Service:MaxRequestBodyBytes` | `33554432` | Kestrel body cap |

## Endpoints for orchestration
- `GET /health`: liveness (process is up).
- `GET /health/ready`: readiness. 503 until the host has fully started, and 503 again as soon as shutdown begins so a load balancer drains the instance.

## Metrics (System.Diagnostics.Metrics, meter `EventIngestion`)
`ingest.readings{outcome=accepted|stale|future|invalid|device_limit}`, `ingest.batches`, `ingest.batches.failed`, `devices.active`. Updated once per batch, never per reading. Inspect locally with `dotnet-counters monitor --name EventIngestion.Api EventIngestion`, or export with OpenTelemetry in a real deployment.

## Logging
Structured, source-generated (zero allocation when the level is off). Warnings for batches that stop early; per-batch summary at `Debug`.

## Dependency versions
All NuGet versions are pinned centrally in `Directory.Packages.props`; the SDK is pinned by `global.json`; warnings are errors.

## Production checklist: what the code does and does not cover
| Area | Status |
|---|---|
| Bounded memory (window, idle sweep, device cap, body cap) | In the service |
| Config validation, readiness/liveness, metrics, structured logs | In the service |
| Graceful shutdown | Host default (drains in-flight requests; readiness flips first) |
| TLS termination, HSTS, forwarded headers | Deploy-time: terminate TLS at the ingress/reverse proxy. Not configured here |
| Containerisation, CI, deployment manifests | Out of scope per the brief; not provided |
| Auth, rate limiting, durability, idempotency | Optional features, see sections below as they are added |
| Capacity | One instance, in memory: size RAM for (devices x readings in window x ~16-48 B) plus headroom; benchmarks are on a 2015 laptop CPU, re-run on target hardware |
| Not measured | Multi-core throughput scaling, long soak test, GC tuning (Server/Concurrent GC); do this on target hardware before setting GC options |
