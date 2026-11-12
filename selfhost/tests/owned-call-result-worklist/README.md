# Owned call result passed through a worklist

This is the direct Tree/Job reproducer for the compiler RC bug exposed by the Step F heap-drop worklist. The source shape is intentional: `resolve` returns a tree owned by the caller, then different recursive branches pass a nested field or a helper-built list of fields into `pending`. The compiler must preserve those fields while the worklist uses them and release the earlier owned result when it is no longer needed.

Do not rewrite the walk into a step/fold/state-return helper to avoid the ownership bug. This fixture must continue to test the direct recursive shape.

Compile with `ASHES_RC_LEAKCHECK=1`, run with `1000` and `10000`, and require equal final live-cell counts. Test stage0 plain, stage0 with `ASHES_RC_POISON=1`, and stage1 plain. All compiler/native commands must use the Step F scoped memory limits. Stage1 does not implement poison instrumentation.

Before the compiler fix, both stages leaked five cells per iteration: `5001→50001`. The expected stdout is `done` for either count. Fixed compiler validation is pending.

Guard modes:
- `escape1000` / `escape10000`: completed aggregate Job heads escape through a sibling accumulator and are read after pending has been consumed. Expected stdout: `escaped head ok`.
- `borrowed1000` / `borrowed10000`: a sibling keeps the borrowed children list of an owned resolved result and is read after pending is consumed. Expected stdout: `borrowed sibling ok`. The existing sibling-placement guard must remain authoritative; compare leak counts to the disabled-rule baseline separately from correctness/poison checks.

Validated stage0 with `GRC_NO_CONSEDTAILCALLREFILL` unset (rule on): original walk and escaped-head modes are flat `1→1`, both plain and poisoned. With the rule disabled, both retain `5001→50001`. The borrowed-sibling mode remains `5001→50001` in all variants, with correct output and no poisoned crash; this negative case confirms the existing sibling protection remains in force. It is not claimed leak-free.

Stage1 previously showed `5001→50001` on the unchanged original walk. Its actual compiler-output acceptance still requires a stage1 compiler rebuilt with the mirror; compiling the shape-analysis fixture with an older stage1 validates the analysis code only.
