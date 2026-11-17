# Mixed constrained bootstrap regressions

Bootstrap32's first discovery failed in the real optimizer: argument 1 of
`lookupAssociation` was fixed to `Int` and then used with `Str` labels by
`countBranchRefsToLabels`. Pinned replay masked that error with missing `Ord`
evidence. The source helper must remain polymorphic; separate typed source
helpers are not an acceptance fix.

`render.sh` inserts the exact current optimizer helper into these fixtures:

- `Main.ash`: mixed Int/Str keys, different value types, and
  recursive forwarding through a list of tables. Keys and retained strings vary
  with the runtime round count.
- `AliasMixedLookup.ash`: the matrix plus a first-class function alias.
- `PartialMixedLookup.ash`: the alias matrix plus local partial application.
- `EscapedMixedLookup.ash`: the matrix plus partial functions returned in tuples
  and passed through another function.
- `InlineEqualityControl.ash`: the separate curried anonymous equality control
  that exposed wrong list equality in bootstrap32. It is preserved without
  converting the comparisons to a different implementation.

The renderer also adapts the existing
`tests/constrained_loop_lowered_at_caller_types_releases_snapshots.ash` by changing
only its runtime round count. Expected output is 9 times that count.

Run from any shell, including fish, using Bash:

```bash
bash selfhost/tests/mixed-constrained-bootstrap/render.sh
bash selfhost/tests/mixed-constrained-bootstrap/validate.sh /absolute/compiler unique-tag plain
```

The strict validator checks `Main` and the snapshot loop at 1, 1,000 and 10,000
rounds, including exact stdout, successful exits, exactly one live-cell counter,
and identical counters. It runs both suites even when one fails and exits
nonzero on any failure. Every compile/program is scoped, swap is disabled, and
time/memory/source hashes are recorded under the unique ignored evidence folder.
Use `poison` only with a compiler that implements the compile-time poison switch;
a stage1 result is not a poisoned result merely because the environment is set.

The broader alias/partial/escaped/anonymous fixtures remain explicit diagnostic cases;
passing the strict validator does not claim they pass. They can be compiled from
the generated `work/mixed-constrained-bootstrap/` files under the same scopes.

Initial stage0 evidence is **not all green**. The alias matrix prints the correct
answer but reports 5/4001/40001 live cells, including after local partial
application is removed. Isolated direct, varied-value and recursive-forwarding
cases are flat at 1/1/1; the first-class alias calls cause the growth.
The escaped matrix crashes in
the string equality implementation with an Int value; this also happens with
`GRC_NO_TUPLESTRINGPARTS=1`, disabling the new tuple-sharing rule. These failures
must not be silently treated as passing baselines or attributed to new stage1
code. Bootstrap32 also fails the anonymous-list equality control at runtime.
The compiler fix and final ownership acceptance remain pending.

## Bootstrap32 baseline

The final direct-call matrix and the existing snapshot loop both pass stage0
plain and poisoned with exactly **1/1/1** live cells at 1/1,000/10,000 rounds.
Plain compilation took 0.56s / 1.74s (Main / SnapshotLoop); poisoned compilation
took 0.57s / 1.76s. Peak compiler RSS was at most 215,224 KiB.

The unchanged bootstrap32 stage1 binary
`9b6ee364da71ca3e7538bb8febbd299676ff4a3ce17e7540fdf012197345ba37`
rejects Main with `ASH002 Type mismatch: Int vs Str` in argument 1 of
`lookupAssociation`. Its snapshot executable produces the correct output but
reports **6/5001/50001** live cells, confirming the previously documented five
cells per round are still present. The strict runner rejects this result.
No stage1 poison result is claimed.

The isolated alias controls each add two cells per round: either Int-key or
Str-key alias calls alone report **3/2001/20001**. Together they account for all
four cells per round in the alias matrix. This is feature attribution, not proof
of the exact unmatched retain. The separate escaped-function failure remains.
