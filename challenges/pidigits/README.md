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
  every step is `Ashes.Number.BigInt` arithmetic (`mul`, `add`, `sub`, `div`, `compare`). This exercises
  the native bignum runtime and its immutable, arena-allocated values under a tight loop.
- **Arena churn.** Each bignum operation allocates a fresh value, so a long digit run stresses
  the arena exactly as the memory model predicts — the cost this benchmark is meant to surface.

## Dependencies / blockers

**None.** Native `Ashes.Number.BigInt` (arbitrary-precision integers) has shipped — see the
[architecture notes](../../docs/md/internals/architecture.md#bigint-arbitrary-precision-integers).
`pidigits.ash` implements the unbounded spigot with it (using `BigInt` operators and `N` literals);
the digits match π (`3141592653 5897932384 …`).

## Status

**Implemented.** `pidigits.ash` streams the first `N` digits of π in the Benchmarks Game output
format (ten digits per line, each tagged with the running count; defaults to 27, or pass `N` as an
argument). Correctness of the bignum runtime is covered by `tests/bigint_pidigits.ash`,
`tests/bigint_core.ash`, `tests/bigint_edge.ash`, `tests/bigint_conversions.ash`, and
`tests/bigint_literals.ash`.

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
use `Ashes.Task.Parallel`), `-O2`:

| N (digits) | Time | Peak RSS |
|------------|------|----------|
| 1,000 | 0.029 s | 0.2 MB |
| 2,000 | 0.123 s | 0.2 MB |
| 5,000 | 0.82 s | 0.5 MB |
| **10,000** (standard) | **3.50 s** | 1.2 MB |

Both original flaws are fixed. Resident memory is **constant-ish** (the working `BigInt`s
themselves) at every `N` — down from `O(N^2)` (`N=1000` was 168 MB) after the fixed-watermark
copy-out arc. Time dropped ~128x when `bignum_divmod` was rewritten from bit-by-bit binary long
division to **Knuth Algorithm D in base 2^32** (changelog CO-33): `N=1000` went 3.46 s -> 0.029 s,
and the standard `N=10000` — formerly ~an hour of extrapolated runtime — now runs in 3.5 s.
Remaining time growth is the schoolbook `mul` (Karatsuba unimplemented; nothing currently hits it
hard enough to matter).
