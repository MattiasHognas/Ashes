# Bugs & gaps surfaced by the challenge benchmarks

Running the full Benchmarks Game suite in its **natural spelling** originally surfaced 18 real
defects and gaps — 16 from writing the benchmarks (a further report was removed as unreproducible)
and 2 more from re-running them at full scale. All but one of that original set were fixed.

The post-migration RC Perceus sweep then found new correctness, scaling, and peak-RSS regressions.
The measurements, minimal reproducers, debugger evidence, and fix order are tracked in the
**[RC Perceus challenge regression sweep](../docs/md/internals/rc-perceus-challenge-regressions.md)**.
Its P1 failures supersede the historical "one partial remainder" status below.

Fixed entries are cleared from this file once shipped — the analysis and measurements live in
[`docs/md/internals/changelog.md`](../docs/md/internals/changelog.md) (rows CO-28 through CO-37
cover the memory-model arc), each fix ships with a regression test under `tests/`, and the
per-challenge `README.md` files carry the final benchmark numbers.

Severity: **P1** blocks a benchmark or is a correctness/inference bug on valid code; **P2** hurts real
use (perf cliff, bad diagnostics, silent data loss); **P3** stdlib gap / minor / cosmetic.

Reproduce any snippet with the prebuilt compiler:
`src/Ashes.Cli/bin/Debug/net10.0/ashes run <file.ash>`.

## Open

### RC Perceus regression sweep

Ten regression clusters are currently open: five correctness/ownership failures, three major
performance-scaling failures, one peak-RSS regression, and one provisional high-concurrency server
regression. Start with **CRP-1**, the TCO/exit double-drop that corrupts the RC free list; it can mask
other failures.

### 1. (P2) List-of-small-`Str` representation constant (~96 B/base) — [PARTIAL]
The last remainder of the growing-accumulator arc. Every quadratic-memory and quadratic-time hole
in growing TCO accumulators is fixed (fixed-watermark reset, deep copy-out, loop-invariant args,
deferred-type resets, amortized compaction, reservation-based affine string growth — see the
changelog), and the shapes are **linear** today. What remains is a *constant*: a list of
single-character `Str` values costs ~96 bytes per element (each element is a separate
length-prefixed string plus a cons cell), which makes `reverse-complement` memory-dense at scale
(~1 GB peak RSS for a 10 MB input). Fixing the constant needs **in-place cons-cell reuse** — the
ownership / in-place reuse milestone, a linearity-engine feature, not a point fix.

## Open questions (not yet classified)
- Does `Ashes.IO.write` buffer, or issue a syscall per call? `fasta`/`reverse-complement` streaming
  emits one `write` per 60-char line (~2M+ syscalls at benchmark scale) — worth confirming the write
  path buffers.
