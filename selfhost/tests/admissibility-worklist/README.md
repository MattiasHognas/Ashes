# Extracted admissibility traversal regression

Run `bash scripts/selfhost-dev/render-admissibility-worklist.sh` from the repository.
It generates an ignored project at `work/admissibility-worklist/ashes.json`, copying
the current production indexed CoreLowering decision engine and StructuralDroppers'
`sameTypeKey`/`containsType`. An optional first argument selects a candidate
CoreLowering source file in another worktree. Renamed or ambiguous boundaries fail
generation. The source hashes and exact extracted sections are saved beside the
generated project; regenerate after source edits.

The independent reference is the legacy path traversal from commit
`cc50f0f52b4ee76ffa0f96ee3c70e288bd273abd`, retained in `Reference.ash.inc` with
function names prefixed by `reference` to avoid collisions with the candidate.
It does not require the original Git object, so shallow and squashed checkouts work.
The original full CoreLowering source SHA256 was
`f36a368656f969561dc75dda1b1917ca147d93384a6cbb7d3207de2d001e5f02`.
The reference identity and tracked reference hash are recorded in
`reference-source.txt`. The candidate engine is extracted verbatim; only the frozen
reference function names were changed.

Compile the generated project and run it under stage 0 plain, stage 0 poisoned, and
stage 1 plain. Expected stdout: `12288 graphs and special cases ok`, exit 0. The
validated single-run counter is 1 in each mode; this is not a plateau measurement.
Use the repository's scoped memory budgets and separate timing files for each build
and run. Stage 1 does not generate poison instrumentation.

Coverage: all 512 directed three-node graphs, eight forbidden-node masks, and three
roots compare old/new answers. Special cases exercise sibling coarse-key collisions
that must fall back, ancestor coarse-key cycle cuts, Task-before-visited ordering,
root-cache-before-reset ordering, a depth-10 reference diamond, and a depth-26 shared
diamond that would cause exponential repeated paths in the old traversal. The
indexed query must return all 27 classification updates from that shared diamond,
and retain the single update performed before an early forbidden-node rejection.

The fixture replaces CoreLoweringState, constructor lookup, type resolution, reset
eligibility, and layout classification with small deterministic functions. It tests
production traversal, fallback logic, and threading of the returned query state; it
does not test real inference, constructor resolution, cache invalidation, frame
restoration, or the complete compiler. Those require the separate context fixture.
The candidate follows source edits while the reference remains fixed.

Validation of indexed-engine extraction SHA256
`8eb813236f134aa019f4e2ede59d18507debca29ea4f0223db093db96ab2605e` passed
all comparisons under stage 0 plain, stage 0 poisoned, and bootstrap27 stage 1 plain,
with live count 1 in each run. Fixture compilation took 21.44s, 21.25s, and 93.69s
respectively (peak 941124, 920244, and 747436 KiB); each executable ran in 0.01s.
This validates the extracted decision engine, not the whole compiler or a repeated
compiler-memory census.
