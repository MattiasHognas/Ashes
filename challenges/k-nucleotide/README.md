# k-nucleotide — Ashes Benchmarks Game challenge

Part of the `challenges/` flaw-finding suite (same ground rules as
[`../1brc/README.md`](../1brc/README.md): **not** run by CI; format `.ash` manually with
`dotnet run --project src/Ashes.Cli -- fmt <file> -w`).

> Source: [Benchmarks Game — k-nucleotide](https://benchmarksgame-team.pages.debian.net/benchmarksgame/performance/knucleotide.html)

## The benchmark

Read a DNA sequence (the third sequence from **fasta**'s output, `>THREE`), then:

- count all 1- and 2-nucleotide frequencies and print them sorted by descending frequency;
- count and print the occurrence counts of several specific k-mers (`GGT`, `GGTA`,
  `GGTATT`, `GGTATTTTAATT`, `GGTATTTTAATTTATAGT`).

It is a hash-map throughput benchmark over a very large number of short keys.

## Intended Ashes approach

Slide a k-wide window across the sequence, keying a map by the window substring. Ashes has
`Ashes.Map` (persistent AVL, needs a `compare`) and `Ashes.HashMap` (persistent, hashed by
`Str`); both are persistent, so each update allocates.

## What it probes (expected flaws)

- **Persistent-map allocation/leak under a hot read-modify-write loop — the same core as
  1BRC #2/#3, on a different shape.** Millions of `get`-then-`set` updates with no mutable
  hashtable and no arena reclamation: expect linear memory growth toward OOM on large `N`.
- Substring/window extraction cost — whether `substring`/`take` are view-based or copy
  (1BRC #7 fixed `uncons` views; `take` is still O(count²) per FLAWS #2 task 3).
- Sorting map entries by frequency then key (needs a total order and a stable sort).
- Float formatting for the printed percentages.

## Dependencies / blockers

Needs **fasta** output as input. No transcendental math (percentages are plain float
division). The interesting blocker is the missing mutable/O(1) hashtable, not math.

## Status

**Scaffold only.** `k-nucleotide.ash` and the `FLAWS.md` writeup are deferred.

## Build & run (once written)

```bash
# input is fasta's >THREE sequence
./challenges/fasta/fasta 1000000 > knucleotide-input.txt
dotnet run --project src/Ashes.Cli -- compile challenges/k-nucleotide/k-nucleotide.ash -o challenges/k-nucleotide/k-nucleotide
./challenges/k-nucleotide/k-nucleotide < knucleotide-input.txt
```
