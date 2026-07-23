# reverse-complement — Ashes Benchmarks Game challenge

Part of the `challenges/` flaw-finding suite (same ground rules as
[`../1brc/README.md`](../1brc/README.md): **not** run by CI; format `.ash` manually with
`dotnet run --project src/Ashes.Cli -- fmt <file> -w`).

> Source: [Benchmarks Game — reverse-complement](https://benchmarksgame-team.pages.debian.net/benchmarksgame/performance/revcomp.html)

## The benchmark

Read FASTA-format DNA (**fasta** output) line by line. For each sequence, reverse it and
map each base to its Watson–Crick complement (`A<->T`, `G<->C`, plus the IUPAC ambiguity
codes), then write the complemented sequence back out wrapped at 60 columns, preserving the
`>` header lines.

## Intended Ashes approach

Per sequence: build the complemented characters while consuming with `uncons`, then reverse
(or prepend as you go, which complements *and* reverses in one pass). Complement lookup via a
`match` over the base character. Re-wrap output at 60 columns.

## What it probes (expected flaws)

- **Streaming IO + string reversal on large input.** Combines the input-streaming path (1BRC
  #1 chunked reads) with per-character transformation and reversal — does reversing a
  multi-MB sequence stay linear, or does list/string reversal/`append` go quadratic?
- Per-character `match`-based complement mapping in a hot `uncons` loop (the TCO-friendly
  regime, good to confirm at scale).
- Output re-wrapping and bulk stdout throughput (write-side buffering, cf. **fasta**).

## Dependencies / blockers

Needs **fasta** output as input. No math lib needed — character mapping + IO only. One of
the more tractable benchmarks to write today.

## Status

**Implemented + benchmarked.** [`reverse-complement.ash`](reverse-complement.ash) reads the FASTA
stream, reverse-complements each sequence with the IUPAC complement table, and re-wraps at 60
columns. Output verified against the reference transform.

## Build & run

```bash
./challenges/fasta/fasta 1000000 > revcomp-input.txt
dotnet run --project src/Ashes.Cli -- compile challenges/reverse-complement/reverse-complement.ash -o challenges/reverse-complement/reverse-complement -O2
./challenges/reverse-complement/reverse-complement < revcomp-input.txt > revcomp-output.txt
```

## Benchmark

```bash
./challenges/fasta/fasta 1000000 > /tmp/fa1m.txt
BENCH_STDIN=/tmp/fa1m.txt challenges/bench.sh reverse-complement
```

Measured on a 32-thread AMD Ryzen 9 9950X3D, Linux x64 (single-threaded), `-O2`:

| Input (fasta N) | Input size | Time | Peak RSS |
|-----------------|-----------|------|----------|
| 250,000 | ~2.5 MB | 0.14 s | 236 MB |
| 1,000,000 | ~10 MB | 0.58 s | 944 MB |

Time and memory are both **linear**, but the memory *constant* is the story: ~96 bytes of resident
set per input base, because the working form is a cons list of single-character `Str` values (a
length-prefixed heap string plus a cons cell per base). That constant is tracked as a forward-looking gap in the
[changelog's Deferred section](../../docs/md/internals/changelog.md#deferred)
— shrinking it needs in-place cons-cell reuse (the ownership milestone), which also gates running
the standard 25M-base workload (extrapolates to ~24 GB).
