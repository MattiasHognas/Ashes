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
//   `verifyModule`/`getTargetFromTriple`/`targetMachineEmitToMemoryBuffer` (`out` parameters) wrap
//   their external calls in a lambda instead of aliasing them directly, since all three kinds
//   require a direct call. `functionType` takes its parameter count from the caller rather than
//   deriving it from the parameter list's length: there is no shipped Int-to-u32 narrowing helper
//   yet (only `Ashes.Number.UInt.fromInt`, which narrows to `u8`), so deriving it here would invent
//   unproven conversion logic rather than port an existing one. `verifyModule` returns
//   `(isBroken, message)`: `isBroken` is LLVM's own verification result (`true` means the module is
//   invalid) and `message` is the diagnostic text, if any (`LLVMVerifierFailureAction`'s three
//   values from llvm-c/Analysis.h are exposed as `verifier*Action`; pass
//   `verifierReturnStatusAction` to get a value back rather than aborting the process or writing
//   straight to stderr).
// - Object emission only initializes the x86 target (linux-x64 first, per
//   docs/md/future/SELF_HOSTING.md's own porting order — it "unblocks every later phase that only
//   needs one working target"); AArch64/Windows initializers are future work. `createTargetMachine`
//   takes `cpu`/`features` as plain arguments rather than always calling `hostCpuName`/
//   `hostCpuFeatures` itself, so a caller can still pass `""`/`""` for LLVM's generic settings
//   (e.g. cross-compiling for a target that isn't the host).
// - `applyDataLayout` mirrors `LlvmTargetSetup.cs`'s own helper: derive the target machine's data
//   layout string and set it on the module, disposing the intermediate `LLVMTargetDataRef`. Callers
//   should call it once, right after `setTarget`, before adding any type or value that depends on
//   struct/pointer layout — this package's own trivial-`i32`-function test doesn't strictly need it
//   (no aggregates), but a real caller building anything with structs, arrays, or ABI-sensitive
//   types does.

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
    value initializeX86Target,
    value setTarget,
    value getTargetFromTriple,
    value createTargetMachine,
    value disposeTargetMachine,
    value targetMachineEmitToMemoryBuffer,
    value getBufferStart,
    value getBufferSize,
    value disposeMemoryBuffer,
    value objectFileType,
    value assemblyFileType,
    value relocModeStatic,
    value codeModelDefault,
    value codeGenOptLevelNone,
    value applyDataLayout,
    value hostCpuName,
    value hostCpuFeatures,
)

external type LLVMContextRef
external type LLVMModuleRef
external type LLVMTypeRef
external type LLVMValueRef
external type LLVMBasicBlockRef
external type LLVMBuilderRef
external type LLVMTargetRef
external type LLVMTargetMachineRef
external type LLVMMemoryBufferRef
external type LLVMTargetDataRef
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
external LLVMInitializeX86TargetInfo() -> void = "LLVMInitializeX86TargetInfo@libLLVM.so"
external LLVMInitializeX86Target() -> void = "LLVMInitializeX86Target@libLLVM.so"
external LLVMInitializeX86TargetMC() -> void = "LLVMInitializeX86TargetMC@libLLVM.so"
external LLVMInitializeX86AsmPrinter() -> void = "LLVMInitializeX86AsmPrinter@libLLVM.so"
external LLVMSetTarget(LLVMModuleRef, Str) -> void = "LLVMSetTarget@libLLVM.so"
external LLVMGetTargetFromTriple(Str, out LLVMTargetRef, out *u8) -> Bool = "LLVMGetTargetFromTriple@libLLVM.so"
external LLVMCreateTargetMachine(LLVMTargetRef, Str, Str, Str, u32, u32, u32) -> LLVMTargetMachineRef = "LLVMCreateTargetMachine@libLLVM.so"
external LLVMDisposeTargetMachine(LLVMTargetMachineRef) -> void = "LLVMDisposeTargetMachine@libLLVM.so"
external LLVMTargetMachineEmitToMemoryBuffer(LLVMTargetMachineRef, LLVMModuleRef, u32, out *u8, out LLVMMemoryBufferRef) -> Bool = "LLVMTargetMachineEmitToMemoryBuffer@libLLVM.so"
external LLVMGetBufferStart(LLVMMemoryBufferRef) -> *u8 = "LLVMGetBufferStart@libLLVM.so"
external LLVMGetBufferSize(LLVMMemoryBufferRef) -> u64 = "LLVMGetBufferSize@libLLVM.so"
external LLVMDisposeMemoryBuffer(LLVMMemoryBufferRef) -> void = "LLVMDisposeMemoryBuffer@libLLVM.so"
external LLVMCreateTargetDataLayout(LLVMTargetMachineRef) -> LLVMTargetDataRef = "LLVMCreateTargetDataLayout@libLLVM.so"
external LLVMCopyStringRepOfTargetData(LLVMTargetDataRef) -> FfiStr(owned LLVMDisposeMessage) = "LLVMCopyStringRepOfTargetData@libLLVM.so"
external LLVMDisposeTargetData(LLVMTargetDataRef) -> void = "LLVMDisposeTargetData@libLLVM.so"
external LLVMSetDataLayout(LLVMModuleRef, Str) -> void = "LLVMSetDataLayout@libLLVM.so"
external LLVMGetHostCPUName() -> FfiStr(owned LLVMDisposeMessage) = "LLVMGetHostCPUName@libLLVM.so"
external LLVMGetHostCPUFeatures() -> FfiStr(owned LLVMDisposeMessage) = "LLVMGetHostCPUFeatures@libLLVM.so"

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

