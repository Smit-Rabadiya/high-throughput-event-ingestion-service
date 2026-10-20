# Design Document

Covers the five topics required by the brief (section 5.2). Numbers refer to [BENCHMARK_RESULTS.md](BENCHMARK_RESULTS.md).

## 1. Data structures and why

**Layers.** `EventIngestion.Core` is a pure library (no ASP.NET). The API project only translates HTTP to Core. Tests and benchmarks therefore drive the real engine directly with an injected `TimeProvider`.

**`Reading`** is conceptually `(deviceId, timestamp, value)`. Inside the engine only `(long unixMs, double value)` is stored per reading: 16 bytes, no per-reading heap object, cheap compare.

**`DeviceStore`**: `ConcurrentDictionary<string, DeviceWindow>` (ordinal). Lock-free reads, and device creation is atomic. It is queried through an *alternate lookup on `ReadOnlySpan<char>`*, so a reading for a known device needs no string allocation; the id string is allocated once per device (~10,000 in total).

**`DeviceWindow`** (one per device) holds three structures, all array-backed (`EntryList`: a struct array with a moving head):
1. `all`: every live reading, sorted by timestamp. Gives *exact* eviction ("drop everything with ts <= now - 5 min") and the exact count.
2. `sum`: running total, so average is O(1): `sum / count`.
3. `minQueue` / `maxQueue`: monotonic queues. Only readings that can still become the minimum (maximum) of some future window are kept: a reading is dropped once a *later* reading has an equal-or-better value, because the later one outlives it. The head is the exact min/max, and when the head expires the next candidate is already there. The max queue stores `-value`, so both share one implementation.

Why not scan on query? That makes a query O(readings in window) and a hot device would be re-scanned on every dashboard poll. Why not a heap or sorted set? They cannot expire by time cheaply, and they allocate a node per reading. A sorted array with a moving head makes eviction a pointer bump, and late readings (slightly out of order) insert near the tail, so the shift cost is proportional to how late the reading is, not to the window size.

Costs per reading: append/insert O(1) typical (O(log n) binary search to place it, plus shifting for late readings), amortised O(1) eviction, O(1) query.

**Average precision.** Add/subtract on a running double can drift. The sum is reset to exactly 0 whenever the window empties and is recomputed from the buffer if it ever becomes non-finite (e.g. overflow with 1e308 values). A randomized test compares against a brute-force recomputation across 200 random histories.

## 2. How allocations are minimised on the ingestion path

| Technique | Where | Why it earns its place |
|---|---|---|
| `PipeReader` + resumable `Utf8JsonReader` | `ReadingStreamParser` | The brief's central ask: the body is never materialised. Memory is constant regardless of batch size; a token or whole reading may straddle network chunks (tested at chunk sizes 1, 2, 3, 7, 64). Each reading is applied the moment its `}` is read. |
| Property-name matching on UTF-8 spans (`ValueTextEquals("deviceId"u8)`) | parser | No string per field name. |
| `CopyString` into one rented `char[]` (`ArrayPool`), then `ReadOnlySpan<char>` into the store | parser + store | Handles escapes/unicode correctly with zero per-reading allocation; the single pooled buffer is the one place pooling pays off (one rent per request). |
| Alternate dictionary lookup by span | `DeviceStore` | Avoids ~50,000 string allocations per batch. |
| `readonly record struct` / struct entries, no `List<Reading>` | everywhere | No per-reading heap object. |
| Parser state as a struct passed by `ref`, unknown/nested values skipped by depth counting | parser | Resumable across `await` boundaries without allocating a state machine object per element. |
| One `TimeProvider` read per batch | parser | Cheap, and gives the whole batch one consistent window. |

Deliberately **not** done (not worth the complexity, measured or reasoned): pooling per-device arrays (`ArrayPool` for `EntryList` growth would add rent/return bookkeeping to a lock-held section for a small gain; arrays are released when a device goes idle), custom allocators, `Memory<T>` plumbing, object pools for requests, lock-free structures.

Measured effect (50,000-reading batch): ~27% faster and ~3.6x fewer bytes allocated than deserialising a `List<Dto>`; what remains allocated per batch is chiefly the stored data itself (see the benchmark note).

## 3. Concurrency model and why it is correct

**Primitives.** `ConcurrentDictionary` for lookup/creation; **one `Lock` per `DeviceWindow`** for all state of that device.

**Why per-device locks.** Requests only contend when they touch the *same device*. The critical section is tiny (a binary search, a few array moves, no I/O, no allocation, no `await`), so a plain mutual-exclusion lock is cheaper and simpler than reader/writer locks (writes dominate; the read section is as short as the write section). Lock-free structures cannot update count, sum, min and max *atomically together*, which is exactly what "no torn reads" demands.

