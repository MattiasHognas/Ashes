# Compiler Optimization — Status & Roadmap

Internal compiler quality-of-implementation improvements.
Nothing here changes the language specification.

------------------------------------------------------------------------

## Completed Work

All original audit findings have been addressed:

| Area | What was done |
|------|---------------|
| **LLVM passes** | Targeted pipeline (mem2reg, instcombine, early-cse, reassociate, gvn, dce, inline, licm, dse) at O1–O3. PLT32 + PE relocation support. Freestanding builtins (memcpy, memset, strlen, memcmp, bcmp) emitted per module. |
| **Memory allocator** | OS-backed `mmap`/`VirtualAlloc` chunks (4 MB each, on demand). Bounds checking with clean error. |
| **Arena deallocation** | Phase 1: scope watermarks for copy-type results. Phase 2a: TCO per-iteration reset for copy-type args. Phase 2b: copy-out (`CopyOutArena` IR instruction) for `TStr` scope results. Phase 2c: TCO copy-out for `TStr` and `TList(copy-type)` args. Phase 2d: abandoned OS chunk reclamation via `ReclaimArenaChunks` (split from `RestoreArenaState` to prevent use-after-free — restore resets pointers, reclaim frees chunks after copy-out completes). Phase 3: per-function-call watermarks. Phase 4: extended copy-out — `CopyOutList` (deep cons-chain copy for `TList` with copy-type element), `CopyOutClosure` (closure struct + env copy; 24-byte closure layout `{code, env, env_size}`), ADT with copy-type fields. Phase 5: extended TCO copy-out — `CopyOutTcoListCell` for `TList(TStr)` and `TList(TList(copy-type))` args (single-cell + head copy), closure and ADT args via `CopyOutClosure`/`CopyOutArena`. |
| **Extended TCO copy-out** | Replaced `CanCopyOutTcoArg` with `GetTcoCopyOutKind` in `Lowering.cs`. Added `CopyOutTcoListCell` IR instruction for single-cell + head copy-out, and `ListHeadCopyKind` enum (`Inline`, `String`, `InnerList`). `TList(TStr)` via `CopyOutTcoListCell(String)`, `TList(TList(copy-type))` via `CopyOutTcoListCell(InnerList)`, closures via `CopyOutClosure`, ADTs via `CopyOutArena(staticSizeBytes)`. |
| **String operations** | `EmitCopyBytes` → `LLVMBuildMemCpy`. Comparison → `memcmp`/`bcmp`. Literals → `.rodata` global constants (no heap alloc). |
| **Pattern matching** | Tag/zero/non-zero checks → single `CmpIntEq`/`CmpIntNe` + one conditional jump. |
| **Function attributes** | `nounwind` on all functions. `willreturn`, `noalias`, `nonnull`, `readonly`, `memory(read)` on builtins. |
| **CPU targeting** | `--target-cpu` CLI flag; `native` auto-detects via `LLVMGetHostCPUName`/`LLVMGetHostCPUFeatures`. |
| **IR optimizer** | Constant folding (with cross-label propagation), identity/strength reduction, unreachable code elimination, dead code elimination. |
| **Borrow elision** | `ElideBorrowsForConstants` in `IrOptimizer.cs`. Temp aliasing infrastructure: use-def chain tracking per temp (copy-type producers via `LoadConst*` scan, per-temp use count via `CollectUsedTemps`). Copy-type elision: `Borrow` instructions whose source is produced by `LoadConstInt`/`LoadConstFloat`/`LoadConstBool` are removed; all uses of the borrow target remapped to the original source temp. Single-use elision: non-copy `Borrow` instructions whose target is used exactly once are also elided. Transitive chain resolution via `ResolveTemp`. `RemapSourceTemps` helper rewrites all source-temp references in any `IrInst` variant using `with` record syntax. |
| **Drop elision** | `ElideRedundantDrops` in `IrOptimizer.cs` (Pass 4). Removes non-resource-type `Drop` instructions (String, List, Tuple, Function, non-resource ADTs) — these are no-ops in codegen since arena deallocation handles bulk memory reclamation. Resource-type drops (Socket) are always preserved for platform-specific cleanup. Also removes the associated `LoadLocal` when its target temp is only used by the elided Drop, and `StoreLocal` instructions to slots with no remaining `LoadLocal` references — cascading dead code cleanup in a single pass. Uses `BuiltinRegistry.IsResourceTypeName` to distinguish resource types. |
| **TCO** | IR-level tail recursion → loop. `LLVMSetTailCall` on tail-position calls. |
| **Escape analysis** | Conservative stack allocation for proven non-escaping values. Added `AllocStack`, `AllocAdtStack`, and `MakeClosureStack` IR instructions plus LLVM `alloca` codegen. Closures are stack-allocated when used only as direct callees within scope (including captured-env closures), and ADTs are stack-allocated for immediate single-arm constructor destructuring (`match Box(42) with | Box(x) -> ...`) and let-bound values destructured immediately (`let box = Box(42) in match box with | Box(x) -> ...`). |
| **Debug info** | `DW_TAG_auto_variable` for locals, `DW_TAG_formal_parameter` for lambda args. Custom DWARF language code `0x8001`. `isOptimized` wired to `-O` level. |
| **Decision-tree matching** | Matches over >4 single-ADT constructor arms (distinct tags, trivial sub-patterns, no guards) lower to one `SwitchTag` IR instruction → LLVM `switch` (branch/bit-test tree; jump tables disabled pending linker relocation support). O(n) tag-comparison chain → O(log n). |
| **String-literal interning** | Identical string-literal `.rodata` globals are content-addressed and emitted once per module (`LlvmTargetContext.GetOrAddStringLiteralGlobal`), shared across all functions and internal call sites. Compile-time, bounded, leak-free. |
| **Mutual-recursion TCO** | Eligible `let rec … and …` groups (same arity, identical parameter types, a cross-member tail call) are merged into one self-recursive `dispatch` function with thin per-member wrappers, so the existing single-function TCO turns mutual recursion into a loop. Ineligible groups keep the closure path. |

