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

### CRP-8 — P2: spectral-norm scalar loop is 2.1-2.3x slower

Output and memory behavior remain correct. Profile generated LLVM and ownership instructions in the
hot recursive vector loops; ordinary scalar-only loops should not pay RC graph-management overhead.

### CRP-9 — P2: mandelbrot standard peak RSS grows 57%

Runtime and output are unchanged, but peak RSS rises from 1,753,540 KB to 2,756,764 KB at N=16,000.
Compare the packed-bitmap list's old copy-out representation with RC headers, duplicates, and
simultaneously live graphs. The fix gate is no regression in the multi-scale RSS slope.

### CRP-10 — P2: TCP high-concurrency throughput may regress

The short c=64 sample fell about 30% while c=1 and c=8 were unchanged/slightly faster. Confirm with
longer interleaved runs after CRP-5 is fixed; do not optimize from this noisy sample alone.

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
