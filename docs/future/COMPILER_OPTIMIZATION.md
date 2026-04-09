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

1. LLVM module-level optimization passes are completely disabled (dead code)
2. ~~Memory is never freed — 4 MB bump allocator with no bounds checking
   silently crashes on overflow~~ ✅ Bounds checking added
3. Manual byte loops replace LLVM intrinsics for string operations
4. ~~Pattern matching uses double comparisons where single equality checks
   suffice~~ ✅ Fixed
5. No function attributes — LLVM cannot reason about `nounwind`, `noalias`,
   `readonly`, etc.
6. CPU features are empty — compiled as generic x86-64 with no SSE4.2/AVX

------------------------------------------------------------------------

## Finding 1 — LLVM Optimization Passes Are Dead Code

**Status:** Open — highest-impact single item  
**Severity:** Critical impact  
**Files:** `LlvmCodegen.cs:129-158`, `LlvmImageLinkerElf.cs:15-17`

`RunLlvmOptimizationPasses()` is defined but **never called**. The custom
ELF/PE linkers cannot handle `R_X86_64_PLT32` and other relocation types
that LLVM passes produce.

### What this means

- No `mem2reg` (promotes alloca to SSA registers) — all locals go through
  memory.
- No function inlining across lambda boundaries.
- No loop-invariant code motion.
- No common subexpression elimination.
- No dead code elimination at the LLVM level.
- No constant propagation beyond what the IR optimizer already does.

Only codegen-level optimizations work — instruction selection, register
allocation, and basic peephole optimizations via `LlvmCodeGenOptLevel`.

### Fix path

Add `R_X86_64_PLT32` (type 4) support to the ELF linker, then wire up the
pass manager. PLT32 is treated identically to PC32 for static executables
(`S + A - P`). For PE, add `IMAGE_REL_AMD64_REL32_1` through `_5` support.

------------------------------------------------------------------------

## Finding 2 — Memory Management

**Status:** Partially addressed ✅  
**Severity:** Critical

### The allocator

```
HeapSizeBytes = 4 MB (fixed)
EmitAlloc: cursor += size; return old_cursor;
```

- ~~No bounds check — if `cursor + size > 4 MB` the program writes past the
  array and crashes with SIGSEGV.~~ ✅ **Fixed.** `EmitAlloc` and
  `EmitAllocDynamic` now compare `nextCursor > heapEnd` and branch to an
  OOM panic that prints `"Runtime error: heap memory exhausted"` and exits
  cleanly.
- The theoretical limit is 262 144 cons cells (each 16 bytes).

### No deallocation (still open)

- `Drop` instructions exist in IR for semantic correctness.
- Backend `EmitDrop()` is a no-op for all heap types (only sockets get
  closed).
- For any long-running program (server, event loop), guaranteed OOM.

### Recommendations

1. ✅ **Done:** Heap bounds checking — clean error on overflow.
2. **Short-term:** Consider growing the heap via `mmap` / `VirtualAlloc`
   when exhausted.
3. **Long-term:** Implement a real allocator (arena + generational GC, or
   reference counting).

------------------------------------------------------------------------

## Finding 3 — Manual Byte Loops Instead of LLVM Intrinsics

**Status:** Open  
**Severity:** High  
**Files:** `LlvmCodegenMemory.cs:103-148`

### String concatenation (`EmitCopyBytes`)

Copies bytes one at a time via a loop:

```
for i = 0 to length:
    dest[i] = src[i]   // one byte at a time
```

Should use `LLVMBuildMemCpy()` which maps to `llvm.memcpy` intrinsic —
vectorized by LLVM to `rep movsb` or `movaps` instructions.

### String comparison

Compares byte-by-byte via 5+ basic blocks. Could use `llvm.memcmp` for the
data portion after a length check.

### String-to-CString conversion

Copies the entire string byte-by-byte to add a null terminator.

### Impact

For a string of N bytes the current code generates N load+store pairs.
`llvm.memcpy` generates 1 instruction that LLVM can optimize to SIMD
copies. For a 1 KB string that is ~1 000× fewer IR instructions.

### Fix path

Add `LLVMBuildMemCpy` to the LLVM interop layer (`LlvmApi.cs`) and replace
`EmitCopyBytes` with a single call.

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

**Status:** Open  
**Severity:** Medium-High  

No functions are annotated with LLVM attributes. The APIs
(`LLVMAddAttributeAtIndex`, `LLVMCreateEnumAttribute`) are not even
declared in the interop layer.

### Missing attributes

