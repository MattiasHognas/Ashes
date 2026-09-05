// The first genuinely IR-driven slice of the self-hosted backend: walks a REAL `IrFunction`
// produced by `AshesCompiler.Semantics` (via `AshesCompiler.Frontend.Parser.parseProgram` +
// `AshesCompiler.Semantics.CoreLowering.lowerCoreProgramWithSource`, the same pipeline
// `selfhost/tests/ir-program-parity` already trusts against stage 0) and drives
// `AshesCompiler.Backend.Llvm` from its actual instructions — not a human hand-simulating what
// codegen should produce, which is all every earlier test in this arc ever did.
//
// Boundary:
// - Covers `LoadConstInt`, `MulInt`, `AddInt`, `SubInt`, `CmpIntGt`, `StoreLocal`, `LoadLocal`,
//   `Label`, `Jump`, `JumpIfFalse`, and `Return` — enough for `simple_arith` and `let_bindings`
//   (`selfhost/tests/ir-program-parity`'s own two trusted scalar fixtures) plus a plain
//   `if`/`then`/`else` expression (not yet one of that suite's fixtures, since `if` still needs
//   the constructor-layout/closure machinery `pattern_match` does before it can join it — this
//   codegen doesn't need that, only real self-hosted lowering to succeed for the shape used).
//   `if`'s own lowered IR is exactly the SAME no-`phi` slot pattern this whole arc's earlier
//   hand-built tests already used (`buildMaxModule` et al.): a `StoreLocal` into a shared result
//   slot in each arm, joined by a `LoadLocal` after both arms converge on one label — the real
//   compiler's own strategy turns out to match this package's LLVM codegen model exactly.
//   `SaveArenaState`/`RestoreArenaState`/`ReclaimArenaChunks` bracket the scoped arena
//   `IrCodegen.Arena` implements (chunked bump allocation, watermark reset, chunk reclaim).
// - `PrintInt` is the first genuinely user-observable instruction this codegen supports: converts
//   its `Int` source to decimal ASCII in a 32-byte stack buffer (`printIntPrologue`/
//   `printIntDigitLoopBody`/`printIntWriteAndNewline`, porting `LlvmCodegenPlatform.cs`'s own
//   `EmitPrintInt`), then writes it via the raw, unbuffered Linux `write` syscall (`1`) —
//   `emitLinuxSyscallCall` generalizes `Return`'s own inline-assembly `syscall` mechanism to any
//   3-argument syscall, shared by `exit` and `write`. Entirely stack-local: no global/`.data`
//   reference anywhere, so it needs nothing new from `AshesCompiler.Backend.ElfLinker`'s current
//   relocation-free scope. A non-RC-managed `AllocAdt` is an arena cell; the RC-managed form is a
//   `malloc`'d header-carrying cell (`emitAllocAdtRuntimeManaged`). Every instruction kind this
//   codegen does not implement panics with a clear "unsupported" message rather than silently
//   producing wrong code.
// - Every IR value is a full-width `i64` word (architecture.md: "every value is an i64 word"), so
//   a temp environment is just `List((IrTemp, LLVMValueRef))` — no type-directed dispatch needed
//   for this instruction subset. `Ashes.Number.UInt.fromInt64` (added alongside the first version
//   of this slice) is what makes `LoadConstInt`'s dynamic `Int` payload usable with `constInt`'s
//   `u64` parameter.
// - Locals get one `buildAlloca`'d `i64` slot each, allocated up front from `IrFunction`'s own
//   `localCount` — looked up by index the same way temps are, in a separate, fixed
//   (never-appended-to) environment: which local index maps to which alloca pointer never changes
//   once the function's slots are built, only the value stored at that pointer does. Every other
//   fixed-size scratch slot an emitter needs (syscall scratch, branch-merging result slots, read
//   buffers) goes through `buildEntryAlloca`, which hoists it to the entry block so a `Jump`-based
//   loop body reuses one frame slot per iteration instead of growing the native stack; only
//   `emitStackAlloc` (`AllocStack`/`MakeClosureStack`) allocates at the current position.
// - Labels need a block PRE-CREATED before the main codegen pass, since a `Jump`/`JumpIfFalse` can
//   name a label that appears later in the instruction stream than the branch itself —
//   `collectLabelNames` walks the instruction list once up front to find every `Label`, and
//   `createLabelBlocks` turns each into a real (initially empty) `LLVMBasicBlockRef` before any
//   instruction is actually codegen'd. `JumpIfFalse` itself has no explicit "otherwise" target in
//   the IR (only the false-branch label; falling through is implicit), so it synthesizes an
//   unnamed continuation block for that implicit fallthrough and repositions the builder there —
//   the same shape `buildCondBr` needs two explicit blocks for, just with one of them anonymous.
// - The fold threads `(tempEnv, terminated)`, not just `tempEnv`: `terminated` tracks whether the
//   block currently being written already ends with a terminator, mirroring `LlvmCodegen.cs`'s own
//   flag exactly. The IR itself can rely on genuinely implicit fallthrough at a `Label` boundary
//   (an arm with no explicit `Jump` before the next label — an `if`'s last arm falling into the
//   merge point, say), but LLVM basic blocks have no fallthrough concept at all: every block must
//   end with an explicit terminator, so `Label` inserts a bridging `buildBr` to its own block
//   first whenever the block being left isn't terminated yet. Getting this wrong doesn't fail to
//   compile — emitting a function with an unterminated block segfaults `LLVMTargetMachineEmitToMemoryBuffer`
//   outright, which is how this was found.
// - `CodegenContext` bundles everything that stays fixed for a whole function (`context`/
//   `function_`, the scalar LLVM types in `CoreLlvmTypes`, the declared libc entry points in
//   `ExternalFunctions`, `localSlots`, `labelBlocks`, `stringLiteralGlobals`) so it threads through
//   as one value instead of an ever-growing parameter list; only `tempEnv` actually grows
//   instruction by instruction. Deeply nested per-block/per-phase construction (a basic block's
//   own several simultaneously-live values feeding one branch, say) is bundled into a small record
//   too (`PrintIntBlocks`, `StrCmpBlocks`, `PrintIntState`) rather than threaded positionally, and
//   the actual block-by-block emission is split into small named phase functions — `emitPrintInt`
//   and `emitStringEquals` are each a short linear sequence of such phases, not one long `let`
//   staircase.
// - `codegenProgram` builds the true program entry AND every lifted function in
//   `IrProgram.functions` (the ordinary helper functions a real program has: every top-level
//   `let f x = ...`, every lambda, every curried partial application), all as
//   `i64 label(i64 env, i64 arg, i64 flag)` with the exact calling convention
//   `LlvmCodegenExpressions.cs`'s `EmitCallClosure`/`EmitCallKnown` use. A lifted function's
//   `Return` is an ordinary `ret` of its result word. The entry function's `Return` is instead
//   lowered the way `LlvmCodegenExpressions.cs`'s `EmitReturn` lowers ONLY the entry function's
//   `Return`: normal program completion is not a `ret` at all — there is no return address on the
//   stack once the OS has jumped straight to this code as the process's actual entry point — it
//   is a raw Linux `exit` syscall (`60`, matching real Ashes semantics: the process always exits
//   `0` on normal completion; a different code needs the separate `Ashes.IO.exit`/`ExitProcess`
//   instruction, not attempted here) followed by `buildUnreachable`, since a syscall that
//   terminates the process never returns to the caller. The entry `Return`'s own `source` temp is
//   therefore unused — the computed value it names was real IR arithmetic and is still genuinely
//   built, just never surfaced as an exit code. `AshesCompiler.Backend.ElfLinker` (linux-x64)
//   links this codegen's output into a directly-runnable executable, so this is observable by
//   actually running one: `strace` shows a single `exit(0)` syscall and nothing else, matching the
//   disassembly's `syscall`+`unreachable` tail.
// - Closures are the real 32-byte `{code, env, packedEnvironmentSize, dropper}` objects
//   `LlvmCodegenExpressions.cs` lays out (`MakeClosure`/`MakeClosureStack`), called indirectly
//   through their `code` word (`CallClosure`) or directly by label once `IrOptimizer.ash` has
//   devirtualized the call (`CallKnown`); a captured environment is an `Alloc`'d block written
//   with `StoreMemOffset` and read back inside the callee with `LoadEnv` through local slot `0`.
//   Every non-RC-managed allocation this needs (`Alloc`, `MakeClosure`) is a scoped-arena bump
//   allocation (`IrCodegen.Arena`) — never a stack slot, since a closure and its environment
//   routinely outlive the frame that built them.

// - Split across slices per family: `IrCodegen.Support` (shared low-level emission helpers),
//   `IrCodegen.Filesystem` (File/Directory/readLine), and `IrCodegen.TextBytes`
//   (Text/Byte/Rune builtins); this file keeps the module/function drivers, the codegen
//   context, string-literal globals, tail-join fusion, and the per-instruction dispatch.

import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.TaglessAdtLayout.adtFieldOffsetBytes
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
import AshesCompiler.Backend.IrCodegen.Syscalls.LinuxX64
import AshesCompiler.Backend.IrCodegen.Arena
import AshesCompiler.Backend.IrCodegen.Copy
import AshesCompiler.Backend.IrCodegen.Rc
import AshesCompiler.Backend.IrCodegen.Filesystem
import AshesCompiler.Backend.IrCodegen.Environment
import AshesCompiler.Backend.IrCodegen.Process
import AshesCompiler.Backend.IrCodegen.Console
import AshesCompiler.Backend.IrCodegen.TextBytes
import Ashes.Number.UInt
export (
    value codegenEntryFunction,
    value codegenProgram,
)

// The narrow set of libc entry points this codegen can call — `malloc`/`free` for RC-managed
// `AllocAdt`/`RcDrop`, `memcmp` for `CmpStrEq`/`CmpStrNe`, `memcpy` for `ConcatStr`/`ConcatStrN`,
// plus the `DirectoryExternals` bundle from `IrCodegen.Filesystem` — declared once per module, matching
// `AshesCompiler.Backend.ElfLinker`'s own recognized-symbol table (any new entry here needs a
// matching one-line addition there).
type ExternalFunctions =
    | mallocFn: LLVMValueRef
    | mallocType: LLVMTypeRef
    | freeFn: LLVMValueRef
    | freeType: LLVMTypeRef
    | memcmpFn: LLVMValueRef
    | memcmpType: LLVMTypeRef
    | memcpyFn: LLVMValueRef
    | memcpyType: LLVMTypeRef
    | directoryExternals: DirectoryExternals

let declareExternalFunctions module_ context types =
    (let mallocType = functionType(types.ptrType)([types.i64])(1u32)(false)
    in
        let freeType =
            functionType(voidType(context))([types.ptrType])(1u32)(false)
        in
            let memcmpType = functionType(types.i32)([types.ptrType, types.ptrType, types.i64])(3u32)(false)
            in
                let memcpyType = functionType(types.ptrType)([types.ptrType, types.ptrType, types.i64])(3u32)(false)
                in
                    ExternalFunctions(
                        mallocFn = addFunction(module_)("malloc")(mallocType),
                        mallocType = mallocType,
                        freeFn = addFunction(module_)("free")(freeType),
                        freeType = freeType,
                        memcmpFn = addFunction(module_)("memcmp")(memcmpType),
                        memcmpType = memcmpType,
                        memcpyFn = addFunction(module_)("memcpy")(memcpyType),
                        memcpyType = memcpyType,
                        directoryExternals = declareDirectoryExternalFunctions(module_)(context)(types)
                    ))

// Bundles everything that stays fixed for a whole function so it threads through as one value
// instead of an ever-growing parameter list; only `tempEnv` (in `codegenInstructionKind`'s own
// fold state) actually grows instruction by instruction.
type CodegenContext =
    | context: LLVMContextRef
    | moduleRef: LLVMModuleRef
    | function_: LLVMValueRef
    | types: CoreLlvmTypes
    | externals: ExternalFunctions
    | localSlots: List((IrLocal, LLVMValueRef))
    | labelBlocks: List((Str, LLVMBasicBlockRef))
    | stringLiteralGlobals: List((Str, LLVMValueRef))
    | liftedFunctions: List((Str, LLVMValueRef))
    | closureFunctionType: LLVMTypeRef
    | envpGlobal: LLVMValueRef
    | consoleGlobals: ConsoleGlobals
    | arenaRuntime: ArenaRuntime
    | copyRuntime: Maybe(CopyRuntime)
    | isEntry: Bool

// Everything shared by every function in one module — computed once by `codegenFunctions`, then
// handed to each function body's own `CodegenContext` construction unchanged.
type ModuleCodegen =
    | moduleRef: LLVMModuleRef
    | moduleContext: LLVMContextRef
    | moduleTypes: CoreLlvmTypes
    | moduleExternals: ExternalFunctions
    | moduleStringLiteralGlobals: List((Str, LLVMValueRef))
    | moduleLiftedFunctions: List((Str, LLVMValueRef))
    | moduleClosureFunctionType: LLVMTypeRef
    | moduleEnvpGlobal: LLVMValueRef
    | moduleConsoleGlobals: ConsoleGlobals
    | moduleArenaRuntime: ArenaRuntime
    | moduleCopyRuntime: Maybe(CopyRuntime)
    | moduleBuilder: LLVMBuilderRef

