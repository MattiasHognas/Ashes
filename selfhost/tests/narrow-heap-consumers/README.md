# Narrow lowering heap queries

Run `python3 selfhost/tests/narrow-heap-consumers/render.py` from the repository.
It generates `work/narrow-consumers/ashes.json`. The historical full classifier
comes from frozen commit b19daa03; only private nominal type and exported value
names are prefixed to prevent cross-module importer collisions. The byte-for-byte
original is retained beside it as `.ash.inc`.

The renderer extracts the actual candidate `classifyClosureCaptureLayout`, `heapClosureCaptureLayoutOf`,
`canNormalizeClosureCapture`, and `ownedChildrenDroppable` query functions.
The old capture expression comes from the frozen Core source; its two full-fact
reads are replaced by equivalent frozen-classifier/cache projections. Both sides
receive the same synthetic compiler state. Resolve/coverage/cache access is
injected, and resource guarding is an explicit Boolean input; scalar-list reset
classification is a shared simplified service. Consequently this fixture proves
query-boundary equivalence and cache authority, not full closure lowering,
resource inference, pending operator defaults or emitted normalization IR.

`check` covers true/false authoritative monomorphic cache answers, resource guard,
resolved-variable cache lookup, generic cache bypass, record/variant/positional
shapes, regular and changing generic recursion, unresolved/resource children,
zero-cost wrappers, opaque handles, missing constructors, colliding names/IDs and
changed environments. The graph answer is compared to the frozen whole-layout
ownedChildrenDroppable field; no approximation from child drop kinds is used.

Compile with `ASHES_RC_LEAKCHECK=1`; run `1000` and `10000` for fresh environment,
type and cache construction each iteration. Compare counts with stage0 plain,
stage0 poison and stage1 plain. Full Core integration and next real compiler
profile remain separate acceptance gates.

Matched `old-generic100`/`old-generic1000` and corresponding `new-` modes
measure each implementation separately on fresh generic environments. The
`old-nonregular100`/`old-nonregular1000` and `new-` modes start at `A(Int)`
whose nested `B(Int)` returns to `A(Str)` or `A(List(Int))`; this forces the
changing-instantiation path rather than merely exercising a regular generic cycle.

`old-cache`/`new-cache` time 50,000 queries against an authoritative cached
record answer with 64 child descriptors. `old-miss`/`new-miss` time 1,000
uncached queries of the same-sized environment. The original proposed copy-plan
projection copied all descriptors on cache hits (stage0 <0.01s old vs0.04s new).
The final boundary projects only the two Boolean flags on cache hits; misses use
the existing narrow view once. Timing results are machine-specific; the real
compiler profile remains the performance acceptance test.

## Focused results

All differential, adversarial and cache-authority checks pass in stage0 plain,
stage0 poison and unchanged bootstrap19 stage1. Fresh simple-environment queries
at 1000/10000 are flat at 5/5 in all three modes.

Matched separate 100/1000 generic-path counts:

| Mode | Generic old | Generic new | Nonregular old | Nonregular new |
|---|---:|---:|---:|---:|
| stage0 plain | 4/4 | 3/3 | 3/3 | 3/3 |
| stage0 poison | 4/4 | 3/3 | 3/3 | 3/3 |
| stage1 plain | 7654/76504 | 1353/13503 | 42853/428503 | 6853/68503 |

Stage1 has pre-existing growth on these broader queries; the candidate reduces
it but is not leak-free. The one-shot adversarial check is not a plateau test.
No unrelated ownership repair was added.

Final cache-hit timings are below 0.01 seconds for both old/new (50,000 queries);
miss timings are 0.10/0.02 seconds (1,000 queries) in stage0 plain and stage1.
These short timings establish removal of the projected-child regression, not a
precise speedup for the whole compiler. Stage1 fixture compile: 2:45.49 wall,
164.94 CPU seconds, 1,211,216 KiB peak RSS, exit 0. Compiler SHA256:
`4d8028174c1b6565dac86674248aa8d3cb02d4f48f50eb786ec9169214342f01`.

Production `.ash` files and generated fixtures were canonically formatted. The
existing selfhost heap-layout test now compares the public Boolean classifier
against the whole fact. Full suites and the subsequent compiler build/profile
remain coordinated integration gates, not claimed complete here.
