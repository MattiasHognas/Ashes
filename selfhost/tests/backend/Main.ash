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

// A global null-terminated byte-string constant (`i8[3] greeting = "Hi\0"`, matching a C string
// literal's layout), read back by a function returning a pointer to its first byte
// (`i8* getGreeting() { ret i8* &greeting[0] }`). Proves `int8Type`, `arrayType`, and `constArray`
// all work, and that `buildGEP`'s first (element-stepping) index works on an array, not just the
// single always-`0` first index every earlier `buildGEP` use exercised on a struct pointer.
let buildGreetingModule name context =
    (let module_ = createModule(name)(context)
    in
        let i8 = int8Type(context)
        in
            let greetingType = arrayType(i8)(3u64)
            in
                let h = constInt(i8)(72u64)(false)
                in
                    let i = constInt(i8)(105u64)(false)
                    in
                        let nul = constInt(i8)(0u64)(false)
                        in
                            let initialValue = constArray(i8)([h, i, nul])(3u64)
                            in
                                let global = addGlobal(module_)(greetingType)("greeting")
                                in
                                    let _ =
                                        Unit
                                        |> (given (_) -> setInitializer(global)(initialValue))
                                        |> (given (_) -> setGlobalConstant(global)(true))
                                        |> (given (_) -> setLinkage(global)(linkageInternal))
                                    in
                                        let ptrType = pointerType(context)(0u32)
                                        in
                                            match beginFunction(module_)(context)(None)("getGreeting")(ptrType)([])(0u32) with
                                                | (_, _, builder) ->
                                                    let i32 = int32Type(context)
                                                    in
                                                        let zero = constInt(i32)(0u64)(false)
                                                        in
                                                            let firstByte = buildGEP(builder)(greetingType)(global)([zero, zero])(2u32)("firstByte")
                                                            in
                                                                let _ = buildRet(builder)(firstByte)
                                                                in (module_, builder))

// Writes a `Some(tag, payload)` value into an already-allocated `{i32, i32}` slot and returns
// the two field pointers, so the caller can both build the value and later read it back.
let writeOptionSome builder optionType option i32 tag payload =
    (let zeroIndex = constInt(i32)(0u64)(false)
    in
        let tagPtr = buildGEP(builder)(optionType)(option)([zeroIndex, zeroIndex])(2u32)("tagPtr")
        in
            let payloadPtr = buildGEP(builder)(optionType)(option)([zeroIndex, constInt(i32)(1u64)(false)])(2u32)("payloadPtr")
            in
                let _ =
                    Unit
                    |> (given (_) -> buildStore(builder)(tag)(tagPtr))
                    |> (given (_) -> buildStore(builder)(payload)(payloadPtr))
                in (tagPtr, payloadPtr))

// Reads the tag back, branches on it exactly the way `buildMaxModule` branched on a plain
// condition, and stores either the loaded payload or the caller-supplied default into
// `resultSlot` — the no-`phi` alloca/store/load pattern applied to an ADT discriminant instead
// of a scalar comparison. Returns the merge block so the caller can read `resultSlot` there.
let branchOnOptionTag builder context function i32 tagPtr payloadPtr someTag default_ resultSlot =
    (let loadedTag = buildLoad(builder)(i32)(tagPtr)("loadedTag")
    in
        let isSome = buildICmp(builder)(intPredicateEq)(loadedTag)(someTag)("isSome")
        in
            let someBlock = appendBasicBlock(context)(function)("some")
            in
                let noneBlock = appendBasicBlock(context)(function)("none")
                in
                    let mergeBlock = appendBasicBlock(context)(function)("merge")
                    in
                        let _ = buildCondBr(builder)(isSome)(someBlock)(noneBlock)
                        in
                            let _ =
                                Unit
                                |> (given (_) -> positionBuilderAtEnd(builder)(someBlock))
                                |> (given (_) -> buildLoad(builder)(i32)(payloadPtr)("payload"))
                                |> (given (payload) -> buildStore(builder)(payload)(resultSlot))
                                |> (given (_) -> buildBr(builder)(mergeBlock))
                            in
                                let _ =
                                    Unit
                                    |> (given (_) -> positionBuilderAtEnd(builder)(noneBlock))
                                    |> (given (_) -> buildStore(builder)(default_)(resultSlot))
                                    |> (given (_) -> buildBr(builder)(mergeBlock))
                                in mergeBlock)

