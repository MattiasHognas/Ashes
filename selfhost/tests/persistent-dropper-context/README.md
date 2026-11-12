# Persistent dropper query context

Run from the repository root using a built stage-0 CLI:

```bash
bash selfhost/tests/persistent-dropper-context/validate.sh /absolute/path/to/ashes
```

The scripts create ignored fixtures and timing logs under `work/`. All compilation
and native execution uses the workstation heavy-job wrapper and memory scopes.
The semantic fixture copies the production dropper module, exposing only its
private path-sensitive admissibility helper for testing. It runs existing exact
dropper IR expectations and cache/projection tests, then checks:

- Shared narrow facts survive opening another body.
- Equal named IDs and names with different generic arguments stay distinct.
- Body-local full facts override a warmed shared projection.
- A new prepared environment starts an empty cache.
- A closure-bearing type is inadmissible with an empty cycle path but admissible
  with its root already blocked; warming the cache does not change either answer.
- Synthesis returning `None` retains completed facts and the graph lineage.

Fresh contexts at 1,000/10,000 rounds and warm cross-body queries at
10,000/100,000 rounds test plain and poisoned lifetimes. The synthesis benchmark
compares the frozen pre-change module at `5804df05` with the current production
module, retaining the returned graph/context while deliberately starting a fresh
label cache each round. It synthesizes a child release for `Box(Box(Box(Str)))`,
compares complete emitted instruction text, and counts instructions/functions.

Recorded on mattias-cachyos, 2026-11-12, with the stage-0 CLI built from
`5804df05` (including the RC correction `5bcfa2dc`):

| Check | Result |
|---|---|
| Existing dropper IR/cache/projection tests plus new context checks | Plain and poison pass |
| Fresh contexts 1,000 / 10,000 | Live cells 2 / 2, plain and poison |
| Warm cross-body queries 10,000 / 100,000 | Live cells 2 / 2, plain and poison |
| Synthesis IR | 36 lines, baseline/candidate byte-identical |
| Synthesis checksum, 100,000 rounds | 400,000, both versions |
| Plain synthesis time, 100,000 rounds | Baseline 3.31s; candidate 1.25s |
| Poison synthesis time, 100,000 rounds | Baseline 3.57s; candidate 1.38s |
| Plain synthesis live cells, 1,000 / 10,000 / 100,000 | 2 / 2 / 2, both versions |
| Poison synthesis live cells, 1,000 / 10,000 / 100,000 | 2 / 2 / 2, both versions |
| Semantic fixture compilation | Plain 23.17s, 1,369,616 KiB peak; poison 21.91s, 1,459,008 KiB peak |
| Synthesis fixture compilation | Baseline 22.49s; candidate 21.39s |
| Poison synthesis fixture compilation | Baseline 21.28s; candidate 20.13s |

These are focused checks and a repeated synthesis measurement, not a stage2
completion result. Full integration gates remain the integration lead's task.