let initializeX86TargetInfo _ = LLVMInitializeX86TargetInfo(Unit)

let initializeX86TargetOnly _ = LLVMInitializeX86Target(Unit)

let initializeX86TargetMC _ = LLVMInitializeX86TargetMC(Unit)

let initializeX86AsmPrinter _ = LLVMInitializeX86AsmPrinter(Unit)

let initializeX86Target unit =
    Unit
    |> initializeX86TargetInfo
    |> initializeX86TargetOnly
    |> initializeX86TargetMC
    |> initializeX86AsmPrinter

let setTarget module_ triple = LLVMSetTarget(module_)(triple)

// Returns `(isBroken, target, errorMessage)`: `isBroken` is `false` when lookup succeeded, `target`
// is `Some` only on success, and `errorMessage` is `Some` only on failure — matching what
// `LLVMGetTargetFromTriple` itself actually populates, not a claim that both are always present.
let getTargetFromTriple triple = LLVMGetTargetFromTriple(triple)

let createTargetMachine target triple cpu features optLevel relocMode codeModel = LLVMCreateTargetMachine(target)(triple)(cpu)(features)(optLevel)(relocMode)(codeModel)

let disposeTargetMachine machine = LLVMDisposeTargetMachine(machine)

// Returns `(isBroken, errorMessage, memoryBuffer)`: `isBroken` is `false` when emission succeeded,
// `memoryBuffer` is `Some` only on success, and `errorMessage` is `Some` only on failure. The
// returned buffer owns native memory and must be freed with `disposeMemoryBuffer` once its bytes
// have been copied out (with `Ashes.Ffi.copyBytes`, via `getBufferStart`/`getBufferSize`).
let targetMachineEmitToMemoryBuffer machine module_ fileType = LLVMTargetMachineEmitToMemoryBuffer(machine)(module_)(fileType)

let getBufferStart memoryBuffer = LLVMGetBufferStart(memoryBuffer)

let getBufferSize memoryBuffer = LLVMGetBufferSize(memoryBuffer)

let disposeMemoryBuffer memoryBuffer = LLVMDisposeMemoryBuffer(memoryBuffer)

// `LLVMCodeGenFileType` (llvm-c/TargetMachine.h).
let objectFileType = 1u32

let assemblyFileType = 0u32

// The one `LLVMRelocMode`/`LLVMCodeModel`/`LLVMCodeGenOptLevel` value each currently in use, mirroring
// the real backend's own choice in `LlvmTargetSetup.cs` (`LlvmRelocMode.Static`,
// `LlvmCodeModel.Default`); optimization is `None` here since this package proves correctness, not
// performance. The remaining enum values are unbound until something needs them.
let relocModeStatic = 1u32

let codeModelDefault = 0u32

let codeGenOptLevelNone = 0u32

// `LLVMCopyStringRepOfTargetData` is direct-call-only (an `FfiStr` return), so this wraps rather
// than aliases the three-call sequence it takes to apply a target machine's data layout to a
// module. Silently leaves the module's data layout unset on the (unexpected) failure branch rather
// than panicking, matching `LLVMSetDataLayout`'s own C API contract: an unset data layout is a
// valid, if suboptimal, module state, not an error condition this binding needs to surface.
let applyDataLayout module_ machine =
    (let targetData = LLVMCreateTargetDataLayout(machine)
    in
        match LLVMCopyStringRepOfTargetData(targetData) with
            | Ok(layout) ->
                let _ = LLVMSetDataLayout(module_)(layout)
                in LLVMDisposeTargetData(targetData)
            | Error(_) -> LLVMDisposeTargetData(targetData))

let hostCpuName unit = LLVMGetHostCPUName(Unit)

let hostCpuFeatures unit = LLVMGetHostCPUFeatures(Unit)