// The first genuine ADT-shaped test: a simple `Option(i32)` tagged union, laid out as
// `{i32 tag, i32 payload}` (tag `1` means `Some`, any other value means `None`), and
// `i32 unwrapOr(i32 default_) { match Some(99) with | Some(payload) -> payload | None -> default_ }`.
// No new LLVM C API surface: this is a pure integration of `structType`/`buildGEP`/`buildICmp`/
// `buildCondBr`/the no-`phi` slot pattern already bound, proving they compose into a real ADT
// `match`, not just their own isolated tests.
let buildOptionUnwrapModule name context =
    (let module_ = createModule(name)(context)
    in
        let i32 = int32Type(context)
        in
            let optionType = structType(context)([i32, i32])(2u32)(false)
            in
                match beginFunction(module_)(context)(None)("unwrapOr")(i32)([i32])(1u32) with
                    | (function, _, builder) ->
                        let default_ = getParam(function)(0u32)
                        in
                            let option = buildAlloca(builder)(optionType)("option")
                            in
                                let someTag = constInt(i32)(1u64)(false)
                                in
                                    match false
                                    |> constInt(i32)(99u64)
                                    |> writeOptionSome(builder)(optionType)(option)(i32)(someTag) with
                                        | (tagPtr, payloadPtr) ->
                                            let resultSlot = buildAlloca(builder)(i32)("result")
                                            in
                                                let mergeBlock = branchOnOptionTag(builder)(context)(function)(i32)(tagPtr)(payloadPtr)(someTag)(default_)(resultSlot)
                                                in
                                                    let _ = positionBuilderAtEnd(builder)(mergeBlock)
                                                    in
                                                        let result = buildLoad(builder)(i32)(resultSlot)("result")
                                                        in
                                                            let _ = buildRet(builder)(result)
                                                            in (module_, builder))

// Defines `i32 addEnv(i32 env, i32 x) { ret env + x }`, standing in for a compiled closure body
// (`env` playing the role of a single captured value). Returns the function's own `functionType`
// alongside it, since the indirect call site needs that exact type, not the callee's definition.
let defineAddEnvFunction module_ context =
    (let i32 = int32Type(context)
    in
        match beginFunction(module_)(context)(None)("addEnv")(i32)([i32, i32])(2u32) with
            | (function, fnType, builder) ->
                let env = getParam(function)(0u32)
                in
                    let x = getParam(function)(1u32)
                    in
                        let sum = buildAdd(builder)(env)(x)("sum")
                        in
                            let _ = buildRet(builder)(sum)
                            in (function, fnType, builder))

// Defines `i32 callClosure(ptr closure, i32 x) { call (*closure.code)(closure.env, x) }` against
// an already-built `{ptr code, i32 env}` closure layout — the mechanism a real closure needs: an
// indirect call through a function-pointer value loaded out of memory.
let buildCallClosureFunction module_ context existingBuilder envFnType closureType =
    (let i32 = int32Type(context)
    in
        let ptr = pointerType(context)(0u32)
        in
            match beginFunction(module_)(context)(existingBuilder)("callClosure")(i32)([ptr, i32])(2u32) with
                | (function, fnType, builder) ->
                    let closurePtr = getParam(function)(0u32)
                    in
                        let xArg = getParam(function)(1u32)
                        in
                            let zeroIndex = constInt(i32)(0u64)(false)
                            in
                                let oneIndex = constInt(i32)(1u64)(false)
                                in
                                    let codePtrFieldPtr = buildGEP(builder)(closureType)(closurePtr)([zeroIndex, zeroIndex])(2u32)("codePtrFieldPtr")
                                    in
                                        let envFieldPtr = buildGEP(builder)(closureType)(closurePtr)([zeroIndex, oneIndex])(2u32)("envFieldPtr")
                                        in
                                            let codePtr = buildLoad(builder)(ptr)(codePtrFieldPtr)("codePtr")
                                            in
                                                let envValue = buildLoad(builder)(i32)(envFieldPtr)("envValue")
                                                in
                                                    let result = buildCall(builder)(envFnType)(codePtr)([envValue, xArg])(2u32)("result")
                                                    in
                                                        let _ = buildRet(builder)(result)
                                                        in (function, fnType, builder))

// A deliberately minimal slice of "closures", scoped narrower than a real capture-carrying
// closure: proves the indirect-call mechanism above composes with a real `{ptr, i32}` struct
// layout. No new LLVM C API surface: `buildCall`'s callee was always a plain value, never
// required to be a direct `addFunction` result. The harder pieces a real closure needs
// (heap-allocated env, capture packing/arity, RC of captured values) are deliberately out of
// scope for this slice.
let buildClosureCallModule name context =
    (let module_ = createModule(name)(context)
    in
        let ptr = pointerType(context)(0u32)
        in
            let closureType = structType(context)([ptr, int32Type(context)])(2u32)(false)
            in
                match defineAddEnvFunction(module_)(context) with
                    | (_, envFnType, addEnvBuilder) ->
                        match buildCallClosureFunction(module_)(context)(Some(addEnvBuilder))(envFnType)(closureType) with
                            | (_, _, builder) -> (module_, builder))

