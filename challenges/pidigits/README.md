# pidigits — Ashes Benchmarks Game challenge

Part of the `challenges/` flaw-finding suite (same ground rules as
[`../1brc/README.md`](../1brc/README.md): **not** run by CI; format `.ash` manually with
`dotnet run --project src/Ashes.Cli -- fmt <file> -w`).

> Source: [Benchmarks Game — pidigits](https://benchmarksgame-team.pages.debian.net/benchmarksgame/performance/pidigits.html)

## The benchmark

Stream the first `N` digits of π using an unbounded spigot algorithm (the standard
Gibbons / Lambert-series streaming approach), printing digits in groups of 10 with a running
count. The defining characteristic: it relies on **arbitrary-precision integer arithmetic** —
the working integers grow without bound as more digits are produced.

## Intended Ashes approach

The algorithm is naturally tail-recursive (a state of a few big integers threaded through a
loop), which suits Ashes — *except* for the arithmetic width.

## What it probes (expected flaws)

- **Arbitrary-precision integer throughput.** The spigot's accumulators grow without bound, so
  every step is `Ashes.BigInt` arithmetic (`mul`, `add`, `sub`, `div`, `compare`). This exercises
  the native bignum runtime and its immutable, arena-allocated values under a tight loop.
- **Arena churn.** Each bignum operation allocates a fresh value, so a long digit run stresses
  the arena exactly as the memory model predicts — the cost this benchmark is meant to surface.

## Dependencies / blockers

**None.** Native `Ashes.BigInt` (arbitrary-precision integers) has shipped — see the
[architecture notes](../../docs/md/internals/architecture.md#bigint-arbitrary-precision-integers).
`pidigits.ash` implements the unbounded spigot with it (using `BigInt` operators and `N` literals);
the digits match π (`3141592653 5897932384 …`).

## Status

**Implemented.** `pidigits.ash` streams the first `N` digits of π in the Benchmarks Game output
format (ten digits per line, each tagged with the running count; defaults to 27, or pass `N` as an
argument). Correctness of the bignum runtime is covered by `tests/bigint_pidigits.ash`,
`tests/bigint_core.ash`, `tests/bigint_edge.ash`, `tests/bigint_conversions.ash`, and
`tests/bigint_literals.ash`; see [FLAWS.md](FLAWS.md) for the writeup.

## Build & run

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/pidigits/pidigits.ash -o challenges/pidigits/pidigits
./challenges/pidigits/pidigits 1000
```

## Benchmark

Reproduce with the shared harness (compiles at `-O2`, times with `hyperfine`, reports peak RSS):

```bash
challenges/bench.sh pidigits 1000
```

Measured on a 32-thread AMD Ryzen 9 9950X3D, Linux x64 (single-threaded — this benchmark does not
use `Ashes.Parallel`):

| N (digits) | Time | Peak RSS |
|------------|------|----------|
| 100 | 0.00 s | 1.2 MB |
| 250 | 0.04 s | 9.0 MB |
| 500 | 0.41 s | 37.8 MB |
| 750 | 1.53 s | 91.2 MB |
| 1,000 | 3.69 s | 168.0 MB |

The headline finding is the **scaling**, not the absolute time: doubling `N` from 500 to 1,000 costs
~9× the wall time and ~4.5× the memory — roughly `O(N³)` time and `O(N²)` resident set. Each spigot
step allocates fresh `Ashes.BigInt` values whose width grows with the digit count, and the bump
arena does not reclaim them within the digit loop, so both time and memory climb super-linearly.
This is exactly the arena-churn cost the challenge was written to probe (`FLAWS.md`, and the memory
model's non-GC reclamation path); it makes the Benchmarks Game standard `N=10000` impractical here
(extrapolates to hours and tens of GB), so the table stops at `N=1000`.
