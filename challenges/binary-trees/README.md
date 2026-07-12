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

**Implemented.** [`binary-trees.ash`](binary-trees.ash) is the recursive-ADT version described
above (`type Tree = Leaf | Node(Tree, Tree)`, recursive `make`/`check`, accumulator-passing outer
loops). Output matches the Benchmarks Game format; at `N=10`:

```
stretch tree of depth 11	 check: 4095
1024	 trees of depth 4	 check: 31744
256	 trees of depth 6	 check: 32512
64	 trees of depth 8	 check: 32704
16	 trees of depth 10	 check: 32752
long lived tree of depth 10	 check: 2047
```

## Build & run

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/binary-trees/binary-trees.ash -o challenges/binary-trees/binary-trees
./challenges/binary-trees/binary-trees 21
```

## Benchmark

```bash
challenges/bench.sh binary-trees 21
```

Measured on a 32-thread AMD Ryzen 9 9950X3D, Linux x64 (single-threaded):

| N (maxDepth) | Time | Peak RSS |
|--------------|------|----------|
| 16 | 0.03 s | 5.8 MB |
| 18 | 0.15 s | 23.8 MB |
| 20 | 0.74 s | 96.0 MB |
| **21** (standard) | **1.51 s** | **191.5 MB** |

The headline probe — *does the per-iteration arena reset fire for a discarded `Tree`, or does
memory grow toward OOM?* — comes out **positive**: `N=21` allocates and discards tens of millions
of short-lived nodes (the depth-4 stage alone builds 2^21 trees) yet completes in constant memory
relative to the churn. Peak RSS tracks the size of the single **long-lived** depth-`N` tree
resident across the run (~4M nodes at `N=21`), not the total allocated — so the bump arena is
reclaiming the per-iteration garbage. This is the isolated counterpoint to `pidigits`, where the
growing-width `BigInt` accumulators are *not* reclaimed within the loop.
