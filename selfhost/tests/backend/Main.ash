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
// Adds a function to `module_`, appends its entry block, and positions a builder at the end of it
// — the shared prefix every module builder below needs before emitting a function body. Pass
// `None` for `existingBuilder` for a module's first (or only) function; pass `Some(builder)` for a
// later function that should share the same builder (an LLVM `IRBuilder` is reusable across
// functions in one module — repositioning it is cheaper and avoids leaking an extra one that
// `emitModule` would never dispose). Returns `(function, functionType, builder)`.
let beginFunction module_ context existingBuilder name returnType paramTypes paramCount =
    (let fnType = functionType(returnType)(paramTypes)(paramCount)(false)
    in
        let function = addFunction(module_)(name)(fnType)
        in
            let entryBlock = appendBasicBlock(context)(function)("entry")
            in
                let builder =
                    match existingBuilder with
                        | Some(existing) -> existing
                        | None -> createBuilder(context)
                in
                    let _ = positionBuilderAtEnd(builder)(entryBlock)
                    in (function, fnType, builder))

let buildTrivialAnswerModule name context =
    (let module_ = createModule(name)(context)
    in
        let i32 = int32Type(context)
        in
            match beginFunction(module_)(context)(None)("answer")(i32)([])(0u32) with
                | (_, _, builder) ->
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
            match beginFunction(module_)(context)(None)("addOne")(i32)([i32])(1u32) with
                | (function, _, builder) ->
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
            match beginFunction(module_)(context)(None)("max")(i32)([i32, i32])(2u32) with
                | (function, _, builder) ->
                    let thenBlock = appendBasicBlock(context)(function)("then")
                    in
                        let elseBlock = appendBasicBlock(context)(function)("else")
                        in
                            let mergeBlock = appendBasicBlock(context)(function)("merge")
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
                                                    let _ =
                                                        Unit
                                                        |> (given (_) -> positionBuilderAtEnd(builder)(thenBlock))
                                                        |> (given (_) -> buildStore(builder)(a)(slot))
                                                        |> (given (_) -> buildBr(builder)(mergeBlock))
                                                    in
                                                        let _ =
                                                            Unit
                                                            |> (given (_) -> positionBuilderAtEnd(builder)(elseBlock))
                                                            |> (given (_) -> buildStore(builder)(b)(slot))
                                                            |> (given (_) -> buildBr(builder)(mergeBlock))
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
            match beginFunction(module_)(context)(None)("addOne")(i32)([i32])(1u32) with
                | (addOneFunction, addOneType, builder) ->
                    let addOneParam = getParam(addOneFunction)(0u32)
                    in
                        let one = constInt(i32)(1u64)(false)
                        in
                            let addOneSum = buildAdd(builder)(addOneParam)(one)("sum")
                            in
                                let _ = buildRet(builder)(addOneSum)
                                in
                                    match beginFunction(module_)(context)(Some(builder))("addTwo")(i32)([i32])(1u32) with
                                        | (addTwoFunction, _, _) ->
                                            let addTwoParam = getParam(addTwoFunction)(0u32)
                                            in
                                                let firstCall = buildCall(builder)(addOneType)(addOneFunction)([addTwoParam])(1u32)("first")
                                                in
                                                    let secondCall = buildCall(builder)(addOneType)(addOneFunction)([firstCall])(1u32)("second")
                                                    in
                                                        let _ = buildRet(builder)(secondCall)
                                                        in (module_, builder))

// A global constant (`i32 counter = 99`) read back by a function
// (`i32 getCounter() { ret i32 counter }`), proving `addGlobal`, `setInitializer`,
// `setGlobalConstant`, and `setLinkage` all work.
let buildGlobalCounterModule name context =
    (let module_ = createModule(name)(context)
    in
        let i32 = int32Type(context)
        in
            let global = addGlobal(module_)(i32)("counter")
            in
                let initialValue = constInt(i32)(99u64)(false)
                in
                    let _ =
                        Unit
                        |> (given (_) -> setInitializer(global)(initialValue))
                        |> (given (_) -> setGlobalConstant(global)(true))
                        |> (given (_) -> setLinkage(global)(linkageInternal))
                    in
                        match beginFunction(module_)(context)(None)("getCounter")(i32)([])(0u32) with
                            | (_, _, builder) ->
                                let value = buildLoad(builder)(i32)(global)("value")
                                in
                                    let _ = buildRet(builder)(value)
                                    in (module_, builder))

