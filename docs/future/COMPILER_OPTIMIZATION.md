# Compiler Optimization Deep Dive — Findings Report

This document captures the results of a comprehensive audit of the Ashes
compiler's code generation, LLVM utilization, memory management, and
optimization pipeline. Each finding includes the current state, the impact,
and the recommended fix. Items that have already been addressed are marked
with ✅.

Nothing here changes the language specification — all items are internal
compiler quality-of-implementation improvements.

------------------------------------------------------------------------

## Executive Summary

The compiler produces correct code and has a solid architecture, but there
are significant optimization opportunities:

1. ~~LLVM module-level optimization passes are completely disabled (dead
   code)~~ ✅ Passes enabled with targeted pipeline (O0–O3)
2. ~~Memory is never freed — 4 MB bump allocator with no bounds checking
   silently crashes on overflow~~ ✅ Dynamic heap via `mmap`/`VirtualAlloc`
3. ~~Manual byte loops replace LLVM intrinsics for string operations~~ ✅
   `EmitCopyBytes` uses `LLVMBuildMemCpy`; comparison uses builtin `memcmp`
4. ~~Pattern matching uses double comparisons where single equality checks
   suffice~~ ✅ Fixed
5. ~~No function attributes — LLVM cannot reason about `nounwind`, `noalias`,
   `readonly`, etc.~~ ✅ `nounwind` on all functions; `willreturn`, `noalias`,
   `nonnull`, `readonly`, `memory(read)` on builtins
6. ~~CPU features are empty — compiled as generic x86-64 with no SSE4.2/AVX~~
   ✅ `--target-cpu` CLI flag added; `native` auto-detects host CPU

------------------------------------------------------------------------

## Finding 1 — LLVM Optimization Passes Are Dead Code

**Status:** ✅ Fixed  
**Severity:** Critical impact  
**Files:** `LlvmCodegen.cs:129-158`, `LlvmImageLinkerElf.cs:15-17`

`RunLlvmOptimizationPasses()` is now called for every target (Linux x64,
Linux ARM64, Windows x64). A targeted pass pipeline avoids `simplifycfg`
(which miscompiles inline-asm control flow) while enabling `mem2reg`,
`instcombine`, `early-cse`, `reassociate`, `gvn`, `dce`, `inline`, `licm`,
and `dse` across O1/O2/O3 levels.

Relocation support was added for `R_X86_64_64` (absolute 64-bit),
`R_X86_64_PLT32` (ELF) and `IMAGE_REL_AMD64_REL32_1` through `_5` (PE).
The ELF linker now resolves SHN_UNDEF (section 0) symbols by name, handling
cases where LLVM inlines a builtin definition and re-introduces an external
call.

Builtin `memcpy`, `memset`, and `strlen` implementations are emitted into
every module so LLVM intrinsic lowering has local definitions for the
freestanding target.

### Validated

All four optimization levels (O0, O1, O2, O3) are validated by 36
parameterized tests in `OptimizationLevelTests.cs` — covering arithmetic,
string operations, pattern matching, recursion, closures, tail recursion,
floats, and string equality. Windows PE compilation is also tested at every
level.

------------------------------------------------------------------------

## Finding 2 — Memory Management

**Status:** ✅ Addressed (Phases 1, 2a–2d, 3 complete)  
**Severity:** Critical

### The allocator

~~The heap was a fixed 4 MB global byte array embedded in the BSS section
of the executable. This imposed a hard limit of ~262 144 cons cells and
bloated every binary by 4 MB.~~

✅ **Fixed.** The allocator now uses OS-backed virtual memory:

- **Linux:** `mmap(NULL, 4MB, PROT_READ|PROT_WRITE, MAP_PRIVATE|MAP_ANONYMOUS, -1, 0)`
  via a new 6-argument syscall wrapper (x86-64 and ARM64).
- **Windows:** `VirtualAlloc(NULL, 4MB, MEM_COMMIT|MEM_RESERVE, PAGE_READWRITE)`
  via a new kernel32 import.
- Chunks are allocated on demand — when the current chunk is exhausted, a
  new 4 MB chunk is allocated from the OS automatically.
- The global array is removed, eliminating the 4 MB BSS overhead from every
  compiled binary.
