# 1BRC in Ashes — flaws found

The [One Billion Row Challenge](https://github.com/gunnarmorling/1brc) reads ~1e9
`Station;Temperature` lines and prints per-station `min/mean/max` sorted by name.
It exercises exactly the things Ashes is weakest at: bulk IO, mutable/hashed
accumulation, ordering, and a long-running hot loop under a non-GC arena.

[`brc.ash`](brc.ash) is a *correct* implementation for ASCII station names at a
modest row count (verified against a hand-computed fixture). It is **not** viable
for the real input. This document is the point of the exercise: what broke, where,
and why. File/line references are into the compiler at the time of writing.

Some findings are **compiler bugs discovered while writing this program**; the rest
are structural limitations of the language / stdlib / memory model.

## Status

| # | Flaw | Status |
|---|------|--------|
| #1 | No buffered/streaming file IO | **stdin half FIXED**; chunked file API open |
| #1b | `readLine` loop segfaults at depth | **FIXED** |
| #2 | Hot-loop arena leak (Map accumulator) | open — ownership milestone |
| #3 | No hash/mutable O(1) accumulator | **hashing+HashMap FIXED**; O(1)/leak-free open |
| #4 | No constructible String ordering | **FIXED** |
| #5 | No data parallelism | open — design-level milestone |
| #6 | Shipped-module ref unresolved in a function body | **FIXED** |

Each section below records the original finding and, where fixed, what changed;
each open section ends with an actionable **Task** breakdown.

---

## #1 — No buffered or streaming file IO  — ⚙️ PARTIALLY FIXED

**Fix (the stdin half):** `readLine`/`readExact` now read through a refillable 64 KB
module-global buffer (`EmitReadLine`/`EmitReadExact`, `LlvmCodegenBuiltins.cs`;
`EmitWindowsReadBlock`, `LlvmCodegenPlatform.cs`) instead of one `read()` syscall per
byte. `readExact` drains the shared buffer first so `readLine`+`readExact` interleave
correctly (`Ashes.Rpc` framing verified by `tests/stdlib_rpc.ash`). Combined with #1b,
streaming a file through stdin is now crash-free *and* fast (1e6 lines in ~15 ms).
**Still open:** the whole-file `readText` path (one big allocation) — a chunked file
API (`open`/`readChunk`/`close`) is the remaining piece; see the Task below. Original
finding follows.



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

### Task

Two independent pieces:

- **(a) Buffer `readLine`'s reads.** Replace the `read(fd, &byte, 1)`-per-byte loop
  in `EmitReadLine` (`LlvmCodegenBuiltins.cs:8`) with a refillable buffer: add module
  globals `__ashes_stdin_buf` (e.g. 64 KB), `__ashes_stdin_pos`, `__ashes_stdin_len`
  (mirror the `ReadLineScratchGlobal` pattern just added for #1b); a "next byte"
  helper refills via one block `read`/`ReadFile` when `pos == len`. Must keep Linux
  (syscall) and Windows (`ReadFile`) paths working; `readExact` and
  `Process.readStdoutLine` share the per-byte pattern and should move to the same
  buffer. Test: a large piped-stdin program, plus byte-exactness on CRLF/EOF edges.
- **(b) Chunked file reading.** Add a streaming file API so a 13 GB file needn't be
  one `readText` allocation: e.g. `Ashes.File.open(path) : Result(Str, FileHandle)`,
  `Ashes.File.readChunk(handle)(maxBytes) : Result(Str, Str)`, `Ashes.File.close`.
  `FileHandle` is a resource type (auto-closed, like `Process`/`Socket`). New
  intrinsics across the 6-file pipeline + backend `open`/`read`/`close` syscalls per
  target. Spec: `STANDARD_LIBRARY.md`, `COMPILER_CLI_SPEC` n/a, `ARCHITECTURE.md`
  (resource types). Bounded but substantial; (a) is the cheaper, higher-leverage half.

---

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

### Task

This is ownership/codegen work, the largest of the bug-class items and the riskiest.
The back-edge can only reset the arena when every carried value is reclaimable, and a
persistent tree accumulator that *shares structure with the previous iteration* is
fundamentally not reset-safe (resetting frees nodes the new tree still points at).
Options, roughly increasing in ambition:

- **(a) Per-iteration compaction / copy-out for arbitrary ADTs.** Generalize
  `CanCopyOutAdt` (`Lowering.Ownership.cs:330`) to deep-copy a mismatched-arity,
  pointer-bearing ADT (like `MapTree`) below the watermark before the reset, then
  reset. Removes the leak but makes each iteration O(tree size) — too slow for 1BRC,
  though it bounds memory. Needs a generic deep-copy emitter keyed on the ADT layout.
- **(b) A generational/region GC or refcounting for the persistent structure.** Out
  of scope for the "no GC/RC" design; explicitly off the roadmap.
- **(c) Linear/owned mutable structure** (ties into #3): if the accumulator is a
  uniquely-owned mutable map updated in place, there is no per-iteration garbage to
  reclaim. This is the ownership/borrowing roadmap (`Lowering.Ownership.cs`,
  `docs/future/FUTURE_FEATURES.md`) and the only path that is both correct and fast.

Recommendation: do **not** hack (a) in; pursue (c) as a real milestone. Spec the
ownership model first (`LANGUAGE_SPEC.md`).

---

## #3 — No mutable or hash-based accumulator  — ⚙️ PARTIALLY FIXED

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

### Task

- **Hashing primitive.** Add `Ashes.Bytes.hash(bytes) : Int` (e.g. FNV-1a/xxHash) as
  an intrinsic. With #4's `Ashes.Bytes.fromText` this gives string hashing today. Small,
  self-contained (6-file intrinsic pipeline + a backend hash loop).
- **Map data structure.** Two routes:
  - *Persistent HAMT* in pure Ashes (`lib/Ashes/HashMap.ash`): O(log32 K) ≈ effectively
    O(1) lookups, no compare function needed, immutable. Buildable today on top of the
    hash primitive. Does **not** fix the allocation/leak volume (#2) — still allocates
    per update — but removes the per-row string-compare cost and the need for an
    ordering. Bounded, no compiler change beyond the hash intrinsic.
  - *Mutable open-addressed table*: true O(1), no per-update garbage, but needs
    owned/mutable arrays, which the language lacks (ties into #2(c) and #3's mutability
    gap). Milestone-scale.

Recommendation: ship the hash intrinsic + persistent HAMT now (real, bounded win);
the mutable table waits on the ownership milestone.

---

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

## #5 — No data parallelism for CPU work

**1BRC needs:** to shard the file across cores. `Ashes.Async` exists but is for IO/
networking tasks (`Task`/`await`), not data-parallel CPU work; there are no threads,
no SIMD, no mmap-slice story.

**Impact:** the whole design is a single immutable fold; the usual 10–100× win from
sharded mutable maps across threads is inexpressible.

### Task — design-level, conflicts with language invariants

This is not a bug fix; it is a major architectural decision that runs against the
documented design ("pure, immutable, strictly evaluated, recursion-based", and "no GC
and no runtime"). Real data parallelism needs a threading runtime (thread spawn/join,
a work-stealing scheduler or at least OS threads), a memory model for sharing across
threads (the bump arena is a single-threaded global), and a safe way to merge
per-shard results. None of that exists, and adding it touches the language's core
guarantees. A staged path *if* pursued as a milestone:

1. Decide the concurrency model (structured parallelism over pure functions, e.g. a
   `parMap`/`fork`-`join` over independent sub-computations) and write it into
   `LANGUAGE_SPEC.md` / `docs/future/FUTURE_FEATURES.md` first.
2. Make the arena thread-safe or per-thread (per-thread arenas + a merge step).
3. Add the runtime primitives (clone/futex on Linux, threads on Windows) as intrinsics.

Recommendation: treat as a roadmap item requiring a design decision, not a fix to land
opportunistically. Flagged here for completeness; deliberately **not** implemented.

---

## #6 — Compiler bug: a shipped-module reference inside a function body fails to resolve  — ✅ FIXED

**Fix:** `FreeVars` (`src/Ashes.Semantics/Lowering.cs`) now treats a `QualifiedVar`
that resolves to a stitched shipped/user-module binding (`Ashes_String_indexOf`, …)
as a captured free variable, while still skipping intrinsics (`Ashes.IO.print`,
`Ashes.Text.uncons`). So `Ashes.Map`/`Ashes.String` calls now work directly inside
lambdas and `let rec` bodies — the alias workaround is no longer needed (and
`brc.ash` no longer uses it). Regression test:
`tests/regress_module_in_lambda_body.ash`. Original finding below.

**Discovered here.** A qualified reference to a *shipped helper* module
(`Ashes.Map`, `Ashes.String`, `Ashes.List`, … — the `.ash` modules under
`lib/Ashes/`, as opposed to intrinsic modules like `Ashes.IO`/`Ashes.Text`) is only
resolved when it appears in a **plain-value binding or the trailing expression**. If
it appears inside a **function body** (a `let f x = …` lambda, or any `let rec`
body) it fails with `Unknown module 'Ashes.String'`
(`src/Ashes.Semantics/Lowering.cs:990`). The import stitcher's reference collection
(`CollectReferencedNames` in `src/Ashes.Semantics/ProjectSupport.cs`) does not reach
into function bodies, so the module source is never stitched in.

Minimal repro (fails):

```ash
import Ashes.String
import Ashes.IO
let rec f n =
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
    let rec f n =
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
