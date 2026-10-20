# Event Ingestion Service â€” Project Plan, Architecture & Rationale

Living reference. Every decision below is tied to a requirement in `Practical.pdf` (section numbers in brackets).
**Bold requirements from the PDF are marked â˜… and are acceptance criteria.**

## 0. Decisions taken (from kickoff Q&A)

| Topic | Decision | Why |
|---|---|---|
| Windowing | **Exact sliding window** (â˜… "continuously, not fixed buckets") | Requirement fidelity first; optimise after it is proven correct by tests. Bucketing remains a documented fallback if memory becomes the limit. |
| Bad data | **Skip bad readings, report counts** | One faulty sensor must not block a 50,000-reading batch from healthy devices. Response reports `accepted` + per-reason `rejected`. |
| Structure | **4 projects, clean layers** | Aggregation engine is pure and benchmarkable without HTTP; API stays thin. |
| Stack | .NET 10, Minimal API, System.Text.Json `Utf8JsonReader`, xUnit, BenchmarkDotNet | Built-in, allocation-friendly, no external datastore (Â§2). |

## 1. Requirement traceability

| Req | Where satisfied |
|---|---|
| â˜… POST batch **up to 50,000** readings, JSON array [3.1.1] | `POST /api/v1/readings`; hard cap enforced while streaming â†’ 413 once count exceeds 50,000 |
| â˜… Don't materialise whole body [3.1.2] | `PipeReader` + `Utf8JsonReader` resumable parse, element by element; no `List<Reading>`, no string body |
| Update aggregates as ingested [3.1.3] | Each parsed reading is applied immediately to its device state |
| Return accepted count [3.1.4] | `{ accepted, rejected: {stale, future, invalid}, ... }` |
| Query by deviceId: count/min/max/avg [3.2] | `GET /api/v1/devices/{deviceId}/aggregates` |
| Unknown / empty-window device [3.2.6] | 404 with problem details (distinct `detail` for unknown vs. no readings in window) |
| â˜… Last 5 minutes relative to now; aged-out stop contributing; continuous [3.3] | Exact sliding window, eviction on both write and read (see Â§4) |
| Out-of-order [3.3] | Sorted insert near tail; see Â§4 |
| Minimise allocations, Span/pooling, justify each [4.1] | See Â§5 (table of technique â†’ justification) |
| â˜… Thread-safe, no lost updates / torn reads; fine-grained locks [4.2] | Per-device lock over `ConcurrentDictionary` (see Â§6) |
| â˜… Memory bounded as readings age out [4.3] | Eviction + idle-device sweeper (`BackgroundService`) |
| Single process [4.3] | No distribution code; scaling discussed in design doc |
| Deliverables: README, design doc, benchmark, tests [5] | `README.md`, `docs/DESIGN.md`, `benchmarks/`, `tests/` |
| Out of scope: auth, persistence, deploy, UI, scaling [6] | **Deliberately not built** (no Dockerfile, no CI, no auth, no UI) |
| Git history + recorded assumptions [7] | Small commits per step; assumptions in Â§9 and README |

## 2. Folder structure & why

```
Ingestion Project/
â”œâ”€ README.md                      build/run/test/bench, API examples, assumptions
â”œâ”€ docs/
â”‚  â”œâ”€ PROJECT_PLAN.md             this file (decisions + rationale)
â”‚  â”œâ”€ DESIGN.md                   required design doc [5.2]
â”‚  â””â”€ BENCHMARK_RESULTS.md        short results note [5.3]
â”œâ”€ src/
â”‚  â”œâ”€ EventIngestion.Core/        PURE logic, zero ASP.NET references
â”‚  â”‚   â”œâ”€ Models/                 Reading (readonly record struct), AggregateSnapshot, IngestResult
â”‚  â”‚   â”œâ”€ Aggregation/            DeviceWindow (per-device state), DeviceStore (sharded registry)
â”‚  â”‚   â”œâ”€ Abstractions/           IClock (TimeProvider), IReadingSink
â”‚  â”‚   â””â”€ Options/                WindowOptions (5 min, skew tolerance, caps)
â”‚  â””â”€ EventIngestion.Api/         thin HTTP shell
â”‚      â”œâ”€ Endpoints/              Ingest + Query endpoint mappings
â”‚      â”œâ”€ Streaming/              ReadingStreamParser (PipeReader + Utf8JsonReader)
â”‚      â”œâ”€ Hosting/                EvictionSweeper (BackgroundService)
â”‚      â””â”€ Program.cs
â”œâ”€ tests/EventIngestion.Tests/    xUnit: unit, concurrency, API integration
â””â”€ benchmarks/EventIngestion.Benchmarks/   BenchmarkDotNet
```

