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
  (`uncons` views are fixed; byte-indexed windows go through `Ashes.Bytes.subText`).
- Sorting map entries by frequency then key (needs a total order and a stable sort).
- Float formatting for the printed percentages (`Ashes.Text.formatFloat(value)(3)`).

## Dependencies / blockers

Needs **fasta** output as input. No transcendental math (percentages are plain float
division). The interesting blocker is the missing mutable/O(1) hashtable, not math.

## Status

**Implemented + benchmarked.** [`k-nucleotide.ash`](k-nucleotide.ash) extracts the `>THREE`
sequence via `Bytes`, counts k-mers (k = 1, 2, then the six named oligonucleotides) with the
persistent `Ashes.Map`, and prints frequency-sorted percentages to 3 dp. Writing it originally
surfaced the superlinear character-indexed `String.substring` (fixed: single offset walk + one
`Bytes.subText`, ~2000x on the sliding window) — the sliding k-mer windows now run on `Bytes`.

## Build & run

```bash
./challenges/fasta/fasta 250000 > knucleotide-input.txt
dotnet run --project src/Ashes.Cli -- compile challenges/k-nucleotide/k-nucleotide.ash -o challenges/k-nucleotide/k-nucleotide -O2
./challenges/k-nucleotide/k-nucleotide < knucleotide-input.txt
```

## Benchmark

```bash
./challenges/fasta/fasta 250000 > /tmp/fa250k.txt
BENCH_STDIN=/tmp/fa250k.txt challenges/bench.sh k-nucleotide
```

Measured on a 32-thread AMD Ryzen 9 9950X3D, Linux x64 (single-threaded), `-O2`:

| Input (fasta N) | >THREE bases | Time | Peak RSS |
|-----------------|--------------|------|----------|
| 250,000 | 1.25M | 2.83 s | 44 MB |
| 1,000,000 | 5M | 11.3 s | 123 MB |

Time and memory scale linearly with the sequence. The cost profile is the expected one: every
k-mer count is an immutable `Map` update (O(log n) + path allocation), where the reference uses a
mutable O(1) hashtable — that constant-factor gap, not any remaining compiler flaw, is what
separates this from the leaderboard. The standard 25M-base input is reachable but was skipped to
keep the sweep short (~5 min extrapolated).