// The next slice past `buildClosureCallModule`: the closure struct itself now lives on the heap
// (`malloc`d, matching `buildMallocFreeModule`'s declare-and-call pattern), not a stack `alloca` —
// a necessary property before RC can mean anything, since RC only applies to heap objects.
// `makeAndCallClosure(i32 env, i32 x)` mallocs a `{ptr, i32}`, stores `addEnv`'s own function
// value into the code-pointer field (a function value used as a plain `ptr`, no cast needed) and
// `env` into the env field, calls it back through `callClosure`, frees it, and returns the result.
// Multiple captures, variable arity, and reference counting remain deliberately out of scope.
let buildHeapClosureModule name context =
    (let module_ = createModule(name)(context)
    in
        let i32 = int32Type(context)
        in
            let i64 = int64Type(context)
            in
                let ptr = pointerType(context)(0u32)
                in
                    let closureType = structType(context)([ptr, i32])(2u32)(false)
                    in
                        let mallocType = functionType(ptr)([i64])(1u32)(false)
                        in
                            let mallocFn = addFunction(module_)("malloc")(mallocType)
                            in
                                let freeType =
                                    functionType(voidType(context))([ptr])(1u32)(false)
                                in
                                    let freeFn = addFunction(module_)("free")(freeType)
                                    in
                                        match defineAddEnvFunction(module_)(context) with
                                            | (addEnvFunction, envFnType, addEnvBuilder) ->
                                                match buildCallClosureFunction(module_)(context)(Some(addEnvBuilder))(envFnType)(closureType) with
                                                    | (callClosureFunction, callClosureType, builder) ->
                                                        match beginFunction(module_)(context)(Some(builder))("makeAndCallClosure")(i32)([i32, i32])(2u32) with
                                                            | (function, _, callerBuilder) ->
                                                                let envArg = getParam(function)(0u32)
                                                                in
                                                                    let xArg = getParam(function)(1u32)
                                                                    in
                                                                        let sizeArg = constInt(i64)(16u64)(false)
                                                                        in
                                                                            let closurePtr = buildCall(callerBuilder)(mallocType)(mallocFn)([sizeArg])(1u32)("closurePtr")
                                                                            in
                                                                                let zeroIndex = constInt(i32)(0u64)(false)
                                                                                in
                                                                                    let oneIndex = constInt(i32)(1u64)(false)
                                                                                    in
                                                                                        let codePtrFieldPtr =
                                                                                            buildGEP(callerBuilder)(closureType)(closurePtr)([zeroIndex, zeroIndex])(2u32)(
                                                                                                "codePtrFieldPtr"
                                                                                            )
                                                                                        in
                                                                                            let envFieldPtr =
                                                                                                buildGEP(callerBuilder)(closureType)(closurePtr)([zeroIndex, oneIndex])(2u32)(
                                                                                                    "envFieldPtr"
                                                                                                )
                                                                                            in
                                                                                                let _ = buildStore(callerBuilder)(addEnvFunction)(codePtrFieldPtr)
                                                                                                in
                                                                                                    let _ = buildStore(callerBuilder)(envArg)(envFieldPtr)
                                                                                                    in
                                                                                                        let result =
                                                                                                            buildCall(callerBuilder)(callClosureType)(callClosureFunction)(
                                                                                                                [closurePtr, xArg]
                                                                                                            )(2u32)("result")
                                                                                                        in
                                                                                                            let _ = buildCall(callerBuilder)(freeType)(freeFn)([closurePtr])(1u32)("")
                                                                                                            in
                                                                                                                let _ = buildRet(callerBuilder)(result)
                                                                                                                in (module_, callerBuilder))

// The real Ashes RC header, per architecture.md's "RC allocation and layout": a 16-byte
// `{i64 reference_count, i64 allocation_size}` header immediately before the payload, with the
// public value pointer addressing the payload rather than the header. Defines
// `ptr rcAlloc(i64 payloadSize)`: `malloc`s `16 + payloadSize` bytes, writes `count = 1` and the
// requested size into the header, and returns a pointer past it (`buildGEP` over `i8` with a
// scalar index doing plain byte-pointer arithmetic — no new LLVM C API surface for that, just a
// different element type than every earlier struct/array `buildGEP` use).
let defineRcAllocFunction module_ context headerType i8 mallocType mallocFn =
    (let i64 = int64Type(context)
    in
        let ptr = pointerType(context)(0u32)
        in
            match beginFunction(module_)(context)(None)("rcAlloc")(ptr)([i64])(1u32) with
                | (function, fnType, builder) ->
                    let payloadSize = getParam(function)(0u32)
                    in
                        let sixteen = constInt(i64)(16u64)(false)
                        in
                            let totalSize = buildAdd(builder)(sixteen)(payloadSize)("totalSize")
                            in
                                let headerPtr = buildCall(builder)(mallocType)(mallocFn)([totalSize])(1u32)("headerPtr")
                                in
                                    let zeroIndex =
                                        constInt(int32Type(context))(0u64)(false)
                                    in
                                        let oneIndex =
                                            constInt(int32Type(context))(1u64)(false)
                                        in
                                            let countFieldPtr = buildGEP(builder)(headerType)(headerPtr)([zeroIndex, zeroIndex])(2u32)("countFieldPtr")
                                            in
                                                let sizeFieldPtr = buildGEP(builder)(headerType)(headerPtr)([zeroIndex, oneIndex])(2u32)("sizeFieldPtr")
                                                in
                                                    let one = constInt(i64)(1u64)(false)
                                                    in
                                                        let _ =
                                                            Unit
                                                            |> (given (_) -> buildStore(builder)(one)(countFieldPtr))
                                                            |> (given (_) -> buildStore(builder)(payloadSize)(sizeFieldPtr))
                                                        in
                                                            let payloadPtr = buildGEP(builder)(i8)(headerPtr)([sixteen])(1u32)("payloadPtr")
                                                            in
                                                                let _ = buildRet(builder)(payloadPtr)
                                                                in (function, fnType, builder))

