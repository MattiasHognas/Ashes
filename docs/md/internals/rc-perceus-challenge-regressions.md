# RC Perceus challenge regression sweep

This is the actionable correctness, performance, and memory baseline captured after the RC Perceus
migration. It records failures to fix and the measurements needed to judge the fixes; it does not
replace the per-challenge benchmark results, which describe the last known-good compiler.

## Comparison

Captured on 2026-07-23 on the same 32-thread AMD Ryzen 9 9950X3D Linux x64 host used for the
published challenge results.

| Side | Revision | Meaning |
|---|---|---|
| Before | `fbef40c28780cebdecf8d878483964903323de93` | untouched pre-migration Ashes worktree |
| After | `c7fb30c89022daf6bc0a730b20dedb64f957bc75` | RC Perceus migration |

Both solutions and every challenge executable were freshly rebuilt in `Release`; challenge sources
were byte-identical between revisions. Executables were compiled at `-O2`. Timings are single
diagnostic runs using `/usr/bin/time`; large effects need fixing before statistical benchmarking.
Output was compared before accepting a performance result.

Not every published standard workload could be completed. A minimal correctness failure stops that
challenge, and workloads whose scaled run already took more than 90 seconds were not extrapolated
into hours of known-bad execution. Every challenge program was exercised.

## Correctness sweep

| Challenge | Before | RC Perceus | Smallest useful reproducer / observation |
|---|---|---|---|
| binary-trees | correct | **SIGSEGV** | `binary-trees 4`; no output |
| fannkuch-redux | correct | **wrong from N=3; hangs from N=6** | N=3: expected checksum/max `2/2`, got `-1/1` |
| fasta | correct | **SIGSEGV** | `fasta 1`; no output |
| k-nucleotide | correct | **SIGSEGV for non-empty input** | empty stdin succeeds; the N=1 fasta fixture crashes |
| mandelbrot | correct | correct, byte-identical | N=100 through standard N=16,000 |
| n-body | correct | **wrong result** | N=0 already prints final energy `0.001335617`; it remains constant through N=1,000 |
| pidigits | correct | **SIGSEGV for N >= 1** | N=0 succeeds; N=1 crashes |
| regex-redux | correct | **size-dependent SIGSEGV** | N=1 fasta succeeds; N=10,000 prints counts, then crashes during substitutions |
| reverse-complement | correct | correct at completed scales | byte-identical through the N=10,000 fasta fixture |
| spectral-norm | correct | correct, byte-identical | through standard N=5,500 |
| 1BRC | correct | correct at completed scales | byte-identical through 100,000 rows |
| TCP server | correct | correct in the sampled sweep | zero request errors |
| HTTP server | correct | correct in the sampled sweep | zero request errors; keep-alive RSS plateau passes |

## Performance and peak RSS

These results compare freshly built binaries from the two revisions on the same host. `RSS` is the
maximum resident set reported by `/usr/bin/time`.

### Correct compute workloads

| Challenge / scale | Before time | RC time | Change | Before RSS | RC RSS | Change |
|---|---:|---:|---:|---:|---:|---:|
| mandelbrot N=1,000 | 0.05 s | 0.05 s | unchanged | 5,172 KB | 13,280 KB | 2.57x |
| mandelbrot N=4,000 | 0.87 s | 0.88 s | 1.01x | 110,144 KB | 175,380 KB | 1.59x |
| mandelbrot N=16,000 | 13.94 s | 13.97 s | unchanged | 1,753,540 KB | 2,756,764 KB | **1.57x** |
| spectral-norm N=1,000 | 0.15 s | 0.33 s | 2.20x | 4,108 KB | 8,208 KB | 2.00x |
| spectral-norm N=3,000 | 1.41 s | 2.98 s | 2.11x | 4,100 KB | 8,192 KB | 2.00x |
| spectral-norm N=5,500 | 4.71 s | 10.95 s | **2.32x** | 4,108 KB | 12,308 KB | 3.00x |

