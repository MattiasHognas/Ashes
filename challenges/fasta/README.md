# fasta — Ashes Benchmarks Game challenge

Part of the `challenges/` flaw-finding suite (same ground rules as
[`../1brc/README.md`](../1brc/README.md): **not** run by CI; format `.ash` manually with
`dotnet run --project src/Ashes.Cli -- fmt <file> -w`).

> Source: [Benchmarks Game — fasta](https://benchmarksgame-team.pages.debian.net/benchmarksgame/performance/fasta.html)

## The benchmark

Generate three DNA sequences and write them in FASTA format:

1. a repeated literal sequence (`ALU`), tiled to `2N` bases;
2. two random sequences of `3N` and `5N` bases, each base drawn from a weighted alphabet
   using a **specified deterministic LCG** (`seed = (seed * 3877 + 29573) mod 139968`) so
   output is bit-for-bit reproducible.

Lines are wrapped at 60 columns. The benchmark is output-bound: large volumes of generated
text written to stdout.

## Intended Ashes approach

A pure tail-recursive generator threading the LCG seed as an accumulator; weighted base
selection via a cumulative-probability table; 60-column line wrapping via a counter. Output
through `Ashes.IO.write`.

## What it probes (expected flaws)

- **Bulk stdout throughput / output buffering.** Like 1BRC's input side, but on the write
  path — does writing tens of MB of generated text go through a buffer, or per-call/per-byte
  syscalls?
- Growing-string concatenation cost when assembling output (cf. 1BRC #8 `join`).
- Float arithmetic for the cumulative-probability selection (`max * seed / 139968`), and
  whether `fromFloat` formatting / float compares behave under volume.

## Dependencies / blockers

None transcendental — integer LCG + a little float division for the cumulative table. No
math lib needed. (`fasta`'s output is the input for **reverse-complement** and
**k-nucleotide**, so it is the natural data generator for those two.)

## Status

**Implemented + benchmarked.** [`fasta.ash`](fasta.ash) generates all three sequences (repeated
ALU, weighted-random IUB and homo sapiens) with the specified LCG, 60-column wrapped, in the
natural accumulator style: each sequence's output is built by a tail-recursive fold appending to a
growing `Str` accumulator. That shape used to be O(N^2) time (every `out + ch` copied the whole
accumulator); reservation-based affine string growth (changelog CO-36) made it amortized O(1) per
appended byte, which is what unlocked benchmark-scale N here.

## Build & run

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/fasta/fasta.ash -o challenges/fasta/fasta -O2
./challenges/fasta/fasta 25000000 > fasta-output.txt
```

## Benchmark

```bash
challenges/bench.sh fasta 25000000
```

Measured on a 32-thread AMD Ryzen 9 9950X3D, Linux x64 (single-threaded), `-O2`, output to
`/dev/null`:

| N | Output | Time | Peak RSS |
|---|--------|------|----------|
| 1,000,000 | ~10 MB | 0.22 s | 34 MB |
| 5,000,000 | ~51 MB | 3.29 s | 168 MB |
| **25,000,000** (standard) | ~254 MB | **17.4 s** | 786 MB |

Before the affine-growth arc this was 67 s at N=80,000 (and quadratically worse beyond); N=25M was
out of reach. Resident memory tracks the largest single sequence's accumulator (the output is held
as one growing string per sequence). Time is not perfectly linear above ~1M — the accumulator
outgrows the CPU caches and every reservation doubling recopies a now-hundreds-of-MB string — but
the curve is gentle enough that the standard workload completes comfortably.
