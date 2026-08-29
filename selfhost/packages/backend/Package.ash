// Public package root for the self-hosted LLVM backend.
//
// Boundary:
// - Owns LLVM C API bindings, IR-to-LLVM codegen, and linking.
// - Depends on Semantics for the typed IR it lowers, now genuinely: `AshesCompiler.Backend.IrCodegen`
//   walks a real `IrFunction` and drives `AshesCompiler.Backend.Llvm` from its actual instructions.
//
// Status: `AshesCompiler.Backend.Llvm` (a growing subset of the LLVM C API) proven end-to-end by
// `selfhost/tests/backend`'s hand-built test programs; `AshesCompiler.Backend.IrCodegen` covers
// scalar arithmetic, locals, control flow, and the entry function's exit-syscall contract;
// `AshesCompiler.Backend.ElfLinker` links a single self-contained function into a runnable static
// linux-x64 executable (no dynamic linking, no relocations yet). See
// docs/md/future/SELF_HOSTING.md's "LLVM code generation and runtime integration" checklist for
// the complete remaining surface (`src/Ashes.Backend/Llvm/Interop/LlvmApi.cs` and
// `src/Ashes.Backend/Llvm/LlvmImageLinkerElf.cs` are the sources of truth for every entry point
// the real backend needs).

import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen
import AshesCompiler.Backend.ElfLinker
Unit
