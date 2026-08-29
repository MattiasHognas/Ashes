// Proves the LLVM C API bindings in `AshesCompiler.Backend.Llvm` work end to end: build a trivial
// `i32 answer() { ret i32 42 }` function in a fresh module, verify it, and emit it to both a real
// linux-x64 object file and its assembly listing with real LLVM, not just exercise the bindings in
// isolation. Running this binary requires a `libLLVM` build (and its own dependencies, on Linux)
// placed next to it — see AshesCompiler.Backend.Llvm's own header comment.
import Ashes.Byte
import Ashes.Ffi
import Ashes.Test as test
import Ashes.Text
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

// A one-parameter function (`i32 addOne(i32 x) { ret i32 (x + 1) }`), proving `functionType`'s
// `FfiBuffer` parameter with a non-empty list, `getParam`, and `buildAdd` all work — everything
// `buildTrivialAnswerModule` above never exercises.
let buildAddOneModule name context =
    (let module_ = createModule(name)(context)
    in
        let i32 = int32Type(context)
        in
            let fnType = functionType(i32)([i32])(1u32)(false)
            in
                let function = addFunction(module_)("addOne")(fnType)
                in
                    let entryBlock = appendBasicBlock(context)(function)("entry")
                    in
                        let builder = createBuilder(context)
                        in
                            let _ = positionBuilderAtEnd(builder)(entryBlock)
                            in
                                let x = getParam(function)(0u32)
                                in
                                    let one = constInt(i32)(1u64)(false)
                                    in
                                        let sum = buildAdd(builder)(x)(one)("sum")
                                        in
                                            let _ = buildRet(builder)(sum)
                                            in (module_, builder))

// A two-parameter, branching function (`i32 max(i32 a, i32 b) { if a > b then a else b }`),
// proving `buildICmp`/`buildCondBr`/`buildBr` and the no-`phi` alloca/store/load slot pattern all
// work together across four basic blocks.
let buildMaxModule name context =
    (let module_ = createModule(name)(context)
    in
        let i32 = int32Type(context)
        in
            let fnType = functionType(i32)([i32, i32])(2u32)(false)
            in
                let function = addFunction(module_)("max")(fnType)
                in
                    let entryBlock = appendBasicBlock(context)(function)("entry")
                    in
                        let thenBlock = appendBasicBlock(context)(function)("then")
                        in
                            let elseBlock = appendBasicBlock(context)(function)("else")
                            in
                                let mergeBlock = appendBasicBlock(context)(function)("merge")
                                in
                                    let builder = createBuilder(context)
                                    in
                                        let _ = positionBuilderAtEnd(builder)(entryBlock)
                                        in
                                            let a = getParam(function)(0u32)
                                            in
                                                let b = getParam(function)(1u32)
                                                in
                                                    let slot = buildAlloca(builder)(i32)("result")
                                                    in
                                                        let cond = buildICmp(builder)(intPredicateSgt)(a)(b)("cmp")
                                                        in
                                                            let _ = buildCondBr(builder)(cond)(thenBlock)(elseBlock)
                                                            in
                                                                let _ = positionBuilderAtEnd(builder)(thenBlock)
                                                                in
                                                                    let _ = buildStore(builder)(a)(slot)
                                                                    in
                                                                        let _ = buildBr(builder)(mergeBlock)
                                                                        in
                                                                            let _ = positionBuilderAtEnd(builder)(elseBlock)
                                                                            in
                                                                                let _ = buildStore(builder)(b)(slot)
                                                                                in
                                                                                    let _ = buildBr(builder)(mergeBlock)
                                                                                    in
                                                                                        let _ = positionBuilderAtEnd(builder)(mergeBlock)
                                                                                        in
                                                                                            let result = buildLoad(builder)(i32)(slot)("result_value")
                                                                                            in
                                                                                                let _ = buildRet(builder)(result)
                                                                                                in (module_, builder))

