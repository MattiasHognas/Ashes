# Dropper worklist emitted-IR oracle

Run from the repository root with a built stage-0 compiler:

```bash
bash selfhost/tests/dropper-worklist-ir/validate.sh /absolute/path/to/ashes
```

An optional second argument selects a candidate `StructuralDroppers.ash` from a
different worktree. The renderer freezes the baseline at `c0e23905` and builds
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
`35654ef9f651893026a91b434891a9000bc356f0c56fc105c6438c6671b11ad3`:

- All 617 emitted lines match in all four baseline/candidate plain/poison runs.
- The recursive expression/list/tuple/box cases emit `__rcdrop_general_N` helpers,
  confirming that the changed admissibility predicate is exercised.
- All existing exact IR/cache/projection assertions pass.
- Both sources leave four live cells for the fixed assertion suite and two for
  the fixed IR dump, with or without poison. These fixed runs are not plateaus.
- Compilation times: baseline plain 24.44s, baseline poison 23.32s, candidate plain
  24.23s, candidate poison 24.11s. Maximum compilation RSS was 1,544,040 KiB.
- Each native assertion/IR run rounded to 0.00s at the timer's hundredth-second
  resolution. Full timing logs remain in `work/dropper-worklist-ir-results/`.