Mandelbrot has essentially unchanged time but a repeatable 57% peak-RSS regression at the standard
scale. Spectral norm has a 2.1-2.3x execution-time regression despite retaining constant, small RSS.

### Correct but infeasibly slow at larger scale

| Challenge / scale | Before time | RC time | Change | Before RSS | RC RSS |
|---|---:|---:|---:|---:|---:|
| reverse-complement, fasta N=10,000 | <0.01 s | 0.38 s | large | 12,368 KB | 16,724 KB |
| reverse-complement, fasta N=100,000 | 0.05 s | >90 s timeout | **>1,800x** | 98,316 KB | not captured |
| 1BRC, 10,000 rows | 0.01 s | 0.52 s | 52x | 225,840 KB | 229,916 KB |
| 1BRC, 30,000 rows | 0.03 s | 4.26 s | 142x | 336,664 KB | 336,704 KB |
| 1BRC, 100,000 rows | 0.06 s | 16.49 s | **275x** | 652,876 KB | 636,444 KB |

The reverse-complement and 1BRC time ratios worsen with input size. Their standard workloads were
therefore not run: the scaled results already establish superlinear regressions. 1BRC's comparable
RSS at each scale shows that its immediate regression is time, not additional retained memory.

### Server sample

The same freshly published .NET load generator drove both compiler revisions for 5,000 requests per
stage. This short run is intentionally diagnostic and noisy.

| TCP concurrency | Before | RC Perceus | Change |
|---:|---:|---:|---:|
| 1 | 24,762 req/s | 25,280 req/s | 1.02x |
| 8 | 107,650 req/s | 113,371 req/s | 1.05x |
| 64 | 85,989 req/s | 60,166 req/s | 0.70x |

TCP remains functional; the c=64 result is a provisional ~30% regression that needs a longer
interleaved A/B run after the correctness blockers are fixed.

After CRP-5 was fixed, a fresh 5,000-request HTTP diagnostic run completed without errors:

| HTTP concurrency | RC Perceus |
|---:|---:|
| 1 | 17,016 req/s |
| 8 | 39,603 req/s |
| 64 | 20,336 req/s |

These are correctness/load samples, not a replacement for the final interleaved A/B performance
run.

After the lazy task-arena footer correction in CRP-10, three interleaved 50,000-request runs per
revision produced these medians:

| TCP concurrency | Before | Fixed RC Perceus | Change |
|---:|---:|---:|---:|
| 1 | 25,761 req/s | 26,026 req/s | 1.01x |
| 8 | 119,178 req/s | 118,879 req/s | unchanged |
| 64 | 43,851 req/s | 43,526 req/s | unchanged |

Every stage completed with zero errors. This longer balanced comparison supersedes the provisional
5,000-request c=64 result.

## Actionable defect ledger

### CRP-1 — P1: TCO/exit double-drop corrupts the exact-size RC free list

**Resolved 2026-07-23.** Three ownership errors combined here:

- guarded TCO exit drops were incorrectly eligible for lexical lifetime placement, which moved
  them outside their active/result-transfer guards;
- parallel TCO assignment could release predecessor values before every successor was normalized,
  and a successor that directly aliases an RC predecessor did not retain that value;
- an early control-flow branch captured the runtime-managed parameter set before later sibling
  branches promoted additional parameters, allowing arena pointers to survive its reset.

All back-edge resets now resolve after the complete lambda body establishes the final managed
parameter set, while retaining branch-specific argument facts. The `ArenaDeallocationTests` class,
dedicated multi-BigInt and late-sibling-promotion native regressions, runtime-RC plateau tests, and
`binary-trees 10`, `pidigits 100`, `fasta 1000`, and `k-nucleotide` over that fasta output pass at
`-O2`.

Minimal reproducers:

```bash
binary-trees 4
pidigits 1
fasta 1
```

