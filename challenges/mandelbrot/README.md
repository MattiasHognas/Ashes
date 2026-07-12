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

**Implemented + benchmarked.** [`mandelbrot.ash`](mandelbrot.ash) runs the pure-Float
`N×N × up-to-50` escape loop and emits the real binary `P4` PBM. The two `Float` inference bugs and
the missing raw-bytes stdout write it originally surfaced are all fixed:

- The escape loop is written **naturally** — `zr * zr`, `cr + zr2 - zi2` — with no `1.0 *` lead and no
  operand reordering. Annotated parameter types are now seeded before the body is lowered.
- It writes the real image via `Ashes.IO.writeBytes : Bytes -> Unit` (a `P4` header plus one bit per
  pixel, packed 8/byte, MSB first, rows padded to a byte). Verified as a valid PBM (`N=200` → 15909
  black pixels). The bit-packing is a single flat cons loop, kept flat on purpose (a helper returning
  the growing byte list would be deep-copied per call — the memory-model limitation noted below).

## Build & run

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/mandelbrot/mandelbrot.ash -o challenges/mandelbrot/mandelbrot
./challenges/mandelbrot/mandelbrot 1000 > out.pbm   # binary P4 PBM on stdout
```

## Benchmark

```bash
challenges/bench.sh mandelbrot 4000
```

Measured on a 32-thread AMD Ryzen 9 9950X3D, Linux x64 (single-threaded), `-O2`:

| N | Time | Peak RSS |
|---|------|----------|
| 1,000 | 0.054 s | 5.2 MB |
| 4,000 | 0.85 s | 102 MB |
| **16,000** (standard) | **13.5 s** | **1.7 GB** |

The escape loop itself is constant-memory (scalar `Int`/`Float` accumulators, the compiler's happy
path). Resident set scales with the **output image**: the packed bitmap is `N^2/8` bytes, built as a
cons list (`2*N^2` bytes of cells) that is reversed and materialized to `Bytes` once at the end.
That is inherent to emitting the real image as a list; the earlier count-only version was constant
0.25 MB because it produced no image.

Re-running this benchmark surfaced (and fixed) a real compiler bug — exactly what the suite is
for: the scope-exit list copier cached head values in an **unbounded dynamic stack alloca** (8
bytes per cell), and the entry frame never pops allocas, so the packed-bitmap list plus its
reversed copy overflowed the 8 MB stack at `N >= 2500` (changelog CO-37). Large head caches now
spill to OS memory; `N=16000` (a 32M-cell list) runs on all three targets.
