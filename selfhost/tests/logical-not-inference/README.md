# Deferred logical-not inference regression

Bootstrap28 (`c0e23905b7c6608e10ef503b97c41ed5f15578b5`) fails while
lowering `wholeResultCandidates`, at `!ownedSlotNumberIn(slot)(releasable)`.
The later mutually recursive predicate has not yet settled its result type.
A frozen debug replay reproduced `CoreOperatorTypeMismatch("not",
SemVariable(352572), SemVariable(352572))` after 6:09.41 (peak 7,365,780 KiB).
Both bootstrap26 and bootstrap28 reject small unannotated and mutual-predicate
reproductions, so the failure predates the concrete callback annotation change.

The fix uses existing typed-goal discovery and replay. A provisional `Not` result
retains its operand type; pending goal variables cannot be generalized. Consumers
or later bodies can settle the type before replay pins it and emits real negation.
Active implementation evidence still dispatches first. The seven public lowering
entry points without replay start with pinned goals, preventing executable
placeholder IR from escaping.

Ordinary constrained generic `Not` bindings still lack mapped evidence in some
paths. Non-Boolean and mixed Boolean/custom-type uses remain explicit errors.
Boolean-only use-driven resolution is not full polymorphic `Not` support. Tests
preserve that limitation rather than silently forcing every operand to Bool.

## Focused real-Core checks

From the worktree root, `perl selfhost/tests/logical-not-inference/render.pl`
generates `work/logical-not-inference/ashes.json`. Compile that project using the
stage-0 CLI with `--debug -O0`, inside the agreed heavy-job wrapper and 20G systemd
scope; run the result in a 4G scope. No arguments runs the assertions. The optional
`program <file>` and `expression <file>` modes report lowering results for a small
source without rebuilding the driver.

`CoreLogicalNotInferenceTests.ash` checks inferred and annotated predicates,
identity calls, single/mutual recursion, the bootstrap's list-predicate shape,
invalid Int, active generic implementation evidence, unsupported explicit/inferred
custom Not and mixed uses. Direct expression checks require an unresolved error,
real `CmpIntEq` for a concrete Bool, and rejection of missing Show evidence.
The module is also registered in the normal semantics suite.

Validated using the frozen round1 stage-0 CLI:

- Real-Core driver: `logical-not inference passed`, exit 0; compile 3:21.40,
  peak 10,154,256 KiB; run 0.05s, peak 87,032 KiB.
- Production Core source SHA256:
  `ca4e6ac6338b34573d4230a94ea4c8335af2518b6aa841ee104dd883aa545371`.
- Test module SHA256:
  `c7efe3d1c4441e63cc789b99da0dfe67ebe8ef0e3eb831e01f57731e99f819d4`.
- `tests/logical_not_inferred_predicates.ash`: plain and poisoned native passes,
  stdout `true|true|1`.
- `tests/logical_not_rejects_integer.ash`: passes the stage-0 `ASH036` diagnostic
  expectation (the CLI rejects unavailable Not(Int) before Core lowering).

Local evidence: `work/logical-not-inference/deferred-compile.{log,time}`,
`check-deferred.{stdout,stderr,time}`, and
`work/not-inference/logical_not_*test*.{log,time}`. The real-Core driver reports
292 live cells once; this is not a plateau measurement or a leak-free claim.
Final integrated stage-1 acceptance and another stage-2 attempt remain required.

## Lifetime check: stage-1 discrepancy remains unresolved

`perl selfhost/tests/logical-not-inference/render-lifetime.pl` extracts the actual
production helpers for goal discovery, proof-map creation, replay, binding and
cache invalidation into `work/logical-not-lifetime/ashes.json`. Prelude and
assertions model the relevant state; instruction emission and trait dispatch are
stubs, so this isolates helper ownership rather than the complete lowering engine.
The real-Core checks above cover actual dispatch and IR separately.

Compile with `ASHES_RC_LEAKCHECK=1` and run with arguments `1000` and `10000`.
Both must print `bindings passed` and have identical live-cell counters. Repeat
stage-0 compilation with `ASHES_RC_POISON=1`. Keep the counter equality assertion.

Observed results:

| Producer | 1000 rounds | 10000 rounds | Result |
|---|---:|---:|---|
| stage 0 plain | 1 | 1 | flat |
| stage 0 poisoned | 1 | 1 | flat |
| frozen bootstrap28 stage 1 | 1001 | 10001 | FAIL, +1 cell/round |

Bootstrap28 SHA256:
`ecd48b70e3c1e5f20796a6eee67425a394641b9fe7d0bd2681d7302c55f0d9a3`.
Stage 1 has no poison backend. Evidence is retained under
`work/logical-not-lifetime/`, including the failing `validate-more.sh` comparison.
An independent allocation/watchpoint investigation is in progress; attribution to
an existing compiler defect versus a newly exercised owner is not yet established.
The semantic fix is therefore not claimed lifetime-accepted. No full suites or
successful stage-2 compilation are claimed by this change.
