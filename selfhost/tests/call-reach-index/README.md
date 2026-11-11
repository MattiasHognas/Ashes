# Frozen call reach queries

`Main.ash check` compares the prepared per-argument queries against the original entry-list predicates in 6,912 cases. Coverage includes duplicate entries, positive/negative/zero counts, bare/component/exposed paths, sibling prefixes, empty and slash-containing parameter names, duplicate parameter names, missing and negative argument positions, poison flags and `UnenumeratedInputs` causes. Prepared flow flags must retain original parameter order.

`Main.ash 1000` and `Main.ash 10000` repeatedly construct and consume fresh reach states and prepared maps using runtime-derived Unicode names. Run separately from exhaustive checks to isolate the prepared-state lifetime slope.

Compile with `ASHES_RC_LEAKCHECK=1` under stage0 plain, stage0 with `ASHES_RC_POISON=1`, and stage1 plain. Stage1 does not implement poison instrumentation. All compiler and native program commands must use Step F's scoped memory budgets.

Expected stdout is `6912 call reach comparisons ok` for `check`, and `call reach index ok` for either fresh-state count. Live-cell counts must be flat across the fresh-state counts.

The Core integration prepares these query maps only after the reach fixpoint completes, keyed by the selected `ReachFunction.key`. Existing identity/name resolution chooses the summary; bare function names are not cache keys. Unregistered lambdas prepare their locally computed reach once per call context. The evolving `ResultReachState` representation is unchanged.

Validated with stage0 plain, stage0 poisoned and stage1: all 6,912 comparisons pass; exhaustive mode and both fresh-state counts end at one live cell. Stage1 compiled the fixture in 2:38.80; stage0 builds took approximately 21 seconds. Detailed time/RSS files are recorded under `work/step-f/call-reach-index-*`.
