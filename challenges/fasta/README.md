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

**Scaffold only.** `fasta.ash` and the `FLAWS.md` writeup are deferred.

## Build & run (once written)

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/fasta/fasta.ash -o challenges/fasta/fasta
./challenges/fasta/fasta 25000000 > fasta-output.txt
```