**Why:** Core has no web dependency, so tests and benchmarks hit the real engine directly (fast, deterministic with a fake `TimeProvider`). The API only translates HTTP â†” Core. Streaming parser lives in Api (it depends on `PipeReader`), but takes a sink interface, so it can be tested with in-memory pipes.

## 3. Data structures & why

- **`Reading`**: `readonly record struct (string DeviceId, long TimestampUnixMs, double Value)` â€” no per-reading heap object. Timestamp stored as unix ms `long` (8 bytes, cheap compare) instead of `DateTime`.
- **`DeviceStore`**: `ConcurrentDictionary<string, DeviceWindow>`, with an alternate-lookup on `ReadOnlySpan<char>`/UTF-8 â†’ avoids allocating a `string` for each reading's deviceId except the first time a device is seen (only ~10,000 distinct strings ever exist).
- **`DeviceWindow`** (one per device, guarded by its own lock):
  - Sorted ring buffer of `(ts, value)` for the last 5 min â†’ exact eviction by timestamp.
  - Running `count` and `sum` (adjusted on insert/evict) â†’ O(1) count/avg.
  - Two monotonic deques (min, max) kept timestamp-ordered with dominance pruning â†’ amortised O(1) min/max under eviction, **exact**; out-of-order inserts are near the tail so cost is O(disorder).
  - Why not recompute min/max by scanning: a query would be O(readings in window) â€“ violates "answer aggregate queries cheaply".
- **Average**: `sum/count`. Drift from repeated add/subtract of doubles is bounded by recomputing the sum from the buffer when the window empties and (optionally) periodically; documented in DESIGN.md.

## 4. Windowing rules (edge cases)

