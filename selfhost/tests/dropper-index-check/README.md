# Structural dropper indexes

This standalone fixture checks the public behavior of prepared dropper indexes:

- First-seen type IDs and constructor declaration order, including conflicting old symbol IDs and repeated type names.
- Renumbering of nested named, generic, list, tuple, and function types; unknown type IDs remain unchanged.
- Prepared-map renumbering agrees with the legacy list API, including that API's first-match handling of duplicate names. Both structural and to-space copier entry points receive the prepared IDs.
- Constructor lookup by name regardless of the requested symbol ID or generic arguments, with empty results for unknown and non-named types.
- Tagless classification for one-constructor, multiple-constructor, and unknown types.
- Fresh preparation with dynamically allocated names. The returned body is queried after the source definitions and preparation record leave scope.

Compile this project with `ASHES_RC_LEAKCHECK=1`, then run the executable with `1000` and `10000`. Both runs must print `dropper index ok` and report the same live-cell count. Run stage 0 plain and with compile-time `ASHES_RC_POISON=1`, and stage 1 plain. Stage 1 does not currently implement poisoning.

Use the repository's scoped resource limits: 8G for compilation and 4G for each executable, with swap disabled. Record wall time and peak RSS separately from leak-check stderr.

The index fixture is not a replacement for `StructuralDroppersTests.ash`: those nine snapshots check generated dropper instructions. The focused validation runs those snapshots with stage 0 plain and poisoned; their mixed assertion types are not part of this standalone stage-1 driver. Full integration suites run at the Step F gate.

Fresh ID lookup and returned-body lifetime checks run independently in each round. Combining the ID assertion with a conditional aggregate return exposed a pre-existing stage-1 failure, reproduced with the unchanged old module and a small standalone map/body example. The split retains both lifetime checks; it does not change production code to work around that compiler issue.
