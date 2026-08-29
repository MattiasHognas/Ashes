// Public package root for the self-hosted LLVM backend.
//
// Boundary:
// - Owns LLVM C API bindings and IR-to-LLVM codegen and linking.
// - Depends on Semantics for the typed IR it lowers, now genuinely: `AshesCompiler.Backend.IrCodegen`
//   walks a real `IrFunction` and drives `AshesCompiler.Backend.Llvm` from its actual instructions.
//
// Status: `AshesCompiler.Backend.Llvm` (a growing subset of the LLVM C API) proven end-to-end by
// `selfhost/tests/backend`'s hand-built test programs, and `AshesCompiler.Backend.IrCodegen` (a
// deliberately narrow scalar-arithmetic subset of real `IrFunction` instructions, no locals/arena
// bookkeeping yet). See docs/md/future/SELF_HOSTING.md's "LLVM code generation and runtime
// integration" checklist for the complete remaining surface
// (`src/Ashes.Backend/Llvm/Interop/LlvmApi.cs` is the source of truth for every entry point the
// real backend needs).

import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen
Unit
