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
| 250 | 0.03 s | 0.25 MB |
| 500 | 0.36 s | 0.25 MB |
| 1,000 | 3.50 s | 0.25 MB |
| 2,000 | 31.7 s | 0.25 MB |

Resident memory is now **constant** (0.25 MB) at every `N` — down from `O(N²)` (`N=1000` was 168 MB).
Two changes did it: (1) a `BigInt` is a self-contained buffer, so it is copied out across the TCO
back-edge reset like a `String`, letting the reset fire and free the spigot's intermediate values; and
(2) a loop threading only non-sharing whole-value accumulators (here `q, r, t` `BigInt`s plus the
`String` output — no cons-lists) resets to a **fixed** loop-entry watermark, so each iteration's
grown accumulator overwrites the previous one instead of being stranded below an advancing watermark.
The growing accumulator now stays `O(current width)`, not `O(sum of all widths)`.

What remains is **time**: it is still ~`O(N³)` (doubling `N` is ~9×), driven by the binary long-
division in the digit-extraction step. That is a bignum-algorithm follow-up (Knuth Algorithm D /
Karatsuba), not a memory-model issue — the standard `N=10000` is now memory-feasible but still
time-bound, so the table stops where the wall time does.
