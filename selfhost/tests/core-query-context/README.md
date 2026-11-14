# Core coverage query context

From the repository root, use an already built stage-0 compiler:

```bash
bash selfhost/tests/core-query-context/validate.sh /absolute/path/to/ashes
```

`render.sh` extracts the current production cache, indexed query/decision engines,
coverage-environment setter, and frame-context restoration helper into the ignored
`work/core-query-context` project. Missing extraction boundaries fail generation.
The fixture uses the real heap-layout classifiers, graph index and dropper context.
`Oracle.ash` freezes the pre-change decision functions from `cc50f0f5`.

The reduced Core state supplies deterministic seams for inference resolution,
constructor lookup, scalar reset eligibility, live-handler state and specialization
state. Both engines receive the same seams. This exercises production query/state
threading and compares complete copy-plan structures and value-type decisions;
it does not type-check all of CoreLowering or replace an integration compiler build.
Recursive worklist/collision behavior is independently covered by the 12,288-case
`admissibility-worklist` oracle.

The `check` mode covers exact generic arguments, warmed cache reuse, settled
monomorphic fact precedence, coverage replacement with the same ID/name,
restoration of both query-context pairs, rejection of restoration across an
environment replacement, and final copy-plan changes when lowering-state contract
facts change. The last case verifies that completed type facts are reused without
caching the final state-dependent plan. Its fixed test scaffolding leaves four
live cells; growth is tested separately.

Fresh-context modes dynamically name and classify 1,000/10,000 independent
environments. Warm modes retain context through the production restoration helper
while repeatedly querying `Box(Box(Str))`. `baseline100000` evaluates the frozen
old fresh-classifier path for the same type, state and checksum. All commands in
`validate.sh` use the workstation heavy-job wrapper and memory scopes, recording
wall time and peak memory under `work/core-query-context/results/`.

Validated on mattias-cachyos, 2026-11-12, using the stage-0 CLI built from
`5804df05` with the callback RC correction:

| Measurement | Plain | Poison |
|---|---:|---:|
| Differential/context checks | Pass | Pass |
| Fresh contexts 1,000 / 10,000, live cells | 2 / 2 | 2 / 2 |
| Warm queries 1,000 / 10,000 / 100,000, live cells | 2 / 2 / 2 | 2 / 2 / 2 |
| Warm 100,000 queries | 0.31s | 0.34s |
| Frozen old 100,000 queries | 4.31s | 4.65s |
| Both query paths' checksum | 100,000 | 100,000 |
| Fixture compile | 26.62s | 26.39s |
| Fixture compile peak RSS | 1,880,568 KiB | 1,870,932 KiB |

Persistent production boundaries in this change are call argument hand-off in
`applyCoreCall`, the value-type query before `closeCallWindow` allocates its restore
slot, `prepareCallAdmissibility`, and nested-function context restoration. Other
pure callers use the same indexed decision engine but discard cold updates at
their answer-only boundary. Full-layout and owned-child views, raw tuple-element
and recursive-copy gates, and closure-capture's settled-cache bypass remain
unmigrated. The benchmark is a focused query workload, not a stage2 build timing.

The renderer runs with `bash selfhost/tests/core-query-context/render.sh`, including
from fish. Ash declarations and assertions are checked-in `.ash` files. The
import-only `Prelude.imports` fragment is formatted as part of the generated
project because the formatter requires a declaration or expression after imports.