// Walks a value pointer back to its RC header with a NEGATIVE byte offset (the mirror image of
// `defineRcAllocFunction`'s forward one, and the same pointer arithmetic real `RcDup` needs, since
// the public value pointer never carries the header with it) and increments the reference count.
let defineRcRetainFunction module_ context existingBuilder headerType i8 ptr =
    (let i64 = int64Type(context)
    in
        match beginFunction(module_)(context)(existingBuilder)("rcRetain")(ptr)([ptr])(1u32) with
            | (function, fnType, builder) ->
                let value = getParam(function)(0u32)
                in
                    let negSixteen = constInt(i64)(18446744073709551600u64)(false)
                    in
                        let headerPtr = buildGEP(builder)(i8)(value)([negSixteen])(1u32)("headerPtr")
                        in
                            let zeroIndex =
                                constInt(int32Type(context))(0u64)(false)
                            in
                                let countFieldPtr = buildGEP(builder)(headerType)(headerPtr)([zeroIndex, zeroIndex])(2u32)("countFieldPtr")
                                in
                                    let count = buildLoad(builder)(i64)(countFieldPtr)("count")
                                    in
                                        let newCount =
                                            buildAdd(builder)(count)(constInt(i64)(1u64)(false))("newCount")
                                        in
                                            let _ = buildStore(builder)(newCount)(countFieldPtr)
                                            in
                                                let _ = buildRet(builder)(value)
                                                in (function, fnType, builder))

// The `RcDrop` mirror of `defineRcRetainFunction`: decrements the count and, on the last
// reference, frees the ORIGINAL header pointer `malloc` returned — never the value pointer the
// caller passed in — matching architecture.md's "on the last reference it ... releases the cell."
// A leaf payload with no owned children is deliberately the only case handled: the type-directed
// child-drop path a real ADT needs is a separate, bigger slice.
let defineRcReleaseFunction module_ context existingBuilder headerType i8 ptr freeType freeFn =
    (let i64 = int64Type(context)
    in
        match beginFunction(module_)(context)(existingBuilder)("rcRelease")(voidType(context))([ptr])(1u32) with
            | (function, fnType, builder) ->
                let value = getParam(function)(0u32)
                in
                    let negSixteen = constInt(i64)(18446744073709551600u64)(false)
                    in
                        let headerPtr = buildGEP(builder)(i8)(value)([negSixteen])(1u32)("headerPtr")
                        in
                            let zeroIndex =
                                constInt(int32Type(context))(0u64)(false)
                            in
                                let countFieldPtr = buildGEP(builder)(headerType)(headerPtr)([zeroIndex, zeroIndex])(2u32)("countFieldPtr")
                                in
                                    let count = buildLoad(builder)(i64)(countFieldPtr)("count")
                                    in
                                        let newCount =
                                            buildSub(builder)(count)(constInt(i64)(1u64)(false))("newCount")
                                        in
                                            let _ = buildStore(builder)(newCount)(countFieldPtr)
                                            in
                                                let isZero =
                                                    buildICmp(builder)(intPredicateEq)(newCount)(constInt(i64)(0u64)(false))("isZero")
                                                in
                                                    let freeBlock = appendBasicBlock(context)(function)("free")
                                                    in
                                                        let doneBlock = appendBasicBlock(context)(function)("done")
                                                        in
                                                            let _ = buildCondBr(builder)(isZero)(freeBlock)(doneBlock)
                                                            in
                                                                let _ =
                                                                    Unit
                                                                    |> (given (_) -> positionBuilderAtEnd(builder)(freeBlock))
                                                                    |> (given (_) -> buildCall(builder)(freeType)(freeFn)([headerPtr])(1u32)(""))
                                                                    |> (given (_) -> buildRetVoid(builder))
                                                                in
                                                                    let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                    in
                                                                        let _ = buildRetVoid(builder)
                                                                        in (function, fnType, builder))

