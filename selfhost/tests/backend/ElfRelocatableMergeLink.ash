// A program emitted as two real LLVM objects, merged by `AshesCompiler.Backend.ElfRelocatableMerge`
// and linked by the single-object `AshesCompiler.Backend.ElfLinker`. The first object defines the
// entry function, a function the second calls back, and an initialized global; the second defines
// the function the entry calls, which reads that global. Every cross-object reference is an
// undefined symbol in one object that the merge resolves to the other's definition: two calls
// (`R_X86_64_PLT32`) and one data address (`R_X86_64_PC32`, from inline assembly, since LLVM
// addresses an external global of default visibility through the GOT). The process exits with
// `2 * 29 + 7`.
import Ashes.Byte
import Ashes.Ffi
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.ElfLinker
import AshesCompiler.Backend.ElfRelocatableMerge
export (
    value testLinkAndRunMergedObjects,
)

let mergeTriple = "x86_64-unknown-linux-gnu"

let resolveMergeTargetMachine unit =
    match getTargetFromTriple(mergeTriple) with
        | (_, None, _) -> Error("could not resolve a target for " + mergeTriple)
        | (_, Some(target), _) ->
            match hostCpuName(Unit) with
                | Error(message) -> Error(message)
                | Ok(cpu) ->
                    match hostCpuFeatures(Unit) with
                        | Error(message) -> Error(message)
                        | Ok(features) ->
                            codeModelDefault
                            |> createTargetMachine(target)(mergeTriple)(cpu)(features)(codeGenOptLevelDefault)(relocModeStatic)
                            |> Ok

let startMergeTargetMachine unit =
    (let _ = initializeX86Target(Unit)
    in resolveMergeTargetMachine(Unit))

// Optimizes `module_` and emits it as an object, then disposes every handle.
let emitMergeObject context module_ builder =
    match startMergeTargetMachine(Unit) with
        | Error(message) -> Error(message)
        | Ok(machine) ->
            let _ = setTarget(module_)(mergeTriple)
            in
                let _ = applyDataLayout(module_)(machine)
                in
                    let _ =
                        let options = createPassBuilderOptions(Unit)
                        in
                            let _ = runPasses(module_)("default<O2>")(machine)(options)
                            in disposePassBuilderOptions(options)
                    in
                        match targetMachineEmitToMemoryBuffer(machine)(module_)(objectFileType) with
                            | (true, _, _) -> Error("LLVM reported the module as broken during emission")
                            | (false, _, None) -> Error("expected an emitted buffer")
                            | (false, _, Some(buffer)) ->
                                let bytesResult =
                                    buffer
                                    |> getBufferSize
                                    |> Ashes.Ffi.copyBytes(getBufferStart(buffer))
                                in
                                    Unit
                                    |> (given (_) -> disposeMemoryBuffer(buffer))
                                    |> (given (_) -> disposeTargetMachine(machine))
                                    |> (given (_) -> disposeBuilder(builder))
                                    |> (given (_) -> disposeModule(module_))
                                    |> (given (_) -> contextDispose(context))
                                    |> (given (_) -> bytesResult)

// The address of the global `mergeShared` through a RIP-relative `lea`, so the reference is a
// plain PC-relative relocation against the symbol whichever object defines it.
let sharedAddress context builder =
    (let ptrType = pointerType(context)(0u32)
    in
        let asmType = functionType(ptrType)([])(0u32)(false)
        in
            let asmValue = getInlineAsm(asmType)("leaq mergeShared(%rip), $0")("=r")(false)(false)
            in buildCall(builder)(asmType)(asmValue)([])(0u32)("shared"))

let emitMergeProcessExit builder i64 exitCode =
    (let syscallType = functionType(i64)([i64, i64, i64, i64])(4u32)(false)
    in
        let syscallAsm = getInlineAsm(syscallType)("syscall")("={rax},{rax},{rdi},{rsi},{rdx},~{rcx},~{r11},~{memory}")(true)(false)
        in
            let sixty = constInt(i64)(60u64)(false)
            in
                let zero = constInt(i64)(0u64)(false)
                in
                    let _ = buildCall(builder)(syscallType)(syscallAsm)([sixty, exitCode, zero, zero])(4u32)("sys_exit")
                    in buildUnreachable(builder))

