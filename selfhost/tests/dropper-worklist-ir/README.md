# Dropper worklist emitted-IR oracle

Run from the repository root with a built stage-0 compiler:

```bash
bash selfhost/tests/dropper-worklist-ir/validate.sh /absolute/path/to/ashes
```

An optional second argument selects a candidate `StructuralDroppers.ash` from a
different worktree. The checked-in `FrozenStructuralDroppers.ash.inc` preserves
the baseline from `c0e23905`, so shallow/fresh clones need no historical Git object.
The renderer builds
coherent baseline/candidate probe modules: both copier modules and all test
modules import the selected dropper module. Source SHA-256 files record the exact
dropper copies. Generated projects and measurements stay under ignored `work/`.

The fixture runs the nine existing exact structural-dropper IR expectations and
the full-cache precedence/projection suites. Its independent output cases cover
a mixed recursive expression type with strings, two recursive fields and lists
of recursive fields; nested generic boxes; repeated tuple children; lists; and
closure-bearing rejected types. It also runs the existing arena/to-space copier
output cases, covering named types, recursive ADTs, lists and tuples.

Comparison includes every emitted instruction, function label, local/temp count,
and synthesis temp/local/lambda/label counter. Plain and poisoned baseline and
candidate output must match byte for byte. No generated release is assumed safe
merely because the eligibility result matches: existing instruction expectations
must pass as well. Fresh/warm worklist lifetime and graph-decision differential
coverage live in the separate `dropper-worklist` fixture.

Validated on mattias-cachyos against candidate source SHA-256
`abed6e121e7cdc1562edae39373ae037950b508f7f61ca29c70e99fbe1571f09`:

- All 617 emitted lines match in all four baseline/candidate plain/poison runs.
- The recursive expression/list/tuple/box cases emit `__rcdrop_general_N` helpers,
  confirming that the changed admissibility predicate is exercised.
- All existing exact IR/cache/projection assertions pass.
- Both sources leave four live cells for the fixed assertion suite and two for
  the fixed IR dump, with or without poison. These fixed runs are not plateaus.
- Compilation times: baseline plain 22.85s, baseline poison 22.59s, candidate plain
  23.69s, candidate poison 22.86s. Maximum compilation RSS was 1,560,356 KiB.
- Each native assertion/IR run rounded to 0.00s at the timer's hundredth-second
  resolution. Full timing logs remain in `work/dropper-worklist-ir-results/`.