Debug builds of all three crash in the exact-size free-list scan. The candidate pointer becomes
`0xffffffffffffffff`: an erroneous second last-drop decrements the released cell's count/next word
from zero to minus one, and the following allocation treats minus one as the next free cell.
Representative requested allocation sizes were 32, 40, and 96 bytes.

This is evidence of an ownership-placement/TCO-exit double drop, not an uninitialized allocator.
Audit tail-loop back-edge and function-exit drops, then retain each minimal program as a regression.
`k-nucleotide` on any non-empty input also dies while walking corrupted ownership/allocation state
and should be retested against this fix before being split into another defect.

### CRP-2 — P1: returned n-body list aliases consumed caller input

**Resolved 2026-07-23.** The initial alias hypothesis was incomplete. `run` promotes its list
parameter to runtime RC only when the later recursive branch is lowered. Its earlier `n == 0`
return therefore lost runtime-managed provenance at the `if` join. Function-exit cleanup dropped
the returned list, and the caller received a pointer into the exact-size free list; its reversed
free-list links made the result look like a one-body system.

TCO result provenance is now refreshed after the complete body establishes the final managed
parameter set. Provenance flows through parameter loads, borrows, duplicates, and locals whose
every incoming value is managed, so the exit transfer guard retains the selected returned owner
without treating mixed managed/arena joins as RC. A dedicated early-return regression passes, as
do `n-body 0` and `n-body 1000` at `-O2`; the latter again ends at `-0.169087605`.

`n-body 0` should print the same initial and final energy. The RC build instead prints:

```text
-0.169075164
0.001335617
```

The second value remains `0.001335617` at N=1, 10, 100, and 1,000. The returned list from `run`
reaches/aliases `system`; evaluating `energy(system)` appears to consume/drop storage still referenced
by `final`. Audit result-reach ownership summaries and the required late `RcDup` for a returned alias.

### CRP-3 — P1: fannkuch permutation state is corrupted

**Resolved 2026-07-23.** `nextPerm` rebuilt its single-constructor positional `State` product around
runtime-managed permutation and counter lists at a TCO back edge. The product itself stayed
arena-managed, so its child lets were dropped at scope exit and the next iteration received dangling
list pointers. TCO argument lowering now admits this narrow monomorphic positional-product shape as
an RC owner, records whether repeated child variables need duplication, and moves unique children
into it. The general escaping-ADT gate remains unchanged, including its borrowed-child behavior.

A dedicated positional-product/owned-child TCO regression passes. Fannkuch now matches the reference
from N=3 through N=8 at `-O2`, including checksum/max `1616/22` for N=8, and the Ownership,
ArenaDeallocation, and ReuseToken test classes pass.

N=1 and N=2 match the baseline. N=3 changes checksum/max from `2/2` to `-1/1`, N=4 changes
`4/4` to `3/4`, N=5 changes `11/7` to `3/4`, and N>=6 does not terminate within 20 seconds.
Audit match-payload ownership and ADT/list state carried between `nextPerm` and the tail loop.

### CRP-4 — P1: regex substitution graph becomes dangling at scale

**Resolved by the CRP-3 child-transfer fix.** Regex-redux's substitution state also crossed a TCO
boundary through an arena parent containing runtime-managed children. After positional-product
ownership was fixed, the N=10,000 fixture completed with the expected lengths
`101745/100000/133640`. The N=100,000 fixture also completed at `-O2` with lengths
`1016745/1000000/1336326`, in 1.97 seconds with 241,068 KB peak RSS in the diagnostic run.

The N=10,000 fasta fixture prints all nine regex counts correctly, then segfaults in `applySubs`.
The N=1 fixture completes. Debugging shows an invalid pointer dereference rather than the
minus-one free-list signature, so treat this as a likely escaping substitution string/list
use-after-free unless CRP-1's fix also removes it.

### CRP-5 — P1: HTTP request handling uses a corrupt allocation length

**Resolved 2026-07-23.** Three arena/coroutine inconsistencies first had to be removed:

