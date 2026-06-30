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

**Scaffold only.** `mandelbrot.ash` and the `FLAWS.md` writeup are deferred.

## Build & run (once written)

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/mandelbrot/mandelbrot.ash -o challenges/mandelbrot/mandelbrot
./challenges/mandelbrot/mandelbrot 1000 > mandelbrot.pbm
```
