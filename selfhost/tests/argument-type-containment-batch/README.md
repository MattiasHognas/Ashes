# Shared containment traversal

`FrozenContainment.ash` is the original single-target implementation from commit
`73d3aab0`. The test compares the new query with the disjunction of independent
frozen queries for all 512 directed three-node graphs, three roots, and 16 target
sets: 24,576 decisions. Additional cases cover empty targets with unknown/function
results, duplicate targets, blocked names, differently instantiated siblings,
nonregular recursion, same-name/different-ID types, and consecutive queries under
different substitutions. Targets themselves are deliberately not resolved by the
query, matching the original contract.

Run from any shell, including fish:

```sh
bash selfhost/tests/argument-type-containment-batch/validate.sh /path/to/ashes s0-plain
bash selfhost/tests/argument-type-containment-batch/validate.sh /path/to/ashes s0-poison poison
```

The validator compiles under an 8 GiB scope, runs under 4 GiB scopes, and uses the
shared heavy-job limiter. It verifies the exact success message and exactly one
live-cell counter, then requires equal counts at 1, 1,000 and 10,000 iterations.
Both old and new modes use runtime-created shared graphs of 31–32 levels and 16
absent candidates. They also exercise singleton success and multi-target early
success with pending work. Timing and peak-memory files are saved under ignored
`work/containment-batch-<tag>/`; compare the whole same-size runs, not rounded tiny
per-call timers.

The actual stage1 compiler can run the same validator in plain mode. Stage1 does
not implement the stage0 poison switch, so that run is not poisoned coverage.
These tests validate the imported production containment module. Combined actual
Core lowering and a subsequent stage2 build remain separate acceptance gates.

Validated on bootstrap32 (`73d3aab0`) with the production module changed as in this
commit. The frozen oracle and strict 1/1/1 live-cell plateaus pass with stage0 plain,
stage0 poisoned, and the actual bootstrap32 stage1 compiler. At 10,000 iterations:

| Compiler | Independent queries | Shared traversal | Peak RSS, old / new |
| --- | ---: | ---: | ---: |
| stage0 plain | 4.67 s | 0.76 s | 8,484 / 8,488 KiB |
| stage0 poisoned | 5.08 s | 0.81 s | 8,492 / 8,496 KiB |
| bootstrap32 stage1 | 2.58 s | 0.39 s | 8,048 / 8,156 KiB |

These measurements concern the shared-graph fixture, not a complete compiler run.