- `now` comes from injected `TimeProvider` (testable). Window = `(now âˆ’ 5 min, now]`, boundary inclusion tested explicitly.
- Evict on every write to that device **and** on every read â†’ a query never returns aged-out data even if the device went silent.
- Reading older than `now âˆ’ 5 min` â†’ rejected as `stale` (would never contribute).
- Reading too far in the future (> configurable skew, default 1 min) â†’ rejected as `future` (otherwise one bad clock pins a device's window).
- Slightly late reading (within window) â†’ inserted in timestamp order; min/max/sum corrected.
- NaN / Â±Infinity value, missing/empty `deviceId`, unparsable timestamp â†’ `invalid`.
- Timestamps with offsets are normalised to UTC.
- Duplicate readings (same device+ts+value) are **counted separately** (assumption; no dedup key in spec).
- Device with all readings expired â†’ `404` ("no readings in window"); memory freed by sweeper.

## 5. Allocation techniques (each must earn its keep)

| Technique | Where | Justification |
|---|---|---|
| `PipeReader` streaming + `Utf8JsonReader` (ref struct, resumable) | ingest parser | Core of â˜… no full-body materialisation; constant memory regardless of 50k batch size |
| `ReadOnlySpan<byte>` property-name matching, `Utf8Parser`/`TryGetDateTime` | parser | No string alloc per field |
| Alternate dictionary lookup by span | store | Avoids ~50k string allocs per batch; only new devices allocate |
| Struct readings, no `List<T>` | everywhere | Zero per-reading heap allocation |
| Ring buffer that grows geometrically, shrinks when idle | `DeviceWindow` | Bounded memory; avoids per-reading node allocation (vs `LinkedList`/`Queue` nodes) |
| `ArrayPool<T>` for ring growth | `DeviceWindow` | Reuses buffers when windows resize (benchmark decides whether kept â€” **remove if no measurable win**) |
| **Not used:** object pooling of requests, `Memory<T>` plumbing, custom allocators | â€” | No measured benefit; avoids over-engineering (Â§4.1 last bullet) |

## 6. Concurrency model

- `ConcurrentDictionary` for device lookup/creation (lock-free reads).
- **One lock per `DeviceWindow`** (plain `Lock` object in .NET 10). Contention only between requests touching the *same* device. Critical section is tiny (insert + evict, no I/O, no allocation), so a lock beats lock-free structures (which cannot update count/sum/min/max atomically together â†’ torn reads).
- Query takes the same lock and copies 4 numbers into an immutable `AggregateSnapshot` struct â†’ consistent, never torn.
- No lock ever held across `await`; no nested locks â†’ no deadlocks.
- Batch ingest applies readings one at a time (lock per reading) â€” keeps parse streaming; optional optimisation (group consecutive same-device readings under one lock) only if benchmarks justify.
- Sweeper removes idle devices: removal races with writers are handled by a `retired` flag checked under the device lock; writer retries the lookup.

## 7. HTTP contract

- `POST /api/v1/readings` â€” body: JSON array, â‰¤ 50,000 items.
  - `200 { accepted, rejected: { stale, future, invalid }, total }`
  - `400` malformed JSON / not an array Â· `413` > 50,000 items or body too large Â· `415` wrong content-type
  - Partial processing on a mid-stream failure: readings before the error stay applied (documented; response states count applied).
- `GET /api/v1/devices/{deviceId}/aggregates` â†’ `200 { deviceId, count, min, max, average, windowStart, windowEnd }` Â· `404` unknown/empty.
- `GET /health` (cheap liveness).
- Kestrel `MaxRequestBodySize` set to a sane cap (~10 MB for 50k readings) â€” protects memory.

## 8. Step-by-step execution plan (each step = one git commit)

1. **Install .NET 10 SDK**, `git init`, `.gitignore`, `global.json`, empty solution + 4 projects (`Directory.Build.props`: nullable, warnings-as-errors, analyzers).
2. **Core models + options + clock** (`Reading`, `AggregateSnapshot`, `WindowOptions`, `TimeProvider`).
3. **`DeviceWindow`** with TDD: insert, eviction, min/max, out-of-order, boundary cases.
4. **`DeviceStore`** (sharded registry, span lookup, sweeper hook).
5. **Streaming parser** with tests (chunk boundaries split mid-token, unicode ids, escaped chars, huge batch, malformed).
6. **API endpoints** + Problem Details + limits; integration tests via `WebApplicationFactory`.
7. **Eviction sweeper** + memory-bound test.
8. **Concurrency tests** (parallel ingest same/overlapping devices, ingest+query race, exact totals).
9. **Benchmarks** (parse+ingest 50k, allocations via `MemoryDiagnoser`; compare naive `JsonSerializer.Deserialize<List<>>` baseline to prove the win).
10. **Docs**: README, DESIGN.md (all 5 required topics), BENCHMARK_RESULTS.md; remove dead code; final review.

## 9. Test matrix (edge cases)

- **Windowing:** reading at exactly nowâˆ’5m; 1 ms inside/outside; time advance via fake clock; all readings expire; device goes silent then queried; min/max after the extreme value expires; negative / zero / very large / subnormal values; avg precision.
- **Out-of-order:** late-but-in-window; late-and-stale; out-of-order min/max; same timestamp ties.
- **Input:** empty array â†’ `accepted 0`; exactly 50,000 ok; 50,001 â†’ 413; non-array root; truncated JSON; missing fields; extra unknown fields (ignored); null values; `1e308`; NaN strings; non-UTC offsets; bad dates; 1 MB deviceId; unicode/escaped deviceId; duplicate keys.
- **Query:** unknown device; URL-encoded ids; case sensitivity (ids are case-sensitive â€” assumption); empty window.
- **Concurrency:** N parallel posts to the same device â†’ exact count & sum; overlapping devices; query during ingest never sees count/min/max/avg inconsistent (min â‰¤ avg â‰¤ max, count â‰¥ 0); sweeper vs writer race.
- **Resource:** memory returns to baseline after window expiry; 10,000 devices Ã— readings stays bounded.

## 10. Assumptions to record in README

Device ids case-sensitive, â‰¤ 256 chars; timestamps normalised to UTC; future skew tolerance 1 min; duplicates counted; server clock is the reference for "now"; partial application on mid-stream failure; single-instance only.

## 11. Scaling beyond one instance (goes into DESIGN.md)

Partition by `hash(deviceId)` so each device's window lives on exactly one node (no cross-node merge); put a stateless gateway/Kafka-style partitioned log in front; per-partition ownership with replay for recovery; query routed by the same hash; or move aggregates into Redis/ClickHouse if durability is needed.

