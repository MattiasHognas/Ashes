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

- **No arbitrary-precision integers (bignum).** Ashes `Int` is a fixed 64-bit machine
  integer; there is no `BigInt`. The spigot's accumulators overflow after a few dozen digits,
  so the canonical algorithm cannot be expressed correctly at all. This is a *fundamental*
  capability gap, not a performance flaw — distinct from the allocator/array/math gaps the
  other benchmarks probe.
- If a bignum were hand-built in Ashes (lists/arrays of limbs), it would then stress the
  persistent-array cost model and the arena leak — but that measures the workaround, not the
  language.

## Dependencies / blockers

**BLOCKED on arbitrary-precision integers (bignum).** Not addressed by the math lib (which is
about `Float` transcendentals); this needs a `BigInt` type / library. Defer until that exists,
or implement only as an explicit "bignum-in-Ashes" stress test with that caveat documented.

## Status

**Scaffold only.** `pidigits.ash` and the `FLAWS.md` writeup are deferred — blocked on bignum.

## Build & run (once written, after bignum)

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/pidigits/pidigits.ash -o challenges/pidigits/pidigits
./challenges/pidigits/pidigits 10000
```
