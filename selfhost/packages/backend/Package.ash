// Public package root for the self-hosted LLVM backend.
//
// Boundary:
// - Owns LLVM C API bindings and (eventually) IR-to-LLVM codegen and linking.
// - Depends on Semantics for the typed IR it will eventually lower; the first slice (LLVM C API
//   bindings) does not use it yet, but the dependency is declared up front per the package DAG.
//
// Status: only `AshesCompiler.Backend.Llvm` exists so far — a small subset of the LLVM C API
// (module/function/type/value/builder creation and verification) proven end-to-end by
// `selfhost/tests/backend`. See docs/md/future/SELF_HOSTING.md's "LLVM code generation and runtime
// integration" checklist for the complete remaining surface
// (`src/Ashes.Backend/Llvm/Interop/LlvmApi.cs` is the source of truth for every entry point the
// real backend needs).

import AshesCompiler.Backend.Llvm
Unit