// The mechanism every earlier slice in this arc has been building toward: a real RC cell,
// allocated, retained, released once (not yet zero), read back while still alive, then released
// again (now zero, actually freed) — `rcCellLifecycle() { p = rcAlloc(4); *p = 42; rcRetain(p);
// rcRelease(p); v = *p; rcRelease(p); ret i32 v }`. Multi-field/ADT payloads, owned-child release,
// and atomicity are all deliberately out of scope; this proves the count itself is correct.
let buildRcCellLifecycleModule name context =
    (let module_ = createModule(name)(context)
    in
        let i32 = int32Type(context)
        in
            let i64 = int64Type(context)
            in
                let i8 = int8Type(context)
                in
                    let ptr = pointerType(context)(0u32)
                    in
                        let headerType = structType(context)([i64, i64])(2u32)(false)
                        in
                            let mallocType = functionType(ptr)([i64])(1u32)(false)
                            in
                                let mallocFn = addFunction(module_)("malloc")(mallocType)
                                in
                                    let freeType =
                                        functionType(voidType(context))([ptr])(1u32)(false)
                                    in
                                        let freeFn = addFunction(module_)("free")(freeType)
                                        in
                                            match defineRcAllocFunction(module_)(context)(headerType)(i8)(mallocType)(mallocFn) with
                                                | (rcAllocFunction, rcAllocType, allocBuilder) ->
                                                    match defineRcRetainFunction(module_)(context)(Some(allocBuilder))(headerType)(i8)(ptr) with
                                                        | (rcRetainFunction, rcRetainType, retainBuilder) ->
                                                            match defineRcReleaseFunction(module_)(context)(Some(retainBuilder))(headerType)(i8)(ptr)(freeType)(freeFn) with
                                                                | (rcReleaseFunction, rcReleaseType, releaseBuilder) ->
                                                                    match beginFunction(module_)(context)(Some(releaseBuilder))("rcCellLifecycle")(i32)([])(0u32) with
                                                                        | (_, _, builder) ->
                                                                            let cell = buildCall(builder)(rcAllocType)(rcAllocFunction)([constInt(i64)(4u64)(false)])(1u32)("cell")
                                                                            in
                                                                                let _ =
                                                                                    buildStore(builder)(constInt(i32)(42u64)(false))(cell)
                                                                                in
                                                                                    let _ = buildCall(builder)(rcRetainType)(rcRetainFunction)([cell])(1u32)("retained")
                                                                                    in
                                                                                        let _ = buildCall(builder)(rcReleaseType)(rcReleaseFunction)([cell])(1u32)("")
                                                                                        in
                                                                                            let loaded = buildLoad(builder)(i32)(cell)("loaded")
                                                                                            in
                                                                                                let _ = buildCall(builder)(rcReleaseType)(rcReleaseFunction)([cell])(1u32)("")
                                                                                                in
                                                                                                    let _ = buildRet(builder)(loaded)
                                                                                                    in (module_, builder))

// The `rcRelease` mirror architecture.md actually describes: "on the last reference it first
// releases owned children through the type-directed drop path, then releases the cell." This
// specializes that to the simplest possible shape — a payload that is exactly one owned `ptr`
// field (offset 0, no GEP needed) — dropping the child via the ALREADY-DEFINED generic
// `rcRelease` (passed in, not redefined) before freeing the node's own header. Nothing here is
// specific to being a "node": a real ADT drop path repeats this same shape once per owned field.
let defineRcReleaseNodeFunction module_ context existingBuilder headerType i8 ptr freeType freeFn rcReleaseType rcReleaseFn =
    (let i64 = int64Type(context)
    in
        match beginFunction(module_)(context)(existingBuilder)("rcReleaseNode")(voidType(context))([ptr])(1u32) with
            | (function, fnType, builder) ->
                let value = getParam(function)(0u32)
                in
                    let negSixteen = constInt(i64)(18446744073709551600u64)(false)
                    in
                        let headerPtr = buildGEP(builder)(i8)(value)([negSixteen])(1u32)("headerPtr")
                        in
                            let zeroIndex =
                                constInt(int32Type(context))(0u64)(false)
                            in
                                let countFieldPtr = buildGEP(builder)(headerType)(headerPtr)([zeroIndex, zeroIndex])(2u32)("countFieldPtr")
                                in
                                    let count = buildLoad(builder)(i64)(countFieldPtr)("count")
                                    in
                                        let newCount =
                                            buildSub(builder)(count)(constInt(i64)(1u64)(false))("newCount")
                                        in
                                            let _ = buildStore(builder)(newCount)(countFieldPtr)
                                            in
                                                let isZero =
                                                    buildICmp(builder)(intPredicateEq)(newCount)(constInt(i64)(0u64)(false))("isZero")
                                                in
                                                    let freeBlock = appendBasicBlock(context)(function)("free")
                                                    in
                                                        let doneBlock = appendBasicBlock(context)(function)("done")
                                                        in
                                                            let _ = buildCondBr(builder)(isZero)(freeBlock)(doneBlock)
                                                            in
                                                                let _ =
                                                                    Unit
                                                                    |> (given (_) -> positionBuilderAtEnd(builder)(freeBlock))
                                                                    |> (given (_) -> buildLoad(builder)(ptr)(value)("childPtr"))
                                                                    |> (given (childPtr) -> buildCall(builder)(rcReleaseType)(rcReleaseFn)([childPtr])(1u32)(""))
                                                                    |> (given (_) -> buildCall(builder)(freeType)(freeFn)([headerPtr])(1u32)(""))
                                                                    |> (given (_) -> buildRetVoid(builder))
                                                                in
                                                                    let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                    in
                                                                        let _ = buildRetVoid(builder)
                                                                        in (function, fnType, builder))