- spawned task-private arenas still used the old fixed-size chunk-end convention, while reclaim now
  follows variable-size chunk footers;
- deferred TCO reset placeholders did not expose their future temp/local reads to coroutine
  liveness, and dead-store elimination counted only explicit `LoadLocal` reads instead of implicit
  arena/stack/reservation-slot reads;
- `CopyOutList(String)` cached only string pointers before restoring the arena. Building the copied
  list could then overwrite a later source string before its bytes were read. The corrupted header
  was interpreted as a length and became the approximately 254 TiB allocation below.

Task-private chunks now use the common header/footer representation; state-machine and optimizer
slot/temp analyses cover the arena, stack, affine-reservation, copy-out, process, and task
instructions they consume; and list copy-out snapshots complete string objects in stack/OS scratch
memory before allocating any destination cells.

The optimized one-worker minimal HTTP regression, routing/buffering, concurrent workers, parking
receive, and the 3,000-request keep-alive RSS plateau test pass. The actual `http_echo.ash` challenge
also completed 5,000 requests at each of concurrency 1, 8, and 64 with zero errors at `-O2`.

One request is enough. Under:

```bash
strace -f -e trace=mmap,mremap,munmap,brk ./http_echo
```

request workers repeatedly execute:

```text
mmap(NULL, 279719803098752, PROT_READ|PROT_WRITE,
     MAP_PRIVATE|MAP_ANONYMOUS, -1, 0) = -1 ENOMEM
```

That is an attempted allocation of about 254 TiB. The server stays alive but emits
`Runtime error: failed to allocate heap memory from OS` and returns no valid response. Audit the
ownership and length provenance of HTTP request/response strings crossing the task/worker boundary.

### CRP-6 — P1: reverse-complement becomes superlinear — resolved

**Resolved 2026-07-23.** `compLine` was called once per input line with the sequence accumulator
produced by the preceding call. Its generic entry could not tell that this argument was already
runtime-managed, so it defensively deep-copied the complete accumulated list on every line. That
made time proportional to the sum of all preceding sequence lengths.

Closure metadata now advertises whether a direct parameter entry can adopt an RC-owned argument. A
fresh result transfers its existing reference; a caller that must preserve its own RC argument
conditionally retains only the root before passing the ownership flag through the closure-call ABI.
Unknown/arena arguments still take the defensive normalization path, and earlier curried parameters
already captured in an environment do not advertise direct-argument adoption. Parameters whose RC
eligibility resolves only after the tail self-call constrains inference are finalized before their
pending call flags are enabled. This preserves the generic-call safety boundary while turning
repeated whole-graph copies into constant-time ownership handoffs.

The optimized challenge remained byte-identical and changed from 0.00/0.04/0.54/4.80 seconds at
fasta N=1,000/3,000/10,000/30,000 to 0.00/0.00/0.00/0.01 seconds. At N=100,000
(1,016,745 input bytes) it completed in 0.06 seconds at 82,000 KB peak RSS. The separate
list-of-small-`Str` representation constant remains tracked below.

### CRP-7 — P1: 1BRC becomes superlinear — resolved

**Resolved 2026-07-23.** The initial persistent-Map/worker-merge hypothesis was incorrect. Running a
single worker retained the regression, the specialized `Map.set` path remained fully reusing, and
debugger sampling placed the hot instruction in `Ashes.Text.join` after computation completed.

The runtime-RC allocator cached every released block up to 4 KiB in one unsorted list. Allocation
linearly searched that list for an exact size. `Text.join`'s balanced reduction creates and releases
many differently sized strings, so otherwise-linear ownership traffic caused quadratic allocator
searches. The allocator now lazily creates one per-thread table with an exact bin for every aligned
cached size. Allocation and release are constant-time; larger blocks still bypass the cache and
return directly to the OS.

