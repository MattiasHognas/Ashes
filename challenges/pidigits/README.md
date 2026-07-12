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
| 100 | 0.00 s | 0.5 MB |
| 250 | 0.04 s | 3.0 MB |
| 500 | 0.40 s | 13.0 MB |
| 750 | 1.50 s | 32.5 MB |
| 1,000 | 3.64 s | 60.4 MB |

The per-iteration `BigInt` garbage is now **reclaimed**: a `BigInt` is a self-contained buffer, so it
is copied out across the TCO back-edge reset like a `String`, letting the reset fire and free the
spigot's intermediate values. That cut resident set ~2.8× (`N=1000` 168 MB → 60 MB) at the same wall
time. The headline finding is still the **scaling**: doubling `N` from 500 to 1,000 costs ~9× the time
and ~4.5× the memory — roughly `O(N³)` time and `O(N²)` resident set. What remains is the *growing
accumulator* itself: the spigot's `q, r, t` widen with the digit count and each iteration's whole-value
copy is preserved below the advancing watermark, so both time and memory still climb super-linearly.
Removing that residual is the ownership / in-place-reuse memory-model milestone (`FLAWS.md`), so the
Benchmarks Game standard `N=10000` remains impractical and the table stops at `N=1000`.