// A two-function module (`i32 addOne(i32 x) { ret i32 (x + 1) }` and
// `i32 addTwo(i32 x) { ret i32 addOne(addOne(x)) }`), proving `buildCall`'s `FfiBuffer` argument
// list and calling one module-local function from another both work. `addOne` and `addTwo` must
// live in the same module: an LLVM call target is a value obtained from `addFunction` in that
// module, not something that can reach across modules directly.
let buildAddTwoModule name context =
    (let module_ = createModule(name)(context)
    in
        let i32 = int32Type(context)
        in
            let addOneType = functionType(i32)([i32])(1u32)(false)
            in
                let addOneFunction = addFunction(module_)("addOne")(addOneType)
                in
                    let addOneEntry = appendBasicBlock(context)(addOneFunction)("entry")
                    in
                        let builder = createBuilder(context)
                        in
                            let _ = positionBuilderAtEnd(builder)(addOneEntry)
                            in
                                let addOneParam = getParam(addOneFunction)(0u32)
                                in
                                    let one = constInt(i32)(1u64)(false)
                                    in
                                        let addOneSum = buildAdd(builder)(addOneParam)(one)("sum")
                                        in
                                            let _ = buildRet(builder)(addOneSum)
                                            in
                                                let addTwoType = functionType(i32)([i32])(1u32)(false)
                                                in
                                                    let addTwoFunction = addFunction(module_)("addTwo")(addTwoType)
                                                    in
                                                        let addTwoEntry = appendBasicBlock(context)(addTwoFunction)("entry")
                                                        in
                                                            let _ = positionBuilderAtEnd(builder)(addTwoEntry)
                                                            in
                                                                let addTwoParam = getParam(addTwoFunction)(0u32)
                                                                in
                                                                    let firstCall = buildCall(builder)(addOneType)(addOneFunction)([addTwoParam])(1u32)("first")
                                                                    in
                                                                        let secondCall = buildCall(builder)(addOneType)(addOneFunction)([firstCall])(1u32)("second")
                                                                        in
                                                                            let _ = buildRet(builder)(secondCall)
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

// Builds a module with `buildModule` (either module builder above), emits it as `fileType`, copies
// the emitted bytes into managed memory, and disposes every LLVM handle it created along the way —
// success or failure. `name` distinguishes the module across test runs.
let emitModule buildModule name fileType =
    (let _ = initializeX86Target(Unit)
    in
        let context = contextCreate(Unit)
        in
            match buildModule(name)(context) with
                | (module_, builder) ->
                    match resolveHostTargetMachine("x86_64-unknown-linux-gnu") with
                        | Error(message) -> Error(message)
                        | Ok(machine) ->
                            let _ = setTarget(module_)("x86_64-unknown-linux-gnu")
                            in
                                let _ = applyDataLayout(module_)(machine)
                                in
                                    match targetMachineEmitToMemoryBuffer(machine)(module_)(fileType) with
                                        | (true, _, _) -> Error("LLVM reported the module as broken during emission")
                                        | (false, _, None) -> Error("expected an emitted buffer")
                                        | (false, _, Some(buffer)) ->
                                            let size = getBufferSize(buffer)
                                            in
                                                let start = getBufferStart(buffer)
                                                in
                                                    let bytesResult = Ashes.Ffi.copyBytes(start)(size)
                                                    in
                                                        Unit
                                                        |> (given (_) -> disposeMemoryBuffer(buffer))
                                                        |> (given (_) -> disposeTargetMachine(machine))
                                                        |> (given (_) -> disposeBuilder(builder))
                                                        |> (given (_) -> disposeModule(module_))
                                                        |> (given (_) -> contextDispose(context))
                                                        |> (given (_) -> bytesResult))

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

let assertLooksLikeAssembly bytes label =
    (let text =
        bytes
        |> Ashes.Byte.length
        |> Ashes.Byte.subText(bytes)(0)
    in
        label
        |> Ashes.Text.contains(text)
        |> test.assertEqual(true))

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
    match emitModule(buildTrivialAnswerModule)("selfhost-backend-object-test")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeElf(bytes)

let testEmitAssemblyForTrivialModule unit =
    match emitModule(buildTrivialAnswerModule)("selfhost-backend-asm-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("answer")

let testEmitAssemblyForAddOneModule unit =
    match emitModule(buildAddOneModule)("selfhost-backend-addone-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("addOne")

let testEmitAssemblyForMaxModule unit =
    match emitModule(buildMaxModule)("selfhost-backend-max-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("max")

let testEmitAssemblyForAddTwoModule unit =
    match emitModule(buildAddTwoModule)("selfhost-backend-addtwo-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("addTwo")

let run unit =
    Unit
    |> testBuildAndVerifyTrivialModule
    |> testEmitObjectFileForTrivialModule
    |> testEmitAssemblyForTrivialModule
    |> testEmitAssemblyForAddOneModule
    |> testEmitAssemblyForMaxModule
    |> testEmitAssemblyForAddTwoModule
    |> (given (_) -> Ashes.IO.print("all self-hosted backend tests passed"))

run(Unit)
