# mandelbrot — Ashes Benchmarks Game challenge

Part of the `challenges/` flaw-finding suite (same ground rules as
[`../1brc/README.md`](../1brc/README.md): **not** run by CI; format `.ash` manually with
`dotnet run --project src/Ashes.Cli -- fmt <file> -w`).

> Source: [Benchmarks Game — mandelbrot](https://benchmarksgame-team.pages.debian.net/benchmarksgame/performance/mandelbrot.html)

## The benchmark

Compute the Mandelbrot set for an `N×N` region of the complex plane and write the result as
a binary PBM (`P4`) image to stdout: for each pixel, iterate `z = z² + c` up to 50 times and
emit one bit (in-set / escaped), packing 8 pixels per output byte.

## Intended Ashes approach

Pure `Float` arithmetic in the inner escape loop (no transcendental functions — only `+`,
`-`, `*` and a `>` magnitude test against `4.0`), tail-recursive over pixels/rows. Bit
packing into bytes, written via `Ashes.IO.write` / a `Bytes` builder.

## What it probes (expected flaws)

- **Float codegen in a tight numeric loop** — the cleanest possible probe of float `+/-/*`
  and comparison throughput with no IO confounding it, and good cross-arch coverage
  (linux-arm64, win-x64 float lowering).
- **Raw byte / binary output.** The result is a packed bitmap, not text — exercises building
  and writing `Bytes` (bit-packing 8 pixels/byte), a path the text-oriented 1BRC never hit.
- Whether the per-pixel escape loop is TCO'd to a flat loop or grows the stack/arena.

## Dependencies / blockers

None — the escape test is plain float arithmetic (`x*x + y*y > 4.0`); **no `sqrt` or
transcendental math is required**. So mandelbrot is writable today, unlike n-body /
spectral-norm. Wants a byte-output builder (verify `Bytes` write path is adequate).

## Status

**Implemented + benchmarked** (with caveats). [`mandelbrot.ash`](mandelbrot.ash) runs the pure-Float
`N×N × up-to-50` escape loop. Two caveats, both in [FLAWS.md](FLAWS.md):

- It reports the **count of in-set pixels** as a checksum rather than writing the binary `P4` PBM —
  `Ashes.IO.write` takes a UTF-8 `Str` and there is no raw-bytes stdout write. The compute (the
  benchmark's work) is identical; the count is deterministic (e.g. `397380 of 1000000` at `N=1000`).
- Two `Float` type-inference bugs had to be worked around: `Float * Float` of annotated parameters
  mis-resolves to `Int` (led with `1.0 *`), and a recursive accumulator must lead with a known-`Float`
  sub-expression. These also block the natural spelling of `n-body` / `spectral-norm`.

## Build & run

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/mandelbrot/mandelbrot.ash -o challenges/mandelbrot/mandelbrot
./challenges/mandelbrot/mandelbrot 1000
```

## Benchmark

```bash
challenges/bench.sh mandelbrot 4000
```

Measured on a 32-thread AMD Ryzen 9 9950X3D, Linux x64 (single-threaded):

| N | Time | Peak RSS |
|---|------|----------|
| 1,000 | 0.04 s | 0.25 MB |
| 2,000 | 0.18 s | 0.25 MB |
| 4,000 | 0.69 s | 0.25 MB |

Clean ~`O(N²)` scaling and **constant 0.25 MB** resident set: the accumulators are scalar `Int`/`Float`
threaded through the pixel loops, so — unlike the pointer-bearing challenges — there is no arena churn
and no growth. A pure-numeric hot loop is the compiler's happy path.