**Correctness argument.**
- *No lost updates:* every mutation of a window (insert, eviction, retirement) happens inside that window's lock, so mutations are serialised per device; different devices share no mutable state.
- *No torn reads:* a query takes the same lock, evicts, and copies count/min/max/average into an immutable `AggregateSnapshot` struct in one critical section. The four numbers always describe the same moment.
- *No deadlocks:* a thread holds at most one window lock, never calls into another lock while holding it, and never awaits while holding it. Dictionary operations happen outside window locks.
- *Sweeper vs writer race:* the sweeper calls `TryRetire` (under the window lock: evict; if empty set `retired`), then removes that exact `(key, window)` pair from the dictionary. A writer that looked the window up just before retirement gets `AddResult.Retired` from inside the lock, removes the dead entry if still present, and retries the lookup; it then creates or finds a fresh window. A reading is therefore either applied to a live window or retried, never written into a window that is about to be discarded. Removal compares the value, so a sweeper cannot delete a window a writer has just replaced it with.
- *Queries during ingest:* a query on a device with all readings expired reports "no readings in window" regardless of whether the sweeper has run (eviction happens on read as well as on write).

**Evidence.** `ConcurrencyTests`: 16 threads x 5,000 readings into one device give the exact expected count/min/max/avg; 8 threads over 50 overlapping devices match a sequential oracle (with out-of-order timestamps); readers racing writers never observe min/avg/max out of order, out-of-range values, or a decreasing count; sweeping at the same instant as writers loses nothing; a sweeper that retires every window while writers hammer them never produces a failed write or leaks `Retired`.

## 4. Windowing approach and tradeoffs

**Exact sliding window** `(now - 5 min, now]`, evaluated at request time, not fixed buckets (the brief says "continuously"). Eviction runs on every write to a device and on every read of it, plus a background sweeper (every 30 s) that removes devices with no live readings.

| | Exact (chosen) | Bucketed approximation |
|---|---|---|
| Accuracy | Exact; a reading leaves the aggregates at the millisecond it expires | Edge error up to one bucket width |
| Memory per device | O(readings in window): 16 B per reading plus the min/max candidate queues (small for noisy data, up to 2 x 16 B per reading for monotonic data) | O(buckets), constant |
| Query cost | O(1) | O(buckets) |
| Out-of-order | Insert at position | Drop into bucket (trivial) |

Memory estimate for the exact approach: 10,000 devices at 1 reading/second = 3,000,000 live readings ~ 48 MB for the main buffer (~144 MB worst case incl. both queues). That is acceptable for a single instance; if the rate were far higher, per-second buckets (min/max/sum/count each) would cap memory at 300 buckets per device and would be the documented fallback.

**Edge cases and decisions.** Boundary: a reading exactly 5 minutes old is expired. Stale readings (already outside the window) are rejected rather than inserted. Readings more than 1 minute in the future are rejected so one device with a wrong clock cannot pin its own window open. Late-but-in-window readings are inserted in order and correct min/max/sum. Timestamps with offsets are normalised to UTC. The server clock is the single reference for "now".

**Memory bound (4.3).** Live data per device is bounded by what the last 5 minutes contain; empty arrays above a small size are released when a window empties; idle devices are removed by the sweeper (verified by tests, including 1,000 devices swept after going silent).

## 5. Scaling beyond a single instance (discussed only, not built)

- **Partition by `hash(deviceId)`.** Each device's window lives on exactly one node, so no cross-node merge and no distributed locks. The per-device lock model carries over unchanged.
- **Front the nodes with a stateless gateway / partitioned log** (e.g. Kafka keyed by deviceId). The gateway accepts the 50,000-reading batches, and splits and routes by partition. Queries are routed with the same hash.
- **Durability and recovery.** Today a restart loses state and any readings sent during the outage are not recorded (persistence is out of scope). With a durable log the service becomes a consumer: on restart a node replays only the last 5 minutes from its partitions' committed offsets to rebuild windows, and devices never see an outage because the log accepts writes while nodes restart. Idempotency (a reading id or a `(deviceId, timestamp)` dedupe key) would be added so replays and device retries are not double-counted.
- **Rebalancing:** consistent hashing so adding nodes moves a small slice of devices; the moving device's window is rebuilt from the log.
- **Alternative:** push aggregates into Redis/ClickHouse if cross-service durability or ad-hoc querying matters more than the in-process latency.
- **Hot spots:** one extremely chatty device is still serialised by its own lock; if that mattered, batch consecutive same-device readings under one lock acquisition (only if profiling showed contention).
