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
| **String operations** | `EmitCopyBytes` → `LLVMBuildMemCpy`. Comparison → `memcmp`/`bcmp`. Literals → `.rodata` global constants (no heap alloc). |
| **Pattern matching** | Tag/zero/non-zero checks → single `CmpIntEq`/`CmpIntNe` + one conditional jump. |
| **Function attributes** | `nounwind` on all functions. `willreturn`, `noalias`, `nonnull`, `readonly`, `memory(read)` on builtins. |
| **CPU targeting** | `--target-cpu` CLI flag; `native` auto-detects via `LLVMGetHostCPUName`/`LLVMGetHostCPUFeatures`. |
| **IR optimizer** | Constant folding (with cross-label propagation), identity/strength reduction, unreachable code elimination, dead code elimination. |
| **TCO** | IR-level tail recursion → loop. `LLVMSetTailCall` on tail-position calls. |
| **Debug info** | `DW_TAG_auto_variable` for locals, `DW_TAG_formal_parameter` for lambda args. Custom DWARF language code `0x8001`. `isOptimized` wired to `-O` level. |

------------------------------------------------------------------------

## Ordered Roadmap — Next Work Items

Every remaining optimization task, in recommended execution order.
Each item builds on the previous ones.

### 1. ~~Extend TCO copy-out beyond TStr and TList(copy-type element)~~ ✅

**Completed.** Replaced `CanCopyOutTcoArg` with `GetTcoCopyOutKind` in `Lowering.cs`.
Added `CopyOutTcoListCell` IR instruction for single-cell + head copy-out,
and `ListHeadCopyKind` enum (`Inline`, `String`, `InnerList`).

- **TList(TStr):** `CopyOutTcoListCell(String)` — copies one cons cell + copies the
  string head to a fresh allocation.
- **TList(TList(copy-type)):** `CopyOutTcoListCell(InnerList)` — copies one cons cell +
  deep-copies the inner list chain via the three-phase count/cache/build algorithm.
- **Closure args:** `CopyOutClosure` — copies 24-byte closure struct + env block.
- **ADT args:** `CopyOutArena(staticSizeBytes)` — shallow copy, reusing the existing
  `CanCopyOutAdt` field-type check.

### 2. Borrow elision pass

Implement `ElideBorrowsForConstants` in `IrOptimizer.cs` (currently no-op).

- Add temp aliasing infrastructure: when a `Borrow` targets a copy-type
  source (constants, `LoadConst*` results), remap all uses of the borrow
  target to the original source temp and remove the `Borrow` instruction.
- Extend to non-copy borrows that are provably single-use (the borrow
  target is used exactly once before being dropped).
- Prerequisite: use-def chain tracking per temp within each function.

### 3. Drop elision pass

Implement `ElideRedundantDrops` in `IrOptimizer.cs` (currently no-op).

- Remove `Drop` instructions for slots that are:
  - Never stored to (uninitialized — dead drop).
  - Copy types (Int, Float, Bool — no cleanup needed).
  - Already consumed (moved into another binding or returned).
- Keep `Drop` for resource types (Socket) and heap types that may still
  hold OS resources.
- Prerequisite: same use-def / ownership flow analysis as borrow elision.

### 4. Escape analysis — stack-allocate non-escaping closures / ADTs

Analyze which closures and ADTs never escape their defining scope. Those
can be stack-allocated (`alloca`) instead of arena-allocated, avoiding
arena pressure entirely.

- **Closures:** If the closure is called within its scope and the result
  is not returned or stored into a heap structure, it doesn't escape.
- **ADTs:** Single-arm pattern matches where the ADT is destructured
  immediately — the ADT never escapes the match scope.
- Enables further drop elision: stack-allocated values don't need drops.

### 5. Decision tree pattern matching for large ADTs

Replace the current linear chain of tag checks with a decision tree
(jump table or balanced binary search) for ADTs with many constructors.

- Threshold: use linear scan for ≤ 4 constructors; decision tree above.
- Reduces match complexity from O(n) to O(log n) or O(1) for dense tags.

### 6. Runtime string interning

Add a runtime intern table for strings. `concat`, `substring`, and other
string-producing operations check the table before allocating.

- Trade-off: hash + lookup overhead vs. deduplication savings.
- Most valuable for programs that build many identical strings in loops.
- Low priority — arena deallocation already handles most memory pressure.

### 7. Mutual recursion TCO

Extend tail call optimization to detect mutual recursion (e.g. `f` calls
`g` in tail position, `g` calls `f` in tail position).

- Requires call graph analysis to identify mutually recursive groups.
- Convert to a shared loop with a dispatch tag.
- Low priority — single-function TCO covers the vast majority of cases.
