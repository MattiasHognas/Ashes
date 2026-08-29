// Proves the LLVM C API bindings in `AshesCompiler.Backend.Llvm` work end to end: build a trivial
// `i32 answer() { ret i32 42 }` function in a fresh module, verify it and emit it to a real
// linux-x64 object file with real LLVM, not just exercise the bindings in isolation. Running this
// binary requires a `libLLVM` build (and its own dependencies, on Linux) placed next to it — see
// AshesCompiler.Backend.Llvm's own header comment.
import Ashes.Byte
import Ashes.Ffi
import Ashes.Test as test
import AshesCompiler.Backend.Llvm
let buildTrivialAnswerModule name context =
    (let module_ = createModule(name)(context)
    in
        let i32 = int32Type(context)
        in
            let fnType = functionType(i32)([])(0u32)(false)
            in
                let function = addFunction(module_)("answer")(fnType)
                in
                    let entryBlock = appendBasicBlock(context)(function)("entry")
                    in
                        let builder = createBuilder(context)
                        in
                            let _ = positionBuilderAtEnd(builder)(entryBlock)
                            in
                                let answer = constInt(i32)(42u64)(false)
                                in
                                    let _ = buildRet(builder)(answer)
                                    in (module_, builder))

let resolveHostTargetMachine triple =
    match getTargetFromTriple(triple) with
        | (_, None, _) -> Error("could not resolve a target for " + triple)
        | (_, Some(target), _) ->
            match hostCpuName(Unit) with
                | Error(message) -> Error(message)
                | Ok(cpu) ->
                    match hostCpuFeatures(Unit) with
                        | Error(message) -> Error(message)
                        | Ok(features) ->
                            codeModelDefault
                            |> createTargetMachine(target)(triple)(cpu)(features)(codeGenOptLevelNone)(relocModeStatic)
                            |> Ok

let assertLooksLikeElf bytes =
    Unit
    |> (given (_) -> test.assertEqual(true)(Ashes.Byte.length(bytes) > 0))
    |> (given (_) ->
        0
        |> Ashes.Byte.get(bytes)
        |> test.assertEqual(127u8))
    |> (given (_) ->
        1
        |> Ashes.Byte.get(bytes)
        |> test.assertEqual(69u8))
    |> (given (_) ->
        2
        |> Ashes.Byte.get(bytes)
        |> test.assertEqual(76u8))
    |> (given (_) ->
        3
        |> Ashes.Byte.get(bytes)
        |> test.assertEqual(70u8))

let testBuildAndVerifyTrivialModule unit =
    (let context = contextCreate(Unit)
    in
        match buildTrivialAnswerModule("selfhost-backend-test")(context) with
            | (module_, builder) ->
                match verifyModule(module_)(verifierReturnStatusAction) with
                    | (isBroken, _) ->
                        Unit
                        |> (given (_) -> disposeBuilder(builder))
                        |> (given (_) -> disposeModule(module_))
                        |> (given (_) -> contextDispose(context))
                        |> (given (_) -> test.assertEqual(false)(isBroken)))

let testEmitObjectFileForTrivialModule unit =
    (let _ = initializeX86Target(Unit)
    in
        let context = contextCreate(Unit)
        in
            match buildTrivialAnswerModule("selfhost-backend-emit-test")(context) with
                | (module_, builder) ->
                    match resolveHostTargetMachine("x86_64-unknown-linux-gnu") with
                        | Error(message) -> test.fail(message)
                        | Ok(machine) ->
                            let _ = setTarget(module_)("x86_64-unknown-linux-gnu")
                            in
                                let _ = applyDataLayout(module_)(machine)
                                in
                                    match targetMachineEmitToMemoryBuffer(machine)(module_)(objectFileType) with
                                        | (emitIsBroken, _, bufferOpt) ->
                                            let _ = test.assertEqual(false)(emitIsBroken)
                                            in
                                                match bufferOpt with
                                                    | None -> test.fail("expected an emitted object buffer")
                                                    | Some(buffer) ->
                                                        let size = getBufferSize(buffer)
                                                        in
                                                            let start = getBufferStart(buffer)
                                                            in
                                                                match Ashes.Ffi.copyBytes(start)(size) with
                                                                    | Error(message) -> test.fail("copyBytes failed: " + message)
                                                                    | Ok(bytes) ->
                                                                        Unit
                                                                        |> (given (_) -> disposeMemoryBuffer(buffer))
                                                                        |> (given (_) -> disposeTargetMachine(machine))
                                                                        |> (given (_) -> disposeBuilder(builder))
                                                                        |> (given (_) -> disposeModule(module_))
                                                                        |> (given (_) -> contextDispose(context))
                                                                        |> (given (_) -> assertLooksLikeElf(bytes)))

let run unit =
    Unit
    |> testBuildAndVerifyTrivialModule
    |> testEmitObjectFileForTrivialModule
    |> (given (_) -> Ashes.IO.print("all self-hosted backend tests passed"))

run(Unit)