- If the OS refuses the allocation (out of address space), the program
  prints `"Runtime error: failed to allocate heap memory from OS"` and exits
  with code 1.
- Verified working with 1 000 000 cons cells (16 MB, 4× the old limit).

### Deallocation — arena-based region deallocation

- `Drop` instructions exist in IR for semantic correctness.
- Backend `EmitDrop()` is a no-op for all heap types (only sockets get
  closed).
- Arena-based deallocation (Phases 1, 2a–2d, 3) reclaims heap memory at
  scope exit, TCO iteration boundaries, and function call boundaries for
  most common patterns. See sections below for details.
- Abandoned OS chunks are now reclaimed via `munmap` / `VirtualFree`
  when `RestoreArenaState` resets to a previous chunk (Phase 2d).

### Arena-based scope deallocation (Phase 1 — done)

The compiler now emits `SaveArenaState` at ownership scope entry and
`RestoreArenaState` at scope exit when the scope result is a **copy type**
(Int, Float, Bool). This resets the bump allocator to the scope-entry
watermark, effectively freeing all heap memory allocated within the scope.

- **Mechanism:** Each `PushOwnershipScope` allocates two local slots and
  emits `SaveArenaState(cursorSlot, endSlot)` to snapshot the heap cursor
  and end pointers. At `PopOwnershipScope`, if the result type is a copy
  type, `RestoreArenaState(cursorSlot, endSlot)` restores both pointers.
- **Safety guarantee:** Copy-type results never reference heap memory, so
  all allocations between save and restore are unreachable garbage after
  the scope's `Drop` instructions have run.
- **Coverage:** Let-expression scopes and match-arm scopes with Int, Float,
  or Bool results. Nested scopes compose correctly (stack-like watermarks).

### TCO loop iteration arena reset (Phase 2a — done)

TCO (tail call optimization) loops now emit per-iteration arena
save/restore when all tail-call arguments are copy types. This prevents
heap memory from growing monotonically across loop iterations.

- **Mechanism:** After the TCO loop body label, `SaveArenaState` snapshots
  the heap cursor and end pointers. Before the tail-call jump-back,
  `RestoreArenaState` resets both pointers to the iteration-start
  watermark, reclaiming all heap allocations from that iteration.
- **Safety guarantee:** When all tail-call argument types are copy types
  (Int, Float, Bool), no heap pointers are stored into the mutable param
  slots — arena reset cannot create dangling references.
- **Skipped when unsafe:** If any tail-call argument is a heap type
  (String, List, ADT, closure), `RestoreArenaState` is not emitted on the
  tail-call path. The `SaveArenaState` is still emitted (for future use
  when copy-out or escape analysis makes arena reset safe).
- **Coverage:** Single- and multi-parameter TCO functions with copy-type
  accumulator patterns (e.g. `sum n acc`, `countdown n`).

### Copy-out for heap-type scope results (Phase 2b — done)

Scopes that return a heap type now use a copy-out strategy to allow arena
reset. After `RestoreArenaState` resets the bump cursor, a new
`CopyOutArena` IR instruction shallow-copies the result object from the
(physically intact but logically freed) arena region to fresh space at the
reset watermark.

- **IR instruction:** `CopyOutArena(destTemp, srcTemp, staticSizeBytes)`.
  `staticSizeBytes = -1` means dynamic size (strings: read the 8-byte
  length field at runtime → `total = 8 + length`). Positive values are
  fixed-size copies (e.g. 16 bytes for a cons cell).
- **Safety guarantee:** Only a shallow copy is needed. Internal pointers
  (e.g. list tail) reference memory allocated in parent scopes or previous
  iterations — outside the current watermark, never reclaimed.
- **Copy direction:** After arena reset the cursor is at watermark W ≤ src,
  so `dest ≤ src` always holds and forward `memcpy` is safe with no
  destructive overlap.
- **Gating condition:** Copy-out is only emitted when the scope contained
  at least one alive owned value (checked via `HasAliveOwnedValuesInCurrentScope`),
  so scopes with nothing to reclaim skip the overhead.
- **Coverage:** String (`TStr`) results from let-expression and match-arm
  scopes. `CanCopyOutArena` currently handles `TStr`; other heap types
  (List, ADT, closure) still skip arena reset as copy-out is not yet safe
  for types with internal heap pointers into the reclaimed region.

