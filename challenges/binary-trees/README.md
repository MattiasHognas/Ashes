# binary-trees — Ashes Benchmarks Game challenge

Part of the `challenges/` flaw-finding suite (same ground rules as
[`../1brc/README.md`](../1brc/README.md): **not** discovered or run by CI, and the
`.ash` files here are not format-checked by any gate — format them manually with
`dotnet run --project src/Ashes.Cli -- fmt <file> -w`).

> Source: [Benchmarks Game — binary-trees](https://benchmarksgame-team.pages.debian.net/benchmarksgame/performance/binarytrees.html)

## The benchmark

Allocate and immediately discard an enormous number of binary tree nodes. Build one
"stretch" tree, then for each depth from `minDepth` (4) to a `maxDepth` (e.g. 21) build
many trees, walk each to compute a checksum, and let it go. The work is dominated by
churning allocation and reclamation of short-lived tree structures — it exists purely to
stress the allocator/collector, not arithmetic or IO.

## Intended Ashes approach

A recursive ADT — `type Tree = Leaf | Node(Tree, Tree)` — built recursively to a given
depth, and a recursive `check : Tree -> Int` that folds the node count. The outer loops
are accumulator-passing tail recursion (the regime Ashes compiles to a real loop).

## What it probes (expected flaws)

- **The non-GC bump-arena reclamation path (FLAWS #2).** This is the headline reason to
  run it: it allocates and discards huge tree structures in a tight loop, which is exactly
  the churn that the 1BRC residual linear leak fails to reclaim — but here in a small,
  isolated reproducer (a tree, not a 50 GB hashmap), far cheaper to debug.
- Deterministic destruction of pointer-bearing recursive ADTs that escape an iteration.
- Whether per-iteration arena reset (`CanArenaReset` / `GetTcoCopyOutKind`) ever fires for
  a `Tree` accumulator, or whether memory grows monotonically toward OOM.

## Dependencies / blockers

None — integer + recursive-ADT only. No math lib needed.

## Status

**Scaffold only.** `binary-trees.ash` and the `FLAWS.md` writeup are deferred — to be
written and run as a later pass.

## Build & run (once written)

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/binary-trees/binary-trees.ash -o challenges/binary-trees/binary-trees
./challenges/binary-trees/binary-trees 21
```
