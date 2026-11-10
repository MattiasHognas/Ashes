# Heap shallow-copy query equivalence

This standalone driver compares `classifyHeapShallowCopy` with the `ShallowCopy`
case of full heap-layout classification. It also checks the existing minimal
admissibility projection. Cases include scalar and string-like primitives,
opaque resources and handles, generic scalar/owned/unresolved fields, recursive
ADTs, records, unequal constructor arities, closures, unknown names, and zero-cost
scalar/string wrappers. Success prints `equivalent`.

The driver uses only Boolean assertions, avoiding the stage-1 mixed-type
`assertEqual` inference limitation in the complete classification test module.
Run stage 0 plain, stage 0 with compile-time `ASHES_RC_POISON=1`, and stage 1 plain.
The complete `HeapLayoutClassificationTests` suite separately checks frozen
expected facts under stage 0 plain and poisoned, including runtime reuse flags.

The single-run live-cell total is not a plateau measurement. Repetition and
fresh-environment lifetime checks live in `../heap-classification-plateau`.
Stage 1 does not generate poison instrumentation.