// The first object: `mergeShared = 7`, `mergeTwice(x) = x * 2`, and the entry, which exits with
// `mergeHelper(29)`; `mergeHelper` is only declared.
let buildMergeFirstObject entryName =
    (let context = contextCreate(Unit)
    in
        let module_ = createModule(entryName)(context)
        in
            let i64 = int64Type(context)
            in
                let shared = addGlobal(module_)(i64)("mergeShared")
                in
                    let _ =
                        Unit
                        |> (given (_) ->
                            false
                            |> constInt(i64)(7u64)
                            |> setInitializer(shared))
                        |> (given (_) -> setLinkage(shared)(linkageExternal))
                    in
                        let unaryType = functionType(i64)([i64])(1u32)(false)
                        in
                            let helper = addFunction(module_)("mergeHelper")(unaryType)
                            in
                                let twice = addFunction(module_)("mergeTwice")(unaryType)
                                in
                                    let builder = createBuilder(context)
                                    in
                                        let _ =
                                            "entry"
                                            |> appendBasicBlock(context)(twice)
                                            |> positionBuilderAtEnd(builder)
                                        in
                                            let doubled =
                                                buildMul(builder)(getParam(twice)(0u32))(constInt(i64)(2u64)(false))("doubled")
                                            in
                                                let _ = buildRet(builder)(doubled)
                                                in
                                                    let entry =
                                                        false
                                                        |> functionType(voidType(context))([])(0u32)
                                                        |> addFunction(module_)(entryName)
                                                    in
                                                        let _ =
                                                            "entry"
                                                            |> appendBasicBlock(context)(entry)
                                                            |> positionBuilderAtEnd(builder)
                                                        in
                                                            let result = buildCall(builder)(unaryType)(helper)([constInt(i64)(29u64)(false)])(1u32)("result")
                                                            in
                                                                let _ = emitMergeProcessExit(builder)(i64)(result)
                                                                in emitMergeObject(context)(module_)(builder))

// The second object: `mergeHelper(x) = mergeTwice(x) + mergeShared`, with `mergeTwice` declared
// and `mergeShared` read through its address.
let buildMergeSecondObject unit =
    (let context = contextCreate(Unit)
    in
        let module_ = createModule("mergeSecond")(context)
        in
            let i64 = int64Type(context)
            in
                let unaryType = functionType(i64)([i64])(1u32)(false)
                in
                    let twice = addFunction(module_)("mergeTwice")(unaryType)
                    in
                        let helper = addFunction(module_)("mergeHelper")(unaryType)
                        in
                            let builder = createBuilder(context)
                            in
                                let _ =
                                    "entry"
                                    |> appendBasicBlock(context)(helper)
                                    |> positionBuilderAtEnd(builder)
                                in
                                    let doubled = buildCall(builder)(unaryType)(twice)([getParam(helper)(0u32)])(1u32)("doubled")
                                    in
                                        let loaded =
                                            buildLoad(builder)(i64)(sharedAddress(context)(builder))("loaded")
                                        in
                                            let _ = setVolatile(loaded)(true)
                                            in
                                                let _ =
                                                    "sum"
                                                    |> buildAdd(builder)(doubled)(loaded)
                                                    |> buildRet(builder)
                                                in emitMergeObject(context)(module_)(builder))

let runMergedExecutable (executableBytes: Bytes) =
    match Ashes.IO.File.writeBytes("selfhost_backend_merged_objects_e2e")(executableBytes) with
        | Error(message) -> Ashes.IO.panic(message)
        | Ok(_) ->
            match Ashes.IO.File.makeExecutable("selfhost_backend_merged_objects_e2e") with
                | Error(message) -> Ashes.IO.panic(message)
                | Ok(_) ->
                    match Ashes.IO.Process.spawn("./selfhost_backend_merged_objects_e2e")([]) with
                        | Error(message) -> Ashes.IO.panic(message)
                        | Ok(process) -> Ashes.IO.Process.waitForExit(process)

// Both objects merged, the merged object linked, and the executable run: exit code 65.
let testLinkAndRunMergedObjects unit =
    match (buildMergeFirstObject("selfhostBackendMergedEntry"), buildMergeSecondObject(Unit)) with
        | (Error(message), _) -> Ashes.IO.panic("merged objects: " + message)
        | (_, Error(message)) -> Ashes.IO.panic("merged objects: " + message)
        | (Ok(first), Ok(second)) ->
            match mergeElfRelocatableObjects([first, second]) with
                | Error(message) -> Ashes.IO.panic("merged objects: the merge failed: " + message)
                | Ok(merged) ->
                    // Kept beside the executable for `readelf`/`objdump` when the link or run fails.
                    let _ = Ashes.IO.File.writeBytes("selfhost_backend_merged_objects.o")(merged)
                    in
                        match linkLinuxExecutable(merged)("selfhostBackendMergedEntry") with
                            | Error(message) -> Ashes.IO.panic("merged objects: the link failed: " + message)
                            | Ok(executableBytes) ->
                                let exitCode = runMergedExecutable(executableBytes)
                                in
                                    if exitCode == 65
                                    then Unit
                                    else Ashes.IO.panic("merged objects: expected exit code 65, got " + Ashes.Text.fromInt(exitCode))
