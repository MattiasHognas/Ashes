# Argument containment allocation plateau

Build this project with `ASHES_RC_LEAKCHECK=1`, using each compiler stage. Run the resulting
program with `1000` and `10000`; both must print `done` and report identical final live-cell
counts. Repeat the stage-0 build with compile-time `ASHES_RC_POISON=1` to detect invalid lifetime
accesses. Stage 1 does not currently implement that switch; setting it is not poisoned coverage.

Each iteration constructs a shared type graph with a runtime-dependent depth and performs a
positive or negative containment query. This exercises early return with pending work as well as
complete traversal, the worklist, visited set, callback-produced fields,
and temporary names instead of allowing the compiler to precompute one constant answer.

Use the repository's memory-scoped execution and heavy-job limits for builds and runs. Keep
generated binaries and measurement logs outside this test directory's tracked source files.