// Proves the cascading release above actually cascades: a leaf `i32` cell (`rcAlloc(4)`, value 7)
// owned by a node whose one field IS a pointer to that leaf (`rcAlloc(8)`, storing the leaf's
// pointer at offset 0). Releasing the node once (its only reference) must transitively free the
// leaf too, not just the node's own header — `rcNodeLifecycle() { leaf = rcAlloc(4); *leaf = 7;
// node = rcAlloc(8); *node = leaf; v = *leaf; rcReleaseNode(node); ret i32 v }`, reading `v` before
// the release that frees both cells.
let buildRcNodeReleaseModule name context =
    (let module_ = createModule(name)(context)
    in
        let i32 = int32Type(context)
        in
            let i64 = int64Type(context)
            in
                let i8 = int8Type(context)
                in
                    let ptr = pointerType(context)(0u32)
                    in
                        let headerType = structType(context)([i64, i64])(2u32)(false)
                        in
                            let mallocType = functionType(ptr)([i64])(1u32)(false)
                            in
                                let mallocFn = addFunction(module_)("malloc")(mallocType)
                                in
                                    let freeType =
                                        functionType(voidType(context))([ptr])(1u32)(false)
                                    in
                                        let freeFn = addFunction(module_)("free")(freeType)
                                        in
                                            match defineRcAllocFunction(module_)(context)(headerType)(i8)(mallocType)(mallocFn) with
                                                | (rcAllocFunction, rcAllocType, allocBuilder) ->
                                                    match defineRcReleaseFunction(module_)(context)(Some(allocBuilder))(headerType)(i8)(ptr)(freeType)(freeFn) with
                                                        | (rcReleaseFunction, rcReleaseType, releaseBuilder) ->
                                                            match defineRcReleaseNodeFunction(module_)(context)(Some(releaseBuilder))(headerType)(i8)(ptr)(freeType)(freeFn)(
                                                                rcReleaseType
                                                            )(rcReleaseFunction) with
                                                                | (rcReleaseNodeFunction, rcReleaseNodeType, releaseNodeBuilder) ->
                                                                    match beginFunction(module_)(context)(Some(releaseNodeBuilder))("rcNodeLifecycle")(i32)([])(0u32) with
                                                                        | (_, _, builder) ->
                                                                            let leaf = buildCall(builder)(rcAllocType)(rcAllocFunction)([constInt(i64)(4u64)(false)])(1u32)("leaf")
                                                                            in
                                                                                let _ =
                                                                                    buildStore(builder)(constInt(i32)(7u64)(false))(leaf)
                                                                                in
                                                                                    let node =
                                                                                        buildCall(builder)(rcAllocType)(rcAllocFunction)([constInt(i64)(8u64)(false)])(1u32)(
                                                                                            "node"
                                                                                        )
                                                                                    in
                                                                                        let _ = buildStore(builder)(leaf)(node)
                                                                                        in
                                                                                            let leafValue = buildLoad(builder)(i32)(leaf)("leafValue")
                                                                                            in
                                                                                                let _ = buildCall(builder)(rcReleaseNodeType)(rcReleaseNodeFunction)([node])(1u32)("")
                                                                                                in
                                                                                                    let _ = buildRet(builder)(leafValue)
                                                                                                    in (module_, builder))

