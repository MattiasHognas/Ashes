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
columns. `Text.uncons` yields `Rune` values, so the reversed working sequence is a `List(Rune)` and
conversion back to `Str` happens only while emitting. Lines use `IO.writeBufferedLine`, avoiding a
system call for every payload and newline. Output is verified by the transform's involution:
applying it twice reproduces the input byte-for-byte.

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

Measured 2026-08-25 (`main` at `cf16de07`) on a 32-thread AMD Ryzen 9 9950X3D, Linux x64
(single-threaded), `-O2`; hyperfine three-run means (25M: single timed run), GNU `time` peak RSS.
This rerun found the program printing only its first header on every input — a tag-group match
lowering bug (a failed nested sub-pattern skipped the trailing `_` arm), fixed in the same sweep;
`rc(rc(x)) == x` is byte-exact on the 250k fixture again:

| Input (fasta N) | Input size | Time | Peak RSS |
|-----------------|-----------|------|----------|
| 250,000 | ~2.5 MB | 0.053 s | 44 MB |
| 1,000,000 | ~10 MB | 0.21 s | 160 MB |
| **25,000,000** (standard) | ~254 MB | **5.76 s** | **3.73 GB** |

Time and memory are both **linear**. Peak RSS follows the largest sequence that must remain live
until its header or EOF is reached: about 34 bytes per stored base, matching the isolated
`List(Rune)` working set. The Rune migration initially exposed a late TCO ownership bug that copied
an already-runtime-managed output buffer at each back-edge; the compiler now refreshes the final
runtime-managed result facts before placing the reset, and a focused IR regression covers it. The
standard workload completes within the justified live-set envelope.

For the buffered-stdout comparison, three standard-workload runs improved from 7.55 s to 7.09 s
mean (1.06x). At fasta N=125,000, the output trace dropped from 20,840 `write` syscalls to 20, with
byte-identical output. The peak-RSS figure above is from the standard buffered run; timing is the
three-run mean.
