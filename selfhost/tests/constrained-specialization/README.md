# Concrete constrained source-copy planning

`CoreConstrainedSpecializationTests.ash` tests immutable source candidate identity,
exact concrete type keys, repeated quantified variables, nominal type identity,
closed result requirements, and cached rejection versus absence. The native driver
constructs and discards fresh candidate/source/cache roots each round.

`CoreConstrainedSpecializationInferenceTests.ash` additionally runs real lambda
inference, generalization, fresh instantiation, call inference and concrete pinning
once before the loop. Pure inferred arrows retain quantified capability rows; the
planner chooses an empty-row instance only for those quantified source variables.
Captured rows, actual capabilities, mismatched evidence and independently open
results remain rejected. Candidate metadata also records the original entry-frame
slot, so equal type schemes do not substitute for lexical binding identity.

Run from any shell, including fish:

```sh
bash selfhost/tests/constrained-specialization/validate.sh /absolute/path/to/ashes unique-tag plain
bash selfhost/tests/constrained-specialization/validate.sh /absolute/path/to/ashes another-tag poison
```

The runner checks exact stdout and requires exactly one valid live-cell counter at
1, 1,000 and 10,000 rounds, with identical counters at all counts. Source hashes and
compile/run timings are saved under ignored `work/`.

The initial stage0 planning check passes plain and poisoned with live2/2/2. This is
only the pure registry/planning lifetime check; actual Core call routing, generated
copy ownership, and rebuilt-stage1 SnapshotLoop acceptance are separate gates and
remain pending while this change is being integrated.

Copy lookup is indexed first by immutable candidate identity, then compares exact
parameter types within that candidate's variants. The indexed version also passes
the strict plain/poisoned plateau (3.42s/3.48s compilation, below288MiB peak RSS).

The final inference-derived gate also passes plain and poisoned with live2/2/2
at all three counts (18.63s/18.86s compilation, 1,145,812/1,166,960KiB peak RSS).
The complete Core driver and exact SnapshotLoop copy selection remain separate
integration checks; these planner results do not establish stage1 or stage2 acceptance.