### TCO loop arena reset for heap-type arguments (Phase 2c — done)

TCO loops with heap-type tail-call arguments now use the copy-out mechanism
to allow per-iteration arena reset, handling the common list-accumulator
pattern (e.g. `build n (x :: acc)`).

- **Mechanism:** Before `RestoreArenaState` on the tail-call path, each
  heap-type argument is checked with `CanCopyOutTcoArg`. If all pass, the
  arena is reset and each heap-type arg is copy-outed, then its param slot
  is overwritten with the fresh copy pointer.
- **Safe types for TCO copy-out:**
  - `TStr` — self-contained, no internal heap pointers.
  - `TList` where the element type is a copy type (Int, Float, Bool) — the
    cons cell is `{head:i64, tail:i64}` where head is a direct value and
    tail points to the previous iteration's copy-outed cell (below the
    watermark, never reclaimed).
- **Conservative fallback:** If any heap-type arg cannot be copy-outed
  (e.g. `List of List` — head is a pointer into the reclaimed region),
  the entire arena reset is skipped on that tail-call path.
- **Copy order:** Left-to-right, matching evaluation order. Each copy
  lands above the previous one — no destructive overlap.

### Abandoned OS chunk reclamation on arena reset (Phase 2d — done)

When `RestoreArenaState` resets the cursor to a previous chunk, abandoned
OS chunks are now reclaimed.

- **Chunk linked list:** Each 4 MB chunk header (first 8 bytes) stores
  the base address of the previous chunk (0 for the first chunk).
  `EmitHeapGrow` writes the outgoing chunk's base into the new chunk's
  header. Allocations start at offset +8 within each chunk.
- **Fast path:** When the saved watermark end matches the current chunk
  end (same chunk), only the cursor is reset — single store, no syscalls.
- **Slow path:** When chunks differ, `RestoreArenaState` walks the linked
  list from the current chunk back to the saved chunk, calling
  `munmap` (Linux: syscall 11 / AArch64 syscall 215) or
  `VirtualFree(ptr, 0, MEM_RELEASE)` (Windows) on each abandoned chunk.
  A loop variable tracks the current chunk end until it matches the saved
  watermark.
- **Outcome:** Long-running programs with periodic arena resets now return
  excess OS memory instead of holding it indefinitely.

### Per-function-call arena watermarks (Phase 3 — done)

Every non-TCO function call chain now gets its own arena watermark.
`SaveArenaState` is emitted before evaluating the callee and arguments;
after the final `CallClosure`, the result type determines cleanup.

- **Mechanism:** `LowerCall` allocates two local watermark slots and emits
  `SaveArenaState` before evaluating the root expression and arguments.
  After the last `CallClosure` in the curried call chain, the final
  result type is checked:
  - Copy type (Int, Float, Bool) → `RestoreArenaState` reclaims all
    allocations from the call chain (intermediate closures, temporary
    data structures inside the callee, argument construction).
  - String (`TStr`) → `RestoreArenaState` + `CopyOutArena` copies the
    result to the watermark position, reclaiming everything else.
  - Other heap types (List, ADT, closure) → no arena action
    (conservative fallback — internal pointers may reference memory
    within the watermark region).
- **Watermark independence:** The per-call watermark slots are managed
  independently of the `_arenaWatermarks` / `_ownershipScopes` stacks,
  avoiding any stack imbalance.
- **Curried calls:** For `f(x)(y)`, a single watermark covers the entire
  chain. Intermediate closures from partial application are reclaimed
  when the final result is a copy type or string.
- **Nesting:** Nested calls (e.g. `f(g(x))`) each get their own
  watermark. Inner watermarks reclaim eagerly; outer watermarks catch
  what inner ones miss.
- **Overhead:** ~10 instructions per call (2 loads + 2 stores for save;
  2 loads + 1 compare + 1 branch + 2 stores for restore fast path).
  No overhead on the slow path unless chunks were actually consumed.

### Remaining limitations (future work)

- Copy-out only supports `TStr` for general scope and per-call results.
  List, ADT, and closure results still skip arena reset (requires
  deep-copy or escape analysis for types with internal heap pointers).
