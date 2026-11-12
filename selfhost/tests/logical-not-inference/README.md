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
An independent baseline reproduction removes `finishOrDeferCoreLogicalNot`
entirely. It constructs a dynamic key in the existing pending-goal state, binds its
type, and calls the unchanged `resolvedTraitGoals` helper. Bootstrap28 emits live
counts 2/1001/10001 at 1/1000/10000 rounds; a literal pending-list control stays
flat at 1/1. Seed creation, key creation, empty proof lookup, deferral alone, and
binding alone are also flat. Adding `resolvedTraitGoals` introduces the growth;
the later proof-map and pinning steps add none.

The leaked cell is a reference-counted string (`2:3:Not:not` in the reduced case),
still at reference count 1. This establishes an existing stage-1 ownership defect
in the borrowed pending-list to resolved-list path, newly exercised by logical
negation. It does not establish a safe release fix. The failing equality check
remains required, and the semantic fix is not claimed lifetime-accepted. No full
suites or successful stage-2 compilation are claimed by this change.

The frozen minimal sources, watchpoint observations, and a counter-equality runner
are tracked in [pending-goal-lifetime](../pending-goal-lifetime/README.md).

## Recursive-group discovery during whole-program replay

Final bootstrap29 passed each small predicate case individually, but the combined
native fixture failed at `!odd(n - 1)` in `even`. The same combined source also
failed in the direct-Core driver; this was a missing combination test, not evidence
of a CLI-only difference. GDB showed `traitGoalsPinned = true` with only the earlier
`invert` site's Boolean proof in the map. The mutual group's first member pass
could no longer infer its later sibling's result, although the group's ordinary
unpinned discovery/replay path handled the group successfully on its own.

The recursive-group helper now permits discovery in its disposable first member
pass. Its final member pass starts from the original group state plus settled
sibling proofs, preserving the caller's pinned flag. A pinned caller always takes
that final pass, even if no sibling result settled; no provisional expression may
escape through a public expression API. Tests combine an inferred `invert` with
mutual predicates and cover concrete and unresolved recursive expressions.

The known pending-goal string ownership defect above remains outstanding. This
inference correction does not change its releases or relax its equality check.

The revised real-Core driver compiled in 3:25.13 (peak 10,151,556 KiB).
Its dynamic source checks now accept the combined `invert`/mutual program; the
previous driver rejects that identical source. Explicit and inferred custom Not
still report the expected Not/Bit error, and mixed uses retain their call mismatch.
A valid nested recursive Boolean expression lowers successfully, while an abstract
recursive `invert` still reports Not/SemVariable. Production Core SHA256 for this
candidate is `3f61297ff2a69465768239a1502dbb6ca8b6758470c447ff659191381d199eac`.

The expanded registered test module has **not yet passed as a whole**: its first
run stopped at a malformed new test using nested mutual `and ... in` syntax.
Mutual groups are top-level syntax. That test now uses a valid nested single
recursive binding, whose success/error cases were checked through the existing
driver's dynamic expression mode. The corrected registered module awaits the
combined real-Core validation build; no extra compiler build was started for it.
Evidence is in `work/logical-not-inference/recursive-compile.{log,time}`,
`check-recursive.{stdout,stderr,time}`, and
`work/not-inference/{s29-invert-mutual-gdb.log,recursive-*.stdout}`.