// `i64 f(i64 env, i64 arg, i64 argumentOwnershipFlag)`: the one uniform native signature every
// lifted (non-entry) function has, matching `LlvmCodegenExpressions.cs`'s own `EmitCallClosure`/
// `EmitCallKnown` exactly — a closure call site never knows its callee's source-level arity, so
// every function takes its environment word and ONE argument word (currying supplies the rest via
// nested closures) plus the runtime-managed-argument flag `LoadArgumentOwnership` reads back.
let closureFunctionTypeOf i64 = functionType(i64)([i64, i64, i64])(3u32)(false)

// Every lifted function gets `internal` linkage, as `LlvmCodegen.cs`'s own declaration loop
// gives it — nothing outside the module ever names one. Not merely tidiness: a modern LLVM no
// longer treats an external-linkage symbol as `dso_local` under the static relocation model, so
// taking a default-linkage function's address (`MakeClosure`'s code word) compiles to a
// GOT-relative load (`R_X86_64_REX_GOTPCRELX`) that no GOT exists to satisfy here, whereas an
// internal symbol's address is a plain `.text`-relative reference and its `call` sites need no
// relocation at all.
let recursive declareLiftedFunctions module_ closureFnType functions =
    match functions with
        | [] -> []
        | function_ :: rest ->
            match function_ with
                | IrFunction { label = label } ->
                    let functionValue = addFunction(module_)(label)(closureFnType)
                    in
                        let _ = setLinkage(functionValue)(linkageInternal)
                        in (label, functionValue) :: declareLiftedFunctions(module_)(closureFnType)(rest)

// Allocates one `i64` slot per local, `0..count-1`, and returns the fixed `IrLocal -> LLVMValueRef`
// mapping every `StoreLocal`/`LoadLocal` in the function looks up by index.
let recursive allocateLocalSlots builder i64 count index =
    if index >= count
    then []
    else (index, buildAlloca(builder)(i64)("local" + Ashes.Text.fromInt(index))) :: allocateLocalSlots(builder)(i64)(count)(index + 1)

let recursive collectLabelNames instructions =
    match instructions with
        | [] -> []
        | instruction :: rest ->
            match instruction with
                | IrInstruction { instruction = kind } ->
                    match kind with
                        | Label(name) -> name :: collectLabelNames(rest)
                        | _ -> collectLabelNames(rest)

let recursive createLabelBlocks context function_ names =
    match names with
        | [] -> []
        | name :: rest -> (name, appendBasicBlock(context)(function_)(name)) :: createLabelBlocks(context)(function_)(rest)

// `1 << 62`: the same immortal-refcount sentinel `LlvmCodegenMemory.cs`'s own `EmitHeapStringLiteral`
// writes into a string literal's header instead of a real count of `1`. Decrementing this value by
// any realistic number of drops never reaches zero, so the existing `RcDrop` codegen (unchanged for
// this) naturally never frees a literal's static storage — no sentinel-aware branch needed there.
let runtimeRcImmortalSentinel = Ashes.Number.UInt.fromInt64(1 << 62)

let recursive stringLiteralByteConstants bytes i8 index length =
    if index >= length
    then []
    else
        let byteConstant =
            index
            |> Ashes.Byte.get(bytes)
            |> Ashes.Number.UInt.toInt
            |> Ashes.Number.UInt.fromInt64
            |> constInt(i8)
            |> (given (build) -> build(false))
        in byteConstant :: stringLiteralByteConstants(bytes)(i8)(index + 1)(length)

// Builds one `.rodata`-shaped global per string literal, matching `EmitHeapStringLiteral`'s exact
// layout — `{i64 immortalRefCount, i64 unusedAllocSize, i64 len, [N x i8] bytes}`, the same 16-byte
// RC header every heap ADT cell has, immediately followed by the string's own `len`+bytes payload —
// so a literal's value pointer (header address + 16, computed the same way `AllocAdt` computes its
// own payload pointer) is safe to pass anywhere an ordinary runtime-managed `Str` is expected, with
// no real heap allocation at all.
let buildStringLiteralGlobal module_ context i64 i8 index literal =
    match literal with
        | IrStringLiteral { label = label, value = value } ->
            let bytes = Ashes.Byte.fromText(value)
            in
                let length = Ashes.Byte.length(bytes)
                in
                    let lengthU64 = Ashes.Number.UInt.fromInt64(length)
                    in
                        let byteConstants = stringLiteralByteConstants(bytes)(i8)(0)(length)
                        in
                            let arrayTy = arrayType(i8)(lengthU64)
                            in
                                let structTy = structType(context)([i64, i64, i64, arrayTy])(4u32)(false)
                                in
                                    let structConst =
                                        constStruct(context)(
                                            [
                                                constInt(i64)(runtimeRcImmortalSentinel)(false),
                                                constInt(i64)(0u64)(false),
                                                constInt(i64)(lengthU64)(false),
                                                constArray(i8)(byteConstants)(lengthU64)
                                            ]
                                        )(4u32)(false)
                                    in
                                        let global = addGlobal(module_)(structTy)(".str_lit_" + Ashes.Text.fromInt(index))
                                        in
                                            let _ =
                                                Unit
                                                |> (given (_) -> setInitializer(global)(structConst))
                                                |> (given (_) -> setGlobalConstant(global)(true))
                                                |> (given (_) -> setLinkage(global)(linkageInternal))
                                            in (label, global)

let recursive buildStringLiteralGlobalsFromIndex module_ context i64 i8 index literals =
    match literals with
        | [] -> []
        | literal :: rest -> buildStringLiteralGlobal(module_)(context)(i64)(i8)(index)(literal) :: buildStringLiteralGlobalsFromIndex(module_)(context)(i64)(i8)(index + 1)(rest)

// `LLVMBuildSwitch`'s case count is a capacity hint, not a hard limit (LLVM grows the case table as
// needed) — a fixed reservation avoids needing an Int-to-`u32` conversion that doesn't exist yet
// (`Ashes.Number.UInt` only narrows to `u8`/widens to `u64`) for a value this codegen already knows
// at LLVM-IR-build time, not one it would need to compute from a runtime IR value.
let switchTagCaseCapacity = 8u32

// Resolves every case's label to its `LLVMBasicBlockRef` FIRST (pure reads out of `labelBlocks`,
// no FFI calls at all), returning `(tag, block)` pairs — deliberately NOT interleaved with the
// `LLVMAddCase` FFI calls that consume this list. Interleaving a `labelBlocks` lookup after an
// `addCase` FFI call was confirmed (by direct experiment) to corrupt later lookups into the same
// list — a real, reproducible miscompilation not yet root-caused to a specific line, most likely
// in how this self-hosted backend's own arena/scope machinery treats an FFI call boundary. Doing
// every read before any FFI call sidesteps it entirely; do not reorder this back into one pass.
let recursive resolveSwitchCases cases labelBlocks =
    match cases with
        | [] -> []
        | IrSwitchCase { tag = tag, label = label } :: rest -> (tag, lookupIndexed(label)(labelBlocks)) :: resolveSwitchCases(rest)(labelBlocks)

let recursive addResolvedSwitchCases switchInst i64 resolved =
    match resolved with
        | [] -> Unit
        | (tag, block) :: rest ->
            let _ =
                addCase(switchInst)(constInt(i64)(Ashes.Number.UInt.fromInt64(tag))(true))(block)
            in addResolvedSwitchCases(switchInst)(i64)(rest)