------------------------------------------------------------------------

## Ordered Roadmap — Next Work Items

Optimization tasks as a tracked checklist, in recommended execution order. The
three original audit items (1–3) are **done**; item 4 is an optional follow-up
enhancement that re-enables a faster codegen strategy once the linkers support
it.

### 1. Decision-tree pattern matching for large ADTs — ✅ Done

Replace the current linear chain of tag checks with a single tag switch
(LLVM `switch` → balanced binary / bit-test tree) for matches over ADTs with
many constructors.

- [x] Add a `SwitchTag` IR instruction (`tag temp` + `(tag, label)` cases + default label).
- [x] Keep `RemapSourceTemps` / `CollectUsedTemps` / branch-ref counting / terminator handling in `IrOptimizer.cs` in sync with the new instruction.
- [x] Lower eligible matches to `SwitchTag` in `Lowering.Patterns.cs`; fall back to the linear chain otherwise.
  - Eligibility: every arm is a constructor pattern (incl. nullary) over one ADT, distinct tags, only trivial (`_` / variable) sub-patterns, no guards, arm count **> 4**.
- [x] Backend: add `LLVMBuildSwitch` / `LLVMAddCase` interop and emit the switch in `LlvmCodegen.cs` (`SwitchTag` is a block terminator).
- [x] Tests: `DecisionTreeMatchTests` (asserts `SwitchTag` above the threshold / linear chain at-or-below, and ineligibility on nested sub-patterns); existing `pattern_large_adt_*` end-to-end tests now exercise the switch path.

Reduces match dispatch from O(n) tag comparisons to O(log n). **Note:** functions
carry `"no-jump-tables"="true"` (see item 4) so LLVM lowers the switch to a
branch/bit-test tree rather than an unrelocatable jump table.

### 2. Compile-time string-literal interning — ✅ Done

Deduplicate identical string-literal `.rodata` globals at compile time so each
distinct literal value is emitted once per module and shared by every use.

