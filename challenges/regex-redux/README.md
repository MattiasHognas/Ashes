# regex-redux — Ashes Benchmarks Game challenge

Part of the `challenges/` flaw-finding suite (same ground rules as
[`../1brc/README.md`](../1brc/README.md): **not** run by CI; format `.ash` manually with
`dotnet run --project src/Ashes.Cli -- fmt <file> -w`).

> Source: [Benchmarks Game — regex-redux](https://benchmarksgame-team.pages.debian.net/benchmarksgame/performance/regexredux.html)

## The benchmark

Read a large DNA sequence (**fasta** output), strip headers/newlines, then:

- count matches of eight given IUPAC variant patterns (e.g. `agggtaaa|tttaccct`,
  `[cgt]gggtaaa|tttaccc[acg]`, …);
- perform a sequence of literal substitutions (`tHa|<2>` etc.) and print the length of the
  original input, the cleaned input, and the final substituted result.

It is a regular-expression throughput benchmark over a multi-megabyte string.

## Intended Ashes approach

Use the shipped `Ashes.Regex` module to match/count and substitute over the cleaned
sequence. Threading the whole input as one large `Str`.

## What it probes (expected flaws)

- **`Ashes.Regex` engine under load** — alternations and character classes matched against a
  multi-MB input, repeated for eight patterns. Probes the self-hosted regex's time and memory
  behaviour at a scale unit tests don't reach; likely surfaces allocation/leak and
  backtracking-cost issues.
- **Whole-input string handling** — holding and repeatedly scanning a large `Str` (cf. 1BRC
  #1 whole-file allocation cap; `readText` was capped at 1 MiB, so input must stream/assemble).
- Global substitution producing a new large string per pass (growing-string cost, #8-adjacent).

## Dependencies / blockers

Needs **fasta** output as input. No math lib needed. Sensitive to the `readText`/input-size
cap noted in 1BRC #1 — large inputs may need the chunked file API rather than `readText`.

## Status

**Implemented + benchmarked.** [`regex-redux.ash`](regex-redux.ash) runs the canonical workload on
`Ashes.Regex` (hermetic PCRE2): strip FASTA headers/newlines, count the nine variant patterns, then
apply the five substitution passes and print the three lengths. Writing it originally surfaced the
chained->4 MiB-allocation OOM (~28 GB) — fixed by variable-sized heap chunks; the whole chain now
runs in bounded memory.

## Build & run

```bash
./challenges/fasta/fasta 1000000 > regexredux-input.txt
dotnet run --project src/Ashes.Cli -- compile challenges/regex-redux/regex-redux.ash -o challenges/regex-redux/regex-redux -O2
./challenges/regex-redux/regex-redux < regexredux-input.txt
```

## Benchmark

```bash
./challenges/fasta/fasta 1000000 > /tmp/fa1m.txt
BENCH_STDIN=/tmp/fa1m.txt challenges/bench.sh regex-redux
```

Measured on a 32-thread AMD Ryzen 9 9950X3D, Linux x64 (single-threaded), `-O2`:

| Input (fasta N) | Input size | Time | Peak RSS |
|-----------------|-----------|------|----------|
| 250,000 | ~2.5 MB | 0.64 s | 54 MB |
| 1,000,000 | ~10 MB | 4.17 s | 240 MB |
| **5,000,000** (standard) | ~51 MB | **63.7 s** | 1.3 GB |

Correct at every scale, but time grows **superlinearly** (5x input -> ~15x time from 1M to 5M):
each variant count is a fresh scan of the whole subject and each substitution pass materializes a
new large string, so the constant re-copying of a tens-of-MB subject dominates once it falls out
of cache. Memory is bounded (the chunk fix) at ~25x the input. Closing the time gap needs
match-iteration over a shared subject view rather than per-pass materialization — stdlib work, not
a compiler flaw.