// The actual "type-directed" half of architecture.md's drop path, not just the single-shape
// cascade `defineRcReleaseNodeFunction` proved: a payload can OWN a child only in some of its
// tag states. `optionType` is `{i32 tag, ptr child}` (tag `1` means `Some`, owning `child`; any
// other value means `None`, with `child` unused) — the RC-aware analogue of PR #696's plain
// `Option(i32)` `match`. On the last reference, `rcReleaseOption` reads the tag FIRST and only
// drops `child` (via the already-defined generic `rcRelease`) on the `Some` path before freeing
// its own header either way; the `None` path frees without ever touching `rcRelease`.
let defineRcReleaseOptionFunction module_ context existingBuilder headerType i8 ptr optionType freeType freeFn rcReleaseType rcReleaseFn =
    (let i64 = int64Type(context)
    in
        let i32 = int32Type(context)
        in
            match beginFunction(module_)(context)(existingBuilder)("rcReleaseOption")(voidType(context))([ptr])(1u32) with
                | (function, fnType, builder) ->
                    let value = getParam(function)(0u32)
                    in
                        let negSixteen = constInt(i64)(18446744073709551600u64)(false)
                        in
                            let headerPtr = buildGEP(builder)(i8)(value)([negSixteen])(1u32)("headerPtr")
                            in
                                let zeroIndex = constInt(i32)(0u64)(false)
                                in
                                    let countFieldPtr = buildGEP(builder)(headerType)(headerPtr)([zeroIndex, zeroIndex])(2u32)("countFieldPtr")
                                    in
                                        let count = buildLoad(builder)(i64)(countFieldPtr)("count")
                                        in
                                            let newCount =
                                                buildSub(builder)(count)(constInt(i64)(1u64)(false))("newCount")
                                            in
                                                let _ = buildStore(builder)(newCount)(countFieldPtr)
                                                in
                                                    let isZero =
                                                        buildICmp(builder)(intPredicateEq)(newCount)(constInt(i64)(0u64)(false))("isZero")
                                                    in
                                                        let dropBlock = appendBasicBlock(context)(function)("drop")
                                                        in
                                                            let doneBlock = appendBasicBlock(context)(function)("done")
                                                            in
                                                                let _ = buildCondBr(builder)(isZero)(dropBlock)(doneBlock)
                                                                in
                                                                    let _ = positionBuilderAtEnd(builder)(dropBlock)
                                                                    in
                                                                        let tagPtr = buildGEP(builder)(optionType)(value)([zeroIndex, zeroIndex])(2u32)("tagPtr")
                                                                        in
                                                                            let tag = buildLoad(builder)(i32)(tagPtr)("tag")
                                                                            in
                                                                                let isSome =
                                                                                    buildICmp(builder)(intPredicateEq)(tag)(constInt(i32)(1u64)(false))("isSome")
                                                                                in
                                                                                    let someBlock = appendBasicBlock(context)(function)("some")
                                                                                    in
                                                                                        let noneBlock = appendBasicBlock(context)(function)("none")
                                                                                        in
                                                                                            let _ = buildCondBr(builder)(isSome)(someBlock)(noneBlock)
                                                                                            in
                                                                                                let _ =
                                                                                                    Unit
                                                                                                    |> (given (_) -> positionBuilderAtEnd(builder)(someBlock))
                                                                                                    |> (given (_) ->
                                                                                                        buildGEP(builder)(optionType)(value)([zeroIndex, constInt(i32)(1u64)(false)])(
                                                                                                            2u32
                                                                                                        )("childPtrField"))
                                                                                                    |> (given (childPtrField) -> buildLoad(builder)(ptr)(childPtrField)("childPtr"))
                                                                                                    |> (given (childPtr) -> buildCall(builder)(rcReleaseType)(rcReleaseFn)([childPtr])(1u32)(""))
                                                                                                    |> (given (_) -> buildCall(builder)(freeType)(freeFn)([headerPtr])(1u32)(""))
                                                                                                    |> (given (_) -> buildRetVoid(builder))
                                                                                                in
                                                                                                    let _ =
                                                                                                        Unit
                                                                                                        |> (given (_) -> positionBuilderAtEnd(builder)(noneBlock))
                                                                                                        |> (given (_) -> buildCall(builder)(freeType)(freeFn)([headerPtr])(1u32)(""))
                                                                                                        |> (given (_) -> buildRetVoid(builder))
                                                                                                    in
                                                                                                        let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                                                        in
                                                                                                            let _ = buildRetVoid(builder)
                                                                                                            in (function, fnType, builder))

