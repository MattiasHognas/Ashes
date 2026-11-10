# Extracted admissibility traversal regression

Run `bash scripts/selfhost-dev/render-admissibility-worklist.sh` from the repository.
It generates an ignored project at `work/admissibility-worklist/ashes.json`, copying
current production old/new CoreLowering traversal functions and StructuralDroppers'
`sameTypeKey`/`containsType`. Renamed or ambiguous boundaries fail generation. The
source hashes are saved beside the generated project; regenerate after source edits.

Compile the generated project and run it under stage 0 plain, stage 0 poisoned, and
stage 1 plain. Expected stdout: `12288 graphs and special cases ok`, exit 0. The
validated single-run counter is 1 in each mode; this is not a plateau measurement.
Use the repository's scoped memory budgets and separate timing files for each build
and run. Stage 1 does not generate poison instrumentation.

Coverage: all 512 directed three-node graphs, eight forbidden-node masks, and three
roots compare old/new answers. Special cases exercise sibling coarse-key collisions
that must fall back, ancestor coarse-key cycle cuts, Task-before-visited ordering,
root-cache-before-reset ordering, a depth-10 reference diamond, and a depth-26 shared
diamond that would cause exponential repeated paths in the old traversal.

The fixture replaces CoreLoweringState, constructor lookup, type resolution, reset
eligibility, and layout classification with small deterministic functions. It tests
production traversal and fallback logic; it does not test real inference, constructor
resolution, cache invalidation, frame restoration, or the complete compiler. No frozen
copy of the production traversal is checked in, so the regression follows source edits.