> **Scope decision.** *Runtime* interning of dynamically-produced strings
> (`concat` / `substring` results) was considered and **rejected**: under the
> arena memory model there is no sound, bounded implementation. A permanent
> intern region never reclaims, so it grows monotonically (a leak that violates
> the "No GC / deterministic reclamation" invariant); interning into the arena
> instead dangles on the next arena reset (use-after-free). A bounded, reclaimed
> table would require reference counting or GC, both of which the language
> forbids. We therefore intern only the compile-time-known literal set, which is
> finite, static, and leak-free by construction.

- [x] `InternString` already dedups literals by value at the IR level; the gap was the backend, where `EmitHeapStringLiteral` emitted a fresh global per call.
- [x] Added a module-level content-addressed cache (`GetOrAddStringLiteralGlobal`, keyed by UTF-8 value) on `LlvmTargetContext`; one global per distinct value is now shared across all functions and internal call sites (error messages, URL prefixes, …).
- [x] Tests: `tests/string_literal_reuse.ash` exercises the same literal reused across functions and uses; the pipeline snapshot (`SnapshotTests.Snapshot_pipeline_for_lambda_program`) and the full string-handling e2e suite guard the emitted-binary change.

### 3. Mutual-recursion TCO — ✅ Done

Extend tail-call optimization to mutually recursive groups (`f` tail-calls `g`,
`g` tail-calls `f`).

- [x] Synthesize a single self-recursive `dispatch(tag, params)` from the group and rebind each member to a thin wrapper, so the **existing** single-function TCO collapses the group into one loop (`TryLowerMutualRecursionTco` in `Lowering.cs`).
- [x] In-group tail calls are rewritten to `dispatch(tag, args)`; non-tail in-group calls keep targeting the member wrappers.
- [x] Eligibility gate (`same arity + identical parameter types + a cross-member tail call`) keeps the transform type-safe and never breaks a valid program; ineligible groups fall back to the closure path.
- [x] Tests: `MutualRecursionTcoTests` (dispatch synthesized only when eligible) and `tests/mutual_recursion_tco_even_odd.ash` / `tests/mutual_recursion_tco_three_cycle.ash` (deep recursion that would overflow the stack without it).

> **Design constraint (discovered).** Members of a mutually-tail-recursive group can legally
> have **different parameter types** (`ping: Int → Str` tail-calls `pong: Str → Str` and back),
> so a group cannot be merged into a single self-recursive `dispatch(tag, args)` function by
> sharing one typed parameter list — that would unify incompatible parameter types and reject a
> valid program. The implemented transform therefore gates on **same arity + identical parameter
> types** (verified against each member's inferred type after the group is type-checked) and
> falls back to the closure path otherwise; this keeps each merged parameter's type intact, so the
> existing single-function TCO and arena copy-out apply unchanged. A future generalization to
> heterogeneous parameter types needs distinct per-member slots (an IR-level slot-union loop) or
> an opaque-coercion dispatch with wrappers re-typed from the inferred member signatures.

### 4. Re-enable LLVM jump tables for `switch` (linker relocation support)

Decision-tree matching (item 1) currently forces `"no-jump-tables"="true"` because the custom
image linkers don't apply the relocations an LLVM switch jump table needs, so the indirect branch
through the `.rodata` address table lands in garbage. Teaching the linkers to handle those
relocations lets us drop the attribute and get LLVM's O(1) jump-table dispatch for free — the
codegen side already produces it. The table is compile-time-constant, immutable, read-only
`.rodata` (same category as the string-literal constants and `__imp_*` import tables the linkers
already handle), so it is fully consistent with Ashes' invariants — no mutation, no runtime
allocation, no GC, deterministic.

- [ ] Identify the relocation kinds LLVM emits for switch jump tables on each target (absolute / PC-relative address-table entries) for ELF x64, ELF arm64, and PE.
- [ ] Add handling for those relocation kinds in `LlvmImageLinkerElf.cs`, `LlvmImageLinkerElfArm64.cs`, and `LlvmImageLinkerPe.cs`.
- [ ] Remove the `"no-jump-tables"="true"` attribute (`LlvmCodegen.cs`) once all three linkers apply the relocations.
- [ ] Tests: the existing large-ADT dispatch tests plus a density level high enough to force a jump table on each target (run arm64/win-x64 under the emulation helpers).
