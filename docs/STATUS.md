# Development Status

Last updated: 2026-10-20

## Where we are
Steps 1-5 of 10 done. Next: step 6, API endpoints + integration tests.
Plan: `PROJECT_PLAN.md`. Rules: `../CLAUDE.md`, `REPO_RULES.md`. Requirements PDF is kept locally only (`docs/reference/Practical.pdf`, git-ignored).

## Remote
https://github.com/Smit-Rabadiya/high-throughput-event-ingestion-service (public, only the owner can push). Push after every step. `gh` CLI installed and logged in; git uses it as credential helper.

## Done
- [x] Step 0: plan + decisions, git repo, rules, GitHub repo created and first commits pushed
- [x] Step 1: solution `EventIngestion.slnx` + Core / Api / Tests / Benchmarks, `Directory.Build.props` (nullable, warnings as errors)
- [x] Steps 2-4: `DeviceWindow` (exact sliding window, monotonic min/max queues), `DeviceStore` (span alternate lookup, `SweepIdle`), models, options
- [x] Step 5: `ReadingStreamParser` (PipeReader + resumable Utf8JsonReader, pooled id buffer, 50,000 cap, bad readings counted as invalid, unknown/nested fields skipped by depth tracking)
- [x] 52 tests passing (window edges, out-of-order, sweeper, randomized oracle, parser at chunk sizes 1..whole, malformed/truncated, limits)

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
