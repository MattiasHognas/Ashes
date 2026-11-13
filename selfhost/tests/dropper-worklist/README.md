# Query-local dropper admissibility

From a worktree root, using a built stage-0 CLI:

```bash
bash selfhost/tests/dropper-worklist/validate.sh /absolute/path/to/ashes
```

The renderer exposes the private production decision in an ignored probe module.
`FrozenDecision.ash.inc` preserves the decision and nominal-key relation from
`c0e23905`, with prefixed names. Both decisions use the same unchanged view-query
and constructor-lookup APIs; the oracle does not call the new worklist.

The `FrozenLinearWorklist.ash.inc` oracle additionally preserves the linear-visited
worklist from `5344d284`. Initial-path tests compare all 10,010 three-way lookup
results across ordered triples of ten adversarial key types and the empty path,
including first-alias priority and the bare/rendered key partition. Linear
worklist versus cached-key worklist runs exercise real diamonds of depths 16, 32
and 64 for 10,000 queries each, checking identical answers and exit counters.
The applied-type graph uses tuple/list arguments so nominal keys require actual
semantic-type rendering. Its depth-64 comparisons run at 1,000 and 10,000 queries,
asserting that completed shared facts cover every graph level. These tests retain
linear membership search; the change caches rendered keys rather than building
a dictionary. Bare names retain their cheap name comparison.

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
`work/dropper-worklist/`. Plain and poisoned modes are both required. Every stderr counter is checked
against its exact expected value, so missing counters cannot pass as a plateau.

Generated `.ash` files are formatted during validation. The checked-in `.ash.inc`
files hold the same canonical source without entering the semantics suite's
ordinary source-root discovery. The integration lead runs the full gates.

Focused results on mattias-cachyos (2026-11-13), candidate source SHA-256
`abed6e121e7cdc1562edae39373ae037950b508f7f61ca29c70e99fbe1571f09`:

| Check | Plain | Poisoned |
|---|---|---|
| 24,576 graph decisions and adversarial cases | Pass | Pass |
| 10,010 ordered initial-path lookup statuses | Pass | Pass |
| Fresh contexts, 1,000 / 10,000 rounds | Live 2 / 2 | Live 2 / 2 |
| Warm queries, 1,000 / 10,000 rounds | Live 2 / 2 | Live 2 / 2 |
| Bare depth-64, 10,000 queries, linear / cached keys | 0.45 / 0.41 s | 0.47 / 0.45 s |
| Applied depth-64, 10,000 queries, linear / cached keys | 9.08 / 0.85 s | 10.95 / 0.92 s |
| Applied cached keys, 1,000 / 10,000 rounds | Live 2 / 2 | Live 2 / 2 |
| Applied cached keys peak RSS, 1,000 / 10,000 rounds | 14,708 / 14,712 KiB | 14,500 / 14,504 KiB |
| Fixture compilation | 21.87 s; 1,374,848 KiB peak | 21.50 s; 1,373,376 KiB peak |

A separate comparison at 10,000 applied queries found cached-key list versus
fresh rendered-key AVL at depth 128: 1.99 / 1.70 s and 14,712 / 506,244 KiB peak;
at depth 256: 5.48 / 3.54 s and 14,708 / 1,145,236 KiB peak. The list was selected
to avoid per-query tree allocation and peak-memory growth. These exploratory
larger-depth measurements preceded the initial-path preparation cleanup; empty
initial paths exercise the same measured search in both versions.

These are focused synthetic measurements, not a stage2 build-time claim.
Independent frozen/candidate synthesis validation in `dropper-worklist-ir`
passed all 617 emitted IR lines and exact fixed counters in plain and poison.
