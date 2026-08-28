// Proves the LLVM C API bindings in `AshesCompiler.Backend.Llvm` work end to end: build a trivial
// `i32 answer() { ret i32 42 }` function in a fresh module and verify it with real LLVM, not just
// exercise the bindings in isolation. Running this binary requires a `libLLVM` build (and its own
// dependencies, on Linux) placed next to it — see AshesCompiler.Backend.Llvm's own header comment.
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
                                                    let _ = disposeBuilder(builder)
                                                    in
                                                        let _ = disposeModule(module_)
                                                        in
                                                            let _ = contextDispose(context)
                                                            in test.assertEqual(false)(isBroken))

let run unit =
    Unit
    |> testBuildAndVerifyTrivialModule
    |> (given (_) -> Ashes.IO.print("all self-hosted backend tests passed"))

run(Unit)