- TCO copy-out only supports `TStr` and `TList(copy-type element)`. More
  complex accumulator types (e.g. `List of List`, ADTs) still skip
  per-iteration arena reset.

### Recommendations

1. ✅ **Done:** Heap bounds checking — clean error on overflow.
2. ✅ **Done:** Growing heap via `mmap` / `VirtualAlloc` — no hard limit.
3. ✅ **Phase 1 done:** Arena-based scope deallocation for copy-type results.
4. ✅ **Phase 2a done:** TCO loop iteration arena reset for copy-type args.
5. ✅ **Phase 2b done:** Copy-out for string scope results.
6. ✅ **Phase 2c done:** TCO copy-out for string and list-of-copy-type args.
7. ✅ **Phase 2d done:** Abandoned OS chunk reclamation on arena reset.
8. ✅ **Phase 3 done:** Per-function-call arena watermarks.
9. **Future:** Extend copy-out to List, ADT, and closure results
   (requires escape analysis or deep-copy for internal heap pointers).

------------------------------------------------------------------------

## Finding 3 — Manual Byte Loops Instead of LLVM Intrinsics

**Status:** ✅ Fixed  
**Severity:** High  
**Files:** `LlvmCodegenMemory.cs:103-148`

### String concatenation (`EmitCopyBytes`)

✅ **Fixed.** `EmitCopyBytes` now uses `LLVMBuildMemCpy` (the `llvm.memcpy`
intrinsic) instead of a manual byte-by-byte loop. LLVM can vectorize this
to `rep movsb` or SIMD copies.

### String comparison

✅ **Fixed.** `EmitStringComparison` now performs a length check followed by
a call to the builtin `memcmp` function instead of a 5-block byte-by-byte
loop. LLVM may further optimize the `memcmp` equality check into `bcmp`. A
freestanding `bcmp` builtin is emitted alongside `memcmp` to support this.

### String-to-CString conversion

✅ **Fixed.** `EmitStringToCString` uses `EmitCopyBytes` (which calls
`LLVMBuildMemCpy`) to copy the string data, then writes a single null
terminator byte.

### Heap string literal initialization

✅ **Fixed.** `EmitHeapStringFromBytes` now creates a global constant byte
array and uses `LLVMBuildMemCpy` instead of emitting N individual store
instructions. For an N-byte string this replaces 2N IR instructions
(N GEPs + N stores) with one global constant and one memcpy intrinsic.

### Stack string/byte array initialization

✅ **Fixed.** `EmitStackStringObject` and `EmitStackByteArray` also use
the global constant + memcpy pattern for initialization.

### String literal rodata placement

✅ **Fixed.** `EmitHeapStringLiteral` now creates a global constant struct
`{ i64 length, [N x i8] data }` in the read-only data section instead of
heap-allocating at runtime. Since Ashes strings are immutable, string
literals never need heap allocation. This eliminates heap pressure and
makes string literal addresses available at link time.

New LLVM C API bindings added: `LLVMConstArray2`, `LLVMSetGlobalConstant`,
`LLVMSetUnnamedAddr`, `LLVMConstStructInContext`, `LLVMStructTypeInContext`.

------------------------------------------------------------------------

## Finding 4 — Pattern Matching Double-Comparison

**Status:** ✅ Fixed  

### What was wrong

`Lowering.cs:3562` said *"IR has <= and >= but no direct == comparison"* —
but `CmpIntEq` existed since the IR was first created.

- `EmitRequireTagMatch` used `CmpIntGe + JumpIfFalse + CmpIntLe +
  JumpIfFalse` (4 IR instructions, 2 conditional jumps).
- `EmitRequireZero` used the same double-comparison pattern.
- `EmitRequireNonZero` used `CmpIntLe + JumpIfFalse + Jump + Label` (4 IR
  instructions, extra basic block).

### Fix applied

- Tag match → `CmpIntEq + JumpIfFalse` (2 instructions, 1 jump).
- Zero check → `CmpIntEq + JumpIfFalse`.
- Non-zero check → `CmpIntNe + JumpIfFalse` (2 instructions, no extra
  block).

Every ADT constructor match and every empty/non-empty list check benefits.

------------------------------------------------------------------------

## Finding 5 — No LLVM Function Attributes

**Status:** ✅ Fixed  
**Severity:** Medium-High  

