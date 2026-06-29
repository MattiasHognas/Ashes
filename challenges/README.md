# challenges/

Stress-test programs that probe the edges of Ashes. **Not** part of the test or
example suites — nothing here is discovered or run by CI (`ci/jobs.sh`,
`scripts/verify.sh`), and the `.ash` files here are not format-checked by any gate.
Format them manually with `dotnet run --project src/Ashes.Cli -- fmt <file> -w`.

## 1 Billion Row Challenge (`brc.ash`)

A faithful attempt at the [1BRC](https://github.com/gunnarmorling/1brc), written to
**find the language's flaws**. It is correct for ASCII station names at a modest row
count; it is _not_ viable for the real 1e9-row input. The interesting output of this
exercise is [`FLAWS.md`](FLAWS.md) — read that.

### Prerequisites

Backend compilation needs the LLVM native runtimes (one-time, per the repo README):

```bash
bash scripts/download-llvm-native.sh --linux-x64
```

### Get the data

The real 1BRC `measurements.txt` (~13 GB, 1e9 rows) is generated from a station list
by the upstream project; there is no canonical download. `download.sh` fetches a
prebuilt file from a URL and can subset it so the program actually finishes.

```bash
# Edit MEASUREMENTS_URL in download.sh (or pass the URL as the first argument),
# then fetch a subset that completes:
ROWS=1000000 bash challenges/download.sh

# Full file (will OOM — that's finding #2):
bash challenges/download.sh
```

`measurements.txt` and `measurements.full.txt` are git-ignored and never committed.

### Build and run

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/brc.ash -o challenges/brc
./challenges/brc challenges/measurements.txt
```

We should probably run it under hyperfine to get actaul data as sonn as we see it actually working, see: https://hotforknowledge.com/2024/01/13/1brc-in-dotnet-among-fastest-on-linux-my-optimization-journey/'

With something like this:

```bash
hyperfine --warmup 1 --runs 5 './challenge/brc challenge/measurements.txt'
```

The file path is the program's first argument. (It does **not** stream stdin: a
`readLine` loop crashes at a few hundred lines — see [`FLAWS.md`](FLAWS.md) #1b — so
the whole file is read with `Ashes.File.readText`.) Output is the canonical
`{Station=min/mean/max, ...}` form, sorted by station name.

### Quick correctness check (no download)

```bash
printf 'Hamburg;12.0\nHamburg;14.0\nBulawayo;8.9\nHamburg;10.0\nPalembang;-5.3\n' \
  > /tmp/brc-fixture.txt
./challenges/brc /tmp/brc-fixture.txt
# {Bulawayo=8.9/8.9/8.9, Hamburg=10.0/12.0/14.0, Palembang=-5.3/-5.3/-5.3}
```

### Reproduce the failure

Increase `ROWS` until the run dies with
`panic("failed to allocate heap memory from OS")` — the arena leak in
[`FLAWS.md`](FLAWS.md) #2. Record the row count where it happens; it depends on
available RAM, not on a billion rows.