| Attribute | Applies to | Effect |
|-----------|-----------|--------|
| `nounwind` | All Ashes functions | Skip unwind tables → smaller code |
| `noalias` | Return values of heap allocations | Unique pointer guarantees |
| `readonly` | Pure helper functions | Enables CSE and LICM |
| `nonnull` | Non-optional pointers | Better optimization |
| `willreturn` | Guaranteed-terminating functions | Enables more transforms |

Industry benchmarks show 5–15 % improvement from proper attribute
annotation even without full passes.

### Fix path

Add `LLVMAddAttributeAtIndex` and `LLVMCreateEnumAttribute` to
`LlvmApi.cs`. Start with `nounwind` on every emitted function.

------------------------------------------------------------------------

## Finding 6 — Generic CPU Target

**Status:** Open  
**Severity:** Medium  
**File:** `LlvmTargetSetup.cs`

- x86-64 uses `cpu = "x86-64"` with **empty features string**.
- ARM64 uses `cpu = "generic"`.
- No SSE4.2, AVX, or AVX2 features are enabled.
- Code runs on any x86-64 CPU but leaves 10–30 % performance on the table
  for modern CPUs.

### Recommendation

Add a `--target-cpu` CLI flag. Default can remain generic but allow users
to target their specific CPU for maximum performance (e.g.
`--target-cpu=native`).

------------------------------------------------------------------------

## Finding 7 — IR Optimizer Is 50 % Disabled

**Status:** Open  
**Severity:** Medium  
**File:** `IrOptimizer.cs`

| Pass | Status | Notes |
|------|--------|-------|
| Borrow Elision | ❌ No-op | Awaits temp aliasing infrastructure |
| Constant Folding | ✅ Active | Good for scalars; resets at labels |
| Dead Code Elimination | ⚠️ Minimal | Only removes unused `LoadConst*` |
| Drop Elision | ❌ No-op | Awaits per-object `free()` allocator |

### Missing optimizations at IR level

- **Identity elimination:** `x + 0`, `x * 1`, `x * 0` not simplified.
- **Strength reduction:** `x * 2` is a multiply, not `x + x` or shift.
- **Unreachable code elimination:** Code after unconditional jumps or
  returns remains.
- **Dead store elimination:** Unused `StoreLocal` not removed.
- **Constant propagation across simple labels:** Labels with a single
  predecessor could propagate constant knowledge.

------------------------------------------------------------------------

## Finding 8 — TCO Implementation

**Status:** Good — minor gaps open  
**Severity:** Low-Medium

### What works

The compiler implements IR-level tail call optimization that converts
tail-recursive self-calls into loops. Verified working with 1 000 000
iterations in constant stack space.

### Gaps

- **Non-tail recursion:** `n + f(n - 1)` with 1 M depth → SIGSEGV.
  Expected, but there is no safety net.
- **No mutual recursion TCO:** Only self-calls are detected.
- **No LLVM-level tail calls:** `LLVMSetTailCall()` is not declared, so
  LLVM cannot optimize calls that the IR-level TCO misses.
- **Stack depth:** Effectively limited to ~10 K–100 K non-TCO recursive
  calls depending on frame size.

### Recommendation

Add `LLVMSetTailCall` to the interop layer and mark tail-position calls at
the LLVM level as a safety net.

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

**Status:** Open  
**Severity:** Low (correctness only, no performance impact)

- `isOptimized` flag is hardcoded to `false` regardless of `-O` level.
- No local variable debug info — only function-level.
- No parameter debug info — subroutine types created with no parameters.
- Uses C99 language identifier (no Ashes DWARF language code).

------------------------------------------------------------------------

## Priority Recommendations

### Quick wins (high impact, low effort)

| # | Item | Status |
|---|------|--------|
| 1 | Add heap bounds check | ✅ Done |
| 2 | Fix pattern match double-comparison | ✅ Done |
| 3 | Add `LLVMBuildMemCpy` interop and use in `EmitCopyBytes` | Open |

### Medium-term (moderate effort, high impact)

| # | Item | Status |
|---|------|--------|
| 4 | Add PLT32 relocation support, then enable LLVM optimization passes | Open |
| 5 | Add `nounwind` attribute to all emitted functions | Open |
| 6 | Add `LLVMSetTailCall` and mark tail calls at LLVM level | Open |
| 7 | Enable IR identity / strength reduction (`x + 0`, `x * 1`, `x * 2`) | Open |

### Long-term (significant effort)

| # | Item | Status |
|---|------|--------|
| 8 | Replace bump allocator with growing heap or real GC | Open |
| 9 | Implement escape analysis — stack-allocate closures / ADTs that don't escape | Open |
| 10 | Decision tree pattern matching for large ADTs | Open |
