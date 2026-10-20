# Project Memory (read first every session)

Claude Code does not carry chat history between sessions. This file and `docs/STATUS.md` are the project's memory.

- At session start: read `docs/STATUS.md` (where we are, next step) and `docs/PROJECT_PLAN.md` (decisions + 10-step plan).
- Requirements: `docs/reference/Practical.pdf`.
- After finishing each step: update `docs/STATUS.md` (done / in progress / next / open questions) and commit.
- One git commit per plan step; small, descriptive messages.

## Non-negotiable rules (always keep in mind)

1. **Follow `docs/reference/Practical.pdf` exactly.** It is the source of truth: every requirement (esp. the bold/acceptance ones), deliverable (README, design doc, benchmark, tests) and out-of-scope item applies. Before finishing any step, re-check the work against the PDF. Do not build out-of-scope items (auth, persistence, Docker/CI, UI, distributed scaling).
2. **Write and optimise as a senior developer / tech lead would.** Hot path (ingestion) is allocation-conscious: streaming parse, spans, struct data, fine-grained locks. Apply techniques only where measurement or reasoning shows they earn their keep (no over-engineering), and be able to justify each in the design doc. Prefer correctness first, prove with tests, then optimise with benchmarks.
3. Record every open-spec decision as an assumption in README / design doc.
4. Keep `docs/STATUS.md` current after every step; commit small and often with clear messages (history is a deliverable).
5. Everything is pushed step by step to the GitHub repository once it exists (see `docs/STATUS.md` for the remote).

6. **Always keep the relevant `.md` documents updated** in the same commit as the change they describe: `docs/STATUS.md` after every step; `README.md`, `docs/DESIGN.md`, `docs/PROJECT_PLAN.md`, `docs/BENCHMARK_RESULTS.md` whenever behaviour, decisions or results change.
7. **Follow `docs/REPO_RULES.md`** for what may / may not be pushed (no secrets, no build output, no assessment PDF). Check staged files before every commit.
