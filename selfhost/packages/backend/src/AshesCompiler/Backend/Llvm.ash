// Pure-Ashes bindings to the LLVM C API used by the self-hosted backend.
//
// Boundary:
// - This is a deliberately small subset of `src/Ashes.Backend/Llvm/Interop/LlvmApi.cs`'s
//   `LibraryImport` surface (the source of truth for every entry point the eventual backend
//   needs): just enough to build, and verify a trivial function in, an LLVM module end to end.
//   Extending this file toward the full surface is future self-hosting work (see
//   docs/md/future/SELF_HOSTING.md's "LLVM code generation and runtime integration" checklist).
// - Every binding names the bare `libLLVM.so`/`.dll` (no path prefix), resolved by the OS loader
//   relative to the compiled executable's own directory: a Linux binary carries a `$ORIGIN`
//   RUNPATH for exactly this, and Windows' default DLL search order already checks the
//   executable's own directory. Placing a matching `libLLVM` build there (and satisfying whatever
//   it itself needs) is the responsibility of whoever runs a program built against this module —
//   it makes no promise about where that copy comes from.
// - `external` declarations (including the opaque `LLVM*Ref` handle types) are never exported, so
//   every binding below is re-exported as an ordinary `let`. A caller never needs to spell one of
//   the handle type names: type inference carries an opaque handle's identity across the module
//   boundary through the exported functions' inferred signatures.
// - `contextCreate` (a truly nullary external), `functionType` (an `FfiBuffer` parameter), and
//   `verifyModule` (an `out` parameter) wrap their external calls in a lambda instead of aliasing
//   them directly, since all three kinds require a direct call. `functionType` takes its parameter
//   count from the caller rather than deriving it from the parameter list's length: there is no
//   shipped Int-to-u32 narrowing helper yet (only `Ashes.Number.UInt.fromInt`, which narrows to
//   `u8`), so deriving it here would invent unproven conversion logic rather than port an existing
//   one. `verifyModule` returns `(isBroken, message)`: `isBroken` is LLVM's own verification result
//   (`true` means the module is invalid) and `message` is the diagnostic text, if any
//   (`LLVMVerifierFailureAction`'s three values from llvm-c/Analysis.h are exposed as
//   `verifier*Action`; pass `verifierReturnStatusAction` to get a value back rather than aborting
//   the process or writing straight to stderr).

export (
    value contextCreate,
    value contextDispose,
    value createModule,
    value disposeModule,
    value createBuilder,
    value disposeBuilder,
    value positionBuilderAtEnd,
    value int32Type,
    value functionType,
    value addFunction,
    value appendBasicBlock,
    value constInt,
    value buildRet,
    value verifyModule,
    value verifierAbortProcessAction,
    value verifierPrintMessageAction,
    value verifierReturnStatusAction,
)

external type LLVMContextRef
external type LLVMModuleRef
external type LLVMTypeRef
external type LLVMValueRef
external type LLVMBasicBlockRef
external type LLVMBuilderRef
external LLVMContextCreate() -> LLVMContextRef = "LLVMContextCreate@libLLVM.so"
external LLVMContextDispose(LLVMContextRef) -> void = "LLVMContextDispose@libLLVM.so"
external LLVMModuleCreateWithNameInContext(Str, LLVMContextRef) -> LLVMModuleRef = "LLVMModuleCreateWithNameInContext@libLLVM.so"
external LLVMDisposeModule(LLVMModuleRef) -> void = "LLVMDisposeModule@libLLVM.so"
external LLVMCreateBuilderInContext(LLVMContextRef) -> LLVMBuilderRef = "LLVMCreateBuilderInContext@libLLVM.so"
external LLVMDisposeBuilder(LLVMBuilderRef) -> void = "LLVMDisposeBuilder@libLLVM.so"
external LLVMPositionBuilderAtEnd(LLVMBuilderRef, LLVMBasicBlockRef) -> void = "LLVMPositionBuilderAtEnd@libLLVM.so"
external LLVMInt32TypeInContext(LLVMContextRef) -> LLVMTypeRef = "LLVMInt32TypeInContext@libLLVM.so"
external LLVMFunctionType(LLVMTypeRef, FfiBuffer(LLVMTypeRef), u32, Bool) -> LLVMTypeRef = "LLVMFunctionType@libLLVM.so"
external LLVMAddFunction(LLVMModuleRef, Str, LLVMTypeRef) -> LLVMValueRef = "LLVMAddFunction@libLLVM.so"
external LLVMAppendBasicBlockInContext(LLVMContextRef, LLVMValueRef, Str) -> LLVMBasicBlockRef = "LLVMAppendBasicBlockInContext@libLLVM.so"
external LLVMConstInt(LLVMTypeRef, u64, Bool) -> LLVMValueRef = "LLVMConstInt@libLLVM.so"
external LLVMBuildRet(LLVMBuilderRef, LLVMValueRef) -> LLVMValueRef = "LLVMBuildRet@libLLVM.so"
external LLVMDisposeMessage(*u8) -> void = "LLVMDisposeMessage@libLLVM.so"
external LLVMVerifyModule(LLVMModuleRef, u32, out FfiStr(owned LLVMDisposeMessage)) -> Bool = "LLVMVerifyModule@libLLVM.so"

let contextCreate unit = LLVMContextCreate(Unit)

let contextDispose context = LLVMContextDispose(context)

let createModule name context = LLVMModuleCreateWithNameInContext(name)(context)

let disposeModule module_ = LLVMDisposeModule(module_)

let createBuilder context = LLVMCreateBuilderInContext(context)

let disposeBuilder builder = LLVMDisposeBuilder(builder)

let positionBuilderAtEnd builder block = LLVMPositionBuilderAtEnd(builder)(block)

let int32Type context = LLVMInt32TypeInContext(context)

let addFunction module_ name type_ = LLVMAddFunction(module_)(name)(type_)

let appendBasicBlock context function name = LLVMAppendBasicBlockInContext(context)(function)(name)

let constInt type_ value signExtend = LLVMConstInt(type_)(value)(signExtend)

let buildRet builder value = LLVMBuildRet(builder)(value)

let verifierAbortProcessAction = 0u32

let verifierPrintMessageAction = 1u32

let verifierReturnStatusAction = 2u32

let functionType returnType paramTypes paramCount isVarArg = LLVMFunctionType(returnType)(paramTypes)(paramCount)(isVarArg)

let verifyModule module_ action = LLVMVerifyModule(module_)(action)
