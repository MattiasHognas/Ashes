# challenges/

Stress-test programs that probe the edges of Ashes. **Not** part of the test or
example suites — nothing here is discovered or run by CI (`ci/jobs.sh`,
`scripts/verify.sh`), and the `.ash` files here are not format-checked by any gate.
Format them manually with `dotnet run --project src/Ashes.Cli -- fmt <file> -w`.

## 1 Billion Row Challenge

A faithful [1BRC](https://github.com/gunnarmorling/1brc) implementation, originally written to
**find the language's flaws**. Every flaw it surfaced has since been fixed in the compiler
(see [`FLAWS.md`](FLAWS.md)), and it now **runs the full 1e9-row challenge**. Three variants:

- **`brc.ash`** — sequential, streaming (`Ashes.File.readLine`), **constant-memory** (~50 MB at any
  size). Correct and unbounded, single-core.
- **`brc_parallel.ash`** — data-parallel: `mmap`s the whole file (`Ashes.File.mmap`, zero-copy), splits
  it into per-core chunks at newline boundaries, folds each on a worker thread
  (`Ashes.Parallel.reduce` / `both`), and merges the partial maps. Uses all cores (the worker cap
  defaults to the detected core count); output is byte-identical to the sequential version (purity makes the fold order-independent).

- **`brc_trie.ash`** — like the parallel variant but folds into `Ashes.HashTrie` (16-ary hash trie,
  ~4-5 dependent node loads per row instead of the AVL's ~17) and re-sorts by name at the end.
  The fastest variant.

Output is the canonical `{Station=min/mean/max, ...}` form, sorted by station name; correct for UTF-8
station names (multibyte names sort by byte order).

### Prerequisites

Backend compilation needs the LLVM native runtimes (one-time, per the repo README):

```bash
bash scripts/download-llvm-native.sh --linux-x64
```

### Get the data

The real 1BRC `measurements.txt` (~15.5 GB, 1e9 rows) is generated from a station list by the upstream
project; there is no canonical download. `download.sh` fetches a prebuilt file from a URL and can subset
it. Any size runs — the sequential variant is constant-memory and the parallel variant scales to the
full file.

```bash
# Edit MEASUREMENTS_URL in download.sh (or pass the URL as the first argument),
# then fetch a subset (or the full file):
ROWS=10000000 bash challenges/1brc/download.sh
bash challenges/1brc/download.sh                 # full 1e9-row file
```

`measurements.txt` and `measurements.full.txt` are git-ignored and never committed.

### Build and run

```bash
# sequential (constant-memory) or parallel (multicore) — same output
dotnet run --project src/Ashes.Cli -- compile challenges/1brc/brc.ash          -o /tmp/brc      -O2
dotnet run --project src/Ashes.Cli -- compile challenges/1brc/brc_parallel.ash -o /tmp/brc_par  -O2
/tmp/brc     challenges/1brc/measurements.txt     # sequential
/tmp/brc_par challenges/1brc/measurements.txt     # parallel
```

The file path is the program's first argument.

### Benchmarks

Measured with `hyperfine` (warm page cache) on a 32-core Linux x64 box; the parallel worker cap
defaults to the core count (override with `--parallel-workers`). Both variants produce **byte-identical** output.

| Rows | Sequential `brc.ash` | Parallel `brc_parallel.ash` | Trie `brc_trie.ash` |
|------|----------------------|-----------------------------|---------------------|
| 10,000,000 | 6.5 s / 52 MB | 0.96 s / 5.1 GB | **0.5 s** (1.6 s single-core) |
| 100,000,000 | ~65 s / 52 MB | 3.1 s / 5.6 GB | **1.5 s** |
| **1,000,000,000** (full challenge) | ~11 min / 52 MB | 24.7 s / 18.7 GB | **12.2 s** / 16.6 GB — 41,343 stations, ≈82 M rows/s |

(Previous compiler generation, for reference: sequential 12.8 s @10M; parallel 2 m 36 s @1e9 at an
8-worker cap. The 2026-07-03 optimization arc — memcmp `Bytes.compare`, closure devirtualization,
arena-bracket elision, `Map.getStr/setStr`/user `upsertMeasurement` reuse, `Bytes.subView`, and a
core-count worker cap — is recorded in `docs/future/COMPILER_OPTIMIZATION.md`.)

The tradeoff: the sequential fold is constant-memory (streams the file, reclaims each iteration in
place) and scales to any size on tiny RAM but is single-core; the parallel fold is ~5–8× faster (near
the 8-worker cap) but holds the mapped file plus per-worker maps in RAM. Both stay near-constant-memory
*per worker* thanks to in-place reuse — before that fix the parallel variant OOM'd past ~15M rows.

```bash
hyperfine --warmup 1 --runs 5 '/tmp/brc_par challenges/1brc/measurements.txt'
```

Scaling note (measured on the reference box, a dual-CCD Ryzen 9 9950X3D): the trie fold saturates
all 32 hardware threads for the first ~70% of the run; the tail is set by the smaller-L3 CCD, whose
workers need ~1.4x the CPU per equal chunk — 16 workers x ~5 MB trie working set fits inside the
96 MB V-cache CCD but thrashes the 32 MB one. Chunk counts above the worker cap measure worse than
32 (a fork-join tree cannot shed a blocked parent's pending work), which puts this runtime's packing
ceiling on this box at ~10.2-10.5 s; the sub-10-s follow-up is the work-conserving chunk queue
(CO-25 in `docs/future/COMPILER_OPTIMIZATION.md`).

### Quick correctness check (no download)

```bash
printf 'Hamburg;12.0\nHamburg;14.0\nBulawayo;8.9\nHamburg;10.0\nPalembang;-5.3\n' \
  > /tmp/brc-fixture.txt
/tmp/brc /tmp/brc-fixture.txt
# {Bulawayo=8.9/8.9/8.9, Hamburg=10.0/12.0/14.0, Palembang=-5.3/-5.3/-5.3}
```
