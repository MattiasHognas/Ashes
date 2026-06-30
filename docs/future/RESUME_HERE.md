# Resume Here — Parallelism (#5) & In-Place Reuse (#2)

**One-stop handoff.** Two milestones are partly built; this is the single place that
says what's done, what's left, and exactly where to start. Point me at this file.

> **Fastest way to resume:** "Read docs/future/RESUME_HERE.md and continue with the
> next step." The next concrete step is in **§3**.

Full designs (already signed off): `STRUCTURED_PARALLELISM.md` (#5),
`REUSE_ANALYSIS.md` (#2). User-facing flaw context: `../../challenges/FLAWS.md`.

---

## 1. DONE (landed, tested, tree green: 1306 unit + 348 e2e, `dotnet format` clean)

- **Shared deep-copy foundation** — the hard piece both milestones depend on.
  - `EmitDeepCopy(temp, type)` + `TrySynthesizeAdtCopier` + `CopyFieldInsideCopier` in
    `src/Ashes.Semantics/Lowering.Ownership.cs` (~line 452+).
  - Handles scalars, `Str`/`Bytes`, tuples, lists, closures, **and recursive
    multi-constructor ADTs** (`Map`/`HashMap`/`Maybe`) via a synthesized, cached,
    self-referential copier closure (tag-level; `env[0]` = self; resource types excluded;
    mutual-recursion cycle guard).
  - Exposed/tested via the intrinsic `Ashes.Internal.deepCopy : ∀a. a→a`
    (`tests/internal_deepcopy.ash`, `tests/internal_deepcopy_recursive.ash`).
- **#5 API layer** — `lib/Ashes/Parallel.ash` (`both`/`map`/`reduce`), deterministic,
  pure Ashes, **sequential for now**. Embedded in `Ashes.Semantics.csproj`, registered in
  `BuiltinRegistry` (`Ashes.Parallel`). Test: `tests/stdlib_parallel.ash`.

## 2. NOT DONE (the high-risk runtime cores — left deliberately, not half-built)

Both were left unbuilt because they're unsafe to write unattended (silent memory
corruption / data races, single-platform-testable). Each now sits on the finished
deep-copy foundation.

### #5 — threading runtime (make `both` actually parallel)
Make `Ashes.Parallel.both` run its two thunks on real threads. Needs:
1. **Freestanding TLS** so each thread gets its own bump arena: turn `__ashes_heap_cursor`/
   `__ashes_heap_end` (`src/Ashes.Backend/Llvm/LlvmCodegen.cs:411`) into `thread_local`; add
   per-thread TLS setup to the entry point (`arch_prctl(ARCH_SET_FS, tcb)` on x64) **and a
   `PT_TLS` segment to the custom image linkers** (`LlvmImageLinkerElf*`, `LlvmImageLinkerPe.cs`).
   *This is the real blocker — binaries are nostdlib/freestanding.*
2. `clone(2)` + `futex` spawn/join (Linux x64/arm64), `CreateThread`/`WaitForSingleObject`
   (Windows) — add to the syscall/import layer like the existing file/socket ops.
3. A `both` **intrinsic** (replace the pure-Ashes `both`) that forks `right` to a worker,
   runs `left` inline, joins, and **deep-copies the worker's result into the parent arena
   via the existing `EmitDeepCopy`** before the worker arena is torn down.
4. Worker pool (size = CPU count); when saturated, `both` runs inline (already correct).
5. Rewrite `map`/`reduce` in `Parallel.ash` to fork through the `both` intrinsic.

### #2 — in-place reuse fast path (fix the hot-loop arena leak, fast)
The user chose *automatic in-place reuse, no new syntax* (not the slow deep-copy stopgap).
Needs:
1. **Interprocedural linearity analysis** (`Lowering.Ownership.cs`): prove a value is
   consumed exactly once (the TCO accumulator threaded through `loop(newAcc)` is the target).
2. **Reuse tokens**: a `match` that deconstructs a linear ADT yields its dead cell; a
   same-size `AllocAdt` consumes it (new IR `AllocReusing`) and writes in place.
3. **TCO arena-reset integration** (the delicate, region-aware step): extend
   `GetTcoCopyOutKind`/`CanArenaReset` (`Lowering.Ownership.cs`) so a linear+reused
   accumulator lets the back-edge reset the arena. *Deep-copy fallback already exists for
   non-linear cases.*

## 3. ⭐ NEXT STEP (start here tomorrow)

**Begin #5 step 1 — the freestanding TLS arena**, since it's the gating blocker and is
verifiable in isolation:

1. Make the two arena globals `thread_local`; add `arch_prctl` TCB setup to the entry.
2. Add `PT_TLS` support to the ELF image linker.
3. **Verify the whole existing suite stays green on the main thread** (no threads yet — just
   prove single-thread TLS works end to end). Only then move to `clone`/`futex` (#5 step 2).

Keep each sub-step green before the next. The deep-copy-on-join (#5 step 3) is already in
hand via `EmitDeepCopy`.

*(Alternative if you'd rather do #2 first: start with the linearity analysis as a read-only
pass and dump which TCO accumulators are provably linear, before touching any codegen.)*
