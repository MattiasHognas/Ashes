# spectral-norm — Ashes Benchmarks Game challenge

Part of the `challenges/` flaw-finding suite (same ground rules as
[`../1brc/README.md`](../1brc/README.md): **not** run by CI; format `.ash` manually with
`dotnet run --project src/Ashes.Cli -- fmt <file> -w`).

> Source: [Benchmarks Game — spectral-norm](https://benchmarksgame-team.pages.debian.net/benchmarksgame/performance/spectralnorm.html)

## The benchmark

Compute the spectral norm of the infinite matrix `A(i,j) = 1 / ((i+j)(i+j+1)/2 + i + 1)`
via 10 iterations of the power method (`u → AᵀA·u`), then print
`sqrt(uᵀ·(AᵀA·u) / uᵀ·u)` to 9 decimal places. The work is `N×N` float multiply-accumulate
inner loops, repeated 20 times (each power step is `A·u` then `Aᵀ·v`).

## Intended Ashes approach

Vectors `u`, `v` as `Ashes.Array` (persistent) or `List(Float)`; the matrix entry is
computed on the fly (no storage). The inner loops are float multiply-accumulate folds; the
final result needs a single `sqrt`.

## What it probes (expected flaws)

- **Float throughput** across the `N×N` multiply-accumulate inner loops; the final norm uses
  `Ashes.Math.sqrt` (hardware `llvm.sqrt`, now shipped), so a correct result is computable.
- **No flat mutable array.** `A·u` is the canonical indexed multiply-accumulate; with only a
  persistent `Ashes.Array` (O(log N) access, per-update allocation) or `List` (O(N) index),
  the `N×N` inner loop is heavily penalised and generates per-iteration garbage (arena leak
  #2). A strong probe of the persistent-array cost model under a numeric hot loop.
- Fixed-precision (9 dp) formatting via `Ashes.Text.formatFloat(value)(9)` (shipped).

## Dependencies / blockers

**No hard blocker.** `Ashes.Math.sqrt` (final norm) and `Ashes.Text.formatFloat` (9-dp
formatting) have shipped, so a correct version is implementable now. **Perf caveat (not a
blocker):** with only a persistent `Ashes.Array` (no flat mutable array), the `N×N` inner
loop pays O(log N) access and per-update allocation — that cost is itself what this
benchmark is meant to probe.

## Status

**Scaffold only.** `spectral-norm.ash` and the `FLAWS.md` writeup are not written yet, but the
benchmark is **unblocked** — the persistent-array perf cost is a probe, not a blocker.

## Build & run (once written)

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/spectral-norm/spectral-norm.ash -o challenges/spectral-norm/spectral-norm
./challenges/spectral-norm/spectral-norm 5500
```
