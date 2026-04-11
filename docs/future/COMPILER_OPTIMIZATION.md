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
| **Arena deallocation** | Phase 1: scope watermarks for copy-type results. Phase 2a: TCO per-iteration reset for copy-type args. Phase 2b: copy-out (`CopyOutArena` IR instruction) for `TStr` scope results. Phase 2c: TCO copy-out for `TStr` and `TList(copy-type)` args. Phase 2d: abandoned OS chunk reclamation via `ReclaimArenaChunks` (split from `RestoreArenaState` to prevent use-after-free — restore resets pointers, reclaim frees chunks after copy-out completes). Phase 3: per-function-call watermarks. Phase 4: extended copy-out — `CopyOutList` (deep cons-chain copy for `TList` with copy-type/TStr element), `CopyOutClosure` (closure struct + env copy; 24-byte closure layout `{code, env, env_size}`), ADT with copy-type fields. |
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

### 1. ~~Extend copy-out to List, ADT, and closure (scope + call results)~~ ✅

Extended copy-out in `Lowering.cs` beyond `TStr`.

- **List:** Deep cons-chain copy via `CopyOutList` IR instruction. Walks tail
  pointers at runtime, allocating and relinking each cell. Safe when element is
  copy-type or self-contained (`TStr`). Shallow 16-byte copy is NOT safe for
  multi-cell chains (inner cells would be left in freed arena space).
- **Closure:** Closure struct + env copy via `CopyOutClosure` IR instruction.
  Closure layout changed from 16 to 24 bytes: `{code:i64, env:i64, env_size:i64}`.
  Reads `env_size` at runtime to copy the env block. Shallow 16-byte copy is
  NOT safe (env pointer would dangle).
- **ADT:** `(1 + fieldCount) * 8` bytes — shallow `CopyOutArena` copy, safe when all
  fields across all constructors are copy types (Int, Float, Bool) and all
  constructors have the same arity.

Applies to both let/match scope results (`PopOwnershipScope`) and
per-call results (`LowerCall` post-`CallClosure`).

### 2. Extend TCO copy-out beyond TStr and TList(copy-type element)

Extend `CanCopyOutTcoArg` in `Lowering.cs`.

- **TList(TStr):** Cons cell is 16 bytes; `head` is a string pointer
  (self-contained, no internal heap pointers). Copy the cons cell, then
  copy the string.
- **TList(TList(copy-type)):** Cons cell where `head` is a pointer to
  another cons cell. Requires copying the head cons cell too — two-level
  copy-out.
- **Closure args:** Same 16-byte shallow copy as scope results.
- **ADT args:** Same field-type recursive check as scope results.

### 3. Borrow elision pass

Implement `ElideBorrowsForConstants` in `IrOptimizer.cs` (currently no-op).

- Add temp aliasing infrastructure: when a `Borrow` targets a copy-type
  source (constants, `LoadConst*` results), remap all uses of the borrow
  target to the original source temp and remove the `Borrow` instruction.
- Extend to non-copy borrows that are provably single-use (the borrow
  target is used exactly once before being dropped).
- Prerequisite: use-def chain tracking per temp within each function.

### 4. Drop elision pass

Implement `ElideRedundantDrops` in `IrOptimizer.cs` (currently no-op).

- Remove `Drop` instructions for slots that are:
  - Never stored to (uninitialized — dead drop).
  - Copy types (Int, Float, Bool — no cleanup needed).
  - Already consumed (moved into another binding or returned).
- Keep `Drop` for resource types (Socket) and heap types that may still
  hold OS resources.
- Prerequisite: same use-def / ownership flow analysis as borrow elision.

### 5. Escape analysis — stack-allocate non-escaping closures / ADTs

Analyze which closures and ADTs never escape their defining scope. Those
can be stack-allocated (`alloca`) instead of arena-allocated, avoiding
arena pressure entirely.

- **Closures:** If the closure is called within its scope and the result
  is not returned or stored into a heap structure, it doesn't escape.
- **ADTs:** Single-arm pattern matches where the ADT is destructured
  immediately — the ADT never escapes the match scope.
- Enables further drop elision: stack-allocated values don't need drops.

### 6. Decision tree pattern matching for large ADTs

Replace the current linear chain of tag checks with a decision tree
(jump table or balanced binary search) for ADTs with many constructors.

- Threshold: use linear scan for ≤ 4 constructors; decision tree above.
- Reduces match complexity from O(n) to O(log n) or O(1) for dense tags.

### 7. Runtime string interning

Add a runtime intern table for strings. `concat`, `substring`, and other
string-producing operations check the table before allocating.

- Trade-off: hash + lookup overhead vs. deduplication savings.
- Most valuable for programs that build many identical strings in loops.
- Low priority — arena deallocation already handles most memory pressure.

### 8. Mutual recursion TCO

Extend tail call optimization to detect mutual recursion (e.g. `f` calls
`g` in tail position, `g` calls `f` in tail position).

- Requires call graph analysis to identify mutually recursive groups.
- Convert to a shared loop with a dispatch tag.
- Low priority — single-function TCO covers the vast majority of cases.
