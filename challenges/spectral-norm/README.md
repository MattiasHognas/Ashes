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

Vectors `u`, `v` as `Ashes.Collection.Array` (persistent) or `List(Float)`; the matrix entry is
computed on the fly (no storage). The inner loops are float multiply-accumulate folds; the
final result needs a single `sqrt`.

## What it probes (expected flaws)

- **Float throughput** across the `N×N` multiply-accumulate inner loops; the final norm uses
  `Ashes.Number.Math.sqrt` (hardware `llvm.sqrt`, now shipped), so a correct result is computable.
- **No flat mutable array.** `A·u` is the canonical indexed multiply-accumulate; with only a
  persistent `Ashes.Collection.Array` (O(log N) access, per-update allocation) or `List` (O(N) index),
  the `N×N` inner loop is heavily penalised and generates per-iteration garbage (arena leak
  #2). A strong probe of the persistent-array cost model under a numeric hot loop.
- Fixed-precision (9 dp) formatting via `Ashes.Text.formatFloat(value)(9)` (shipped).

## Dependencies / blockers

**No hard blocker.** `Ashes.Number.Math.sqrt` (final norm) and `Ashes.Text.formatFloat` (9-dp
formatting) have shipped, so a correct version is implementable now. **Perf caveat (not a
blocker):** with only a persistent `Ashes.Collection.Array` (no flat mutable array), the `N×N` inner
loop pays O(log N) access and per-update allocation — that cost is itself what this
benchmark is meant to probe.

## Status

**Implemented + benchmarked.** [`spectral-norm.ash`](spectral-norm.ash) runs the standard power
iteration (10 rounds of `A^T A`) with the implicit matrix `a(i,j) = 1/((i+j)(i+j+1)/2 + i + 1)`,
printing the norm to 9 dp. Output matches the reference (`1.274224153` at the standard `N=5500`).

## Build & run

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/spectral-norm/spectral-norm.ash -o challenges/spectral-norm/spectral-norm -O2
./challenges/spectral-norm/spectral-norm 5500
```

## Benchmark

```bash
challenges/bench.sh spectral-norm 5500
```

Measured on a 32-thread AMD Ryzen 9 9950X3D, Linux x64 (single-threaded), `-O2`:

| N | Time | Peak RSS |
|---|------|----------|
| 1,000 | 0.16 s | 0.2 MB |
| 3,000 | 1.48 s | 1.0 MB |
| **5,500** (standard) | **4.72 s** | **1.5 MB** |

Time scales as the O(N^2) matrix-vector products predict (5.5^2 ~ 30x from 1000 to 5500) — the
persistent-vector access cost the benchmark was expected to expose is visible but constant-factor,
not asymptotic, and memory stays at the size of the working vectors.
