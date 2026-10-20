# Development Status

Last updated: 2026-10-20

## Where we are
Steps 1-4 of 10 done (Core engine complete and tested). Next: step 5, the streaming JSON parser.
Plan: `PROJECT_PLAN.md`. Requirements: `reference/Practical.pdf`.

## Done
- [x] Step 0: plan + decisions; git repo; PDF in `docs/reference/`; `CLAUDE.md` + this file
- [x] Step 1: solution `EventIngestion.slnx` + Core / Api / Tests / Benchmarks, `Directory.Build.props` (nullable, warnings as errors)
- [x] Steps 2-4: `Reading`, `AggregateSnapshot`, `AddResult`, `WindowOptions`, `EntryList`, `DeviceWindow` (exact sliding window, monotonic min/max queues), `DeviceStore` (span alternate lookup, `SweepIdle`)
- [x] 14 unit tests passing (edge boundaries, out-of-order, expiry, sweeper, randomized oracle test)

## Next
- [ ] Step 5: `ReadingStreamParser` (PipeReader + Utf8JsonReader) in Api, with chunk-boundary tests
- [ ] Step 6: endpoints + integration tests; 7: EvictionSweeper; 8: concurrency tests; 9: benchmarks; 10: README/DESIGN/BENCHMARK_RESULTS

## Notes
- Run tests: `dotnet test` from repo root.
- ArrayPool deliberately not used in Core (plain arrays; revisit only if benchmarks show a win).
- GitHub remote not configured yet (needed only to submit the repo link).
- Local git identity: SMIT <gencore.ai.labs@gmail.com> (repo-local).
