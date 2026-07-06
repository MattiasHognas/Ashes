# 1 Billion Row Challenge

A faithful [1BRC](https://github.com/gunnarmorling/1brc) implementation in Ashes, run as a
stress test of the language on the full 1e9-row workload. **Not** part of the test or example
suites — nothing here is discovered or run by CI (`ci/jobs.sh`, `scripts/verify.sh`), and the
`.ash` file is not format-checked by any gate. Format it manually with
`dotnet run --project src/Ashes.Cli -- fmt challenges/1brc/brc.ash -w`.

## The solution

[`brc.ash`](brc.ash) processes the full billion rows in **~8.3 s** on a 32-thread desktop, with
output byte-identical to a straightforward sequential fold. It:

- `mmap`s the whole file (`Ashes.File.mmap`, zero-copy) and splits it into per-core chunks at
  newline boundaries,
- folds each chunk on a worker thread (`Ashes.Parallel`) into a purpose-built **16-ary hash trie**
  whose leaf holds the min/max/sum/count aggregate **inline** (four Ints, no value-tuple pointer,
  no `onHit` closure on the hot path) — the "custom table with inline aggregates" that fast 1BRC
  entries use, kept at constant memory per worker by the compiler's reuse specialization,
- merges the partial tries and emits the canonical `{Station=min/mean/max, ...}` form sorted by
  station name (correct for UTF-8 names, which sort by byte order).

Purity makes the fold order-independent, so the output is deterministic regardless of chunk and
worker scheduling. The hot path **trusts the 64-bit FNV-1a hash** (an equal hash is an equal key,
no leaf byte-compare on hit); the file header documents this and how to restore the byte-compare
for adversarial-input safety at roughly +2 s.

Because it maps the file and keeps per-worker tries resident, the run needs roughly 1.5× the file
size in RAM (~22 GB for the full 15.5 GB challenge file); it is not constant-memory.

## Prerequisites

Backend compilation needs the LLVM native runtimes (one-time, per the repo README):

```bash
bash scripts/download-llvm-native.sh --linux-x64
```

## Get the data

The real 1BRC `measurements.txt` (~15.5 GB, 1e9 rows) is generated from a station list by the
upstream project; there is no canonical download. `download.sh` fetches a prebuilt file from a URL
and can subset it. `measurements.txt` (and any subsets) are git-ignored and never committed.

```bash
# Edit MEASUREMENTS_URL in download.sh (or pass the URL as the first argument),
# then fetch a subset (or the full file):
ROWS=10000000 bash challenges/1brc/download.sh
bash challenges/1brc/download.sh                 # full 1e9-row file
```

## Build and run

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/1brc/brc.ash -o /tmp/brc -O2
/tmp/brc challenges/1brc/measurements.txt        # the file path is the first argument
```

## Benchmarks

Measured with `hyperfine` (warm page cache) on a 32-thread AMD Ryzen 9 9950X3D, Linux x64; the
worker cap defaults to the detected thread count (override with `--parallel-workers`).

| Rows | Time | Peak RSS |
|------|------|----------|
| 10,000,000 | 0.56 s | 6.6 GB |
| 100,000,000 | 1.26 s | 8.6 GB |
| **1,000,000,000** (full challenge) | **8.31 s** (8.19–8.48) | 21.7 GB |

At the full billion rows: 41,343 stations, ≈120 M rows/s.

```bash
hyperfine --warmup 1 --runs 5 '/tmp/brc challenges/1brc/measurements.txt'
```

## Quick correctness check (no download)

```bash
printf 'Hamburg;12.0\nHamburg;14.0\nBulawayo;8.9\nHamburg;10.0\nPalembang;-5.3\n' \
  > /tmp/brc-fixture.txt
/tmp/brc /tmp/brc-fixture.txt
# {Bulawayo=8.9/8.9/8.9, Hamburg=10.0/12.0/14.0, Palembang=-5.3/-5.3/-5.3}
```
