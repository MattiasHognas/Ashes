# Admissibility cache regression and plateau

This standalone program calls the production `AdmissibilityCache` module. It checks
exact identity for same-name/different-ID types, different generic arguments,
unresolved-variable/parameter rejection, and the bounded 64-entry generation.
Each iteration creates and releases fresh cache entries, including 130 insertions.

Compile with `ASHES_RC_LEAKCHECK=1`, then run the same binary with `1000` and `10000`.
Both runs must print `cache ok` and equal live-cell counts (validated baseline: 1/1).
Validate stage 0 plain, stage 0 with compile-time `ASHES_RC_POISON=1`, and stage 1
plain. Stage 1 does not generate poison instrumentation.

This tests the cache helper, not CoreLowering's state invalidation or frame lifecycle.
Those require compiler integration tests. Keep the stage-1 compiler's own leakcheck
stderr separate from the emitted program's counters.
