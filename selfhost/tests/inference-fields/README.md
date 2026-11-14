# Record-field inference boundary

`Main.ash` repeatedly creates fresh inference environments and runs sixteen
assertions from `RecordFieldInferenceTests.ash`. They cover known and inferred
record receivers, generic field substitution, independent instantiation versus
captured monomorphism, ambiguous and shadowed record definitions, dotted-value
and capability precedence, existing substitutions, and retained trait constraints.
The ambiguous search exits after two distinct visible matching records; a string
map suppresses shadowed constructor definitions without a quadratic name scan.

The `queries/` programs exercise the same inference boundary through actual Core
lowering. `query-cases.tsv` lists their expected native output. Each program also
contains its ordinary end-to-end test directive. They cover constructor values,
external arguments, intrinsic builtins, named and aliased annotations, fully
qualified trait methods, and record fields. The actual-Core driver must receive
one absolute source path at a time; compilation acceptance is separate from
executing the emitted program.

## Validation status

The focused sixteen assertions pass with the accepted stage-0 compiler. Repeating
them currently exposes a lifetime blocker: live counts are 13 / 12001 / 120001 at
1 / 1000 / 10000 rounds. Group isolation yields +8 for known/generic fields, +2 for
unique/ambiguous lookup, +1 for precedence, and +1 for constraints. This is not a
passing plateau, and the change is not accepted until the allocation and missing
release are attributed and fixed. No full suite or bootstrap result is implied.
