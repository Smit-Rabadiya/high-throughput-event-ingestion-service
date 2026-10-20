# Benchmark Results

Run: `dotnet run -c Release --project benchmarks/EventIngestion.Benchmarks -- --filter "*"`
Machine: Intel Core i5-5300U 2.30 GHz (2 cores / 4 threads, 2015 laptop CPU), Windows 10, .NET 10.0.13, BenchmarkDotNet 0.15.8. Absolute numbers will be better on modern hardware; the ratios are what matter.

## Workload
One maximum-size batch per invocation: **50,000 readings**, ~10,000 distinct devices, timestamps spread over the last 4 minutes in random (out-of-order) order, ~5 MB of JSON, delivered through a `PipeReader`. Single thread.

## Ingest (batch of 50,000)

| Method | Mean | Allocated | vs baseline (time / alloc) |
|---|---:|---:|---:|
| **Streaming, warm store** (production steady state) | 53.3 ms | 2.15 MB | 0.64x / 0.12x |
| **Streaming, cold store** (first batch after start-up) | 61.1 ms | 12.17 MB | 0.74x / 0.69x |
| Baseline: `Deserialize<List<Dto>>` then apply, cold store | 82.7 ms | 17.74 MB | 1.00x / 1.00x |
| Baseline: `Deserialize<List<Dto>>` then apply, warm store | 72.9 ms | 7.73 MB | 0.88x / 0.44x |

Like-for-like (warm vs warm): streaming is **~27% faster and allocates ~3.6x less** (2.15 MB vs 7.73 MB); cold vs cold: ~26% faster, ~31% less allocation. Throughput of the streaming path is roughly **0.9 million readings/s per core** on this old CPU.

## Query
`GET` aggregate lookup against 10,000 populated devices (500,000 readings): **~39 ns, 0 bytes allocated** per lookup (O(1): count/sum/min/max are maintained incrementally, not recomputed).

## Reading the numbers
- The streaming path allocates **nothing per reading** for parsing (no strings for known devices, no DTOs, no list). What remains in the warm number is mostly the *state the service must keep*: the per-device sorted entry arrays growing as 50,000 readings are stored (3 arrays x 16-byte entries, geometric growth). That is retained data, not request garbage. (This attribution follows from the data structure sizes; I have not isolated it with an allocation profiler.)
- The cold-store figure additionally includes creating ~10,000 device windows and their id strings, paid once per device, not per batch.
- The gap in time is smaller than the gap in allocations: with a warm store most of the CPU time is the actual work (UTF-8 token parsing, ISO-8601 parsing, lock, sorted insert, three queue updates), which both approaches share. The allocation win matters more under load: fewer Gen0/Gen1/Gen2 collections (see the Gen columns in the raw output) means less GC pause jitter while many 50,000-reading batches arrive concurrently.
- Memory per request stays flat regardless of batch size, because only the current token and one pooled 1,536-char id buffer are held; the baseline's footprint grows linearly with the batch.

## Method notes
- Fixed clock (`TimeProvider`) so the same payload is always inside the window.
- Warm-store benchmarks rebuild a store with all device windows pre-created at the start of every iteration (16 invocations per iteration), so each iteration begins from the same state.
- Not included: HTTP/Kestrel overhead and network time (this isolates the ingestion engine); multi-threaded scaling (covered for correctness in `ConcurrencyTests`, not benchmarked).

## Regression check after the production features (v1.0.0)
Re-run after adding the optional journal hook, idempotency, auth and rate limiting: allocations unchanged (streaming warm 2.15 MB vs naive 7.73 MB) and streaming/naive time ratio unchanged within noise (0.61x warm, 0.76x cold; absolute times were ~15% higher on that run because the laptop was busy, and the baseline slowed equally). With the journal disabled the only added hot-path cost is one null check per accepted reading.