Fresh `-O2` runs changed from 0.84/7.47 seconds to 0.01/0.03 seconds at 10,000/30,000 rows, matching
the pre-migration 0.01/0.03-second control. On a freshly generated 100,000-row fixture the fixed RC
binary took 0.10 seconds versus the pre-migration binary's 0.09 seconds. Peak RSS remained in the
same bands (229,932/336,684 KB at 10,000/30,000 rows and 640,824 KB at 100,000), and output was
byte-identical at every scale. A focused variable-size RC-recycling CPU regression now compares the
native RC path with its arena control.

### CRP-8 — P2: spectral-norm scalar loop is 2.1-2.3x slower — resolved

**Resolved 2026-07-23.** `avRow` and `atRow` traverse `List(Float)` through a pattern tail. The
consumed-tail rule normalized each caller-owned vector to RC at function entry, then duplicated and
dropped one list reference per element. That added a complete extra list pass for every matrix row.

A tail cursor through an inline-element list now borrows the caller-owned graph: all tails remain
below the loop watermark and no pointer-bearing head ownership must move out of a discarded cell.
Pointer-bearing consumed lists stay runtime-managed. When the TCO frame has no other managed
parameters, lowering also avoids a redundant back-edge arena reset; nested call scopes already
reclaim their own scratch.

At N=3,000 the fixed `-O2` binary took 1.38 seconds versus 1.43 seconds before migration. A
three-run `hyperfine` at the standard N=5,500 measured 4.639 s ± 0.001 for the fixed binary and
4.683 s ± 0.033 before migration; output remained byte-identical. The focused IR regression forbids
both list RC traffic and the redundant scalar-frame reset.

### CRP-9 — P2: mandelbrot standard peak RSS grows 57% — resolved

**Resolved 2026-07-23.** The packed bitmap is accumulated as a `List(Int)` whose elements are inline
scalars. As with spectral norm, treating its tail-recursive cursor as runtime-managed added RC
headers and normalization copies to a graph that could safely remain borrowed below the loop
watermark. CRP-8's inline-element borrowed-cursor correction therefore fixes this memory regression
as well; no Mandelbrot-specific ownership rule was required.

Fresh `-O2` output remained byte-identical at N=1,000, N=4,000, and N=16,000. Peak RSS for the fixed
binary was 9,440/114,200/1,757,596 KB versus 5,172/110,144/1,753,536 KB before migration. The small
fixed process overhead is visible at N=1,000, but the absolute difference stays approximately
4 MiB while the live bitmap grows: at the standard N=16,000 scale, fixed RSS is only 0.23% above
the pre-migration control rather than 57% above it. Runtime remained comparable at 14.18 seconds
versus 13.94 seconds in the final diagnostic run.

### CRP-10 — P2: TCP high-concurrency throughput may regress — resolved

**Resolved 2026-07-23.** The original short c=64 sample was noisy, but the first longer retest after
CRP-5 exposed a real, broader regression: 18,500/44,268/22,184 req/s at concurrency 1/8/64 versus
26,099/120,155/42,772 req/s before migration in the balanced sequence.

Commit isolation and optimized LLVM comparison traced the loss to CRP-5's task-private chunk-format
correction. Each spawned request maps a 4 MiB private arena. Eagerly writing that chunk's footer at
its far end faulted a second physical page even when the request used only its frame page; the
mapping is released after the handler, so the unnecessary fault recurred on every connection and
contended heavily at concurrency.

