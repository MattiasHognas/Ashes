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

**Status:** Mostly addressed ✅  
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

### No deallocation (still open)

- `Drop` instructions exist in IR for semantic correctness.
- Backend `EmitDrop()` is a no-op for all heap types (only sockets get
  closed).
- For any long-running program (server, event loop), memory usage will grow
  monotonically. The OS will eventually refuse an allocation, but this is
  significantly more forgiving than the old hard 4 MB ceiling.

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
- **Limitations (Phase 2 work):**
  - Scopes returning heap types (String, List, ADTs, closures) skip arena
    reset — requires escape analysis or copy-out to support.
  - TCO loop iterations don't individually arena-reset yet.
  - Abandoned OS chunks from `EmitHeapGrow` are not reclaimed on reset.

### Recommendations

1. ✅ **Done:** Heap bounds checking — clean error on overflow.
2. ✅ **Done:** Growing heap via `mmap` / `VirtualAlloc` — no hard limit.
3. ✅ **Phase 1 done:** Arena-based scope deallocation for copy-type results.
4. **Phase 2:** Extend arena reset to heap-type results via copy-out or
   escape analysis. Integrate with TCO loop iterations.
5. **Long-term:** Per-function arena regions with `munmap` / `VirtualFree`
   for full memory reclamation.

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
| 9 | Implement arena-based region deallocation (no GC — ownership-driven) | Phase 1 ✅ |
| 10 | Implement escape analysis — stack-allocate closures / ADTs that don't escape | Open |
| 11 | Decision tree pattern matching for large ADTs | Open |
