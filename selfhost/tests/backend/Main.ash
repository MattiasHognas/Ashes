// Proves the LLVM C API bindings in `AshesCompiler.Backend.Llvm` work end to end: build a trivial
// `i32 answer() { ret i32 42 }` function in a fresh module, verify it and emit it to a real
// linux-x64 object file with real LLVM, not just exercise the bindings in isolation. Running this
// binary requires a `libLLVM` build (and its own dependencies, on Linux) placed next to it — see
// AshesCompiler.Backend.Llvm's own header comment.
import Ashes.Byte
import Ashes.Ffi
import Ashes.Test as test
import AshesCompiler.Backend.Llvm
let testBuildAndVerifyTrivialModule unit =
    (let context = contextCreate(Unit)
    in
        let module_ = createModule("selfhost-backend-test")(context)
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
                                        in
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
            let module_ = createModule("selfhost-backend-emit-test")(context)
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
                                            in
                                                match getTargetFromTriple("x86_64-unknown-linux-gnu") with
                                                    | (targetIsBroken, targetOpt, _) ->
                                                        let _ = test.assertEqual(false)(targetIsBroken)
                                                        in
                                                            match targetOpt with
                                                                | None -> test.fail("expected a resolved target for x86_64-unknown-linux-gnu")
                                                                | Some(target) ->
                                                                    let machine = createTargetMachine(target)("x86_64-unknown-linux-gnu")("")("")(codeGenOptLevelNone)(relocModeStatic)(codeModelDefault)
                                                                    in
                                                                        let _ = setTarget(module_)("x86_64-unknown-linux-gnu")
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
                                                                                                                    |> test.assertEqual(70u8)))

let run unit =
    Unit
    |> testBuildAndVerifyTrivialModule
    |> testEmitObjectFileForTrivialModule
    |> (given (_) -> Ashes.IO.print("all self-hosted backend tests passed"))

run(Unit)
