# Repository Rules: what is and is not pushed

Remote: https://github.com/Smit-Rabadiya/high-throughput-event-ingestion-service (public, read-only for others; only the owner can push).

## Never push
- Secrets of any kind: tokens, passwords, API keys, connection strings, certificates / private keys (`.env*`, `*.pfx`, `*.pem`, `*.key`, `secrets.json`, `appsettings.*.local.json`). This service needs none; if one ever appears, it stays out.
- Personal data beyond the git author identity; machine-specific paths or user names in docs/code.
- Build output and caches: `bin/`, `obj/`, `.vs/`, `*.user`, `BenchmarkDotNet.Artifacts/`, test results.
- The assessment brief `docs/reference/Practical.pdf` (kept locally only; summarised in `PROJECT_PLAN.md`).
- Claude Code local state (`.claude/`), backups (`*.bundle`), scratch files.

## Always push
- Source (`src/`, `tests/`, `benchmarks/`), solution, `Directory.Build.props`, `.gitignore`, `global.json`.
- `README.md`, `docs/DESIGN.md`, `docs/BENCHMARK_RESULTS.md`, `docs/PROJECT_PLAN.md`, `docs/STATUS.md`, `docs/REPO_RULES.md`, `CLAUDE.md`.

## Procedure before every push
1. `git status` and `git diff --cached --stat`: read the list of files being committed.
2. Scan staged content for secrets (keys, tokens, `password`, connection strings, absolute user paths).
3. Update relevant docs (`STATUS.md` always; README / DESIGN / PLAN when behaviour or decisions changed) in the same commit.
4. Commit small with a clear message; push to `main` after each plan step. Never force-push; never rewrite pushed history.