// A function that declares (not defines) two external C functions, `malloc` and `free`, and calls
// them: `i32 allocateStoreLoadFree() { p = malloc(4); *p = 7; v = *p; free(p); ret i32 v }`. Proves
// `int64Type`, `voidType`, `pointerType`, and calling a genuinely external (undefined-in-this-module)
// function all work — `addOne`/`addTwo` above only ever called functions defined in the same module.
// A function added with `addFunction` but never given a basic block is a declaration, matching how
// real compiled code references libc — `malloc`/`free` skip `beginFunction` entirely since they
// never get a body.
let buildMallocFreeModule name context =
    (let module_ = createModule(name)(context)
    in
        let i32 = int32Type(context)
        in
            let i64 = int64Type(context)
            in
                let voidT = voidType(context)
                in
                    let ptrType = pointerType(context)(0u32)
                    in
                        let mallocType = functionType(ptrType)([i64])(1u32)(false)
                        in
                            let mallocFn = addFunction(module_)("malloc")(mallocType)
                            in
                                let freeType = functionType(voidT)([ptrType])(1u32)(false)
                                in
                                    let freeFn = addFunction(module_)("free")(freeType)
                                    in
                                        match beginFunction(module_)(context)(None)("allocateStoreLoadFree")(i32)([])(0u32) with
                                            | (_, _, builder) ->
                                                let sizeArg = constInt(i64)(4u64)(false)
                                                in
                                                    let ptr = buildCall(builder)(mallocType)(mallocFn)([sizeArg])(1u32)("ptr")
                                                    in
                                                        let seven = constInt(i32)(7u64)(false)
                                                        in
                                                            let _ = buildStore(builder)(seven)(ptr)
                                                            in
                                                                let loaded = buildLoad(builder)(i32)(ptr)("loaded")
                                                                in
                                                                    let _ = buildCall(builder)(freeType)(freeFn)([ptr])(1u32)("")
                                                                    in
                                                                        let _ = buildRet(builder)(loaded)
                                                                        in (module_, builder))

// A function using a real two-field struct type (`{i32, i32}`, matching a pair/record layout): it
// allocates one on the stack, addresses each field with `buildGEP`, stores into both, loads both
// back, and returns their sum. Proves `structType` and `buildGEP`'s `FfiBuffer` index list both
// work — `buildMaxModule` above only ever addressed a single scalar slot, never a field within a
// larger aggregate.
let buildStructPairModule name context =
    (let module_ = createModule(name)(context)
    in
        let i32 = int32Type(context)
        in
            let pairType = structType(context)([i32, i32])(2u32)(false)
            in
                match beginFunction(module_)(context)(None)("pairSum")(i32)([])(0u32) with
                    | (_, _, builder) ->
                        let pair = buildAlloca(builder)(pairType)("pair")
                        in
                            let zeroIndex = constInt(i32)(0u64)(false)
                            in
                                let firstFieldIndex = constInt(i32)(0u64)(false)
                                in
                                    let secondFieldIndex = constInt(i32)(1u64)(false)
                                    in
                                        let firstFieldPtr = buildGEP(builder)(pairType)(pair)([zeroIndex, firstFieldIndex])(2u32)("first")
                                        in
                                            let secondFieldPtr = buildGEP(builder)(pairType)(pair)([zeroIndex, secondFieldIndex])(2u32)("second")
                                            in
                                                let three = constInt(i32)(3u64)(false)
                                                in
                                                    let four = constInt(i32)(4u64)(false)
                                                    in
                                                        let _ =
                                                            Unit
                                                            |> (given (_) -> buildStore(builder)(three)(firstFieldPtr))
                                                            |> (given (_) -> buildStore(builder)(four)(secondFieldPtr))
                                                        in
                                                            let firstValue = buildLoad(builder)(i32)(firstFieldPtr)("v0")
                                                            in
                                                                let secondValue = buildLoad(builder)(i32)(secondFieldPtr)("v1")
                                                                in
                                                                    let sum = buildAdd(builder)(firstValue)(secondValue)("sum")
                                                                    in
                                                                        let _ = buildRet(builder)(sum)
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

// Builds a module with `buildModule` (any module builder above), emits it as `fileType`, copies
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

let testEmitAssemblyForGlobalCounterModule unit =
    match emitModule(buildGlobalCounterModule)("selfhost-backend-global-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("counter")

let testEmitAssemblyForMallocFreeModule unit =
    match emitModule(buildMallocFreeModule)("selfhost-backend-mallocfree-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("allocateStoreLoadFree")

let testEmitAssemblyForStructPairModule unit =
    match emitModule(buildStructPairModule)("selfhost-backend-struct-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("pairSum")

let run unit =
    Unit
    |> testBuildAndVerifyTrivialModule
    |> testEmitObjectFileForTrivialModule
    |> testEmitAssemblyForTrivialModule
    |> testEmitAssemblyForAddOneModule
    |> testEmitAssemblyForMaxModule
    |> testEmitAssemblyForAddTwoModule
    |> testEmitAssemblyForGlobalCounterModule
    |> testEmitAssemblyForMallocFreeModule
    |> testEmitAssemblyForStructPairModule
    |> (given (_) -> Ashes.IO.print("all self-hosted backend tests passed"))

run(Unit)