All emitted functions (entry + lifted closures) now have the `nounwind`
attribute applied via `LLVMAddAttributeAtIndex` / `LLVMCreateEnumAttribute`.
This eliminates unwind table generation for smaller, faster code.

All freestanding builtin functions (`memcpy`, `memset`, `strlen`, `memcmp`,
`bcmp`) now have comprehensive LLVM attributes:

| Attribute | Scope | Applied to |
|-----------|-------|------------|
| `nounwind` | Function | All builtins — no exceptions |
| `willreturn` | Function | All builtins — bounded loops, guaranteed to terminate |
| `memory(read)` | Function | `memcmp`, `bcmp`, `strlen` — read-only, enables CSE/LICM |
| `noalias` | Parameters | `memcpy` dest/src — non-overlapping per C standard |
| `nonnull` | Parameters + return | All pointer params; return value for pointer-returning builtins |
| `readonly` | Parameters | `memcmp`, `bcmp`, `strlen` pointer params — no writes through them |

The `LLVMCreateStringAttribute` interop was added to support LLVM 16+
`memory(...)` string attributes. `AttributeIndexReturn` (index 0) was added
for return-value attributes.

------------------------------------------------------------------------

## Finding 6 — Generic CPU Target

**Status:** ✅ Fixed  
**Severity:** Medium  
**File:** `LlvmTargetSetup.cs`, `BackendCompileOptions.cs`, `Program.cs`

- x86-64 defaults to `cpu = "x86-64"` with **empty features string**.
- ARM64 defaults to `cpu = "generic"`.
- A `--target-cpu` CLI flag is now available on `compile`, `run`, `repl`,
  and `test` commands.
- `--target-cpu native` auto-detects the host CPU name and feature string
  via `LLVMGetHostCPUName` / `LLVMGetHostCPUFeatures`.
- Any LLVM-recognized CPU name (e.g. `skylake`, `znver3`, `apple-m1`) can
  be passed directly.
- The flag selects the CPU microarchitecture (instruction set extensions,
  scheduling model) — the OS/arch target remains controlled by `--target`.

------------------------------------------------------------------------

## Finding 7 — IR Optimizer Is 50 % Disabled

**Status:** ✅ Mostly fixed  
**Severity:** Medium  
**File:** `IrOptimizer.cs`

| Pass | Status | Notes |
|------|--------|-------|
| Borrow Elision | ❌ No-op | Awaits temp aliasing infrastructure |
| Constant Folding | ✅ Active | Folds scalars; propagates across single-predecessor labels |
| Identity/Strength Reduction | ✅ Active | `x+0`, `x*1`, `x*0`, `x*2→x+x`, `x/1`, `x-0` |
| Unreachable Code Elimination | ✅ Active | Removes code after unconditional jumps/returns |
| Dead Code Elimination | ✅ Active | Removes unused `LoadConst*` and dead `StoreLocal` |
| Drop Elision | ❌ No-op | Awaits per-object `free()` allocator |

### Missing optimizations at IR level

- ~~**Identity elimination:** `x + 0`, `x * 1`, `x * 0` not simplified.~~ ✅ Fixed
- ~~**Strength reduction:** `x * 2` is a multiply, not `x + x` or shift.~~ ✅ Fixed
- ~~**Unreachable code elimination:** Code after unconditional jumps or
  returns remains.~~ ✅ Fixed
- ~~**Dead store elimination:** Unused `StoreLocal` not removed.~~ ✅ Fixed
- ~~**Constant propagation across simple labels:** Labels with a single
  predecessor could propagate constant knowledge.~~ ✅ Fixed — Both
  `FoldConstants` and `ReduceIdentitiesAndStrength` passes now pre-scan
  label predecessors (branch references + fall-through analysis) and save/
  restore constant state at single-predecessor labels. Labels reached only
  by fall-through (no branches target them) also preserve state.

------------------------------------------------------------------------

## Finding 8 — TCO Implementation

**Status:** ✅ Partially fixed (`LLVMSetTailCall` added)  
**Severity:** Low-Medium

### What works

The compiler implements IR-level tail call optimization that converts
tail-recursive self-calls into loops. Verified working with 1 000 000
iterations in constant stack space.

