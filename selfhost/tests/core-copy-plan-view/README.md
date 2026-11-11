# Core copy-plan decisions and cache authority

Generate the runnable fixture with:

```sh
bash scripts/selfhost-dev/render-core-copy-plan-view.sh
```

The script extracts the current production `heapFactsOf`, narrow view boundary,
and recursive copy planner unchanged into `work/core-copy-plan-view`. It compares
those decisions with `OldPlan.ash.inc`, the pre-change planner from commit
`4f1c1bd2` (only its mutually recursive function names are prefixed `old_`).
`Prelude.ash.inc` injects surrounding policy answers and constructor sizes, so
this fixture isolates planner decisions rather than pretending to run all Core
lowering. The actual classifier remains the production module.

Compile the generated project plain/poisoned with stage 0 and with stage 1, using
`ASHES_RC_LEAKCHECK=1`. Run `check`, `1000`, and `10000` with the documented memory
scopes and stage-1 stack limit. `check` exercises 192 combinations of copy flags
and recorded contract answers, both ordinary/general plans, recursive-copy and
admissibility gates, constructor/tag/field ordering, tuple/list recursion, empty
and multi-constructor layouts, generic cache bypass, and resolved variable roots.
Injected settled facts intentionally differ from environmental classification:
projecting the authoritative cache is required to pass.

The larger counts create fresh states, names, maps, environments, and plans on
every query; their live counters must remain flat. Full compiler builds separately
validate the complete Core wiring.

Validation with the integration stage-0 compiler and `s1-f-bootstrap17-debug`:
all 192 cases pass plain, poisoned, and self-hosted. Fresh 1,000/10,000-query
runs retain **5/5 live cells** in each mode. This tests temporary view records,
children, cache maps, and planner results together. The companion full-view
fixture also passes all three modes with **5/5** fresh live cells.

For matched timing, run `old-cache`, `new-cache` (100,000 queries each) and
`old-miss`, `new-miss` (10,000 queries each) on the same binary. They consume
plan-variant checksums; paired checksums must agree. The decision oracle above
checks complete payload equality independently. Keep cached-hit measurements:
projecting a settled full fact allocates a narrow view even though the original
planner could inspect that cached record directly.

Matched fixture wall times (seconds, one run per mode, 0.01 s timer resolution):

| Compiler | Cached old/new, 100k | Miss old/new, 10k |
|---|---:|---:|
| Stage 0 | 0.02 / 0.03 | 0.05 / 0.02 |
| Stage 0 poisoned | 0.03 / 0.04 | 0.06 / 0.02 |
| Stage 1 bootstrap17 | 0.03 / 0.04 | 0.05 / 0.02 |

Both paths produce the same normalizer-plan checksums (900000 cached, 90000
misses), with 4 live cells after each benchmark. The cache projection overhead
is real at this scale; the narrower miss query improves this fixture. These short
measurements are indicative, not a whole-build speed claim. The final stage-1
fixture compilation took 1:58.33 with 843508 KiB maximum RSS. All final decision
and fresh-lifetime checks passed again after the benchmark harness was added.

The renderer also extracts the actual `heapChildrenOfNamed` and
`emitNormalizerAdtCopy` caller (changing only `and` to `let` at the isolated
function boundary). Minimal emission stubs record complete constructor/switch
plans and check the production label names. The oracle invokes the old planner
on full children and compares exact emitted plans and source temps against the
production caller, including settled cache hits, misses, generic bypass, empty,
single and multiple constructors, and non-named fallback. This catches a missing
full-child to copy-plan-child adapter at the caller boundary during compilation.

The added normalizer boundary passed the 192-case oracle and 1,000/10,000 fresh
queries in stage 0 plain and poisoned modes: **5/5 live cells** in each mode.
An ignored generated control with only the child projection removed fails with
`ASH002 Type mismatch: HeapCopyPlanChild vs HeapLayoutChild` in argument 3 of
`constructorCopyPlansOfWith`, reproducing the bootstrap18 failure. Plain/poison
fixture compilation took 16.79/17.35 seconds, with 1090940/1116360 KiB peak RSS.
This extension has not had a separate stage-1 fixture compilation; the subsequent
full bootstrap validates that integration boundary. Evidence and separate timing
files are under `work/core-copy-plan-view/normalizer-caller-*`.