The initial task chunk's footer is now lazy. A task reaper recognizes that fixed first chunk from
the task address and frees it directly; any grown chunks retain the common footer/previous-end
format, so variable-size growth and cross-chunk reset correctness from CRP-5 are unchanged. In a
1,000-request diagnostic stage (plus the load generator's setup connections), minor faults fell
from 2,416 to 1,215, matching the 1,215 pre-migration control. An 8 MiB receive stress verified that
grown chunks still complete and reap at stable RSS.

The final three-run, 50,000-request interleaved A/B medians shown above match the pre-migration
binary at all three concurrency levels with zero errors. A focused native regression counts minor
faults across 300 spawned handlers so an eager far-footer write cannot silently return.

### CRP-11 — P1: pidigits exact-line results crash above 4 KiB — resolved

**Resolved 2026-07-23 during the final standard-workload sweep.** The earlier small `pidigits`
retests did not exercise the scale boundary: exact multiples of ten returned the threaded `output`
accumulator directly, and N=2,500 or greater crashed after its RC allocation crossed 4 KiB and was
returned to the OS. Adjacent partial-line runs (N=2,499 and N=2,501) succeeded because their exit
arm built one final concatenation.

Two late-TCO facts were missing. First, unreachable dummy stores emitted after tail jumps poisoned
the result-join provenance scan. Second, after whole-body analysis promoted `output` to runtime RC,
the partial-line `ConcatStr` arm still retained its earlier arena allocation regime. Reachability is
now computed over the function IR before join provenance is refreshed, and string concatenations
derived from managed values are upgraded so every reachable exit arm has one ownership regime.

Affine back-edge concatenation has a separate consuming RC contract: its in-place path transfers
the predecessor reference, while its fallback copies into a fresh RC reservation and releases the
predecessor. The TCO parallel assignment therefore does not drop that predecessor a second time,
and RC reservation slots survive arena resets. This retains the linear unoptimized
`tco_affine_string_append.ash` path instead of reintroducing a full copy per append.

The focused >4 KiB direct/concat exit regression and the existing 6 MB affine-growth regression
both pass. Fresh `-O2` `pidigits` runs at N=2,499/2,500/2,501 all succeed; standard N=10,000 is
byte-identical to the pre-migration control and completed in 3.26 seconds versus 3.47 seconds.
Peak RSS was 12,556 KB versus 4,108 KB; this is a bounded process-level RC/arena overhead, not a
growing retained-output leak, and remains part of the final multi-scale memory comparison.

### CRP-12 — P2: n-body immutable rebuild pays per-cell RC traffic

**Open; isolated during the final standard-workload sweep.** Correctness remains restored: the
standard N=50,000,000 output is byte-identical to the pre-migration control
(`-0.169075164` / `-0.169059907`). Peak RSS is also bounded at 8,204 KB versus 4,108 KB. Runtime,
however, regressed from 20.40 to 37.00 seconds (1.81x); an N=5,000,000 diagnostic reproduces the
same proportional loss at about 2.00 versus 3.82 seconds.

The five-body live graph is constant-sized. Each timestep nevertheless rebuilds five `Body` records
and five cons cells in `updateVel`, then rebuilds both layers again in `updatePos`. The RC lowering
allocates the escaping replacement cells from exact-size bins and releases the preceding graph.
That keeps memory flat, but adds reference-count, free-bin, and recursive child-drop work to the
hot float loop. Returning these cells to the arena instead is not a fix: a diagnostic arena build
retained about 272 MB after only 5,000,000 steps and did not recover the lost CPU time.

The required fix is ownership-proven in-place reuse of untagged list cells together with their
single-constructor record heads. `AllocReusing` now represents both tagged ADT and untagged list
layouts, including the fresh-RC fallback; ownership lowering does not emit the list form until the
following alias proof is established. The extension must preserve the important boundary in
`updateVel`: `allBodies`
reads the same source graph as `remaining`, so a cell can be overwritten only after the recursive
suffix and its final acceleration read have completed, or after a defensive unique copy. A fresh
`updateVel` result is uniquely consumed by `updatePos` and is the simpler first reuse boundary.
Verification must keep the standard output byte-identical, restore time close to the pre-migration
control, and confirm a flat RSS slope across increasing and repeated workloads.

The first implementation slice now specializes recursive scalar-list rewriters: `DropReuse`
publishes a matched untagged cell, recursive suffix results stay below their call watermarks, and
the rebuilt cons consumes that token. The focused 100,000-turn native loop completes at 8,204 KB
peak RSS, and the reuse/arena unit classes plus `just ci-quick` pass. N-body's binary is intentionally
unchanged at this point because `List(Body)` needs the conditional parent-to-child transfer above;
the scalar path is the layout and recursive-call foundation, not a premature relaxation of that
alias boundary.

The next slice proves the simpler pointer-bearing boundary. Result reach now ignores record fields
whose declared types are copied inline, so `updateVel` is correctly summarized as fresh.
Multi-parameter recursive functions specialize on their final accumulator, and `updatePos` can
consume the fresh `updateVel` result while both are still inside the same arena call window. Its
specialization is fully reusing: one untagged list-cell token and one `Body` token per element, with
no fresh ADT/to-space allocation. The result deliberately remains arena-managed until the enclosing
`advance` escape performs normal RC normalization; marking it RC early was rejected by native
N=2/N=3 correctness probes.

Correctness is byte-identical through N=1,000 and RSS remains flat at about 8,204 KB. This does not
recover the CPU regression: three N=5,000,000 runs took 4.05–4.11 seconds, slightly slower than the
roughly 3.82-second pre-slice RC baseline. Arena bump allocation was not the dominant cost; the
remaining hot cost is the per-timestep RC normalization/release of the five-cell escaping graph.
The next performance slice must remove or amortize that RC boundary while preserving
`updateVel`'s aliased `allBodies` read, rather than broadening local reuse unsafely.

Inlining a fresh-result helper specifically while evaluating a TCO successor argument removes one
such boundary without changing general call ownership. `run` now lowers `advance` inside its back
edge; capture expansion makes `updateVel` and `updatePos` available there, and the existing
fresh-result composition applies before the loop's established copy/reset boundary. Helpers whose
ownership result aliases an input or is poisoned retain their ordinary call boundary.

This is a measurable but partial recovery. Three N=5,000,000 runs completed in 3.62, 3.63, and
3.72 seconds at roughly 8.2 MB peak RSS, versus 4.05–4.11 seconds before the inline and about
3.82 seconds before the pointer-bearing reuse slice. N=1,000 remains byte-identical. The
pre-migration diagnostic was roughly 2.00 seconds, so the item remains open; the next slice must
profile the remaining list-of-record RC normalization and release path rather than attributing it
to arena allocation.

## Remaining benchmark gaps outside the RC regression sweep

These issues predate the RC Perceus migration. They remain relevant, but are not regressions against
the pre-migration comparison revision:

### P2: list-of-small-`Str` representation constant (~96 B/base)

Every quadratic memory/time hole in growing TCO accumulators is fixed, and reverse-complement's
completed pre-migration workloads scaled linearly. The remaining constant is large: a list of
single-character `Str` values costs about 96 bytes per element because each element is a separate
length-prefixed string plus a cons cell. That produced about 1 GB peak RSS for a 10 MB input.

CRP-6 must first restore linear RC-migration scaling. Reducing this separate representation constant
then requires in-place cons-cell reuse, an ownership/linearity feature rather than a point fix.

### Open question: line-oriented output buffering

Confirm whether `Ashes.IO.write` buffers output or issues one syscall per call. Fasta and
reverse-complement emit one write per 60-character line, exceeding two million calls at benchmark
scale. If the path is unbuffered, capture and prioritize the resulting throughput cost separately.

## Fix and verification order

1. Fix CRP-1 first and rerun every crashing challenge; allocator corruption can mask later defects.
2. Fix independent correctness defects CRP-2 through CRP-5.
3. Re-run every challenge at small and intermediate scales, comparing output byte-for-byte.
4. Fix the scaling regressions CRP-6 through CRP-9, using at least three increasing input sizes.
5. Run the published standard workload for every challenge and a longer interleaved server A/B.
6. Run sustained repeated-workload RSS slopes after correctness is restored. Crashes currently block
   meaningful leak conclusions; a clean single-run peak does not prove the absence of retained RC
   graphs or region leaks.
7. Update each per-challenge README only after its standard workload is correct and stable.