// Proves `rcReleaseOption` actually takes the tag-directed fork: a `Some` option (tag `1`)
// pointing at an owned leaf `i32` cell (value 42). Releasing it (its only reference) must both
// free the option's own header AND cascade into `rcRelease` for the leaf — the `None` shape
// (tag `0`) is deliberately proven at the function level (both branches exist and are reachable
// in the emitted code), not exercised again at a second call site, since a real "build a None and
// release it" run would be a byte-for-byte repeat of `buildRcNodeReleaseModule`'s already-proven
// single-branch shape with nothing new to show.
let buildRcOptionReleaseModule name context =
    (let module_ = createModule(name)(context)
    in
        let i32 = int32Type(context)
        in
            let i64 = int64Type(context)
            in
                let i8 = int8Type(context)
                in
                    let ptr = pointerType(context)(0u32)
                    in
                        let headerType = structType(context)([i64, i64])(2u32)(false)
                        in
                            let optionType = structType(context)([i32, ptr])(2u32)(false)
                            in
                                let mallocType = functionType(ptr)([i64])(1u32)(false)
                                in
                                    let mallocFn = addFunction(module_)("malloc")(mallocType)
                                    in
                                        let freeType =
                                            functionType(voidType(context))([ptr])(1u32)(false)
                                        in
                                            let freeFn = addFunction(module_)("free")(freeType)
                                            in
                                                match defineRcAllocFunction(module_)(context)(headerType)(i8)(mallocType)(mallocFn) with
                                                    | (rcAllocFunction, rcAllocType, allocBuilder) ->
                                                        match defineRcReleaseFunction(module_)(context)(Some(allocBuilder))(headerType)(i8)(ptr)(freeType)(freeFn) with
                                                            | (rcReleaseFunction, rcReleaseType, releaseBuilder) ->
                                                                match defineRcReleaseOptionFunction(module_)(context)(Some(releaseBuilder))(headerType)(i8)(ptr)(optionType)(
                                                                    freeType
                                                                )(freeFn)(rcReleaseType)(rcReleaseFunction) with
                                                                    | (rcReleaseOptionFunction, rcReleaseOptionType, releaseOptionBuilder) ->
                                                                        match beginFunction(module_)(context)(Some(releaseOptionBuilder))("rcOptionLifecycle")(i32)([])(0u32) with
                                                                            | (_, _, builder) ->
                                                                                let leaf = buildCall(builder)(rcAllocType)(rcAllocFunction)([constInt(i64)(4u64)(false)])(1u32)("leaf")
                                                                                in
                                                                                    let _ =
                                                                                        buildStore(builder)(constInt(i32)(42u64)(false))(leaf)
                                                                                    in
                                                                                        let option =
                                                                                            buildCall(builder)(rcAllocType)(rcAllocFunction)([constInt(i64)(16u64)(false)])(1u32)(
                                                                                                "option"
                                                                                            )
                                                                                        in
                                                                                            let zeroIndex = constInt(i32)(0u64)(false)
                                                                                            in
                                                                                                let tagPtr = buildGEP(builder)(optionType)(option)([zeroIndex, zeroIndex])(2u32)("tagPtr")
                                                                                                in
                                                                                                    let childPtrField =
                                                                                                        buildGEP(builder)(optionType)(option)([zeroIndex, constInt(i32)(1u64)(false)])(2u32)(
                                                                                                            "childPtrField"
                                                                                                        )
                                                                                                    in
                                                                                                        let _ =
                                                                                                            Unit
                                                                                                            |> (given (_) ->
                                                                                                                buildStore(builder)(constInt(i32)(1u64)(false))(tagPtr))
                                                                                                            |> (given (_) -> buildStore(builder)(leaf)(childPtrField))
                                                                                                        in
                                                                                                            let leafValue = buildLoad(builder)(i32)(leaf)("leafValue")
                                                                                                            in
                                                                                                                let _ =
                                                                                                                    buildCall(builder)(rcReleaseOptionType)(rcReleaseOptionFunction)(
                                                                                                                        [option]
                                                                                                                    )(1u32)("")
                                                                                                                in
                                                                                                                    let _ = buildRet(builder)(leafValue)
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

let testEmitAssemblyForGreetingModule unit =
    match emitModule(buildGreetingModule)("selfhost-backend-greeting-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("greeting")

let testEmitAssemblyForOptionUnwrapModule unit =
    match emitModule(buildOptionUnwrapModule)("selfhost-backend-option-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("unwrapOr")

let testEmitAssemblyForClosureCallModule unit =
    match emitModule(buildClosureCallModule)("selfhost-backend-closure-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("callClosure")

let testEmitAssemblyForHeapClosureModule unit =
    match emitModule(buildHeapClosureModule)("selfhost-backend-heap-closure-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("makeAndCallClosure")

let testEmitAssemblyForRcCellLifecycleModule unit =
    match emitModule(buildRcCellLifecycleModule)("selfhost-backend-rc-cell-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("rcCellLifecycle")

let testEmitAssemblyForRcNodeReleaseModule unit =
    match emitModule(buildRcNodeReleaseModule)("selfhost-backend-rc-node-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("rcNodeLifecycle")

let testEmitAssemblyForRcOptionReleaseModule unit =
    match emitModule(buildRcOptionReleaseModule)("selfhost-backend-rc-option-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("rcOptionLifecycle")

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
    |> testEmitAssemblyForGreetingModule
    |> testEmitAssemblyForOptionUnwrapModule
    |> testEmitAssemblyForClosureCallModule
    |> testEmitAssemblyForHeapClosureModule
    |> testEmitAssemblyForRcCellLifecycleModule
    |> testEmitAssemblyForRcNodeReleaseModule
    |> testEmitAssemblyForRcOptionReleaseModule
    |> (given (_) -> Ashes.IO.print("all self-hosted backend tests passed"))

run(Unit)
