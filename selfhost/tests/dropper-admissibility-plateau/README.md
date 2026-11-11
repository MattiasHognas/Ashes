# Dropper admissibility cache projection lifetime check

Build this project with leak checking, using stage 0 or a stage 1 compiler. Run the
result with `cached 1000`, `cached 10000`, `miss 1000`, and `miss 10000`.
Each round creates fresh constructor names and a fresh synthesis environment. The
cached path populates the existing full-layout cache before querying its
admissibility projection; the miss path uses the dedicated classifier unchanged.
Both must print `done`. Compare live-cell growth for each path separately.

Stage 0 additionally supports compile-time `ASHES_RC_POISON=1`; an environment
variable alone does not provide poison instrumentation in the current stage 1
backend. Keep compiler/run timing output separate from leakcheck stderr.

The exact facts and unchanged synthesized instructions are checked by
`DropperAdmissibilityProjectionTests` and `StructuralDroppersTests` in the semantics
suite. This fixture measures temporary ownership; it does not replace those
semantic assertions or the full compiler gates.