✅ **New:** `LLVMSetTailCall` is now declared in the interop layer and
tail-position calls are marked at the LLVM level as a safety net for cases
the IR-level TCO misses.

### Gaps

- **Non-tail recursion:** `n + f(n - 1)` with 1 M depth → SIGSEGV.
  Expected, but there is no safety net.
- **No mutual recursion TCO:** Only self-calls are detected.
- **Stack depth:** Effectively limited to ~10 K–100 K non-TCO recursive
  calls depending on frame size.

------------------------------------------------------------------------

## Finding 9 — No Runtime String Interning

**Status:** Open  
**Severity:** Low-Medium

The compiler interns string literals at compile time (`_stringIntern`
dictionary in `Lowering.cs`), but at runtime every string operation
(concat, substring, etc.) allocates a new heap string. There is no runtime
deduplication.

For programs that create many identical strings (e.g. in loops building
structured output), this wastes heap space and comparison time.

------------------------------------------------------------------------

## Finding 10 — Debug Info Is Incomplete

**Status:** ✅ Fixed  
**Severity:** Low (correctness only, no performance impact)

- ~~`isOptimized` flag is hardcoded to `false` regardless of `-O` level.~~
  ✅ Fixed — `isOptimized` is now `true` when optimization level > O0.
- ~~No local variable debug info — only function-level.~~
  ✅ Fixed — Named locals (let bindings, let rec, match bindings) now emit
  `DW_TAG_auto_variable` entries with `llvm.dbg.declare` linking each DWARF
  variable to its corresponding alloca slot. Source-level names are carried
  from lowering via `IrFunction.LocalNames`.
- ~~No parameter debug info — subroutine types created with no parameters.~~
  ✅ Fixed — Lambda parameters (arg slot 1) now emit `DW_TAG_formal_parameter`
  via `DIBuilderCreateParameterVariable`, making them visible in debuggers.
- ~~Uses C99 language identifier (no Ashes DWARF language code).~~
  ✅ Fixed — Now uses a custom language code (`0x8001`) from the DWARF
  user-defined range (`DW_LANG_lo_user`–`DW_LANG_hi_user`), so debuggers
  no longer misidentify Ashes programs as C99.

------------------------------------------------------------------------

## Priority Recommendations

### Quick wins (high impact, low effort)

| # | Item | Status |
|---|------|--------|
| 1 | Add heap bounds check | ✅ Done |
| 2 | Fix pattern match double-comparison | ✅ Done |
| 3 | Replace fixed heap with dynamic `mmap`/`VirtualAlloc` chunks | ✅ Done |
| 4 | Add `LLVMBuildMemCpy` interop and use in `EmitCopyBytes` | ✅ Done |
| 5 | Replace string comparison byte loop with `memcmp` | ✅ Done |
| 6 | Wire `isOptimized` debug flag to optimization level | ✅ Done |

### Medium-term (moderate effort, high impact)

| # | Item | Status |
|---|------|--------|
| 7 | Add PLT32 relocation support, then enable LLVM optimization passes | ✅ Done |
| 8 | Add function attributes to all emitted and builtin functions | ✅ Done |
| 9 | Add `LLVMSetTailCall` and mark tail calls at LLVM level | ✅ Done |
| 10 | Enable IR identity / strength reduction (`x + 0`, `x * 1`, `x * 2`) | ✅ Done |
| 11 | Add `--target-cpu` CLI flag for CPU-specific codegen | ✅ Done |
| 12 | Dead store elimination for unused `StoreLocal` | ✅ Done |
| 13 | Constant propagation across single-predecessor labels | ✅ Done |
| 14 | Replace all byte-by-byte store loops with global constant + `memcpy` | ✅ Done |
| 15 | Place string literals in `.rodata` as global constants (no heap alloc) | ✅ Done |
| 16 | Emit DWARF local variable and parameter debug info | ✅ Done |

### Long-term (significant effort)

| # | Item | Status |
|---|------|--------|
| 9 | Implement arena-based region deallocation (no GC — ownership-driven) | Phase 1 ✅, Phase 2a ✅, Phase 2b ✅, Phase 2c ✅, Phase 2d ✅, Phase 3 ✅ |
| 10 | Implement escape analysis — stack-allocate closures / ADTs that don't escape | Open |
| 11 | Decision tree pattern matching for large ADTs | Open |
