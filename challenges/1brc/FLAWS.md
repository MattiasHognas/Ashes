# 1BRC in Ashes — flaws found

The [One Billion Row Challenge](https://github.com/gunnarmorling/1brc) reads ~1e9
`Station;Temperature` lines and prints per-station `min/mean/max` sorted by name.
It exercises exactly the things Ashes is weakest at: bulk IO, mutable/hashed
accumulation, ordering, and a long-running hot loop under a non-GC arena.

[`brc.ash`](brc.ash) is a *correct* implementation (verified against a hand-computed
fixture; UTF-8 station names sort by byte order). It started out **not** viable for
the real input — every flaw below was found while getting it there — and now runs the
full 1e9-row input (`brc_parallel.ash` does it in ~25 s after the 2026-07-03
compiler-optimization arc; see [`README.md`](README.md) for current benchmarks). This document is the point of the exercise: what broke, where,
and why. File/line references are into the compiler at the time of writing.

Some findings are **compiler bugs discovered while writing this program**; the rest
are structural limitations of the language / stdlib / memory model.

## Status

| # | Flaw | Status |
|---|------|--------|
| #1 | No buffered/streaming file IO | **FIXED** (buffered stdin + chunked file API + buffered `File.readLine`) |
| #1b | `readLine` loop segfaults at depth | **FIXED** |
| #2 | Hot-loop arena leak (Map accumulator) | **FIXED** — brc is now constant-memory (45 MB @ 1M, 49 MB @ 10M): in-place `Map.set` reuse + single-loop `File.readLine` fold + reset-safe resource handles |
| #3 | No hash/mutable O(1) accumulator | **FIXED for the fold** — persistent `Map` with in-place reuse gives O(log K), constant-memory updates (HashMap also shipped) |
| #4 | No constructible String ordering | **FIXED** |
| #5 | No data parallelism | **FIXED** — `Ashes.Parallel.both` genuinely parallel on all 3 targets (per-thread arenas: GS/TEB/ELF-TLS; clone-futex/CreateThread). Full data-parallel `map`/`reduce` still needs monomorphization |
| #6 | Shipped-module ref unresolved in a function body | **FIXED** |
| #7 | Char-by-char `uncons` is O(T²) (tail copied each step) | **FIXED** (zero-copy string views) |
| #8 | Output fold is O(K²) (`acc + sep + entry` growing-string concat) | **FIXED** (tail-recursive divide-and-conquer `Ashes.String.join`) |
| #9 | `+` with two unresolved operands eagerly defaults to Int | **FIXED** (deferred AddInt/ConcatStr + monomorphic `+`-vars) |

Each section below records the original finding and what changed to fix it. **All flaws are now
fixed** (brc is correct and constant-memory on all three targets). The compiler-side improvements this
exercise drove — data-parallel `map`/`reduce`, the move/linearity analysis, arm64/win-x64
networking+parallelism coexistence, and more — have all **landed**; see the *Completed Work* record in
[docs/future/COMPILER_OPTIMIZATION.md](../../docs/future/COMPILER_OPTIMIZATION.md).

## Performance pass (2026-06-30) — benchmark + remaining work

Benchmarked `brc.ash` on subsets of `measurements.txt` (8700 unique stations in the
first 10k rows; `hyperfine`, peak RSS from `/proc/<pid>/status`):

| Rows | Baseline | After #7 (uncons views) | After #8 (`join`) | After #2a + streaming |
|------|----------|-------------------------|-------------------|-----------------------|
| 100 | 0.92 ms | 0.56 ms | 0.43 ms | 0.4 ms |
| 1 000 | 57 ms | 18 ms | 9.6 ms | <10 ms |
| 10 000 | 5.2 s / 15 GB | 1.57 s / 3.6 GB | 0.79 s / 2.6 GB | **~0.1 s / 105 MB** |
| 20 000 | 56 s | 6 s | 4 s | <1 s |
| 30 000 | >90 s | 12 s | 7 s | <1 s |
| 50 000 | (OOM ~46 GB) | — | — | **1 s / 508 MB** |
| 100 000 | (OOM) | — | — | **1 s / 1.0 GB** (37 191 stations) |
| 1 000 000 | (OOM) | — | — | **4 s / 9.3 GB** (41 343 stations) |
| 10 000 000 | (OOM) | — | — | OOM at ~50 GB after ~86 s (residual #2 leak; machine has 60 GB) |

Cumulative at 10k rows: **5.2 s / 15 GB → ~0.1 s / 105 MB** (≈25× less memory; quadratic → linear).
brc now runs to ~1M rows in seconds (was OOM past ~22k). Memory is **linear** in rows (≈9 GB at 1M,
≈9 KB/row), so 10M still OOMs on a 60 GB machine — that is the residual #2 leak (per-row `Map.set`
garbage the loop never reclaims), not the file: input is now **streamed** in
64 KiB chunks (`Ashes.File.open`/`readChunk`/`close`, **#1**), so file memory is constant and brc
reads any size (whole-file `readText` capped at 1 MiB and OOM'd past ~50k). Output verified correct at
1M (41 343 entries == unique stations; md5-stable vs whole-file on 30k). The remaining limiter for
much larger inputs is the residual #2 linear leak (needs the hot-loop arena reset).

## Performance pass (2026-07-01) — TCO stack-leak fix + string-keyed in-place reuse

Landed the compiler side of the in-place-reuse milestone (#2/#3) and, in the process, found and fixed a
**general TCO stack leak**: a tail-call loop body makes dynamic stack allocations each iteration
(per-iteration string/syscall scratch — `fromInt`, `compare`), and a TCO back-edge *jumps* rather than
*returns*, so those `alloca`s never free and pile up until the 8 MB stack overflows. Fix: mirror the
per-iteration arena reset for the stack — `llvm.stacksave` at the loop header, `llvm.stackrestore` at the
back-edge (`IrInst.SaveStackPointer`/`RestoreStackPointer`). This is what made a **string-keyed** `Map.set`
fold reach scale at all; before, it crashed (SIGSEGV) at ~250–270k rows. Same class as the old `readLine`
TCO segfault.

Benchmarks (wall / peak RSS, `/proc/<pid>/VmHWM`, `1brc-perf`):

| Rows | brc.ash (Str key + **tuple** value) | smap2 microbench (Str key + Int value, reuse fires) |
|------|-------------------------------------|-----------------------------------------------------|
| 100 000 | 0.43 s / 1.0 GB | — |
| 1 000 000 | 4.0 s / 9.7 GB | 0.41 s / **1 MB** (constant) |
| 10 000 000 | (OOM) | 4.3 s / **1 MB** (constant; was: crash) |

Status: for a string-keyed, **copy-value** map (smap2), the reuse is now **correct, fast, and CONSTANT
memory** (1 MB flat, 1M→10M) — FLAWS #2 (leak) and #3 (O(1)) met. Two things got it there: the TCO
stack-leak fix (above) and reset-safety — `IsFullyReusing` no longer bails on the `compare`/`height`/`max`
non-self calls (they return `Int` and never enter the tree), and the accumulator is marked reset-safe when
its heap fields are provably persistent (`AccumulatorIsFullyPersistent`: MapTree key is
materialized-on-insert + kept-on-update, value must be a copy type), so the per-iteration arena scratch is
reclaimed.

**Heap-value in-place reuse — DONE for tuple values (set path).** Fresh heap Map values are now materialized
into the blob on insert and update (Str/Bytes dynamic; tuple-of-copy fixed-size), and on update the fresh
tuple overwrites the superseded value's blob cell in place (`IrInst.CopyFixedInto`) so nothing leaks. Result:

| Map shape (Str key, tuple value), 1M / 10M rows | memory |
|---|---|
| set-only fold (`tsmap`) | **1 MB / 1 MB** (constant) |
| get-then-set fold (`gsmap`) | 16 MB / 154 MB (~15 B/row) |

So a set-only tuple-valued map is now fully bounded (#2/#3 met). Two things remain for **brc** specifically:
1. **`Map.get`-before-`set` residual (~15 B/row).** Isolated to the `get` borrow (a get-then-set fold leaks
   even when the fetched value is ignored; set-only is flat). The borrow appears to cost the following `set`
   its in-place reuse tokens (or blocks the per-iteration reset), so a little to-space/scratch accrues each
   row. This is the last thing between brc and constant memory once its reuse fires.
2. **Reuse must fire for brc's shape.** brc's `Map.set` lives in the `processLine` *helper*, not the loop
   body, so the loop's reuse analysis never sees the accumulator and no `set$reuse` is generated → brc still
   uses the normal (leaky) `Map.set` (9.7 KB/row). A synthetic get-then-set fold with the set *in the loop*
   (`gsmap`) does fire the reuse.

   **Tried:** inlining `processLine`'s get+set into `scanChunk`'s newline case. The reuse then fires
   (`fullyReusing=True`), BUT it is a net regression (25 GB, no output): making `map` a reset-safe accumulator
   makes the *whole* `scanChunk` loop reset-safe, so every other loop arg must survive the per-iteration reset
   — including `rest`, the shrinking `Str` view of the chunk. Copying that view out each character is O(chunk²).
   So brc can't just thread a shrinking view through a reset-safe map loop.

   **Proper fix (the "restructure brc" task):** scan the chunk by an integer index `pos` (copy-type → reset-safe,
   no copy-out) instead of a shrinking `rest` view, and take whole lines with `substring`/`indexOf`-from-`pos`
   rather than a char-by-char `lineAcc + c` accumulation. Then the loop is reset-safe without the view-copy
   blowup, the map reuse fires, and the get residual (#1) is the only thing left between brc and constant memory.

## Performance pass (2026-07-01) — pos-indexed scan lands; the real residual is the loop RE-ENTRY deep-copy

**Done (committed).** `scanChunk` now scans each chunk by an integer byte index `pos`, using two new
byte-level intrinsics — `Ashes.Bytes.indexOf(bytes)(needle)(from) : Int` (memchr from an offset) and
`Ashes.Bytes.subText(bytes)(start)(len) : Str` (bounds-clamped byte-range slice) — so the per-line
fold `go pos m` carries only copy-type args plus the `Ashes.Map` accumulator, and get+set are inlined
into `go`'s body. Reuse fires (`fullyReusing=True`), output is byte-identical to the baseline (41343
stations @ 1M), and peak RSS drops **9.2×**: 9709 MB → **1054 MB @ 1M**, 1004 MB → **87 MB @ 100k**.
';'/'\n' are ASCII so byte-slicing never splits a multibyte codepoint. `processLine` is kept for the
carry-prefixed first line and streamLoop's final partial.

**But it is still linear (~1 KB/row), and the cause is NOT the get borrow.** Bisected to a clean
20-line synthetic: a get-then-set OR even a **set-only** reuse fold that is nested inside an outer loop
threading the accumulator (`stream c m` → `scan base m` → inner `go`) leaks ~1 KB/row (≈ one root-to-
leaf spine), while the *identical* fold as a single flat loop is bounded (23 MB @ 1M, 40k keys). The
mechanism: in-place reuse makes the accumulator uniquely-owned by **deep-copying it once at loop entry**
(`Lowering.cs` ~3005, `reuseDefensiveCopy`). That entry copy is part of the loop-containing function's
body, so it re-executes on **every re-entry** — brc calls `scanChunk` (which contains `go`) once per
64 KiB chunk (~250× at 1M), deep-copying the growing tree each time, and because the outer `streamLoop`
is not itself reset-safe (its accumulator isn't a direct `Map.set(...)(acc)`), every prior copy leaks.
Sum over chunks ≈ Σ tree-size ≈ the ~1 GB.

**Confirmed fix direction: a single loop entry.** A stdin variant (`loop map = match readLine with … ->
loop(set … map)`), one flat loop, is **22 MB @ 1M and 159 MB @ 10M** (both correct, 41343) — i.e. bounded
at ~16 B/row, which is exactly the get-then-set residual (#1 below). So the two remaining items are
orthogonal and both real:
  - **(A) Loop re-entry deep-copy — RESOLVED via a single-loop file fold.** Added a buffered
    `Ashes.File.readLine(handle) : Maybe(Str)` intrinsic (module-global refill buffer + fd guard,
    mirroring the stdin `readLine`; `EmitFileReadLine`), so brc folds the whole file in ONE loop over
    `readLine` — no `scanChunk`/`streamLoop`/carry, `readLine` reassembles a line straddling a 64 KiB
    read internally. One loop entry ⇒ the accumulator is deep-copied to uniqueness exactly once, and
    the get+set are inlined into the loop body so the map's in-place reuse fires. A second fix was
    needed: the loop also threads the `handle`, and a `FileHandle` (a resource type) was not
    reset-safe, so the arena reset never fired and per-row scratch leaked (14 GB/10M!). Fix
    (`Lowering.cs`/`Lowering.Ownership.cs`, `IsResourceHandleType`): a resource handle is a scalar
    fd/HANDLE with no heap payload and is never Dropped by a reset, so it is reset-safe as a back-edge
    arg (general — helps any socket/process/file read loop). **Result: brc is now constant-memory —
    45 MB @ 1M and 49 MB @ 10M** (was 9.7 GB @ 1M / OOM @ 10M), output byte-identical to baseline.
    Tests: `tests/file_readline.ash`.
  - **(ii) General nested-re-entry fix — the move/linearity milestone (OPEN, deferred).** The entry
    deep-copy (`Lowering.cs` ~3013, `reuseDefensiveCopy`) makes the loop accumulator uniquely owned so
    in-place reuse is sound. It re-executes on every re-entry, so an *outer* loop that threads the
    accumulator into an *inner* reuse fold re-copies the growing tree each outer step (repro: a set-only
    or get-then-set fold nested under an outer accumulator-threading loop — `gsstream`, still **978 MB
    @ 1M**). Skipping the copy is sound only if the accumulator is provably unaliased at the call site.
    A syntactic "argument variable used exactly once" check is *not* enough: the accumulator is used
    once **per control-flow path** (`if c>=250 then m else stream(c+1)(scan(...)(m))` uses `m` in both
    arms), so a sound analysis must be **path-aware and interprocedural** — a whole-program fixpoint
    marking a function's accumulator parameter "owned" iff at every call site the argument is a
    per-path-linear, last-used value rooted (through unique hops / reuse results) at a fresh
    construction. That is the ownership/borrowing milestone (`Lowering.Ownership.cs`,
    `docs/future/FUTURE_FEATURES.md`); this reuse+arena code is corruption-prone, and an unsound skip
    silently miscompiles (in-place mutation of a live alias), so it is intentionally **not** hacked in.
    Note: brc no longer needs it (its file fold is a single loop after (A)); it remains the right fix
    for the general pattern and for letting `streamLoop`-style code be constant without `readLine`.
  - **(B) The get-then-set ~16 B/row residual** — **FIXED.** Root cause: `Ashes.Map.get` returns
    `Maybe(V)`, not `MapTree`, so it rebuilds nothing — yet because `map` was deep-copied to a linear
    accumulator for the *set*, the *get* call was also routed to a reuse specialization, and inside a
    spec every fresh cell (here the `Some(value)` result) is allocated into the never-reset to-space →
    one leaked cell per row. Fix (`Lowering.cs`, `SpecializationRebuildsAccumulator`): only route a call
    to a reuse spec when the function's result type is the same named ADT as its accumulator (last)
    parameter — i.e. it actually rewrites the tree (`MapTree -> MapTree`). Pure readers (`MapTree ->
    Maybe`) stay on the normal path, where their result lives in the reclaimed main arena. A get-then-set
    fold (even using the fetched value) is now **1 MB flat at 10M** (was 154 MB); the single-loop stdin
    brc is **7 MB at 10M** (was 159 MB). Regression-covered by `tests/reuse_lookup_then_update_bounded.ash`.

### #2a — the real hot-loop blowup was a reuse defensive-copy bug (FIXED)

The design assumed the O(N²) blowup was `Map.set` superseded-node sharing. Empirically it was not:
the tree stays balanced (height 15 at 8700 keys), `compare` is cheap, and a *set-only* fold is
linear. The blowup came from the **`Map.get` (lookup) before each `set`**. A tail-recursive lookup
that `match`es the accumulator was (incorrectly) given the in-place-reuse **defensive deep copy** of
its recursive subtree argument at every recursion level — even though it never rebuilds the
accumulator (it returns `Maybe`, and at most reuses a dead nullary leaf, `Empty -> None`). That made
each lookup O(size) and the whole fold O(N·K). Fix (`Lowering.cs`): keep the defensive copy only when
in-place reuse rebuilds **non-nullary structure** (a field-bearing `AllocReusing`); otherwise skip it
and revert the trivial nullary reuses to fresh allocations (sound — no copy backs them). General fix
(any immutable recursive structure with a lookup-then-update fold), regression-tested in
`tests/reuse_lookup_then_update_bounded.ash`.

**Root causes, in order of impact:** (1) `Ashes.Text.uncons`'s tail was a full byte
copy, so a char-by-char loop was O(T²) in file size *and* allocated O(T²) bytes — that
is #7, now fixed. (2) The output fold is O(K²) in the station count — ~45 % of the
10k-row runtime — that is #8. (3) The Map accumulator never lets the hot loop reset its
arena (#2); note the loop *not* resetting is what keeps the `remaining` view cheap, so a
naive reset would re-introduce O(T²) view materialization — #2 must come with cheap,
below-watermark view copy-out.

**Done:** memcpy for the slice path; #7 zero-copy `uncons` views (header bit 63 = view,
`{len|VIEW, backing-ptr}`; copy-out/deep-copy materialize, so nothing dangles across a reset);
**#9 the `+` overload fix** — `+` with two unresolved operands no longer eagerly defaults to Int;
it emits a provisional `AddInt` carrying the operand type var, keeps that var monomorphic (excluded
from generalization, compared by union-find representative), and a post-inference pass patches it to
`ConcatStr`/`AddFloat` or defaults it to Int. `acc + x` string/float reductions now compile and run
with no `"" +` hint; oversaturated-call detection and REPL type display preserved; **#8 the output
`join`** — `Ashes.String.join` (tail-recursive divide-and-conquer, O(total·log K)) replaced the
O(K²) growing-string render; `brc` now collects entries with `Map.foldLeft` + `List.reverse` + `join`.
Halved the 10k-row runtime (was ~45 % of it).

**All resolved (2026-07-01).** Streaming file IO (#1), the hot-loop arena leak (#2), and the
in-place-reuse work are complete; brc folds the whole file in a single `Ashes.File.readLine` loop
with in-place `Map.set` reuse and is constant-memory. The general in-place-reuse follow-up (the
nested-re-entry deep-copy) is tracked as `CO-2` in
[docs/future/COMPILER_OPTIMIZATION.md](../../docs/future/COMPILER_OPTIMIZATION.md) (since landed — see
the 2026-07-02 pass below).

## Performance pass (2026-07-02) — constant-memory to 100M rows

Re-benchmarked the current `brc.ash` (`-O2`; `hyperfine` warm timing, peak RSS from
`/usr/bin/time -v`) after the move/linearity reuse-copy-elision milestone (`CO-2`, item (ii) above)
and the follow-up TCO reuse-correctness fixes (`CO-8` back-edge reset stability, `CO-9` param-slot
mapping) all landed:

| Rows | Time | Peak RSS | Stations |
|------|------|----------|----------|
| 100 000 | 147 ms ± 3 ms | 45 MB | 37 191 |
| 1 000 000 | 1.32 s ± 0.01 s | 50 MB | 41 343 |
| 10 000 000 | 12.96 s ± 0.05 s | 50 MB | 41 343 |
| 100 000 000 | 2 m 10 s (single run) | 50 MB | 41 343 |

**Memory is flat (~45–50 MB) across a 1000× range in rows** — the residual leaks the earlier passes
chased are gone, and brc stays constant-memory well past the 10M where the earliest table OOM'd. **Time
is linear** (~10× per 10× rows, ≈1.3 µs/row), so the full 1e9-row / 15.5 GB input should finish in
~21–22 min at the same ~50 MB.

Correctness re-verified: the 41 343 stations exactly match `cut -d';' -f1 measurements | sort -u`. The
leading empty-name entry (`{=…`) is faithful, not a bug — the generator emits some malformed
empty-station lines (23 in the 1M subset; none in the 100k subset, which is why it has no empty entry
and only 37 191 stations), and brc aggregates them under `""`. Non-ASCII names (`A Coruña`,
`’s-Hertogenbosch`) sort by byte order (#4).

Net: #1–#9 are closed and brc runs the challenge correctly and constant-memory at every size tested.
The general reuse follow-up (`CO-2`) deferred in the 2026-07-01 pass has since landed, and the
`CO-8`/`CO-9` reuse-correctness fixes cause no regression here.

**Why still ~50–1000× off the fastest cross-language entries (analysis + one experiment).** Rewriting
brc to use `Ashes.HashMap` (theoretically O(1)-ish, integer-hash compares) instead of the ordered
`Ashes.Map` made it **2.6× slower and 200× larger (10.1 GB @ 1M)** — because the in-place-reuse
specialization is wired only to `Map.set`, so `HashMap.set` allocates a fresh tree per row *and*
re-hashes the whole key twice. So the program is already near the frontier of what the current
compiler/stdlib allow; the gap was compiler/stdlib/runtime work, not the `.ash`. The concrete,
actionable pieces became `CO-10`…`CO-14` and have all **landed** (see the *Completed Work* record in
[docs/future/COMPILER_OPTIMIZATION.md](../../docs/future/COMPILER_OPTIMIZATION.md)): a `u8`→`Int`
widening for byte-level integer parsing (`CO-11`), SIMD `memchr` scan (`CO-13`), zero-copy `mmap` input
(`CO-12`), a data-parallel chunked fold + loop-invariant reset-safety making it constant-memory
(`CO-14` / `CO-10`) — together the parallel `brc` now runs the full 1e9-row challenge in ~2m36s. A
smaller reuse-*eligibility* generalization (`HashMap.set`) remains — tracked as `CO-15` in
[docs/future/COMPILER_OPTIMIZATION.md](../../docs/future/COMPILER_OPTIMIZATION.md).

## Performance pass (2026-07-02) — data-parallel `brc` reaches the full 1e9-row challenge

`brc_parallel.ash` shards the file across cores: `Ashes.File.mmap` (zero-copy read) → newline-aligned
`(bytes, lo, hi)` chunks → per-core `Ashes.Parallel.reduce`/`both` fold → merge the partial maps. The
loop-invariant reset-safety fix (`CO-10`) makes each worker's fold constant-memory, so the parallel path
finally scales past ~15M rows (before it, the per-worker maps leaked and 1e9 needed ~120 GB — OOM).
Byte-identical output to the sequential `brc`. Measured with `hyperfine` (warm cache) on a 32-core Linux
x64 box; the parallel variant caps at 8 workers.

| Rows | Sequential `brc.ash` | Parallel `brc_parallel.ash` |
|------|----------------------|-----------------------------|
| 10,000,000 | 12.8 s / 50 MB | 2.6 s / 1.6 GB |
| 100,000,000 | 2 m 07 s / 50 MB | 16.9 s / 2.9 GB |
| **1,000,000,000** (full challenge) | ~21 min / 50 MB | **2 m 36 s** / 15.9 GB — 41,343 stations, ≈6.4 M rows/s |

**The 1BRC ultimate goal — 1e9 rows — now runs in 2 m 36 s at 15.9 GB, correct**, on a commodity 60 GB
box, in a pure/immutable/no-GC functional language compiled to a standalone native binary. The tradeoff:
the sequential fold is constant-memory (~50 MB at any size) but single-core and scales anywhere; the
parallel fold is ~5–8× faster (near the 8-worker cap) but holds the mapped file plus per-worker maps in
RAM. See [`README.md`](README.md) for how to reproduce.

---

## #1 — No buffered or streaming file IO  — ✅ FIXED

**Fix, part 1 (stdin):** `readLine`/`readExact` now read through a refillable 64 KB
module-global buffer (`EmitReadLine`/`EmitReadExact`, `LlvmCodegenBuiltins.cs`;
`EmitWindowsReadBlock`, `LlvmCodegenPlatform.cs`) instead of one `read()` syscall per
byte. `readExact` drains the shared buffer first so `readLine`+`readExact` interleave
correctly (`Ashes.Rpc` framing — `tests/stdlib_rpc.ash`). With #1b, streaming a file
through stdin is crash-free *and* fast (1e6 lines in ~15 ms).

**Fix, part 2 (file):** new chunked file API — `Ashes.File.open(path) : Result(Str,
FileHandle)`, `readChunk(handle)(maxBytes) : Result(Str, Str)`, `close(handle)`.
`FileHandle` is a resource type (auto-closed on scope exit, like `Socket`/`Process`).
A 13 GB file can now be streamed in fixed-size chunks instead of one `readText`
allocation. Cross-platform (Linux x64/arm64 via `open`/`openat`, Windows via
`CreateFileA`/`ReadFile`/`CloseHandle`). Test: `tests/file_chunked_read.ash`.

(Minor follow-up: the *compile-time* use-after-close check only tracks `let`-bound
resources, not match-arm-bound ones; a `FileHandle` always comes out of a `Result` via
`match`, so reads after an explicit `close` aren't flagged at compile time — they fail
gracefully at runtime with an `Error`.) Original finding follows.



**1BRC needs:** to stream ~13 GB without loading it into memory, ideally reading
large buffers per syscall.

**Ashes has:** only `Ashes.File.readText(path)` — reads the *entire* file into one
heap `Str` (`EmitLinuxFileReadText`, `src/Ashes.Backend/Llvm/LlvmCodegenBuiltins.cs:539`).
For 13 GB that is a single ~13 GB arena allocation: a non-starter.

The streaming alternative is piping the file through stdin and looping on
`Ashes.IO.readLine()` (what `brc.ash` does). But `EmitReadLine`
(`src/Ashes.Backend/Llvm/LlvmCodegenBuiltins.cs:8`) issues a `read(fd=0, &byte, 1)`
syscall **per byte** (the length argument is the constant `1`, around `:42`). There
is no buffering anywhere. For ~13 GB that is ~13 billion syscalls — the dominant
cost before any computation happens. (Per-line cap is 64 KiB,
`src/Ashes.Backend/Llvm/LlvmCodegen.cs:12`; fine for 1BRC line lengths.)

**Impact:** even a correct run is bottlenecked on per-byte syscalls; there is no
API to read a megabyte at a time.

## #1b — Compiler bug: a `readLine` loop segfaults after a few hundred lines  — ✅ FIXED

**Fix:** `readLine`'s 64 KB line buffer and scratch slots are now reused module
globals (`.bss`) instead of a per-call stack `alloca`
(`src/Ashes.Backend/Llvm/LlvmCodegenBuiltins.cs` `EmitReadLine`/`ReadLineScratchGlobal`,
`LlvmTargetSetup.cs` `GetOrAddNamedGlobal`). A `readLine` loop now costs zero stack
per iteration and runs to millions of lines. Regression test:
`tests/regress_readline_loop_depth.ash` (5,000-line loop). `io_echo_all.ash` is also
fixed. Original finding below.

**Discovered here.** The streaming idiom above doesn't merely run slowly — it
**crashes**. A tail loop whose body calls `Ashes.IO.readLine` segfaults (SIGSEGV,
exit 139) after a few hundred iterations:

- `brc.ash`'s original `readLine` loop: fine on a 6-line fixture, dies at ~300 lines.
- The **shipped** `examples/io_echo_all.ash` — the canonical
  `match Ashes.IO.readLine(Unit) with None -> Unit | Some(line) -> … loop(Unit)` —
  **also segfaults at 500 lines**.
- A minimal `readLine` counting loop with no Map, no strings, no tuples crashes too.

**Root cause (confirmed):** it is a stack overflow, and the crash threshold scales
*linearly* with the stack limit — ~118 lines at the default 8 MB, ~235 at 16 MB,
~508 at 32 MB, i.e. **~64 KB of stack consumed per line**. That number is the tell:
`EmitReadLine` (`src/Ashes.Backend/Llvm/LlvmCodegenBuiltins.cs:11`) does
`BuildAlloca` of a **64 KB line buffer (`InputBufSize`) on every call**. When
`readLine` is invoked inside a TCO'd loop — which the lowering turns into a *single*
stack frame that jumps backward instead of returning — those per-call `alloca`s are
never reclaimed, so the stack grows ~64 KB per iteration and overflows after
`stack_size / 64 KB` lines. (It is **not** the uncommitted `Lowering.cs` work in the
tree; that diff touches nothing IO/TCO-related.)

A fix would hoist that `alloca` to the function entry block (allocate once, reuse),
use a module-global scratch buffer, or bracket the read in `stacksave`/
`stackrestore`. Until then, a plain `readLine` loop is unusable past a few hundred
lines, and even the shipped `io_echo_all.ash` example is broken at depth.

A pure-scrutinee `match` loop (e.g. over `Ashes.Text.uncons`) allocates nothing on
the stack per iteration and is correctly TCO'd, surviving millions of iterations —
so the fault is specific to looping over `readLine`. `brc.ash` therefore reads the
whole file with `Ashes.File.readText` and splits it with `uncons` instead — which
re-incurs #1 (one giant allocation at full scale) as the price of not crashing.

**Impact:** the two ways to get bulk input into an Ashes program are both broken for
1BRC — `readText` can't hold 13 GB (#1), and `readLine` crashes at depth (#1b).

---

## #2 — The hot loop leaks: the arena never reclaims per-row garbage (fatal)

**1BRC needs:** to process a billion rows in O(1) live memory.

**Ashes is:** non-GC. Memory is a chunked bump arena (`docs/ARCHITECTURE.md:241`+);
`Drop` is a no-op for ordinary heap values. Memory is reclaimed only at scope exits
and at TCO back-edges, via an arena watermark reset.

TCO itself works (see "What Ashes gets right"), so the fold loop does not grow the
stack. But the per-iteration arena reset on the back-edge only fires when **every**
back-edge argument is reclaimable:

- `CanArenaReset` (`src/Ashes.Semantics/Lowering.Ownership.cs:247`) is true only for
  `Int/UInt/Float/Bool`.
- Heap arguments need copy-out, gated by `GetTcoCopyOutKind`
  (`Lowering.Ownership.cs:443`) and, for ADTs, `CanCopyOutAdt`
  (`Lowering.Ownership.cs:330`), which requires **all constructors to have the same
  arity** and **all fields to be copy types**.

The accumulator here is an `Ashes.Map` (`MapTree = Empty | Node(Int, MapTree, K, V,
MapTree)`). It fails `CanCopyOutAdt` on both counts: `Empty` is arity 0 vs `Node`
arity 5, and `Node` holds pointer fields. So `GetTcoCopyOutKind` returns `None` and
the back-edge takes the "no arena reset" branch (`Lowering.cs:2785`).

**Impact:** with a `Map` accumulator the loop keeps the stack flat but accumulates
*every iteration's allocations forever* — the O(log K) new `Node`s per `Map.set`,
the line string, every `substring`/`uncons`/`parseInt` temporary. With no GC this
grows monotonically and OOMs (`panic("failed to allocate heap memory from OS")`)
long before a billion rows. No scalar accumulator can hold per-station state, so
there is no way to keep the loop in the arena-resettable regime.

## #3 — No mutable or hash-based accumulator  — ✅ FIXED (for the fold)

**Fix (the hashing half):** added `Ashes.Bytes.hash` (64-bit FNV-1a intrinsic) and a
new shipped module `Ashes.HashMap` (`lib/Ashes/HashMap.ash`) — a persistent map keyed
by `Str` that needs **no caller-supplied ordering** and navigates by cheap integer
hash comparison instead of per-node string compare. Tests: `tests/bytes_hash.ash`,
`tests/stdlib_hashmap.ash`. **Still open:** it is persistent, so it does not remove the
per-update allocation/leak (#2), and it is *unsorted* (hash order) — 1BRC's sorted
output still wants `Ashes.Map` + `Ashes.String.compare`. True O(1), leak-free updates
need an owned mutable table (the ownership milestone, #2(c)). Original finding follows.



**1BRC needs:** an O(1) per-row update — canonically a hash map of mutable
accumulators, often sharded across threads.

**Ashes has:** no mutation, no hash map, no mutable cell anywhere. The only keyed
container is `Ashes.Map` (`lib/Ashes/Map.ash`), a persistent AVL tree whose `set`
(`Map.ash:79`) copies every node on the root-to-leaf path — O(log K) fresh `Node`s
(≥48 bytes each) per update. `Ashes.Array` (`lib/Ashes/Array.ash`) is *also* a
persistent tree, not a flat buffer (`docs/STANDARD_LIBRARY.md:138`).

**Impact:** with K ≈ 4,415 stations, ~12 node allocations × 1e9 rows ≈ **~12 billion
`Node` allocations**, every read being read-modify-write (a `get` *and* a `set`).
There is no O(1) update available.

## #4 — A correct String ordering is not constructible  — ✅ FIXED

**Fix:** added the intrinsic `Ashes.Bytes.fromText(text) : Bytes` (an O(1) identity
reinterpret — `Str` and `Bytes` share the `[len][bytes]` layout) and the stdlib
helper `Ashes.String.compare(a)(b) : Int` (`lib/Ashes/String.ash`), which compares
UTF-8 bytes. Byte-lexicographic order equals Unicode codepoint order, so `compare`
is a correct total order over *all* strings and plugs straight into
`Ashes.Map`/`Ashes.Array`. `brc.ash` now uses it (the hand-rolled ASCII comparator
is gone) and multibyte names sort correctly and never merge. Tests:
`tests/bytes_from_text.ash`, `tests/stdlib_string_compare.ash`. Original finding below.



**1BRC needs:** to group by station name and print sorted by name. `Ashes.Map`
requires a caller-supplied total order `compare : K -> K -> Int`
(`docs/STANDARD_LIBRARY.md:162`).

**Ashes has:** string **equality only**. `==`/`!=` lower to `CmpStrEq`/`CmpStrNe`
(`src/Ashes.Semantics/Lowering.cs:1553`); the relational operators `<`/`>`/`<=`/`>=`
route through `LowerNumericComparisonOp` (`Lowering.Ownership.cs`), which accepts
only `Int/Float/UInt` and rejects `Str`. To build an ordering you'd need an integer
rank per character — but there is **no `ord`/codepoint→Int builtin**, and **no
String→Bytes** conversion to reach raw bytes. `Ashes.Text.uncons`
(`LlvmCodegenBuiltins.cs:121`) yields a single-codepoint *substring*, comparable
only with `==`.

`brc.ash` works around this with a hand-enumerated `charOrd` `match` over ASCII (so
the output sort is canonical for ASCII names). For the real dataset's 4,415
**multibyte** names (`Kāshān`, `Châu Đốc`, `Sŏngnam`, …) every non-ASCII codepoint
collapses to one fallback rank. That is not a total order: two distinct non-ASCII
names can compare equal, which makes `Ashes.Map` **silently merge distinct
stations** — a correctness blocker, not just a sort-order nicety.

**Impact:** the canonical, idiomatic solution (Map keyed by station name) cannot be
made correct for the real input without language/stdlib additions.

---

## #5 — No data parallelism for CPU work  — ✅ FIXED

**1BRC needs:** to shard the file across cores. `Ashes.Async` is for IO/networking
tasks (`Task`/`await`), not data-parallel CPU work.

**Fix:** `Ashes.Parallel.both` is now a genuinely parallel fork/join of two pure thunks
on all three targets (per-thread bump arenas + worker threads + deep-copy-on-join,
deterministic and memory-bounded) — see the structured-parallelism entry in
[docs/future/COMPILER_OPTIMIZATION.md](../../docs/future/COMPILER_OPTIMIZATION.md), and full
data-parallel `map`/`reduce` (`CO-1`) landed. brc is now sharded across cores: `brc_parallel.ash`
splits the file into per-core chunks and folds each on a worker (`CO-14`), constant-memory per worker
(`CO-10`) — running the full 1e9-row challenge in ~2m36s.

## #6 — Compiler bug: a shipped-module reference inside a function body fails to resolve  — ✅ FIXED

**Fix:** `FreeVars` (`src/Ashes.Semantics/Lowering.cs`) now treats a `QualifiedVar`
that resolves to a stitched shipped/user-module binding (`Ashes_String_indexOf`, …)
as a captured free variable, while still skipping intrinsics (`Ashes.IO.print`,
`Ashes.Text.uncons`). So `Ashes.Map`/`Ashes.String` calls now work directly inside
lambdas and `let recursive` bodies — the alias workaround is no longer needed (and
`brc.ash` no longer uses it). Regression test:
`tests/regress_module_in_lambda_body.ash`. Original finding below.

**Discovered here.** A qualified reference to a *shipped helper* module
(`Ashes.Map`, `Ashes.String`, `Ashes.List`, … — the `.ash` modules under
`lib/Ashes/`, as opposed to intrinsic modules like `Ashes.IO`/`Ashes.Text`) is only
resolved when it appears in a **plain-value binding or the trailing expression**. If
it appears inside a **function body** (a `let f x = …` lambda, or any `let recursive`
body) it fails with `Unknown module 'Ashes.String'`
(`src/Ashes.Semantics/Lowering.cs:990`). The import stitcher's reference collection
(`CollectReferencedNames` in `src/Ashes.Semantics/ProjectSupport.cs`) does not reach
into function bodies, so the module source is never stitched in.

Minimal repro (fails):

```ash
import Ashes.String
import Ashes.IO
let recursive f n =
    if n <= 0
    then 0
    else Ashes.String.indexOf("abc")("b")   // Unknown module 'Ashes.String'
in Ashes.IO.print(f(1))
```

Workaround (compiles): alias the module function to a local in a plain `let`
binding, then call the alias from inside the function:

```ash
import Ashes.String
import Ashes.IO
let strIndexOf = Ashes.String.indexOf       // plain-value binding: resolved + stitched
in
    let recursive f n =
        if n <= 0
        then 0
        else strIndexOf("abc")("b")          // local, not a module ref: fine
    in Ashes.IO.print(f(1))
```

This is why `brc.ash` aliases `Ashes.Map.*`/`Ashes.String.*` up front and only ever
calls the aliases inside `compareStr`/`loop`/the helper functions. (Note: flat
top-level `let` declarations and the nested `let … in` pyramid behave identically
here — the deciding factor is *function body vs plain value*, not the declaration
style.)

---

## What Ashes gets right

For balance: the recursion story is genuinely solid *for pure loops*.
Accumulator-passing tail self-recursion is compiled to a real loop with a backward
`Jump`, no stack growth (`LowerLetRec`, `src/Ashes.Semantics/Lowering.cs:2030`;
`HasTailSelfCalls`, `:2189`; exercised at depth by
`tests/tco_self_recursion_large.ash`). Eligible same-arity, same-type mutual
recursion is collapsed into one dispatch loop too (`TryLowerMutualRecursionTco`,
`Lowering.cs:377`). A `match` loop over the pure `Ashes.Text.uncons` builtin runs to
millions of iterations without growing the stack — which is exactly why `brc.ash`
splits the file body with `uncons` rather than `readLine` (#1b). `uncons` also
correctly decodes the file's multibyte UTF-8. The billion-row fold would not
overflow the stack — it is everything *around* the loop (IO, accumulation, ordering,
and the arena) that defeats the challenge.

---

## Reproducing

```bash
# Build
dotnet run --project src/Ashes.Cli -- compile challenges/brc.ash -o challenges/brc

# Correctness on a tiny fixture (the file path is the argument)
printf 'Hamburg;12.0\nHamburg;14.0\nBulawayo;8.9\nHamburg;10.0\nPalembang;-5.3\n' \
  > /tmp/brc-fixture.txt
./challenges/brc /tmp/brc-fixture.txt
# {Bulawayo=8.9/8.9/8.9, Hamburg=10.0/12.0/14.0, Palembang=-5.3/-5.3/-5.3}

# Scale: fetch data, subset to a size that completes, then crank ROWS up to OOM
ROWS=1000000 bash challenges/download.sh
./challenges/brc challenges/measurements.txt
```

See [`README.md`](README.md) for the full workflow.
