# Pattern-owned tail in a temporary cons argument

This keeps the original direct recursive worklist shape. A nested `Many` node calls the refill helper with `resolved :: path`. The arena cons retains the nonempty pattern-owned path tail, and the compiler must release that temporary retain after the helper's result owns its references.

Compile with `ASHES_RC_LEAKCHECK=1` and run `1000` and `10000`; both print `done`. Stage 0 plain and poisoned now report `1→1`. With `GRC_NO_ARGUMENTCELLOWNERDUP=1`, the same source reports `8001→80001`. Stage 1 mirror validation requires the rebuilt compiler. Use the Step F memory scopes and record build/run times.

The source must not be rewritten into a step/fold workaround. See the companion `owned-call-result-worklist` fixture for the independent pop/push/helper-refill placement bug and escaping-head/borrowed-sibling guards.