let codegenInstructionKind cx builder kind state =
    match state with
        | (tempEnv, terminated) ->
            match cx with
                | CodegenContext { context = context, moduleRef = moduleRef, function_ = function_, types = types, externals = externals, localSlots = localSlots, labelBlocks = labelBlocks, stringLiteralGlobals = stringLiteralGlobals, liftedFunctions = liftedFunctions, closureFunctionType = closureFunctionType, envpGlobal = envpGlobal, consoleGlobals = consoleGlobals, arenaRuntime = arena, copyRuntime = copyRuntime, isEntry = isEntry } ->
                    match types with
                        | CoreLlvmTypes { i64 = i64, i8 = i8, i1 = i1, ptrType = ptrType } ->
                            match externals with
                                | ExternalFunctions { mallocFn = mallocFn, mallocType = mallocType, freeFn = freeFn, freeType = freeType, memcmpFn = memcmpFn, memcmpType = memcmpType, memcpyFn = memcpyFn, memcpyType = memcpyType, directoryExternals = directoryExternals } ->
                                    match kind with
                                        | LoadConstInt(target, value) ->
                                            ((target, constInt(i64)(Ashes.Number.UInt.fromInt64(value))(true)) :: tempEnv, terminated)
                        // Represented the same as every other scalar in `tempEnv` — a plain `i64`
                        // (0 or 1), matching `StoreLocal`/`LoadLocal`'s uniform `i64` local slots.
                        // `CmpIntGt`/`CmpIntEq`/`CmpIntNe` below zero-extend their native `i1`
                        // `icmp` result to the same representation for exactly this reason: a Bool
                        // value must round-trip through a local slot (the `&&`/`||` desugaring in
                        // `CoreLowering.ash` stores its branch result into one) with no bits lost.
                                        | LoadConstBool(target, value) ->
                                            ((target, constInt(i64)(Ashes.Number.UInt.fromInt64(if value
                                            then 1
                                            else 0))(true)) :: tempEnv, terminated)
                        // The global's own value IS a pointer (to its header word), so the value
                        // pointer (past the header, matching `AllocAdt`'s own convention) is just a
                        // `+16` byte GEP off it directly — no `buildIntToPtr` round-trip needed
                        // first, unlike a temp-held pointer that already went through `i64`.
                                        | LoadConstStr(target, label) ->
                                            let global = lookupIndexed(label)(stringLiteralGlobals)
                                            in
                                                let valuePtr = gepBytes(builder)(i64)(i8)(global)(16)("str_lit_value_ptr")
                                                in ((target, buildPtrToInt(builder)(valuePtr)(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | MulInt(target, left, right) ->
                                            ((target, buildMul(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | AddInt(target, left, right) ->
                                            ((target, buildAdd(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | SubInt(target, left, right) ->
                                            ((target, buildSub(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | DivInt(target, left, right) ->
                                            ((target, buildSDiv(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | DivUInt(target, left, right) ->
                                            ((target, buildUDiv(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | AndInt(target, left, right) ->
                                            ((target, buildAnd(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | OrInt(target, left, right) ->
                                            ((target, buildOr(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | XorInt(target, left, right) ->
                                            ((target, buildXor(builder)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        // Both shifts mask the amount to `0..63` first, exactly as
                        // `LlvmCodegenExpressions.cs`'s `EmitShiftInt` does: an LLVM shift by 64 or
                        // more is poison, and `>>` on `Int` is the LOGICAL right shift (`lshr`),
                        // never arithmetic — the same choice stage 0 makes.
                                        | ShlInt(target, left, right) ->
                                            ((target, buildShl(builder)(lookupIndexed(left)(tempEnv))(tempEnv
                                            |> lookupIndexed(right)
                                            |> maskShiftAmount(builder)(i64))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | ShrInt(target, left, right) ->
                                            ((target, buildLShr(builder)(lookupIndexed(left)(tempEnv))(tempEnv
                                            |> lookupIndexed(right)
                                            |> maskShiftAmount(builder)(i64))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpIntGe(target, left, right) ->
                                            ((target, buildZExt(builder)(buildICmp(builder)(intPredicateSge)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target) + "_i1"))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpIntLt(target, left, right) ->
                                            ((target, buildZExt(builder)(buildICmp(builder)(intPredicateSlt)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target) + "_i1"))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpIntLe(target, left, right) ->
                                            ((target, buildZExt(builder)(buildICmp(builder)(intPredicateSle)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target) + "_i1"))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpUIntGt(target, left, right) ->
                                            ((target, buildZExt(builder)(buildICmp(builder)(intPredicateUgt)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target) + "_i1"))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpUIntGe(target, left, right) ->
                                            ((target, buildZExt(builder)(buildICmp(builder)(intPredicateUge)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target) + "_i1"))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpUIntLt(target, left, right) ->
                                            ((target, buildZExt(builder)(buildICmp(builder)(intPredicateUlt)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target) + "_i1"))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpUIntLe(target, left, right) ->
                                            ((target, buildZExt(builder)(buildICmp(builder)(intPredicateUle)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target) + "_i1"))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | PrintBool(source) ->
                                            let _ =
                                                tempEnv
                                                |> lookupIndexed(source)
                                                |> emitPrintBool(context)(function_)(i64)(i8)(builder)
                                            in (tempEnv, false)
                                        | CmpIntGt(target, left, right) ->
                                            ((target, buildZExt(builder)(buildICmp(builder)(intPredicateSgt)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target) + "_i1"))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpIntEq(target, left, right) ->
                                            ((target, buildZExt(builder)(buildICmp(builder)(intPredicateEq)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target) + "_i1"))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpIntNe(target, left, right) ->
                                            ((target, buildZExt(builder)(buildICmp(builder)(intPredicateNe)(lookupIndexed(left)(tempEnv))(lookupIndexed(right)(tempEnv))("t" + Ashes.Text.fromInt(target) + "_i1"))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpStrEq(target, left, right) ->
                                            let result =
                                                tempEnv
                                                |> lookupIndexed(right)
                                                |> emitStringEquals(context)(function_)(i64)(ptrType)(builder)(memcmpFn)(memcmpType)(lookupIndexed(left)(tempEnv))
                                            in ((target, result) :: tempEnv, terminated)
                        // `1 - equalResult`, not a second comparison: `emitStringEquals` always
                        // returns exactly `0` or `1`, so inverting it arithmetically is sound and
                        // needs no extra branch beyond the one `CmpStrEq` already builds.
                                        | CmpStrNe(target, left, right) ->
                                            let equalResult =
                                                tempEnv
                                                |> lookupIndexed(right)
                                                |> emitStringEquals(context)(function_)(i64)(ptrType)(builder)(memcmpFn)(memcmpType)(lookupIndexed(left)(tempEnv))
                                            in
                                                let result =
                                                    buildSub(builder)(constInt(i64)(1u64)(false))(equalResult)("t" + Ashes.Text.fromInt(target))
                                                in ((target, result) :: tempEnv, terminated)
                        // `IrOptimizer.ash`'s `foldConcatStrChains` runs as the very last pass over
                        // the whole program and rewrites every `ConcatStr` it can safely fold into
                        // a `ConcatStrN` (declined only when an arena/stack bracket, a label, or a
                        // branch sits between the chain's parts — `chainRangeIsSafe`), so real
                        // source reaching this codegen almost always presents as `ConcatStrN`, not
                        // a bare two-operand `ConcatStr` — this case exists for robustness against
                        // whatever the fold declines, sharing the exact same N-ary helper with a
                        // two-element part list rather than a separate pairwise implementation.
                        // `parts`/`[left, right]` are resolved to LLVM values here, at the
                        // `codegenInstructionKind` call site — where every other case in this match
                        // resolves its temps via `lookupIndexed` — so `emitStringConcatN` and its
                        // helpers take plain `List(LLVMValueRef)` and never touch `tempEnv`.
                                        | BytesGet(target, bytes, index) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(index)
                                            |> emitBytesGet(context)(function_)(i64)(i8)(ptrType)(builder)(lookupIndexed(bytes)(tempEnv))) :: tempEnv, terminated)
                                        | BytesIndexOf(target, bytes, needle, from) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(from)
                                            |> emitBytesIndexOf(context)(function_)(i64)(i8)(ptrType)(builder)(lookupIndexed(bytes)(tempEnv))(lookupIndexed(needle)(tempEnv))) :: tempEnv, terminated)
                                        | BytesCompare(target, left, right) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(right)
                                            |> emitBytesCompare(context)(i64)(ptrType)(builder)(memcmpFn)(memcmpType)(lookupIndexed(left)(tempEnv))) :: tempEnv, terminated)
                                        | BytesSubText(target, bytes, start, count, _managed) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(count)
                                            |> emitBytesSubText(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(lookupIndexed(bytes)(tempEnv))(lookupIndexed(start)(tempEnv))) :: tempEnv, terminated)
                                        | BytesSubView(target, bytes, start, count) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(count)
                                            |> emitBytesSubView(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(lookupIndexed(bytes)(tempEnv))(lookupIndexed(start)(tempEnv))) :: tempEnv, terminated)
                        // A Float value travels through the uniform `i64` word as its raw `f64`
                        // bits — bitcast to `double` around each operation and back for storage,
                        // `LlvmCodegen.cs`'s `LoadTempAsFloat` shape exactly; an `fcmp` result
                        // zero-extends to the canonical 0/1 `i64` like every int comparison.
                                        | LoadConstFloat(target, value) ->
                                            ((target, buildBitCast(builder)(constReal(doubleType(context))(value))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | AddFloat(target, left, right) ->
                                            ((target, buildBitCast(builder)(buildFAdd(builder)(buildBitCast(builder)(lookupIndexed(left)(tempEnv))(doubleType(context))("fl" + Ashes.Text.fromInt(target)))(buildBitCast(builder)(lookupIndexed(right)(tempEnv))(doubleType(context))("fr" + Ashes.Text.fromInt(target)))("fadd" + Ashes.Text.fromInt(target)))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | SubFloat(target, left, right) ->
                                            ((target, buildBitCast(builder)(buildFSub(builder)(buildBitCast(builder)(lookupIndexed(left)(tempEnv))(doubleType(context))("fl" + Ashes.Text.fromInt(target)))(buildBitCast(builder)(lookupIndexed(right)(tempEnv))(doubleType(context))("fr" + Ashes.Text.fromInt(target)))("fsub" + Ashes.Text.fromInt(target)))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | MulFloat(target, left, right) ->
                                            ((target, buildBitCast(builder)(buildFMul(builder)(buildBitCast(builder)(lookupIndexed(left)(tempEnv))(doubleType(context))("fl" + Ashes.Text.fromInt(target)))(buildBitCast(builder)(lookupIndexed(right)(tempEnv))(doubleType(context))("fr" + Ashes.Text.fromInt(target)))("fmul" + Ashes.Text.fromInt(target)))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | DivFloat(target, left, right) ->
                                            ((target, buildBitCast(builder)(buildFDiv(builder)(buildBitCast(builder)(lookupIndexed(left)(tempEnv))(doubleType(context))("fl" + Ashes.Text.fromInt(target)))(buildBitCast(builder)(lookupIndexed(right)(tempEnv))(doubleType(context))("fr" + Ashes.Text.fromInt(target)))("fdiv" + Ashes.Text.fromInt(target)))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpFloatEq(target, left, right) ->
                                            ((target, buildZExt(builder)(buildFCmp(builder)(realPredicateOeq)(buildBitCast(builder)(lookupIndexed(left)(tempEnv))(doubleType(context))("fl" + Ashes.Text.fromInt(target)))(buildBitCast(builder)(lookupIndexed(right)(tempEnv))(doubleType(context))("fr" + Ashes.Text.fromInt(target)))("fcmp" + Ashes.Text.fromInt(target)))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpFloatNe(target, left, right) ->
                                            ((target, buildZExt(builder)(buildFCmp(builder)(realPredicateOne)(buildBitCast(builder)(lookupIndexed(left)(tempEnv))(doubleType(context))("fl" + Ashes.Text.fromInt(target)))(buildBitCast(builder)(lookupIndexed(right)(tempEnv))(doubleType(context))("fr" + Ashes.Text.fromInt(target)))("fcmp" + Ashes.Text.fromInt(target)))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpFloatGt(target, left, right) ->
                                            ((target, buildZExt(builder)(buildFCmp(builder)(realPredicateOgt)(buildBitCast(builder)(lookupIndexed(left)(tempEnv))(doubleType(context))("fl" + Ashes.Text.fromInt(target)))(buildBitCast(builder)(lookupIndexed(right)(tempEnv))(doubleType(context))("fr" + Ashes.Text.fromInt(target)))("fcmp" + Ashes.Text.fromInt(target)))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpFloatGe(target, left, right) ->
                                            ((target, buildZExt(builder)(buildFCmp(builder)(realPredicateOge)(buildBitCast(builder)(lookupIndexed(left)(tempEnv))(doubleType(context))("fl" + Ashes.Text.fromInt(target)))(buildBitCast(builder)(lookupIndexed(right)(tempEnv))(doubleType(context))("fr" + Ashes.Text.fromInt(target)))("fcmp" + Ashes.Text.fromInt(target)))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpFloatLt(target, left, right) ->
                                            ((target, buildZExt(builder)(buildFCmp(builder)(realPredicateOlt)(buildBitCast(builder)(lookupIndexed(left)(tempEnv))(doubleType(context))("fl" + Ashes.Text.fromInt(target)))(buildBitCast(builder)(lookupIndexed(right)(tempEnv))(doubleType(context))("fr" + Ashes.Text.fromInt(target)))("fcmp" + Ashes.Text.fromInt(target)))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CmpFloatLe(target, left, right) ->
                                            ((target, buildZExt(builder)(buildFCmp(builder)(realPredicateOle)(buildBitCast(builder)(lookupIndexed(left)(tempEnv))(doubleType(context))("fl" + Ashes.Text.fromInt(target)))(buildBitCast(builder)(lookupIndexed(right)(tempEnv))(doubleType(context))("fr" + Ashes.Text.fromInt(target)))("fcmp" + Ashes.Text.fromInt(target)))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | TextUnconsText(target, text, _managed) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(text)
                                            |> emitTextUnconsText(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)) :: tempEnv, terminated)
                                        | RuneToText(target, rune, managed) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(rune)
                                            |> emitRuneToText(builder)(i64)(i8)(emitPlacedPayloadPtr(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(managed))) :: tempEnv, terminated)
                                        | TextFromInt(target, value, managed) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(value)
                                            |> emitTextFromInt(context)(function_)(i64)(builder)(given (srcBytesAddr) ->
                                                given (len) -> emitPlacedStringFromBytesAddr(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(managed)(srcBytesAddr)(len)("from_int"))) :: tempEnv, terminated)
                                        | TextByteLength(target, text) ->
                                            ((target, emitStringLengthValue(builder)(i64)(ptrType)(lookupIndexed(text)(tempEnv))("text_byte_length")) :: tempEnv, terminated)
                                        | BytesLength(target, bytes) ->
                                            ((target, emitStringLengthValue(builder)(i64)(ptrType)(lookupIndexed(bytes)(tempEnv))("bytes_length")) :: tempEnv, terminated)
                                        | TextUncons(target, text, _managed) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(text)
                                            |> emitTextUncons(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)) :: tempEnv, terminated)
                                        | TextParseInt(target, text, _managed) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(text)
                                            |> emitTextParseInt(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)) :: tempEnv, terminated)
                                        | TextParseFloat(target, text, _managed) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(text)
                                            |> emitTextParseFloat(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)) :: tempEnv, terminated)
                                        | BytesSingleton(target, byte, managed) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(byte)
                                            |> emitBytesSingleton(builder)(i64)(i8)(emitPlacedPayloadPtr(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(managed))) :: tempEnv, terminated)
                                        | BytesHash(target, bytes) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(bytes)
                                            |> emitBytesHash(context)(function_)(i64)(i8)(ptrType)(builder)) :: tempEnv, terminated)
                                        | BytesAppendByte(target, bytes, byte, managed) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(byte)
                                            |> emitBytesAppendByte(builder)(i64)(i8)(ptrType)(emitPlacedPayloadPtrDynamic(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(managed))(memcpyFn)(memcpyType)(lookupIndexed(bytes)(tempEnv))) :: tempEnv, terminated)
                                        | BytesAllocate(target, length, managed) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(length)
                                            |> emitBytesAllocate(context)(function_)(i64)(i8)(builder)(emitPlacedPayloadPtrDynamic(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(managed))) :: tempEnv, terminated)
                                        | BytesFromList(target, list, _managed) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(list)
                                            |> emitBytesFromList(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)) :: tempEnv, terminated)
                                        | BytesEmpty(target, managed) ->
                                            ((target, managed
                                            |> emitPlacedPayloadPtr(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)
                                            |> emitBytesEmpty(builder)(i64)(i8)) :: tempEnv, terminated)
                                        | BytesAppend(target, left, right, managed) -> ((target, emitPlacedStringConcatN(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(managed)([lookupIndexed(left)(tempEnv), lookupIndexed(right)(tempEnv)])) :: tempEnv, terminated)
                                        | BytesU16Le(target, value, managed) ->
                                            ((target, emitBytesUnsignedLe(builder)(i64)(i8)(emitPlacedPayloadPtr(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(managed))(2)(lookupIndexed(value)(tempEnv))("bytes_u16")) :: tempEnv, terminated)
                                        | BytesU32Le(target, value, managed) ->
                                            ((target, emitBytesUnsignedLe(builder)(i64)(i8)(emitPlacedPayloadPtr(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(managed))(4)(lookupIndexed(value)(tempEnv))("bytes_u32")) :: tempEnv, terminated)
                                        | BytesU64Le(target, value, managed) ->
                                            ((target, emitBytesUnsignedLe(builder)(i64)(i8)(emitPlacedPayloadPtr(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(managed))(8)(lookupIndexed(value)(tempEnv))("bytes_u64")) :: tempEnv, terminated)
                                        | BytesGetU16Le(target, bytes, offset) ->
                                            ((target, emitBytesReadLeUnsigned(context)(function_)(i64)(i8)(ptrType)(builder)(2)([66, 121, 116, 101, 115, 46, 103, 101, 116, 85, 49, 54, 76, 101, 58, 32, 111, 102, 102, 115, 101, 116, 32, 111, 117, 116, 32, 111, 102, 32, 98, 111, 117, 110, 100, 115, 10])(lookupIndexed(bytes)(tempEnv))(lookupIndexed(offset)(tempEnv))("bytes_getu16")) :: tempEnv, terminated)
                                        | BytesGetU32Le(target, bytes, offset) ->
                                            ((target, emitBytesReadLeUnsigned(context)(function_)(i64)(i8)(ptrType)(builder)(4)([66, 121, 116, 101, 115, 46, 103, 101, 116, 85, 51, 50, 76, 101, 58, 32, 111, 102, 102, 115, 101, 116, 32, 111, 117, 116, 32, 111, 102, 32, 98, 111, 117, 110, 100, 115, 10])(lookupIndexed(bytes)(tempEnv))(lookupIndexed(offset)(tempEnv))("bytes_getu32")) :: tempEnv, terminated)
                                        | BytesGetU64Le(target, bytes, offset) ->
                                            ((target, emitBytesReadLeUnsigned(context)(function_)(i64)(i8)(ptrType)(builder)(8)([66, 121, 116, 101, 115, 46, 103, 101, 116, 85, 54, 52, 76, 101, 58, 32, 111, 102, 102, 115, 101, 116, 32, 111, 117, 116, 32, 111, 102, 32, 98, 111, 117, 110, 100, 115, 10])(lookupIndexed(bytes)(tempEnv))(lookupIndexed(offset)(tempEnv))("bytes_getu64")) :: tempEnv, terminated)
                                        | BytesSet(target, bytes, index, item, _reuse, _managed) ->
                                            ((target, emitBytesSetUnsigned(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(1)([66, 121, 116, 101, 115, 46, 115, 101, 116, 58, 32, 114, 97, 110, 103, 101, 32, 111, 117, 116, 32, 111, 102, 32, 98, 111, 117, 110, 100, 115, 10])(lookupIndexed(bytes)(tempEnv))(lookupIndexed(index)(tempEnv))(lookupIndexed(item)(tempEnv))("bytes_set")) :: tempEnv, terminated)
                                        | BytesSetU16Le(target, bytes, index, item, _reuse, _managed) ->
                                            ((target, emitBytesSetUnsigned(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(2)([66, 121, 116, 101, 115, 46, 115, 101, 116, 85, 49, 54, 76, 101, 58, 32, 114, 97, 110, 103, 101, 32, 111, 117, 116, 32, 111, 102, 32, 98, 111, 117, 110, 100, 115, 10])(lookupIndexed(bytes)(tempEnv))(lookupIndexed(index)(tempEnv))(lookupIndexed(item)(tempEnv))("bytes_setu16")) :: tempEnv, terminated)
                                        | BytesSetU32Le(target, bytes, index, item, _reuse, _managed) ->
                                            ((target, emitBytesSetUnsigned(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(4)([66, 121, 116, 101, 115, 46, 115, 101, 116, 85, 51, 50, 76, 101, 58, 32, 114, 97, 110, 103, 101, 32, 111, 117, 116, 32, 111, 102, 32, 98, 111, 117, 110, 100, 115, 10])(lookupIndexed(bytes)(tempEnv))(lookupIndexed(index)(tempEnv))(lookupIndexed(item)(tempEnv))("bytes_setu32")) :: tempEnv, terminated)
                                        | BytesSetU64Le(target, bytes, index, item, _reuse, _managed) ->
                                            ((target, emitBytesSetUnsigned(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(8)([66, 121, 116, 101, 115, 46, 115, 101, 116, 85, 54, 52, 76, 101, 58, 32, 114, 97, 110, 103, 101, 32, 111, 117, 116, 32, 111, 102, 32, 98, 111, 117, 110, 100, 115, 10])(lookupIndexed(bytes)(tempEnv))(lookupIndexed(index)(tempEnv))(lookupIndexed(item)(tempEnv))("bytes_setu64")) :: tempEnv, terminated)
                                        | BytesCopyRange(target, first, firstOffset, second, secondOffset, length, _reuse, _managed) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(length)
                                            |> emitBytesCopyRange(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(lookupIndexed(first)(tempEnv))(lookupIndexed(firstOffset)(tempEnv))(lookupIndexed(second)(tempEnv))(lookupIndexed(secondOffset)(tempEnv))) :: tempEnv, terminated)
                                        | BytesScanHash(target, bytes, needle, from) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(from)
                                            |> emitBytesScanHash(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(lookupIndexed(bytes)(tempEnv))(lookupIndexed(needle)(tempEnv))) :: tempEnv, terminated)
                                        | TextToHex(target, value, _managed) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(value)
                                            |> emitTextToHex(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)) :: tempEnv, terminated)
                                        | ConcatStr(target, left, right, managed) ->
                                            let result =
                                                emitPlacedStringConcatN(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(managed)(
                                                    [lookupIndexed(left)(tempEnv), lookupIndexed(right)(tempEnv)]
                                                )
                                            in ((target, result) :: tempEnv, terminated)
                                        | ConcatStrN(target, parts, managed) ->
                                            let result =
                                                emitPlacedStringConcatN(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(managed)(
                                                    Ashes.Collection.List.map(given (part) -> lookupIndexed(part)(tempEnv))(parts)
                                                )
                                            in ((target, result) :: tempEnv, terminated)
                        // A `Borrow` is a Perceus book-keeping marker (no retain/drop obligation
                        // crosses it) — with no real reference-count tracking in this codegen yet,
                        // it is exactly an alias of the same SSA value under a new temp number.
                                        | Borrow(target, sourceTemp) -> ((target, lookupIndexed(sourceTemp)(tempEnv)) :: tempEnv, terminated)
                        // An ordinary (arena) `RcDup` is a placement marker with no count to
                        // retain: an alias, like `Borrow`. The RC-managed form is the real retain
                        // (`IrCodegen.Rc`), identity-preserving so the target is the same word.
                                        | RcDup(target, sourceTemp, runtimeManaged, mayBeEmpty) ->
                                            if runtimeManaged
                                            then
                                                ((target, tempEnv
                                                |> lookupIndexed(sourceTemp)
                                                |> emitRuntimeManagedDup(context)(function_)(i64)(i8)(ptrType)(builder)(mayBeEmpty)) :: tempEnv, terminated)
                                            else ((target, lookupIndexed(sourceTemp)(tempEnv)) :: tempEnv, terminated)
                                        | RcIsUnique(target, sourceTemp) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(sourceTemp)
                                            |> emitRuntimeRcIsUnique(builder)(i64)(i8)(ptrType)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        // A closure's `CleanupResource` releases nothing, as in stage 0; a
                        // `FileHandle` closes its fd and a `Process` closes its pipes and reaps
                        // the child (`EmitResourceCleanup`). Sockets and declared external
                        // resources still need their destructors ported.
                                        | CleanupResource(sourceTemp, typeName, _destructor) ->
                                            if typeName == "Function"
                                            then (tempEnv, terminated)
                                            else
                                                if typeName == "FileHandle"
                                                then
                                                    let _ =
                                                        tempEnv
                                                        |> lookupIndexed(sourceTemp)
                                                        |> emitLinuxClose(builder)(i64)
                                                    in (tempEnv, terminated)
                                                else
                                                    if typeName == "Process"
                                                    then
                                                        let _ =
                                                            tempEnv
                                                            |> lookupIndexed(sourceTemp)
                                                            |> emitProcessDrop(context)(function_)(builder)(i64)(i8)(ptrType)
                                                        in (tempEnv, terminated)
                                                    else Ashes.IO.panic("codegen: CleanupResource for " + typeName + " not yet supported")
                        // `CopyOutArena`/`CopyOutList` move a value that lives above a scope's
                        // restored watermark to storage that survives the reset — an RC cell or an
                        // arena block below the watermark — before the chunks are reclaimed. Each
                        // is one call into the module-level helpers `IrCodegen.Arena` defines; the
                        // purpose operand does not change the copy.
                                        | CopyOutArena(destTemp, srcTemp, staticSizeBytes, runtimeManaged, _purpose, _semanticType) ->
                                            ((destTemp, emitCopyOutArena(builder)(i64)(arena)(lookupIndexed(srcTemp)(tempEnv))(staticSizeBytes)(runtimeManaged)("t" + Ashes.Text.fromInt(destTemp))) :: tempEnv, terminated)
                                        | CopyOutList(destTemp, srcTemp, headCopy, runtimeManaged, _purpose) ->
                                            ((destTemp, emitCopyOutList(builder)(i64)(arena)(lookupIndexed(srcTemp)(tempEnv))(headCopy)(runtimeManaged)("t" + Ashes.Text.fromInt(destTemp))) :: tempEnv, terminated)
                        // The rest of the copy family lives in `IrCodegen.Copy`: the closure copy
                        // (whose runtime-managed form dispatches to the program's `$env_normalize`
                        // functions by code address), the TCO accumulator's single-cell copy, and
                        // the persistent to-space/blob region allocations of the in-place reuse
                        // specializations.
                                        | CopyOutClosure(destTemp, srcTemp, runtimeManaged, _purpose) ->
                                            ((destTemp, emitCopyOutClosure(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(copyOutRuntimeOf(arena))(mallocFn)(mallocType)(closureFunctionType)(liftedFunctions)(runtimeManaged)(lookupIndexed(srcTemp)(tempEnv))("t" + Ashes.Text.fromInt(destTemp))) :: tempEnv, terminated)
                                        | CopyOutTcoListCell(destTemp, srcTemp, headCopy, _purpose) ->
                                            ((destTemp, emitCopyOutTcoListCell(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(copyOutRuntimeOf(arena))(headCopy)(lookupIndexed(srcTemp)(tempEnv))("t" + Ashes.Text.fromInt(destTemp))) :: tempEnv, terminated)
                                        | AllocAdtToSpace(target, tag, fieldCount, tagless) ->
                                            ((target, emitAllocAdtToSpace(context)(function_)(builder)(i64)(i8)(ptrType)(copyRuntimeOf(copyRuntime))(tag)(fieldCount)(tagless)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | CopyOutArenaToSpace(destTemp, srcTemp, staticSizeBytes) ->
                                            ((destTemp, emitCopyOutArenaToSpace(context)(function_)(builder)(i64)(i8)(ptrType)(copyRuntimeOf(copyRuntime))(copyOutRuntimeOf(arena))(memcpyFn)(memcpyType)(lookupIndexed(srcTemp)(tempEnv))(staticSizeBytes)("t" + Ashes.Text.fromInt(destTemp))) :: tempEnv, terminated)
                                        | CopyFixedInto(destTemp, srcTemp, sizeBytes) ->
                                            let _ =
                                                emitCopyFixedInto(builder)(i64)(ptrType)(memcpyFn)(memcpyType)(lookupIndexed(destTemp)(tempEnv))(lookupIndexed(srcTemp)(tempEnv))(sizeBytes)
                                            in (tempEnv, terminated)
                                        | CopyStringIntoOrFresh(destTemp, oldBlobTemp, srcTemp) ->
                                            ((destTemp, emitCopyStringIntoOrFresh(context)(function_)(builder)(i64)(i8)(ptrType)(copyRuntimeOf(copyRuntime))(copyOutRuntimeOf(arena))(lookupIndexed(oldBlobTemp)(tempEnv))(lookupIndexed(srcTemp)(tempEnv))("t" + Ashes.Text.fromInt(destTemp))) :: tempEnv, terminated)
                                        | CopyFixedIntoOrFresh(destTemp, oldBlobTemp, srcTemp, sizeBytes) ->
                                            ((destTemp, emitCopyFixedIntoOrFresh(context)(function_)(builder)(i64)(i8)(ptrType)(copyRuntimeOf(copyRuntime))(memcpyFn)(memcpyType)(lookupIndexed(oldBlobTemp)(tempEnv))(lookupIndexed(srcTemp)(tempEnv))(sizeBytes)("t" + Ashes.Text.fromInt(destTemp))) :: tempEnv, terminated)
                        // A `TcoResetPending` is the lowerer's placeholder for a back-edge block
                        // whose copy-out decision waited on inference; lowering replaces every one
                        // before the program is handed over, so reaching codegen is a lowering bug.
                                        | TcoResetPending(id, _usedTemps, _readLocalSlots) -> Ashes.IO.panic("codegen: TcoResetPending " + Ashes.Text.fromInt(id) + " reached the backend; lowering must resolve every deferred TCO reset")
                                        | StoreLocal(slot, source) ->
                                            let _ =
                                                localSlots
                                                |> lookupIndexed(slot)
                                                |> buildStore(builder)(lookupIndexed(source)(tempEnv))
                                            in (tempEnv, terminated)
                                        | LoadLocal(target, slot) ->
                                            ((target, buildLoad(builder)(i64)(lookupIndexed(slot)(localSlots))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | Label(name) ->
                                            let labelBlock = lookupIndexed(name)(labelBlocks)
                                            in
                                                let _ =
                                                    if terminated
                                                    then Unit
                                                    else
                                                        let _ = buildBr(builder)(labelBlock)
                                                        in Unit
                                                in
                                                    let _ = positionBuilderAtEnd(builder)(labelBlock)
                                                    in (tempEnv, false)
                                        | Jump(target) ->
                                            let _ =
                                                labelBlocks
                                                |> lookupIndexed(target)
                                                |> buildBr(builder)
                                            in (tempEnv, true)
                                        | JumpIfFalse(cond, target) ->
                                            let fallthroughBlock = appendBasicBlock(context)(function_)("fallthrough")
                                            in
                                // Every Bool value in `tempEnv` is a canonical 0/1 `i64` (see
                                // `LoadConstBool`/`CmpIntGt` above), but `buildCondBr` requires an
                                // `i1` condition — truncate back down right at the branch, the one
                                // place this codegen actually needs the narrower type.
                                                let condI1 =
                                                    buildTrunc(builder)(lookupIndexed(cond)(tempEnv))(i1)("cond_i1")
                                                in
                                                    let _ =
                                                        labelBlocks
                                                        |> lookupIndexed(target)
                                                        |> buildCondBr(builder)(condI1)(fallthroughBlock)
                                                    in
                                                        let _ = positionBuilderAtEnd(builder)(fallthroughBlock)
                                                        in (tempEnv, false)
                                        | SwitchTag(tagTemp, cases, defaultLabel) ->
                                            let resolved = resolveSwitchCases(cases)(labelBlocks)
                                            in
                                                let switchInst =
                                                    buildSwitch(builder)(lookupIndexed(tagTemp)(tempEnv))(lookupIndexed(defaultLabel)(labelBlocks))(switchTagCaseCapacity)
                                                in
                                                    let _ = addResolvedSwitchCases(switchInst)(i64)(resolved)
                                                    in (tempEnv, true)
                        // The TCO loop body's stack-pointer bracket — see `IrCodegen.Arena`.
                                        | SaveStackPointer(slot) ->
                                            let _ =
                                                localSlots
                                                |> lookupIndexed(slot)
                                                |> emitSaveStackPointer(builder)(i64)(arena)
                                            in (tempEnv, terminated)
                                        | RestoreStackPointer(slot) ->
                                            let _ =
                                                localSlots
                                                |> lookupIndexed(slot)
                                                |> emitRestoreStackPointer(builder)(i64)(ptrType)(arena)
                                            in (tempEnv, terminated)
                        // The scoped-arena brackets — see `IrCodegen.Arena`. The coroutine-loop
                        // form belongs to the async scheduler, which is not ported.
                                        | SaveArenaState(cursorSlot, endSlot, coroutineLoop) ->
                                            if coroutineLoop
                                            then Ashes.IO.panic("codegen: coroutine-loop arena bookkeeping not yet supported")
                                            else
                                                let _ =
                                                    localSlots
                                                    |> lookupIndexed(endSlot)
                                                    |> emitSaveArenaState(builder)(i64)(arena)(lookupIndexed(cursorSlot)(localSlots))
                                                in (tempEnv, terminated)
                                        | RestoreArenaState(cursorSlot, endSlot, preRestoreSlot, coroutineLoop) ->
                                            if coroutineLoop
                                            then Ashes.IO.panic("codegen: coroutine-loop arena bookkeeping not yet supported")
                                            else
                                                let _ =
                                                    localSlots
                                                    |> lookupIndexed(preRestoreSlot)
                                                    |> emitRestoreArenaState(builder)(i64)(arena)(lookupIndexed(cursorSlot)(localSlots))(lookupIndexed(endSlot)(localSlots))
                                                in (tempEnv, terminated)
                                        | ReclaimArenaChunks(savedEndSlot, preRestoreSlot, coroutineLoop) ->
                                            if coroutineLoop
                                            then Ashes.IO.panic("codegen: coroutine-loop arena bookkeeping not yet supported")
                                            else
                                                let _ =
                                                    localSlots
                                                    |> lookupIndexed(preRestoreSlot)
                                                    |> emitReclaimArenaChunks(context)(function_)(builder)(i64)(arena)(lookupIndexed(savedEndSlot)(localSlots))
                                                in (tempEnv, terminated)
                                        | PrintInt(source) ->
                                            let _ =
                                                tempEnv
                                                |> lookupIndexed(source)
                                                |> emitPrintInt(context)(function_)(i64)(builder)
                                            in (tempEnv, false)
                                        | PrintStr(source) ->
                                            let _ =
                                                tempEnv
                                                |> lookupIndexed(source)
                                                |> emitPrintStrBytesWithNewline(builder)(i64)(i8)(ptrType)
                                            in (tempEnv, false)
                        // Matches `LlvmCodegenExpressions.cs`'s own `EmitPanic` exactly: print the
                        // message through the SAME helper `PrintStr` uses (stage 0's own
                        // `EmitPanic` calls `EmitPrintStringFromTemp` — a panic's message goes to
                        // stdout, not a stderr-specific path), then exit `1` rather than `0`. A
                        // syscall that terminates the process never returns, so `terminated = true`
                        // here matches `Return`'s own case below, not the `false` every other
                        // instruction in this function returns.
                                        | PanicStr(source) ->
                                            let _ =
                                                tempEnv
                                                |> lookupIndexed(source)
                                                |> emitPrintStrBytesWithNewline(builder)(i64)(i8)(ptrType)
                                            in
                                                let _ =
                                                    false
                                                    |> constInt(i64)(1u64)
                                                    |> emitLinuxProcessExitWithCode(builder)(i64)
                                                in (tempEnv, true)
                        // See `emitAllocAdtRuntimeManaged` for the RC cell's layout; the ordinary
                        // form is a `[tag][fields...]` cell bumped from the scoped arena
                        // (`IrCodegen.Arena`'s `emitArenaAllocAdt`). A tagless cell (the flag every
                        // ADT instruction carries, see TaglessAdtLayout) has no tag word: its
                        // payload starts at offset 0 and the allocation is one word smaller.
                                        | AllocAdt(target, tag, fieldCount, runtimeManaged, tagless) ->
                                            let resultName = "t" + Ashes.Text.fromInt(target)
                                            in
                                                if runtimeManaged
                                                then ((target, emitAllocAdtRuntimeManaged(builder)(i64)(i8)(mallocFn)(mallocType)(tag)(fieldCount)(tagless)(resultName)) :: tempEnv, terminated)
                                                else ((target, emitArenaAllocAdt(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(tag)(fieldCount)(tagless)(resultName)) :: tempEnv, terminated)
                        // The reuse pair (`IrCodegen.Rc`): an arena `DropReuse` is statically
                        // unique, so its token is the cell itself; the RC-managed form consumes
                        // the source and yields the cell only when its count is `1`, else the null
                        // token that makes `AllocReusing` allocate a fresh cell.
                                        | DropReuse(target, sourceTemp, _fieldCount, runtimeManaged) ->
                                            if runtimeManaged
                                            then
                                                ((target, tempEnv
                                                |> lookupIndexed(sourceTemp)
                                                |> emitRuntimeDropReuse(context)(function_)(i64)(i8)(ptrType)(builder)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                            else ((target, lookupIndexed(sourceTemp)(tempEnv)) :: tempEnv, terminated)
                                        | AllocReusing(target, tag, fieldCount, tokenTemp, runtimeManaged, listCell, tagless) ->
                                            ((target, tempEnv
                                            |> lookupIndexed(tokenTemp)
                                            |> emitAllocReusing(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(tag)(fieldCount)(runtimeManaged)(listCell)(tagless)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        // Stores one field into an already-allocated ADT's payload: word `1 + fieldIndex`
                        // of a tagged cell (word `0` is the tag — see `AllocAdt`'s own layout comment
                        // above), word `fieldIndex` of a tagless one. The `ptr` operand arrives as this
                        // codegen's universal `i64` word representation, so it round-trips through
                        // `buildIntToPtr` before the byte-offset GEP.
                                        | SetAdtField(ptr, fieldIndex, source, tagless) ->
                                            let basePtr =
                                                buildIntToPtr(builder)(lookupIndexed(ptr)(tempEnv))(ptrType)("adt_field_base")
                                            in
                                                let fieldPtr =
                                                    gepBytes(builder)(i64)(i8)(basePtr)(adtFieldOffsetBytes(tagless)(fieldIndex))("adt_field_ptr")
                                                in
                                                    let _ =
                                                        buildStore(builder)(lookupIndexed(source)(tempEnv))(fieldPtr)
                                                    in (tempEnv, terminated)
                        // The read half of `SetAdtField`: same word offset, a load instead of a store.
                                        | GetAdtField(target, ptr, fieldIndex, tagless) ->
                                            let basePtr =
                                                buildIntToPtr(builder)(lookupIndexed(ptr)(tempEnv))(ptrType)("adt_field_base")
                                            in
                                                let fieldPtr =
                                                    gepBytes(builder)(i64)(i8)(basePtr)(adtFieldOffsetBytes(tagless)(fieldIndex))("adt_field_ptr")
                                                in ((target, buildLoad(builder)(i64)(fieldPtr)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        // Reads word `0` (the tag) — the same offset `AllocAdt` writes it to. A tagless
                        // cell has no tag word; `rejectTagReadsOfTaglessCells` refuses the function
                        // before any of its instructions are emitted.
                                        | GetAdtTag(target, ptr) ->
                                            let basePtr =
                                                buildIntToPtr(builder)(lookupIndexed(ptr)(tempEnv))(ptrType)("adt_tag_base")
                                            in ((target, buildLoad(builder)(i64)(basePtr)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        // An arena `RcDrop` is a placement marker with nothing to release. The
                        // RC-managed form is `IrCodegen.Rc`'s release: a structural dropper label
                        // resolves to the lifted function that owns the whole cascade, a `Function`
                        // releases its environment with itself, and `mayBeEmpty` guards the null
                        // empty list.
                                        | RcDrop(sourceTemp, typeName, _ownerSlot, runtimeManaged, mayBeEmpty, structuralDropperLabel) ->
                                            if runtimeManaged == false
                                            then (tempEnv, terminated)
                                            else
                                                let structuralDropper =
                                                    match structuralDropperLabel with
                                                        | Some(label) ->
                                                            liftedFunctions
                                                            |> lookupIndexed(label)
                                                            |> Some
                                                        | None -> None
                                                in
                                                    let _ =
                                                        tempEnv
                                                        |> lookupIndexed(sourceTemp)
                                                        |> emitRuntimeManagedDrop(context)(function_)(i64)(i8)(ptrType)(builder)(freeFn)(freeType)(closureFunctionType)(structuralDropper)(typeName == "Function")(mayBeEmpty)
                                                    in (tempEnv, false)
                        // See `closureSizeBytes`/`emitStoreClosureWords` above for the object's
                        // layout. The RC-managed form gets the same 16-byte header every other
                        // RC-managed allocation here has (so a future closure drop can walk back to
                        // it); the ordinary form is an arena bump allocation, since a closure and
                        // its environment routinely outlive the frame that built them.
                                        | MakeClosure(target, funcLabel, envPtrTemp, envSizeBytes, runtimeManaged, returnsRuntimeManaged, acceptsRuntimeManagedArgument) ->
                                            let closurePtr =
                                                if runtimeManaged
                                                then emitRcAllocPayloadPtr(builder)(i64)(i8)(mallocFn)(mallocType)(closureSizeBytes)("rc_closure")
                                                else
                                                    buildIntToPtr(builder)(emitArenaAlloc(context)(function_)(builder)(i64)(arena)(closureSizeBytes)("closure"))(ptrType)("closure_ptr")
                                            in
                                                let result =
                                                    emitStoreClosureWords(builder)(i64)(i8)(closurePtr)(lookupIndexed(funcLabel)(liftedFunctions))(lookupIndexed(envPtrTemp)(tempEnv))(
                                                        packClosureEnvironmentSize(envSizeBytes)(returnsRuntimeManaged)(acceptsRuntimeManagedArgument)
                                                    )("t" + Ashes.Text.fromInt(target))
                                                in ((target, result) :: tempEnv, terminated)
                                        | MakeClosureStack(target, funcLabel, envPtrTemp, envSizeBytes, returnsRuntimeManaged, acceptsRuntimeManagedArgument) ->
                                            let closurePtr = emitStackAlloc(builder)(i64)(closureSizeBytes)("closure_stack")
                                            in
                                                let result =
                                                    emitStoreClosureWords(builder)(i64)(i8)(closurePtr)(lookupIndexed(funcLabel)(liftedFunctions))(lookupIndexed(envPtrTemp)(tempEnv))(
                                                        packClosureEnvironmentSize(envSizeBytes)(returnsRuntimeManaged)(acceptsRuntimeManagedArgument)
                                                    )("t" + Ashes.Text.fromInt(target))
                                                in ((target, result) :: tempEnv, terminated)
                                        | LoadFuncAddr(target, funcLabel) ->
                                            ((target, buildPtrToInt(builder)(lookupIndexed(funcLabel)(liftedFunctions))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        // The runtime-managed-argument flag temp is `-1` when the call site has
                        // none (`IrText.ash`'s own `optionalIntOperand` convention, and what
                        // `LlvmCodegen.cs`'s `LoadRuntimeManagedArgumentFlag` checks for), in which
                        // case the callee receives a literal `0`. Resolved inline here rather than
                        // in a helper — see the `ConcatStr` cases above.
                                        | CallClosure(target, closureTemp, argTemp, flagTemp) ->
                                            let flagRef =
                                                if flagTemp < 0
                                                then constInt(i64)(0u64)(false)
                                                else lookupIndexed(flagTemp)(tempEnv)
                                            in
                                                let result =
                                                    emitCallClosure(builder)(i64)(i8)(ptrType)(closureFunctionType)(lookupIndexed(closureTemp)(tempEnv))(lookupIndexed(argTemp)(tempEnv))(flagRef)(
                                                        "t" + Ashes.Text.fromInt(target)
                                                    )
                                                in ((target, result) :: tempEnv, terminated)
                        // A direct call of a statically-known lifted function: same `(env, arg,
                        // flag)` signature as `CallClosure`, just naming the callee outright so
                        // LLVM can see (and inline) it. Always a plain call, never a native tail
                        // call: `LlvmCodegen.cs`'s `DetermineTailCallKind` analysis is not ported.
                                        | CallKnown(target, funcLabel, envTemp, argTemp, flagTemp, _environmentIsStackAllocated) ->
                                            let flagRef =
                                                if flagTemp < 0
                                                then constInt(i64)(0u64)(false)
                                                else lookupIndexed(flagTemp)(tempEnv)
                                            in
                                                let result =
                                                    buildCall(builder)(closureFunctionType)(lookupIndexed(funcLabel)(liftedFunctions))([lookupIndexed(envTemp)(tempEnv), lookupIndexed(argTemp)(tempEnv), flagRef])(3u32)(
                                                        "t" + Ashes.Text.fromInt(target)
                                                    )
                                                in ((target, result) :: tempEnv, terminated)
                                        | LoadEnv(target, index) ->
                                            ((target, emitLoadEnv(builder)(i64)(i8)(ptrType)(lookupIndexed(0)(localSlots))(index)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | LoadArgumentOwnership(target) -> ((target, getParam(function_)(2u32)) :: tempEnv, terminated)
                        // See `emitRcAllocPayloadPtr` for the RC form; the ordinary form is an
                        // arena bump allocation (`IrCodegen.Arena`).
                                        | Alloc(target, sizeBytes, runtimeManaged) ->
                                            let blockRef =
                                                if runtimeManaged
                                                then
                                                    buildPtrToInt(builder)(emitRcAllocPayloadPtr(builder)(i64)(i8)(mallocFn)(mallocType)(sizeBytes)("rc_alloc"))(i64)("t" + Ashes.Text.fromInt(target))
                                                else emitArenaAlloc(context)(function_)(builder)(i64)(arena)(sizeBytes)("t" + Ashes.Text.fromInt(target))
                                            in ((target, blockRef) :: tempEnv, terminated)
                                        | AllocStack(target, sizeBytes) ->
                                            ((target, buildPtrToInt(builder)(emitStackAlloc(builder)(i64)(sizeBytes)("stack_alloc"))(i64)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                                        | StoreMemOffset(basePtr, offsetBytes, source) ->
                                            let _ =
                                                "store_mem"
                                                |> memOffsetPtr(builder)(i64)(i8)(ptrType)(lookupIndexed(basePtr)(tempEnv))(offsetBytes)
                                                |> buildStore(builder)(lookupIndexed(source)(tempEnv))
                                            in (tempEnv, terminated)
                                        | LoadMemOffset(target, basePtr, offsetBytes) ->
                                            ((target, buildLoad(builder)(i64)(memOffsetPtr(builder)(i64)(i8)(ptrType)(lookupIndexed(basePtr)(tempEnv))(offsetBytes)("load_mem"))("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        // Only the entry function's `Return` is the process's own exit (see the
                        // header comment); a lifted function's is an ordinary `ret` of its `i64`
                        // result to whichever `CallClosure`/`CallKnown` invoked it.
                                        | Return(source) ->
                                            if isEntry
                                            then
                                                let _ = emitLinuxProcessExit(builder)(i64)
                                                in (tempEnv, true)
                                            else
                                                let _ =
                                                    tempEnv
                                                    |> lookupIndexed(source)
                                                    |> buildRet(builder)
                                                in (tempEnv, true)
                        // `Ashes.IO.exit(code)` — terminates the process immediately with the
                        // caller-chosen code, unlike `Return`'s always-`0` normal exit.
                                        | ExitProcess(source) ->
                                            let _ =
                                                tempEnv
                                                |> lookupIndexed(source)
                                                |> emitLinuxProcessExitWithCode(builder)(i64)
                                            in (tempEnv, true)
                        // `Ashes.IO.write`/`writeBytes` — a raw `write` syscall to stdout with no
                        // trailing newline. `CoreBuiltinLowering.ash`'s `emitCoreBuiltin` lowers
                        // both `CoreWrite` and `CoreWriteBytes` to this same instruction: a `Bytes`
                        // value shares its `[len:i64][bytes...]` header layout with `Str`, so no
                        // separate instruction is needed for the two builtins.
                                        | WriteStr(source) ->
                                            let _ =
                                                tempEnv
                                                |> lookupIndexed(source)
                                                |> emitWriteStrBytesToFd(builder)(i64)(ptrType)(constInt(i64)(1u64)(false))
                                            in (tempEnv, false)
                        // `Ashes.IO.writeError`/`writeErrorLine` — the same raw write, to fd 2
                        // (stderr) instead of fd 1, with the newline appended only when `newline`
                        // (`writeErrorLine`) is set.
                                        | WriteErrorStr(source, newline) ->
                                            let stringRef = lookupIndexed(source)(tempEnv)
                                            in
                                                let _ =
                                                    emitWriteStrBytesToFd(builder)(i64)(ptrType)(constInt(i64)(2u64)(false))(stringRef)
                                                in
                                                    let _ =
                                                        if newline
                                                        then
                                                            false
                                                            |> constInt(i64)(2u64)
                                                            |> emitWriteNewlineToFd(builder)(i64)(i8)
                                                        else constInt(i64)(0u64)(false)
                                                    in (tempEnv, false)
                        // `Ashes.IO.writeBuffered`/`writeBufferedLine` — the language contract only
                        // guarantees buffered output becomes visible by the next `flush` or process
                        // exit, never a stronger ordering; an immediate, unbuffered write to fd 1
                        // (the same raw-write path `WriteStr` already uses) trivially satisfies that
                        // — there is nothing left for a real buffer to defer. Trades the syscall
                        // count a real 64 KiB buffering ring would save for not needing one at all,
                        // matching this file's other "correct, not yet optimized" stand-ins (readLine
                        // above). `FlushStdout` below is a no-op for the same reason: an already-
                        // unbuffered stream has nothing to flush.
                                        | WriteBufferedStr(source, newline) ->
                                            let stringRef = lookupIndexed(source)(tempEnv)
                                            in
                                                let _ =
                                                    emitWriteStrBytesToFd(builder)(i64)(ptrType)(constInt(i64)(1u64)(false))(stringRef)
                                                in
                                                    let _ =
                                                        if newline
                                                        then
                                                            false
                                                            |> constInt(i64)(1u64)
                                                            |> emitWriteNewlineToFd(builder)(i64)(i8)
                                                        else constInt(i64)(0u64)(false)
                                                    in (tempEnv, false)
                                        | FlushStdout -> (tempEnv, false)
                        // `Ashes.IO.File.exists` — `openat`, then `close` on success. Linux's
                        // `open`/`openat` never fails to report existence in a way this builtin
                        // needs to surface as `Error` (a permission-denied path is simply "not
                        // opened", which stage 0's own `EmitLinuxFileExists` also reports as
                        // `Ok(false)`), so this always resolves `Ok(...)`, never `Error(...)`.
                                        | FileExists(target, path) ->
                                            let pathCstr =
                                                emitStringToCString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(lookupIndexed(path)(tempEnv))("file_exists_path")
                                            in
                                                let pathAddr = buildPtrToInt(builder)(pathCstr)(i64)("file_exists_path_addr")
                                                in
                                                    let resultSlot = buildEntryAlloca(builder)(i64)("file_exists_result")
                                                    in
                                                        let foundBlock = appendBasicBlock(context)(function_)("file_exists_found")
                                                        in
                                                            let missingBlock = appendBasicBlock(context)(function_)("file_exists_missing")
                                                            in
                                                                let continueBlock = appendBasicBlock(context)(function_)("file_exists_continue")
                                                                in
                                                                    let fd =
                                                                        false
                                                                        |> constInt(i64)(0u64)
                                                                        |> emitLinuxOpenat(builder)(i64)(pathAddr)(constInt(i64)(0u64)(false))
                                                                    in
                                                                        let openFailed =
                                                                            buildICmp(builder)(intPredicateSlt)(fd)(constInt(i64)(0u64)(false))("file_exists_open_failed")
                                                                        in
                                                                            let _ = buildCondBr(builder)(openFailed)(missingBlock)(foundBlock)
                                                                            in
                                                                                let _ = positionBuilderAtEnd(builder)(foundBlock)
                                                                                in
                                                                                    let _ = emitLinuxClose(builder)(i64)(fd)
                                                                                    in
                                                                                        let _ =
                                                                                            buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(constInt(i64)(1u64)(false))("file_exists_result"))(resultSlot)
                                                                                        in
                                                                                            let _ = buildBr(builder)(continueBlock)
                                                                                            in
                                                                                                let _ = positionBuilderAtEnd(builder)(missingBlock)
                                                                                                in
                                                                                                    let _ =
                                                                                                        buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(constInt(i64)(0u64)(false))("file_exists_result"))(resultSlot)
                                                                                                    in
                                                                                                        let _ = buildBr(builder)(continueBlock)
                                                                                                        in
                                                                                                            let _ = positionBuilderAtEnd(builder)(continueBlock)
                                                                                                            in ((target, buildLoad(builder)(i64)(resultSlot)("t" + Ashes.Text.fromInt(target))) :: tempEnv, terminated)
                        // `Ashes.IO.readLine` — one line from stdin as `Option(Str)`, `None` only
                        // at EOF with nothing left unread.
                                        | ReadLine(target) ->
                                            let resultValue = emitReadLine(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)
                                            in ((target, resultValue) :: tempEnv, terminated)
                        // `Ashes.IO.Console` — raw stdin mode over `TCGETS`/`TCSETS`, `ppoll` on stdin,
                        // and the monotonic clock.
                                        | ConsoleEnableRaw(target) ->
                                            let resultValue = emitConsoleEnableRaw(context)(function_)(builder)(i64)(i8)(types.i32)(ptrType)(consoleGlobals)
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | ConsoleRestore(_token) ->
                                            let _ = emitConsoleRestore(context)(function_)(builder)(i64)(ptrType)(consoleGlobals)
                                            in (tempEnv, terminated)
                                        | ConsolePoll(target, timeout) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(timeout)
                                                |> emitConsolePoll(context)(function_)(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | MonotonicMillis(target) ->
                                            let resultValue = emitMonotonicMillis(builder)(i64)(i8)
                                            in ((target, resultValue) :: tempEnv, terminated)
                        // `Ashes.IO.File.writeText`/`Ashes.IO.File.replace`/`Ashes.IO.Directory.createAll` —
                        // raw `openat`/`write`/`close`/`rename`/`mkdir` syscalls, no libc dependency.
                                        | FileWriteText(target, path, text) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(text)
                                                |> emitFileWriteText(context)(function_)(i64)(i8)(ptrType)(builder)(arena)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(lookupIndexed(path)(tempEnv))
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | FileReplace(target, source, destination) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(destination)
                                                |> emitFileReplace(context)(function_)(i64)(i8)(ptrType)(builder)(arena)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(lookupIndexed(source)(tempEnv))
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | DirectoryCreateAll(target, path) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(path)
                                                |> emitDirectoryCreateAll(context)(function_)(i64)(i8)(ptrType)(builder)(arena)(mallocFn)(mallocType)(memcpyFn)(memcpyType)
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | DirectoryEntries(target, path) ->
                                            let resultValue =
                                                emitDirectoryEntries(moduleRef)(context)(function_)(i64)(i8)(types.i32)(ptrType)(builder)(mallocFn)(mallocType)(freeFn)(freeType)(memcpyFn)(memcpyType)(directoryExternals)(lookupIndexed(path)(tempEnv))("dir_entries_t" + Ashes.Text.fromInt(target))
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | DirectoryRemoveTree(target, path) ->
                                            let resultValue =
                                                emitDirectoryRemoveTree(moduleRef)(context)(function_)(i64)(i8)(types.i32)(ptrType)(builder)(arena)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(directoryExternals)(lookupIndexed(path)(tempEnv))("dir_remove_t" + Ashes.Text.fromInt(target))
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | FileOpen(target, path) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(path)
                                                |> emitFileOpen(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | FileReadChunk(target, fileHandle, count) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(count)
                                                |> emitFileReadChunk(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(lookupIndexed(fileHandle)(tempEnv))
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | FileReadLine(target, fileHandle) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(fileHandle)
                                                |> emitReadLineFromFd(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | FileClose(target, fileHandle) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(fileHandle)
                                                |> emitFileClose(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | FileReadText(target, path) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(path)
                                                |> emitFileReadText(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | FileReadAllBytes(target, path) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(path)
                                                |> emitFileReadAllBytes(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | FileMmap(target, path) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(path)
                                                |> emitFileMmap(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | FileWriteBytes(target, path, bytes) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(bytes)
                                                |> emitFileWriteText(context)(function_)(i64)(i8)(ptrType)(builder)(arena)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(lookupIndexed(path)(tempEnv))
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | FileMakeExecutable(target, path) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(path)
                                                |> emitFileMakeExecutable(context)(function_)(i64)(i8)(types.i32)(ptrType)(builder)(arena)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(directoryExternals)
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | EnvironmentDirectory(target, directoryKind) ->
                                            let resultValue =
                                                match directoryKind with
                                                    | CurrentDirectory -> emitEnvironmentCurrentDirectory(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(directoryExternals)
                                                    | ExecutableDirectory -> emitEnvironmentExecutableDirectory(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(directoryExternals)
                                                    | TemporaryDirectory -> emitEnvironmentTemporaryDirectory(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(directoryExternals)
                                                    | CacheDirectory -> emitEnvironmentCacheDirectory(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(directoryExternals)
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | EnvironmentGet(target, name) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(name)
                                                |> emitEnvironmentGet(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(directoryExternals)
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | SpawnProcess(target, executable, args) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(args)
                                                |> emitSpawnProcess(context)(function_)(i64)(i8)(types.i32)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(envpGlobal)(lookupIndexed(executable)(tempEnv))
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | ProcessWriteStdin(target, process, text) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(text)
                                                |> emitProcessWriteStdin(context)(function_)(i64)(i8)(ptrType)(builder)(arena)(lookupIndexed(process)(tempEnv))
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | ProcessReadStdoutLine(target, process) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(process)
                                                |> emitProcessReadLine(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(true)
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | ProcessReadStderrLine(target, process) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(process)
                                                |> emitProcessReadLine(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(false)
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | ProcessWaitForExit(target, process) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(process)
                                                |> emitProcessWaitForExit(builder)(i64)(i8)(ptrType)
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | ProcessKill(target, process) ->
                                            let resultValue =
                                                tempEnv
                                                |> lookupIndexed(process)
                                                |> emitProcessKill(context)(function_)(builder)(i64)(i8)(ptrType)(arena)
                                            in ((target, resultValue) :: tempEnv, terminated)
                                        | _ -> Ashes.IO.panic("codegen: unsupported IrInstructionKind for this minimal slice")

// Whether any instruction allocates native stack memory reachable outside its own frame slot
// bookkeeping. `musttail` is a hard guarantee that the callee may reuse the caller's frame
// immediately, so a function that stack-allocates anything a callee could still reach (a closure
// environment, a handler frame) only gets the advisory `tail` marker — port of `LlvmCodegen.cs`'s
// `FunctionAllocatesNativeStackMemory`.
let recursive functionAllocatesStackMemory instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = AllocStack(_, _) } :: _ -> true
        | IrInstruction { instruction = MakeClosureStack(_, _, _, _, _, _) } :: _ -> true
        | _ :: rest -> functionAllocatesStackMemory(rest)

// The join every lowered multi-arm function body converges on: a function whose last three
// instructions are `Label(end); LoadLocal(x, slot); Return(x)` returns whatever each arm stored
// into `slot` before jumping to `end`. An arm whose stored value is a just-made `CallKnown` result
// is a tail call through that join, which the fusion below turns into a native tail call.
let recursive reverseInstructionList instructions acc =
    match instructions with
        | [] -> acc
        | head :: rest -> reverseInstructionList(rest)(head :: acc)

// Maps every join label from which the function's tail is nothing but slot-copy forwarding to the
// slot whose value it ultimately returns. Walking the reversed instruction list from the end:
// `LoadLocal(x, s); Return(x)` returns slot `s`; a `Label(L)` above that records `L -> s`; a
// `LoadLocal(t, s2); StoreLocal(s, t)` pair above forwards `s2` into `s`, so labels above it map
// to `s2` instead — multi-level match/if joins chain arbitrarily deep this way. The walk stops at
// the first instruction outside the copy chain. An arm that stores a call result into a mapped
// label's slot and jumps (or falls) into it is a tail call through the join, which the fusion
// below turns into a native tail call. A join reached only by a `Jump` (an `if` join whose
// block forwards its slot into the enclosing `match` join) is off the fallthrough chain, so
// `extendTailJoins` below adds every block that is nothing but such a forward into a mapped join.
let recursive collectTailJoins reversedInstructions currentSlot acc =
    match reversedInstructions with
        | IrInstruction { instruction = Label(name) } :: rest -> collectTailJoins(rest)(currentSlot)((name, currentSlot) :: acc)
        | IrInstruction { instruction = StoreLocal(storeSlot, stored) } :: IrInstruction { instruction = LoadLocal(loaded, sourceSlot) } :: rest ->
            if storeSlot == currentSlot
            then
                if stored == loaded
                then collectTailJoins(rest)(sourceSlot)(acc)
                else acc
            else acc
        | _ -> acc

let fallthroughTailJoins instructions =
    match reverseInstructionList(instructions)([]) with
        | IrInstruction { instruction = Return(source) } :: IrInstruction { instruction = LoadLocal(loaded, slot) } :: rest ->
            if source == loaded
            then collectTailJoins(rest)(slot)([])
            else []
        | _ -> []

// The lowerer closes a match arm's arena bracket (`RestoreArenaState`/`ReclaimArenaChunks`)
// between the arm's `StoreLocal` and its `Jump`. The tail-call fusion below matches on adjacency,
// so it looks past that bookkeeping; on the fused path a `musttail` call replaces this frame, so
// the arm's own restore can never run and the next enclosing bracket reclaims its scratch instead.
// Restores only ever release memory, so skipping one is always safe.
let recursive skipArenaBookkeeping instructions =
    match instructions with
        | IrInstruction { instruction = SaveArenaState(_cursor, _end, _managed) } :: rest -> skipArenaBookkeeping(rest)
        | IrInstruction { instruction = RestoreArenaState(_cursor, _end, _preRestore, _managed) } :: rest -> skipArenaBookkeeping(rest)
        | IrInstruction { instruction = ReclaimArenaChunks(_end, _preRestore, _managed) } :: rest -> skipArenaBookkeeping(rest)
        | _ -> instructions

let recursive lookupTailJoin (label: Str) (joins: List((Str, Int))) =
    match joins with
        | [] -> None
        | (candidate, slot) :: rest ->
            if candidate == label
            then Some(slot)
            else lookupTailJoin(label)(rest)

// Whether the instructions past a block's forwarding copy jump or fall into a join mapped to
// `storeSlot`, the slot that copy stored into.
let forwardsIntoTailJoin (joins: List((Str, Int))) (storeSlot: Int) instructions =
    match instructions with
        | IrInstruction { instruction = Jump(label) } :: _rest -> lookupTailJoin(label)(joins) == Some(storeSlot)
        | IrInstruction { instruction = Label(label) } :: _rest -> lookupTailJoin(label)(joins) == Some(storeSlot)
        | _ -> false

// One pass over the function: a block whose entire body (arena bookkeeping aside) is
// `LoadLocal(t, s2); StoreLocal(s1, t)` followed by a jump or fall into a join mapped to `s1` is
// itself a join mapped to `s2`. Returns the extended map and whether anything was added.
let recursive extendTailJoinsOnce (joins: List((Str, Int))) instructions (added: Bool) =
    match instructions with
        | [] -> (joins, added)
        | IrInstruction { instruction = Label(name) } :: rest ->
            match skipArenaBookkeeping(rest) with
                | IrInstruction { instruction = LoadLocal(loaded, sourceSlot) } :: IrInstruction { instruction = StoreLocal(storeSlot, stored) } :: afterStore ->
                    if loaded == stored && lookupTailJoin(name)(joins) == None && forwardsIntoTailJoin(joins)(storeSlot)(skipArenaBookkeeping(afterStore))
                    then extendTailJoinsOnce((name, sourceSlot) :: joins)(rest)(true)
                    else extendTailJoinsOnce(joins)(rest)(added)
                | _ -> extendTailJoinsOnce(joins)(rest)(added)
        | _ :: rest -> extendTailJoinsOnce(joins)(rest)(added)

// Iterates `extendTailJoinsOnce` to a fixed point, so a chain of jump-reached joins is mapped
// innermost-last however deeply the `if` and `match` joins nest.
let recursive extendTailJoins (joins: List((Str, Int))) instructions =
    match extendTailJoinsOnce(joins)(instructions)(false) with
        | (extended, true) -> extendTailJoins(extended)(instructions)
        | (extended, false) -> extended

let computeTailJoins instructions =
    extendTailJoins(fallthroughTailJoins(instructions))(instructions)

// How a `CallKnown` whose result an arm stores into a tail join's slot is fused. `fusionMustTail`
// false is the stack-allocating case: the call only gets the advisory `tail` marker and the store
// and jump are still emitted normally, so there is no continuation to skip to. Otherwise the call
// becomes `musttail` + `ret`, and `fusionContinuation` is what to resume with — everything after
// the arm's `Jump`, or the `Label` itself when the arm merely falls into the join (other blocks
// still branch to that label, so it must survive).
type TailJoinFusion =
    | fusionMustTail: Bool
    | fusionContinuation: List(IrInstruction)

let tailJoinFusionPlan (cx: CodegenContext) tailJoins allocatesStack environmentIsStackAllocated target storeSlot storeSource afterStore =
    (let storeForwardsCallResult = environmentIsStackAllocated == false && cx.isEntry == false && storeSource == target
    in
        match afterStore with
            | IrInstruction { instruction = Jump(jumpLabel) } :: restAfterJump ->
                match lookupTailJoin(jumpLabel)(tailJoins) with
                    | Some(joinSlot) ->
                        if storeForwardsCallResult && storeSlot == joinSlot
                        then Some(TailJoinFusion(fusionMustTail = allocatesStack == false, fusionContinuation = restAfterJump))
                        else None
                    | None -> None
            | IrInstruction { instruction = Label(nextLabel) } :: _restAfterLabel ->
                match lookupTailJoin(nextLabel)(tailJoins) with
                    | Some(joinSlot) ->
                        if storeForwardsCallResult && allocatesStack == false && storeSlot == joinSlot
                        then Some(TailJoinFusion(fusionMustTail = true, fusionContinuation = afterStore))
                        else None
                    | None -> None
            | _ -> None)

// The `(env, arg, flag)` direct call `CallKnown`'s dispatch case emits, shared with the fused
// tail-call path below.
let emitKnownCallValue cx builder tempEnv funcLabel envTemp argTemp flagTemp target =
    match cx with
        | CodegenContext { types = CoreLlvmTypes { i64 = i64 }, liftedFunctions = liftedFunctions, closureFunctionType = closureFunctionType } ->
            let flagRef =
                if flagTemp < 0
                then constInt(i64)(0u64)(false)
                else lookupIndexed(flagTemp)(tempEnv)
            in
                buildCall(builder)(closureFunctionType)(lookupIndexed(funcLabel)(liftedFunctions))([lookupIndexed(envTemp)(tempEnv), lookupIndexed(argTemp)(tempEnv), flagRef])(3u32)(
                    "t" + Ashes.Text.fromInt(target)
                )

// A `CallKnown` whose result the very next instruction returns is a native tail call: the loop a
// TCO'd recursive function compiles to. Without the marker every iteration pushes a frame and a
// deep loop overflows the stack (`LlvmCodegen.cs`'s `DetermineTailCallKind`). When nothing in the
// function stack-allocates escaping memory the pair is fused into `musttail` + `ret` (LLVM's
// verifier requires the call to precede its ret directly, so the ordinary temp store/load round
// trip must not run between them); otherwise the call keeps the advisory `tail` marker and the
// `Return` is emitted through the ordinary dispatch.
// Stage 0's fallthrough rule: an instruction past a terminator that is not a label (the dead code
// after a loop's back-edge jump, say) opens an unreachable block of its own, so no LLVM block
// ever carries an instruction after its terminator.
let reopenAfterTerminator (cx: CodegenContext) builder kind state =
    match (state, kind) with
        | ((_tempEnv, false), _) -> state
        | ((_tempEnv, true), Label(_name)) -> state
        | ((tempEnv, true), _) ->
            match cx with
                | CodegenContext { context = context, function_ = function_ } ->
                    let _ =
                        "fallthrough"
                        |> appendBasicBlock(context)(function_)
                        |> positionBuilderAtEnd(builder)
                    in (tempEnv, false)

let recursive codegenInstructions (cx: CodegenContext) builder allocatesStack tailJoins instructions state =
    match instructions with
        | [] -> state
        | IrInstruction { instruction = CallKnown(target, funcLabel, envTemp, argTemp, flagTemp, environmentIsStackAllocated) } :: afterCall ->
            state
            |> reopenAfterTerminator(cx)(builder)(CallKnown(target)(funcLabel)(envTemp)(argTemp)(flagTemp)(environmentIsStackAllocated))
            |> codegenKnownCall(cx)(builder)(allocatesStack)(tailJoins)(target)(funcLabel)(envTemp)(argTemp)(flagTemp)(environmentIsStackAllocated)(afterCall)
        | instruction :: rest ->
            match instruction with
                | IrInstruction { instruction = kind } ->
                    state
                    |> reopenAfterTerminator(cx)(builder)(kind)
                    |> codegenInstructionKind(cx)(builder)(kind)
                    |> codegenInstructions(cx)(builder)(allocatesStack)(tailJoins)(rest)
// A `CallKnown` whose result the next instruction past any arena bookkeeping returns or stores
// into a tail join's slot is fused into a tail call. The lowerer closes a call's own arena window
// between the call and that return or store; on the fused path the window's restore can never
// run, and the ordinary path emits it as written.
and codegenKnownCall (cx: CodegenContext) builder allocatesStack tailJoins target funcLabel envTemp argTemp flagTemp environmentIsStackAllocated afterCall state =
    match skipArenaBookkeeping(afterCall) with
        | IrInstruction { instruction = Return(source) } :: restAfterReturn ->
            if environmentIsStackAllocated || source != target || cx.isEntry
            then
                state
                |> codegenInstructionKind(cx)(builder)(CallKnown(target)(funcLabel)(envTemp)(argTemp)(flagTemp)(environmentIsStackAllocated))
                |> codegenInstructions(cx)(builder)(allocatesStack)(tailJoins)(afterCall)
            else
                match state with
                    | (tempEnv, _terminated) ->
                        let call = emitKnownCallValue(cx)(builder)(tempEnv)(funcLabel)(envTemp)(argTemp)(flagTemp)(target)
                        in
                            if allocatesStack
                            then
                                let _ = setTailCallKind(call)(tailCallKindTail)
                                in codegenInstructions(cx)(builder)(allocatesStack)(tailJoins)(afterCall)(((target, call) :: tempEnv, false))
                            else
                                let _ = setTailCallKind(call)(tailCallKindMustTail)
                                in
                                    let _ = buildRet(builder)(call)
                                    in codegenInstructions(cx)(builder)(allocatesStack)(tailJoins)(restAfterReturn)(((target, call) :: tempEnv, true))
        | IrInstruction { instruction = StoreLocal(storeSlot, storeSource) } :: afterStore ->
            match afterStore
            |> skipArenaBookkeeping
            |> tailJoinFusionPlan(cx)(tailJoins)(allocatesStack)(environmentIsStackAllocated)(target)(storeSlot)(storeSource) with
                | None ->
                    state
                    |> codegenInstructionKind(cx)(builder)(CallKnown(target)(funcLabel)(envTemp)(argTemp)(flagTemp)(environmentIsStackAllocated))
                    |> codegenInstructions(cx)(builder)(allocatesStack)(tailJoins)(afterCall)
                | Some(TailJoinFusion { fusionMustTail = false }) ->
                    match state with
                        | (tempEnv, _terminated) ->
                            let call = emitKnownCallValue(cx)(builder)(tempEnv)(funcLabel)(envTemp)(argTemp)(flagTemp)(target)
                            in
                                let _ = setTailCallKind(call)(tailCallKindTail)
                                in codegenInstructions(cx)(builder)(allocatesStack)(tailJoins)(afterCall)(((target, call) :: tempEnv, false))
                | Some(TailJoinFusion { fusionContinuation = continuation }) ->
                    match state with
                        | (tempEnv, _terminated) ->
                            let call = emitKnownCallValue(cx)(builder)(tempEnv)(funcLabel)(envTemp)(argTemp)(flagTemp)(target)
                            in
                                let _ = setTailCallKind(call)(tailCallKindMustTail)
                                in
                                    let _ = buildRet(builder)(call)
                                    in codegenInstructions(cx)(builder)(allocatesStack)(tailJoins)(continuation)(((target, call) :: tempEnv, true))
        | _ ->
            state
            |> codegenInstructionKind(cx)(builder)(CallKnown(target)(funcLabel)(envTemp)(argTemp)(flagTemp)(environmentIsStackAllocated))
            |> codegenInstructions(cx)(builder)(allocatesStack)(tailJoins)(afterCall)

// Builds one function's own scaffolding (entry block, local slots, label blocks) once its
// `irFunction`'s instructions are known and returns the `CodegenContext` its body is emitted
// under — everything module-level and function-independent comes from `mc` unchanged. A lifted
// function with `hasEnvAndArgParams` stores its two incoming words into local slots `0` (the
// environment — `LoadEnv` reads through it) and `1` (the argument) before anything else, exactly
// as `LlvmCodegen.cs`'s `EmitFunctionBodyAllocateSlots` does; the entry function takes no
// parameters at all.
let buildFunctionContext mc functionValue isEntry irFunction =
    match mc with
        | ModuleCodegen { moduleContext = context, moduleTypes = types, moduleExternals = externals, moduleStringLiteralGlobals = stringLiteralGlobals, moduleLiftedFunctions = liftedFunctions, moduleClosureFunctionType = closureFnType, moduleArenaRuntime = arena, moduleBuilder = builder } ->
            let entryBlock = appendBasicBlock(context)(functionValue)("entry")
            in
                let _ = positionBuilderAtEnd(builder)(entryBlock)
                in
                    match irFunction with
                        | IrFunction { instructions = instructions, localCount = localCount, hasEnvAndArgParams = hasEnvAndArgParams } ->
                            let localSlots = allocateLocalSlots(builder)(types.i64)(localCount)(0)
                            in
                                let _ =
                                    if isEntry == false && hasEnvAndArgParams
                                    then
                                    // The entry also maps the arena's first chunk before any
                                    // instruction can allocate.
                                        let _ =
                                            localSlots
                                            |> lookupIndexed(0)
                                            |> buildStore(builder)(getParam(functionValue)(0u32))
                                        in
                                            let _ =
                                                localSlots
                                                |> lookupIndexed(1)
                                                |> buildStore(builder)(getParam(functionValue)(1u32))
                                            in Unit
                                    else Unit
                                in
                                    let _ =
                                    // The entry receives the initial stack pointer in `rdi` (the
                                    // trampoline's `mov rdi, rsp`); the SysV layout there is
                                    // `[argc][argv...][NULL][envp...]`, so the environment vector
                                    // base is `sp + 8 * (argc + 2)`. Captured once into
                                    // `__ashes_envp` so `Process.spawn`'s child `execve` can hand
                                    // the parent environment on — stage 0's own
                                    // `EmitLinuxEntryEnvpCapture` exactly.
                                        if isEntry
                                        then
                                            let stackPointer = getParam(functionValue)(0u32)
                                            in
                                                let argc =
                                                    buildLoad(builder)(types.i64)(buildIntToPtr(builder)(stackPointer)(types.ptrType)("envp_stack_ptr"))("envp_argc")
                                                in
                                                    let envpBase =
                                                        buildAdd(builder)(stackPointer)(buildMul(builder)(buildAdd(builder)(argc)(constInt(types.i64)(2u64)(false))("envp_words"))(constInt(types.i64)(8u64)(false))("envp_offset"))("envp_base")
                                                    in
                                                        let _ = buildStore(builder)(envpBase)(mc.moduleEnvpGlobal)
                                                        in Unit
                                        else Unit
                                    in
                                        let _ =
                                            if isEntry
                                            then emitArenaInit(context)(functionValue)(builder)(types.i64)(types.i8)(types.ptrType)(arena)
                                            else Unit
                                        in
                                            let labelBlocks =
                                                instructions
                                                |> collectLabelNames
                                                |> createLabelBlocks(context)(functionValue)
                                            in
                                                let cx =
                                                    CodegenContext(
                                                        context = context,
                                                        moduleRef = mc.moduleRef,
                                                        function_ = functionValue,
                                                        types = types,
                                                        externals = externals,
                                                        localSlots = localSlots,
                                                        labelBlocks = labelBlocks,
                                                        stringLiteralGlobals = stringLiteralGlobals,
                                                        liftedFunctions = liftedFunctions,
                                                        closureFunctionType = closureFnType,
                                                        envpGlobal = mc.moduleEnvpGlobal,
                                                        consoleGlobals = mc.moduleConsoleGlobals,
                                                        arenaRuntime = arena,
                                                        copyRuntime = mc.moduleCopyRuntime,
                                                        isEntry = isEntry
                                                    )
                                                in (cx, instructions)

// The temps holding cells this function allocates with the tagless layout.
let recursive taglessCellTemps instructions =
    match instructions with
        | [] -> []
        | IrInstruction { instruction = AllocAdt(target, _tag, _fieldCount, _runtimeManaged, true) } :: rest -> target :: taglessCellTemps(rest)
        | IrInstruction { instruction = AllocAdtStack(target, _tag, _fieldCount, true) } :: rest -> target :: taglessCellTemps(rest)
        | IrInstruction { instruction = AllocAdtToSpace(target, _tag, _fieldCount, true) } :: rest -> target :: taglessCellTemps(rest)
        | IrInstruction { instruction = AllocReusing(target, _tag, _fieldCount, _tokenTemp, _runtimeManaged, _listCell, true) } :: rest -> target :: taglessCellTemps(rest)
        | _ :: rest -> taglessCellTemps(rest)

// A tagless cell has no tag word, so lowering never emits `GetAdtTag` for one (its single
// constructor tag is loaded as a constant instead). A tag read of a cell this function allocated
// tagless is a lowering bug that would otherwise silently read the first payload word.
let recursive rejectTagReadsOfTaglessCells taglessTemps instructions =
    match instructions with
        | [] -> Unit
        | IrInstruction { instruction = GetAdtTag(_target, ptr) } :: rest ->
            if containsInt(ptr)(taglessTemps)
            then Ashes.IO.panic("codegen: GetAdtTag reads t" + Ashes.Text.fromInt(ptr) + ", a tagless single-constructor cell with no tag word")
            else rejectTagReadsOfTaglessCells(taglessTemps)(rest)
        | _ :: rest -> rejectTagReadsOfTaglessCells(taglessTemps)(rest)

let codegenFunctionBody mc functionValue isEntry irFunction =
    match buildFunctionContext(mc)(functionValue)(isEntry)(irFunction) with
        | (cx, instructions) ->
            Unit
            |> (given (_) ->
                rejectTagReadsOfTaglessCells(taglessCellTemps(instructions))(instructions))
            |> (given (_) ->
                codegenInstructions(cx)(mc.moduleBuilder)(functionAllocatesStackMemory(instructions))(computeTailJoins(instructions))(instructions)(([], false)))
            |> (given (_) -> Unit)

// Emits every lifted function's body into the `LLVMValueRef` `declareLiftedFunctions` already
// created for its label — every function is declared before ANY body is emitted, so a body can
// name a function that appears later in `functions` (or itself, for recursion) via `MakeClosure`/
// `CallKnown`/`LoadFuncAddr` without any ordering constraint.
let recursive codegenLiftedFunctions mc functions =
    match functions with
        | [] -> Unit
        | function_ :: rest ->
            match function_ with
                | IrFunction { label = label } ->
                    let _ =
                        codegenFunctionBody(mc)(lookupIndexed(label)(mc.moduleLiftedFunctions))(false)(function_)
                    in codegenLiftedFunctions(mc)(rest)

// Whether any instruction is one of the copy family: only then does the module get the copy-out
// helpers, whose libc calls would otherwise make every program dynamically linked, and the
// persistent-region runtime (`IrCodegen.Copy`).
let recursive instructionsUseCopyOut instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = CopyOutArena(_dest, _src, _size, _managed, _purpose, _elementType) } :: _ -> true
        | IrInstruction { instruction = CopyOutList(_dest, _src, _headCopy, _managed, _purpose) } :: _ -> true
        | IrInstruction { instruction = CopyOutClosure(_dest, _src, _managed, _purpose) } :: _ -> true
        | IrInstruction { instruction = CopyOutTcoListCell(_dest, _src, _headCopy, _purpose) } :: _ -> true
        | IrInstruction { instruction = AllocAdtToSpace(_target, _tag, _fieldCount, _tagless) } :: _ -> true
        | IrInstruction { instruction = CopyOutArenaToSpace(_dest, _src, _size) } :: _ -> true
        | IrInstruction { instruction = CopyFixedInto(_dest, _src, _size) } :: _ -> true
        | IrInstruction { instruction = CopyStringIntoOrFresh(_dest, _old, _src) } :: _ -> true
        | IrInstruction { instruction = CopyFixedIntoOrFresh(_dest, _old, _src, _size) } :: _ -> true
        | _ :: rest -> instructionsUseCopyOut(rest)

let recursive functionsUseCopyOut functions =
    match functions with
        | [] -> false
        | IrFunction { instructions = instructions } :: rest -> instructionsUseCopyOut(instructions) || functionsUseCopyOut(rest)

// Builds `void <name>()` for `entryFunction` plus `i64 <label>(i64, i64, i64)` for every function
// in `functions`, all in one fresh module, and returns `(module_, builder)`, matching every other
// module builder's shape in `selfhost/tests/backend` so the same `emitModule` verification
// pipeline applies unchanged. The entry is `void`, not `i64`, since it genuinely never returns a
// value — every path ends in the exit syscall's `unreachable`, not a `ret`; a lifted function
// returns its `i64` result word normally. `malloc`/`free`/`memcmp`/`memcpy` are declared once per
// module (not re-declared per use site) with real pointer return/param types, and the string
// literal globals are built once per module too — both are shared by every function body. One
// `IRBuilder` serves every function (it is repositioned at each new entry block), so the caller
// still disposes exactly one, as before. The entry function is declared first so it stays at
// `.text` offset `0`; its body is emitted LAST, after every lifted function, purely so the
// lifted-function lookups it needs (`MakeClosure`/`CallKnown` naming a label) resolve the same
// way a lifted body's own do.
let codegenFunctions name context entryFunction functions stringLiterals =
    (let module_ = createModule(name)(context)
    in
        let types = coreLlvmTypes(context)
        in
            let entryValue =
                false
                |> functionType(voidType(context))([types.i64])(1u32)
                |> addFunction(module_)(name)
            in
                let closureFnType = closureFunctionTypeOf(types.i64)
                in
                    let envpGlobal = addGlobal(module_)(types.i64)("__ashes_envp")
                    in
                        let _ =
                            false
                            |> constInt(types.i64)(0u64)
                            |> setInitializer(envpGlobal)
                        in
                            let _ = setLinkage(envpGlobal)(linkageInternal)
                            in
                                let builder = createBuilder(context)
                                in
                                    let externals = declareExternalFunctions(module_)(context)(types)
                                    in
                                        let usesCopy = functionsUseCopyOut(entryFunction :: functions)
                                        in
                                            let arena = defineArenaRuntime(module_)(context)(builder)(types.i64)(types.i8)(types.ptrType)(usesCopy)(externals.mallocFn)(externals.mallocType)(externals.freeFn)(externals.freeType)(externals.memcpyFn)(externals.memcpyType)
                                            in
                                                let mc =
                                                    ModuleCodegen(
                                                        moduleRef = module_,
                                                        moduleContext = context,
                                                        moduleTypes = types,
                                                        moduleExternals = externals,
                                                        moduleStringLiteralGlobals = buildStringLiteralGlobalsFromIndex(module_)(context)(types.i64)(types.i8)(0)(stringLiterals),
                                                        moduleLiftedFunctions = declareLiftedFunctions(module_)(closureFnType)(functions),
                                                        moduleClosureFunctionType = closureFnType,
                                                        moduleEnvpGlobal = envpGlobal,
                                                        moduleConsoleGlobals = defineConsoleGlobals(module_)(types.i64)(types.i8),
                                                        moduleArenaRuntime = arena,
                                                        moduleCopyRuntime = if usesCopy
                                                        then
                                                            arena
                                                            |> defineCopyRuntime(module_)(context)(builder)(types.i64)(types.i8)(types.ptrType)
                                                            |> Some
                                                        else None,
                                                        moduleBuilder = builder
                                                    )
                                                in
                                                    let _ = codegenLiftedFunctions(mc)(functions)
                                                    in
                                                        let _ = codegenFunctionBody(mc)(entryValue)(true)(entryFunction)
                                                        in (module_, mc.moduleBuilder))

// The entry function alone, for a hand-built `IrFunction` with no lifted functions at all.
let codegenEntryFunction name context irFunction stringLiterals = codegenFunctions(name)(context)(irFunction)([])(stringLiterals)

// A whole lowered program: its entry function plus every lifted helper it contains.
let codegenProgram name context program =
    match program with
        | IrProgram { entryFunction = entryFunction, functions = functions, stringLiterals = stringLiterals } -> codegenFunctions(name)(context)(entryFunction)(functions)(stringLiterals)
