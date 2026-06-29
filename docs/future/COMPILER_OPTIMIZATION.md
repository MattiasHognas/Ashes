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
| **Decision-tree matching** | Matches over >4 single-ADT constructor arms (distinct tags, trivial sub-patterns, no guards) lower to one `SwitchTag` IR instruction → LLVM `switch`. O(n) tag-comparison chain → O(log n) (or O(1) where LLVM picks a jump table). |
| **Jump-table linking** | The image linkers apply switch jump-table relocations (`R_X86_64_64` in `.rela.rodata`, `IMAGE_REL_AMD64_ADDR64` in `.rdata`, defensive `R_AARCH64_ABS64`), so LLVM's O(1) table dispatch links and runs correctly; the `no-jump-tables` attribute was removed. |
| **String-literal interning** | Identical string-literal `.rodata` globals are content-addressed and emitted once per module (`LlvmTargetContext.GetOrAddStringLiteralGlobal`), shared across all functions and internal call sites. Compile-time, bounded, leak-free. |
| **Mutual-recursion TCO** | Eligible `let rec … and …` groups (same arity, identical parameter types, a cross-member tail call) are merged into one self-recursive `dispatch` function with thin per-member wrappers, so the existing single-function TCO turns mutual recursion into a loop. Ineligible groups keep the closure path. **Design constraint:** members can legally have different parameter types (`ping: Int → Str` tail-calls `pong: Str → Str`), which a single shared typed parameter list cannot merge without unifying incompatible types; hence the same-arity + identical-parameter-types gate (verified against each member's inferred type). Heterogeneous-parameter generalization would need distinct per-member slots (an IR-level slot-union loop) or opaque-coercion dispatch. |

> **Scope decision — runtime string interning rejected.** *Runtime* interning of dynamically-produced
> strings (`concat` / `substring` results) was considered and **rejected**: under the arena memory
> model there is no sound, bounded implementation. A permanent intern region never reclaims, so it
> grows monotonically (a leak that violates the "No GC / deterministic reclamation" invariant);
> interning into the arena instead dangles on the next arena reset (use-after-free). A bounded,
> reclaimed table would require reference counting or GC, both of which the language forbids. We
> therefore intern only the compile-time-known literal set, which is finite, static, and leak-free
> by construction.
