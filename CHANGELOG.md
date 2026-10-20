# Changelog

All notable changes to this project. Format: [Keep a Changelog](https://keepachangelog.com/), versioning: [SemVer](https://semver.org/).
The version lives in `Directory.Build.props`; release by bumping it, updating this file, and tagging `vX.Y.Z`.

## [1.0.0] - 2026-10-21
### Added
- Ingestion API: `POST /api/v1/readings` (streaming parse, up to 50,000 readings/batch), `GET /api/v1/devices/{id}/aggregates` and `?deviceId=` form, exact 5-minute sliding window with count/min/max/average.
- Optional `Idempotency-Key` header for safe retries.
- Production hardening: device cap, typed + validated configuration (env-var overridable), `/health`, `/health/ready`, `/version`, metrics, structured JSON logs, graceful shutdown.
- Optional API-key authentication (scopes `ingest`, `query`), per-client rate limiting, global ingest concurrency cap, trusted-proxy handling.
- Optional durable write-ahead journal with crash-safe replay on restart.
- Dockerfile, docker-compose, Production configuration, locked dependencies.
- Tests (unit, randomized oracle, parser, API, concurrency, security, journal) and BenchmarkDotNet benchmarks.
