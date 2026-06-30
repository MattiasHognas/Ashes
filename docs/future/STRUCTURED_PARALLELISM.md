# Structured Deterministic Parallelism — Design

Status: **Implemented on linux-x64 (2026-06-30).** `Ashes.Parallel.both` forks its right thunk
to a `clone`/`futex` worker thread with its own GS-segment per-thread arena and deep-copies the
result back on join; deterministic, memory-bounded, ~2x measured speedup. Implementation:
`src/Ashes.Backend/Llvm/LlvmCodegenParallel.cs` + `LowerParallelBoth`. **Deviations from this
design:** (1) `both` is parallel only at a *concrete* result type — under uniform value
representation, deep-copy-on-join needs the concrete type, which is unavailable for an abstract
result; abstract/polymorphic uses run sequentially (a correct fallback). (2) For the same reason
`map`/`reduce` (§1) stay **sequential** — their element type is abstract inside the polymorphic
body, so they can't route through the parallel `both`; data parallelism is via direct concrete
`both` (shard-and-merge). Full data-parallel `map`/`reduce` would need monomorphization.
(3) arm64/win-x64 run `both` inline. (4) Bounded by a `lock xadd` worker counter (cap 8) rather
than a persistent pool.

This specs CPU-bound parallelism for Ashes that stays inside the language's Ground
Rules: values are immutable, all APIs are pure, there is no GC, and cleanup is
deterministic. It is **not** a general threads/shared-memory model — it is structured
parallel evaluation of **pure** functions whose results are **deterministic** (identical
to the sequential evaluation, only faster).

## 1. Surface API (`Ashes.Parallel`)

- **Core primitive — fork/join of two pure tasks:**
  `both(left)(right) : ((Unit -> A) -> ((Unit -> B) -> (A, B)))`
  Evaluates `left(Unit)` and `right(Unit)` potentially on different OS threads and
  returns the tuple of their results. Semantically identical to
  `(left(Unit), right(Unit))` — purity guarantees order-independence, so the result is
  deterministic regardless of which finishes first.

- **Derived (pure Ashes, built on `both`) — data parallelism:**
  `map(f)(list) : List(B)` for `f : A -> B`. Splits the list, recurses with `both` on
  the halves down to a grain-size threshold, then `Ashes.List.map`s sequentially per
  leaf. Result order is identical to a sequential `map`.
  `reduce(combine)(identity)(f)(list) : B` — parallel map-then-fold for associative
  `combine` (the 1BRC shape: shard → per-shard map → merge).

Only `both` is a compiler intrinsic; `map`/`reduce` ship as ordinary `lib/Ashes/Parallel.ash`.

## 2. Determinism & purity

Every Ashes function is already pure (Ground Rule 5), so a parallel task cannot observe
or mutate shared state. `both` therefore always returns the same `(A, B)` it would
sequentially. There is **no** user-visible nondeterminism, no locks, no atomics in user
code. IO-performing tasks (which run in `async`, not here) are out of scope — `both` is
for pure CPU work.

## 3. Memory model — per-thread arenas + result copy-out (the hard part)

Today the bump arena state (`__ashes_heap_cursor`, `__ashes_heap_end`) lives in module
globals shared by the single thread. For parallelism:

1. **Thread-local arenas.** The arena cursor/end globals become `thread_local`. Each
   worker thread `mmap`s and bump-allocates in its **own** arena — no shared cursor, no
   atomics, no data race. (The main thread keeps today's behaviour.)
2. **Result copy-out on join.** A task allocates its result `B` in its own arena, which
   is torn down when the task ends. So on join, the result is **deep-copied** from the
   worker arena into the *parent* thread's arena before the worker arena is freed. This
   needs a generic, type-directed deep-copy emitter (`EmitDeepCopyInto(value, type)`)
   that walks strings/lists/tuples/ADTs/closures. **Note:** this same deep-copy routine
   is exactly what #2's arena-compaction fallback needs — the two milestones share it.
3. **Worker pool.** A small fixed pool (size = CPU count, capped) created lazily on first
   `both`; `both` enqueues the `right` task and runs `left` inline, then joins. Recursion
   through `map`/`reduce` fans out naturally; when the pool is saturated, `both` runs both
   sides inline (graceful degradation, still correct).

## 4. Threading primitives (per target, all three up front)

- **linux-x64 / linux-arm64:** `clone(2)` (or raw `pthread_create` if we link libc;
  current binaries are nostdlib, so raw `clone` + a `futex`-based join is the likely
  path) with a fresh `mmap`'d stack per worker. Join via `futex` wake/wait on a per-task
  done-word. (See `project_arm64_syscall_quirks` for `clone`/`SIGCHLD` gotchas.)
- **win-x64:** `CreateThread` + `WaitForSingleObject` (imports added to the PE linker,
  like the existing `CreateFileA`/`ReadFile` IAT entries).

## 5. Implementation plan

1. Spec sign-off (this doc) + `LANGUAGE_SPEC.md` section on parallel evaluation semantics.
2. Make arena globals `thread_local`; per-thread lazy arena init. Verify single-thread
   behaviour unchanged (all existing tests stay green).
3. `EmitDeepCopyInto(value, type)` deep-copy emitter (shared with #2).
4. Worker pool + `both` intrinsic (the 6-file intrinsic pipeline + backend thread
   spawn/join), linux-x64 first within this step, then arm64 + win-x64.
5. `lib/Ashes/Parallel.ash`: `map`, `reduce` on top of `both`.
6. Tests: determinism (parallel == sequential) at scale; a parallel 1BRC variant in
   `challenges/` (shard the file, per-shard `Ashes.Map`, merge).

## 6. Risks / open points

- nostdlib `clone`+`futex` is fiddly; if it proves too brittle we fall back to linking a
  minimal libc for threads (a build-system change worth calling out).
- Deep-copy cost on join: large results copied across arenas. Acceptable for
  shard-and-merge (results are small summaries); documented for callers.
- Grain size for `map`/`reduce` default; expose as an optional parameter.
- Stack size per worker (fixed `mmap`, e.g. 8 MB) — configurable later.
