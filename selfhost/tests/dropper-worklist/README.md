# Query-local dropper admissibility

From a worktree root, using a built stage-0 CLI:

```bash
bash selfhost/tests/dropper-worklist/validate.sh /absolute/path/to/ashes
```

The renderer exposes the private production decision in an ignored probe module.
`FrozenDecision.ash.inc` preserves the decision and nominal-key relation from
`c0e23905`, with prefixed names. Both decisions use the same unchanged view-query
and constructor-lookup APIs; the oracle does not call the new worklist.

The differential suite checks 24,576 decisions: all 512 directed graphs on three
named nodes, four leaf-rejection configurations, each root, and empty or singly
blocked paths. Additional cases cover exact types sharing a rendered key,
generic-versus-argument-free key distinctions, opaque-versus-named arguments,
unsupported child drops, missing constructors behind a blocked path, and a
body-local authoritative full-fact override after warming shared views.

Real constructor graphs form a diamond chain: each node owns two references to
the previous node, with a string-bearing leaf. Definitions are ordered to match
the prepared symbol IDs; the fixture asserts that the root has its expected
fields before measuring. The same executable runs the frozen and new decisions
at depths 6, 10 and 14, with identical answers and exit counters required.

Fresh dynamic contexts at 1,000 and 10,000 rounds test release of whole graphs.
Warm diamond queries at those counts test query-local worklist release while
retaining the graph and completed views. Every compilation and execution uses
`heavy2.sh`, a memory scope, and saved `/usr/bin/time -v` logs under
`work/dropper-worklist/`. Plain and poisoned modes are both required.

Generated `.ash` files are formatted during validation. The checked-in `.ash.inc`
files hold the same canonical source without entering the semantics suite's
ordinary source-root discovery. The integration lead runs the full gates.

Focused results on mattias-cachyos (2026-11-12):

| Check | Plain | Poisoned |
|---|---|---|
| Frozen graph decisions and adversarial cases | Pass | Pass |
| Fresh contexts, 1,000 / 10,000 rounds | Live 2 / 2 | Live 2 / 2 |
| Real warm diamond, 1,000 / 10,000 rounds | Live 2 / 2 | Live 2 / 2 |
| Depth-14 diamond, 20 frozen queries | 0.27 s | 0.30 s |
| Depth-14 diamond, 20 worklist queries | <0.01 s | <0.01 s |
| Scaling output and exit counts, depths 6 / 10 / 14 | Identical | Identical |
| Fixture compilation | 21.64 s; 1,255,404 KiB peak | 21.96 s; 1,296,172 KiB peak |

The time tool reports hundredths of a second; `<0.01 s` denotes its displayed
`0:00.00`, not zero execution time. These are focused synthetic measurements,
not a stage2 build-time claim. Independent frozen/candidate synthesis validation
also found all 617 emitted IR lines and counters identical, including exercised
general droppers, in both plain and poisoned modes.
