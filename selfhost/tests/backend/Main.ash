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
import AshesCompiler.Backend.IrCodegen
import AshesCompiler.Backend.ElfLinker
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOptimizer
import AshesCompiler.Semantics.ProjectSyntaxStitching
import AshesCompiler.Semantics.ShippedModuleStitching
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

// A hand-built entry function's own exit-syscall tail, matching
// `AshesCompiler.Backend.IrCodegen`'s `emitLinuxProcessExit` exactly (that function isn't exported
// from the package — this file has always hand-built every LLVM sequence it tests independently).
let emitLinuxProcessExitForTest builder i64 =
    (let syscallType = functionType(i64)([i64, i64, i64, i64])(4u32)(false)
    in
        let syscallAsm = getInlineAsm(syscallType)("syscall")("={rax},{rax},{rdi},{rsi},{rdx},~{rcx},~{r11},~{memory}")(true)(false)
        in
            let sixty = constInt(i64)(60u64)(false)
            in
                let zero = constInt(i64)(0u64)(false)
                in
                    let _ = buildCall(builder)(syscallType)(syscallAsm)([sixty, zero, zero, zero])(4u32)("sys_exit")
                    in buildUnreachable(builder))

// An entry-shaped module (`void`, ends in the exit syscall, never `ret`) that also calls
// `malloc`/`free` — proving `AshesCompiler.Backend.ElfLinker`'s dynamic linking against a
// genuinely linkable program (matching `AshesCompiler.Backend.IrCodegen`'s own entry-function
// contract), not just a `ret`-based leaf function like `buildMallocFreeModule` above.
let buildMallocFreeEntryModule name context =
    (let module_ = createModule(name)(context)
    in
        let i64 = int64Type(context)
        in
            let ptrType = pointerType(context)(0u32)
            in
                let mallocType = functionType(ptrType)([i64])(1u32)(false)
                in
                    let mallocFn = addFunction(module_)("malloc")(mallocType)
                    in
                        let freeType =
                            functionType(voidType(context))([ptrType])(1u32)(false)
                        in
                            let freeFn = addFunction(module_)("free")(freeType)
                            in
                                let functionValue =
                                    false
                                    |> functionType(voidType(context))([])(0u32)
                                    |> addFunction(module_)(name)
                                in
                                    let entryBlock = appendBasicBlock(context)(functionValue)("entry")
                                    in
                                        let builder = createBuilder(context)
                                        in
                                            let _ = positionBuilderAtEnd(builder)(entryBlock)
                                            in
                                                let sizeArg = constInt(i64)(8u64)(false)
                                                in
                                                    let ptr = buildCall(builder)(mallocType)(mallocFn)([sizeArg])(1u32)("ptr")
                                                    in
                                                        let seven = constInt(i64)(7u64)(false)
                                                        in
                                                            let _ = buildStore(builder)(seven)(ptr)
                                                            in
                                                                let _ = buildCall(builder)(freeType)(freeFn)([ptr])(1u32)("")
                                                                in
                                                                    let _ = emitLinuxProcessExitForTest(builder)(i64)
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
let defineAddEnvFunction module_ context existingBuilder =
    (let i32 = int32Type(context)
    in
        match beginFunction(module_)(context)(existingBuilder)("addEnv")(i32)([i32, i32])(2u32) with
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
                match defineAddEnvFunction(module_)(context)(None) with
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
                                        match defineAddEnvFunction(module_)(context)(None) with
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

// Closures capturing MORE THAN ONE value: `{ptr code, i32 c1, i32 c2}`, extending the
// single-capture closure struct above the same way `defineRcReleaseTreeFunction` (below) extends
// single-child release to multiple owned fields — a deliberately narrow next slice, not the real
// feature. `addTwoCaptures(i32 c1, i32 c2, i32 x) { ret c1 + c2 + x }` stands in for a compiled
// closure body over two captures; `callMultiCaptureClosure` loads all three fields and calls the
// loaded function pointer with both captures plus the argument — still one flat call, not curried,
// and still scalar-only captures (an RC-owned capture's own drop, and a general N-capture/N-arity
// calling convention, are separate, later slices). No new LLVM C API surface.
let defineAddTwoCapturesFunction module_ context =
    (let i32 = int32Type(context)
    in
        match beginFunction(module_)(context)(None)("addTwoCaptures")(i32)([i32, i32, i32])(3u32) with
            | (function, fnType, builder) ->
                let c1 = getParam(function)(0u32)
                in
                    let c2 = getParam(function)(1u32)
                    in
                        let x = getParam(function)(2u32)
                        in
                            let sum1 = buildAdd(builder)(c1)(c2)("sum1")
                            in
                                let sum2 = buildAdd(builder)(sum1)(x)("sum2")
                                in
                                    let _ = buildRet(builder)(sum2)
                                    in (function, fnType, builder))

let buildCallMultiCaptureClosureFunction module_ context existingBuilder closureFnType closureType =
    (let i32 = int32Type(context)
    in
        let ptr = pointerType(context)(0u32)
        in
            match beginFunction(module_)(context)(existingBuilder)("callMultiCaptureClosure")(i32)([ptr, i32])(2u32) with
                | (function, fnType, builder) ->
                    let closurePtr = getParam(function)(0u32)
                    in
                        let xArg = getParam(function)(1u32)
                        in
                            let zeroIndex = constInt(i32)(0u64)(false)
                            in
                                let codePtrFieldPtr = buildGEP(builder)(closureType)(closurePtr)([zeroIndex, zeroIndex])(2u32)("codePtrFieldPtr")
                                in
                                    let c1FieldPtr = buildGEP(builder)(closureType)(closurePtr)([zeroIndex, constInt(i32)(1u64)(false)])(2u32)("c1FieldPtr")
                                    in
                                        let c2FieldPtr = buildGEP(builder)(closureType)(closurePtr)([zeroIndex, constInt(i32)(2u64)(false)])(2u32)("c2FieldPtr")
                                        in
                                            let codePtr = buildLoad(builder)(ptr)(codePtrFieldPtr)("codePtr")
                                            in
                                                let c1 = buildLoad(builder)(i32)(c1FieldPtr)("c1")
                                                in
                                                    let c2 = buildLoad(builder)(i32)(c2FieldPtr)("c2")
                                                    in
                                                        let result = buildCall(builder)(closureFnType)(codePtr)([c1, c2, xArg])(3u32)("result")
                                                        in
                                                            let _ = buildRet(builder)(result)
                                                            in (function, fnType, builder))

let buildMultiCaptureClosureModule name context =
    (let module_ = createModule(name)(context)
    in
        let ptr = pointerType(context)(0u32)
        in
            let closureType = structType(context)([ptr, int32Type(context), int32Type(context)])(3u32)(false)
            in
                match defineAddTwoCapturesFunction(module_)(context) with
                    | (_, closureFnType, addTwoCapturesBuilder) ->
                        match buildCallMultiCaptureClosureFunction(module_)(context)(Some(addTwoCapturesBuilder))(closureFnType)(closureType) with
                            | (_, _, builder) -> (module_, builder))

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

// One arm owning MORE THAN ONE child — `defineRcReleaseNodeFunction`/`defineRcReleaseOptionFunction`
// above each ever dropped at most one. `treeType` is `{i32 tag, ptr left, ptr right}` (tag `1`
// means `Node`, owning BOTH `left` and `right`; anything else means `Leaf`, owning neither).
// `rcReleaseTree`'s `Node` arm drops both children, in field order, via the already-defined
// generic `rcRelease` — the same "once per owned field" repetition a real multi-field ADT drop
// path needs, just written out twice instead of generated. `Leaf` frees without dropping anything.
let defineRcReleaseTreeFunction module_ context existingBuilder headerType i8 ptr treeType freeType freeFn rcReleaseType rcReleaseFn =
    (let i64 = int64Type(context)
    in
        let i32 = int32Type(context)
        in
            match beginFunction(module_)(context)(existingBuilder)("rcReleaseTree")(voidType(context))([ptr])(1u32) with
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
                                                                        let tagPtr = buildGEP(builder)(treeType)(value)([zeroIndex, zeroIndex])(2u32)("tagPtr")
                                                                        in
                                                                            let tag = buildLoad(builder)(i32)(tagPtr)("tag")
                                                                            in
                                                                                let isNode =
                                                                                    buildICmp(builder)(intPredicateEq)(tag)(constInt(i32)(1u64)(false))("isNode")
                                                                                in
                                                                                    let nodeBlock = appendBasicBlock(context)(function)("node")
                                                                                    in
                                                                                        let leafBlock = appendBasicBlock(context)(function)("leaf")
                                                                                        in
                                                                                            let _ = buildCondBr(builder)(isNode)(nodeBlock)(leafBlock)
                                                                                            in
                                                                                                let _ =
                                                                                                    Unit
                                                                                                    |> (given (_) -> positionBuilderAtEnd(builder)(nodeBlock))
                                                                                                    |> (given (_) ->
                                                                                                        buildGEP(builder)(treeType)(value)([zeroIndex, constInt(i32)(1u64)(false)])(2u32)(
                                                                                                            "leftPtrField"
                                                                                                        ))
                                                                                                    |> (given (leftPtrField) -> buildLoad(builder)(ptr)(leftPtrField)("leftPtr"))
                                                                                                    |> (given (leftPtr) -> buildCall(builder)(rcReleaseType)(rcReleaseFn)([leftPtr])(1u32)(""))
                                                                                                    |> (given (_) ->
                                                                                                        buildGEP(builder)(treeType)(value)([zeroIndex, constInt(i32)(2u64)(false)])(2u32)(
                                                                                                            "rightPtrField"
                                                                                                        ))
                                                                                                    |> (given (rightPtrField) -> buildLoad(builder)(ptr)(rightPtrField)("rightPtr"))
                                                                                                    |> (given (rightPtr) -> buildCall(builder)(rcReleaseType)(rcReleaseFn)([rightPtr])(1u32)(""))
                                                                                                    |> (given (_) -> buildCall(builder)(freeType)(freeFn)([headerPtr])(1u32)(""))
                                                                                                    |> (given (_) -> buildRetVoid(builder))
                                                                                                in
                                                                                                    let _ =
                                                                                                        Unit
                                                                                                        |> (given (_) -> positionBuilderAtEnd(builder)(leafBlock))
                                                                                                        |> (given (_) -> buildCall(builder)(freeType)(freeFn)([headerPtr])(1u32)(""))
                                                                                                        |> (given (_) -> buildRetVoid(builder))
                                                                                                    in
                                                                                                        let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                                                        in
                                                                                                            let _ = buildRetVoid(builder)
                                                                                                            in (function, fnType, builder))

// Proves `rcReleaseTree`'s `Node` arm drops BOTH children, in order: two leaf `i32` cells (10 and
// 20) owned by one node. Releasing the node once must cascade into `rcRelease` twice — once per
// owned field — before freeing its own header, reading both leaf values first (`leftValue +
// rightValue = 30`) so the return value proves neither leaf's storage was disturbed by the drop
// sequencing.
let buildRcTreeReleaseModule name context =
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
                            let treeType = structType(context)([i32, ptr, ptr])(3u32)(false)
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
                                                                match defineRcReleaseTreeFunction(module_)(context)(Some(releaseBuilder))(headerType)(i8)(ptr)(treeType)(
                                                                    freeType
                                                                )(freeFn)(rcReleaseType)(rcReleaseFunction) with
                                                                    | (rcReleaseTreeFunction, rcReleaseTreeType, releaseTreeBuilder) ->
                                                                        match beginFunction(module_)(context)(Some(releaseTreeBuilder))("rcTreeLifecycle")(i32)([])(0u32) with
                                                                            | (_, _, builder) ->
                                                                                let leftLeaf =
                                                                                    buildCall(builder)(rcAllocType)(rcAllocFunction)([constInt(i64)(4u64)(false)])(1u32)(
                                                                                        "leftLeaf"
                                                                                    )
                                                                                in
                                                                                    let _ =
                                                                                        buildStore(builder)(constInt(i32)(10u64)(false))(leftLeaf)
                                                                                    in
                                                                                        let rightLeaf =
                                                                                            buildCall(builder)(rcAllocType)(rcAllocFunction)([constInt(i64)(4u64)(false)])(1u32)(
                                                                                                "rightLeaf"
                                                                                            )
                                                                                        in
                                                                                            let _ =
                                                                                                buildStore(builder)(constInt(i32)(20u64)(false))(rightLeaf)
                                                                                            in
                                                                                                let node =
                                                                                                    buildCall(builder)(rcAllocType)(rcAllocFunction)([constInt(i64)(24u64)(false)])(
                                                                                                        1u32
                                                                                                    )("node")
                                                                                                in
                                                                                                    let zeroIndex = constInt(i32)(0u64)(false)
                                                                                                    in
                                                                                                        let tagPtr = buildGEP(builder)(treeType)(node)([zeroIndex, zeroIndex])(2u32)("tagPtr")
                                                                                                        in
                                                                                                            let leftPtrField =
                                                                                                                buildGEP(builder)(treeType)(node)([zeroIndex, constInt(i32)(1u64)(false)])(
                                                                                                                    2u32
                                                                                                                )("leftPtrField")
                                                                                                            in
                                                                                                                let rightPtrField =
                                                                                                                    buildGEP(builder)(treeType)(node)([zeroIndex, constInt(i32)(2u64)(false)])(
                                                                                                                        2u32
                                                                                                                    )("rightPtrField")
                                                                                                                in
                                                                                                                    let _ =
                                                                                                                        Unit
                                                                                                                        |> (given (_) ->
                                                                                                                            buildStore(builder)(constInt(i32)(1u64)(false))(tagPtr))
                                                                                                                        |> (given (_) -> buildStore(builder)(leftLeaf)(leftPtrField))
                                                                                                                        |> (given (_) -> buildStore(builder)(rightLeaf)(rightPtrField))
                                                                                                                    in
                                                                                                                        let leftValue = buildLoad(builder)(i32)(leftLeaf)("leftValue")
                                                                                                                        in
                                                                                                                            let rightValue = buildLoad(builder)(i32)(rightLeaf)("rightValue")
                                                                                                                            in
                                                                                                                                let sum = buildAdd(builder)(leftValue)(rightValue)("sum")
                                                                                                                                in
                                                                                                                                    let _ =
                                                                                                                                        buildCall(builder)(rcReleaseTreeType)(
                                                                                                                                            rcReleaseTreeFunction
                                                                                                                                        )([node])(1u32)("")
                                                                                                                                    in
                                                                                                                                        let _ = buildRet(builder)(sum)
                                                                                                                                        in (module_, builder))

// The Perceus reuse contract's drop half (architecture.md's "Drop specialization and reuse"): "if
// the cell is unique, release/transfer its old fields and return the cell address as the token; if
// shared, decrement it and return null." This proves the token itself, not the field
// release/transfer step (which composes with the drop functions already built above — a real
// caller would run those first, then decide whether to keep the freed memory as a token instead
// of calling `free`). `rcDropReuseToken` decrements and, on reaching zero, returns the ORIGINAL
// header pointer WITHOUT freeing it — the memory stays allocated, ready for reuse; on a shared
// cell it just decrements and returns `constNull`.
let defineRcDropReuseTokenFunction module_ context existingBuilder headerType i8 ptr =
    (let i64 = int64Type(context)
    in
        match beginFunction(module_)(context)(existingBuilder)("rcDropReuseToken")(ptr)([ptr])(1u32) with
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
                                                    let uniqueBlock = appendBasicBlock(context)(function)("unique")
                                                    in
                                                        let sharedBlock = appendBasicBlock(context)(function)("shared")
                                                        in
                                                            let _ = buildCondBr(builder)(isZero)(uniqueBlock)(sharedBlock)
                                                            in
                                                                let _ =
                                                                    Unit
                                                                    |> (given (_) -> positionBuilderAtEnd(builder)(uniqueBlock))
                                                                    |> (given (_) -> buildRet(builder)(headerPtr))
                                                                in
                                                                    let _ =
                                                                        Unit
                                                                        |> (given (_) -> positionBuilderAtEnd(builder)(sharedBlock))
                                                                        |> (given (_) ->
                                                                            ptr
                                                                            |> constNull
                                                                            |> buildRet(builder))
                                                                    in (function, fnType, builder))

// The reuse contract's allocate half: "`AllocReusing` overwrites a compatible non-null cell; a
// null token allocates a fresh RC cell." A non-null token is exactly `rcDropReuseToken`'s unique
// path — the original header pointer, memory intact — so the reuse branch just re-initializes the
// header in place (count back to `1`, the new size) and returns the same payload pointer as
// before; the null branch calls the already-defined generic `rcAlloc` for a genuinely fresh
// allocation. Size compatibility between the old and new cell is deliberately assumed here (both
// callers below request the same size) rather than checked — the check itself is a separate,
// bigger design question this slice isn't answering.
let defineRcAllocReusingFunction module_ context existingBuilder headerType i8 ptr rcAllocType rcAllocFn =
    (let i64 = int64Type(context)
    in
        match beginFunction(module_)(context)(existingBuilder)("rcAllocReusing")(ptr)([ptr, i64])(2u32) with
            | (function, fnType, builder) ->
                let token = getParam(function)(0u32)
                in
                    let payloadSize = getParam(function)(1u32)
                    in
                        let isNull =
                            buildICmp(builder)(intPredicateEq)(token)(constNull(ptr))("isNull")
                        in
                            let freshBlock = appendBasicBlock(context)(function)("fresh")
                            in
                                let reuseBlock = appendBasicBlock(context)(function)("reuse")
                                in
                                    let _ = buildCondBr(builder)(isNull)(freshBlock)(reuseBlock)
                                    in
                                        let _ =
                                            Unit
                                            |> (given (_) -> positionBuilderAtEnd(builder)(freshBlock))
                                            |> (given (_) -> buildCall(builder)(rcAllocType)(rcAllocFn)([payloadSize])(1u32)("fresh"))
                                            |> (given (fresh) -> buildRet(builder)(fresh))
                                        in
                                            let _ = positionBuilderAtEnd(builder)(reuseBlock)
                                            in
                                                let zeroIndex =
                                                    constInt(int32Type(context))(0u64)(false)
                                                in
                                                    let countFieldPtr = buildGEP(builder)(headerType)(token)([zeroIndex, zeroIndex])(2u32)("countFieldPtr")
                                                    in
                                                        let sizeFieldPtr =
                                                            buildGEP(builder)(headerType)(token)([zeroIndex, constInt(int32Type(context))(1u64)(false)])(2u32)("sizeFieldPtr")
                                                        in
                                                            let sixteen = constInt(i64)(16u64)(false)
                                                            in
                                                                let _ =
                                                                    Unit
                                                                    |> (given (_) ->
                                                                        buildStore(builder)(constInt(i64)(1u64)(false))(countFieldPtr))
                                                                    |> (given (_) -> buildStore(builder)(payloadSize)(sizeFieldPtr))
                                                                in
                                                                    let reusedPayloadPtr = buildGEP(builder)(i8)(token)([sixteen])(1u32)("reusedPayloadPtr")
                                                                    in
                                                                        let _ = buildRet(builder)(reusedPayloadPtr)
                                                                        in (function, fnType, builder))

// Proves the striking, easily-checked property reuse exists for: dropping `old` (its only
// reference) and immediately `rcAllocReusing`-ing a same-size cell must NOT call `malloc` again —
// the freed memory is handed straight back. `rcReuseLifecycle() { old = rcAlloc(4); *old = 100;
// token = rcDropReuseToken(old); new = rcAllocReusing(token, 4); *new = 200; v = *new;
// rcRelease(new); ret i32 v }`. The null-token/fresh-allocation branch is proven at the function
// level (both branches of `rcAllocReusing` are real, reachable, disassembled code, per the
// established discipline in `buildRcOptionReleaseModule`'s own comment) rather than exercised
// again at a second call site here.
let buildRcReuseModule name context =
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
                                                            match defineRcDropReuseTokenFunction(module_)(context)(Some(releaseBuilder))(headerType)(i8)(ptr) with
                                                                | (rcDropReuseTokenFunction, rcDropReuseTokenType, dropBuilder) ->
                                                                    match defineRcAllocReusingFunction(module_)(context)(Some(dropBuilder))(headerType)(i8)(ptr)(rcAllocType)(
                                                                        rcAllocFunction
                                                                    ) with
                                                                        | (rcAllocReusingFunction, rcAllocReusingType, reusingBuilder) ->
                                                                            match beginFunction(module_)(context)(Some(reusingBuilder))("rcReuseLifecycle")(i32)([])(0u32) with
                                                                                | (_, _, builder) ->
                                                                                    let old = buildCall(builder)(rcAllocType)(rcAllocFunction)([constInt(i64)(4u64)(false)])(1u32)("old")
                                                                                    in
                                                                                        let _ =
                                                                                            buildStore(builder)(constInt(i32)(100u64)(false))(old)
                                                                                        in
                                                                                            let token =
                                                                                                buildCall(builder)(rcDropReuseTokenType)(rcDropReuseTokenFunction)([old])(1u32)(
                                                                                                    "token"
                                                                                                )
                                                                                            in
                                                                                                let new_ =
                                                                                                    buildCall(builder)(rcAllocReusingType)(rcAllocReusingFunction)(
                                                                                                        [token, constInt(i64)(4u64)(false)]
                                                                                                    )(2u32)("new")
                                                                                                in
                                                                                                    let _ =
                                                                                                        buildStore(builder)(constInt(i32)(200u64)(false))(new_)
                                                                                                    in
                                                                                                        let loadedNew = buildLoad(builder)(i32)(new_)("loadedNew")
                                                                                                        in
                                                                                                            let _ = buildCall(builder)(rcReleaseType)(rcReleaseFunction)([new_])(1u32)("")
                                                                                                            in
                                                                                                                let _ = buildRet(builder)(loadedNew)
                                                                                                                in (module_, builder))

// Drops a closure's ONE owned capture without ever touching its code-pointer field — a genuinely
// different drop shape than every earlier one in this arc: not tag-gated
// (`defineRcReleaseOptionFunction`), not "every field is owned"
// (`defineRcReleaseNodeFunction`/`defineRcReleaseTreeFunction`), but a FIXED mix of one unowned
// field (the code address, never RC-managed) and one owned field (the capture). `rcClosureType` is
// `{ptr code, ptr capturedRc}`; on the last reference this drops only field index `1` via the
// already-defined generic `rcRelease` before freeing its own header.
let defineRcReleaseClosureFunction module_ context existingBuilder headerType i8 ptr rcClosureType freeType freeFn rcReleaseType rcReleaseFn =
    (let i64 = int64Type(context)
    in
        let i32 = int32Type(context)
        in
            match beginFunction(module_)(context)(existingBuilder)("rcReleaseClosure")(voidType(context))([ptr])(1u32) with
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
                                                                    let _ =
                                                                        Unit
                                                                        |> (given (_) -> positionBuilderAtEnd(builder)(dropBlock))
                                                                        |> (given (_) ->
                                                                            buildGEP(builder)(rcClosureType)(value)([zeroIndex, constInt(i32)(1u64)(false)])(2u32)(
                                                                                "capturedFieldPtr"
                                                                            ))
                                                                        |> (given (capturedFieldPtr) -> buildLoad(builder)(ptr)(capturedFieldPtr)("capturedPtr"))
                                                                        |> (given (capturedPtr) -> buildCall(builder)(rcReleaseType)(rcReleaseFn)([capturedPtr])(1u32)(""))
                                                                        |> (given (_) -> buildCall(builder)(freeType)(freeFn)([headerPtr])(1u32)(""))
                                                                        |> (given (_) -> buildRetVoid(builder))
                                                                    in
                                                                        let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                        in
                                                                            let _ = buildRetVoid(builder)
                                                                            in (function, fnType, builder))

// Calls an RC-managed closure: loads the code pointer (field `0`, unowned) and the captured RC
// pointer (field `1`, owned), dereferences the capture to its `i32` value, and calls the loaded
// function with the captured value plus the argument — the same indirect-call shape as
// `buildCallClosureFunction`, just reading its capture through an RC pointer instead of an
// embedded scalar.
let defineCallRcClosureFunction module_ context existingBuilder rcClosureType envFnType =
    (let i32 = int32Type(context)
    in
        let ptr = pointerType(context)(0u32)
        in
            match beginFunction(module_)(context)(existingBuilder)("callRcClosure")(i32)([ptr, i32])(2u32) with
                | (function, fnType, builder) ->
                    let closurePtr = getParam(function)(0u32)
                    in
                        let xArg = getParam(function)(1u32)
                        in
                            let zeroIndex = constInt(i32)(0u64)(false)
                            in
                                let codePtrFieldPtr = buildGEP(builder)(rcClosureType)(closurePtr)([zeroIndex, zeroIndex])(2u32)("codePtrFieldPtr")
                                in
                                    let capturedFieldPtr = buildGEP(builder)(rcClosureType)(closurePtr)([zeroIndex, constInt(i32)(1u64)(false)])(2u32)("capturedFieldPtr")
                                    in
                                        let codePtr = buildLoad(builder)(ptr)(codePtrFieldPtr)("codePtr")
                                        in
                                            let capturedPtr = buildLoad(builder)(ptr)(capturedFieldPtr)("capturedPtr")
                                            in
                                                let capturedValue = buildLoad(builder)(i32)(capturedPtr)("capturedValue")
                                                in
                                                    let result = buildCall(builder)(envFnType)(codePtr)([capturedValue, xArg])(2u32)("result")
                                                    in
                                                        let _ = buildRet(builder)(result)
                                                        in (function, fnType, builder))

// The composition every closure and every RC test in this arc has been building toward: a closure
// whose capture is itself an RC-managed value, retained when captured and released when the
// closure itself is released — proving the two mechanisms actually compose, not just coexist.
// `rcClosureLifecycle() { leaf = rcAlloc(4); *leaf = 50; rcRetain(leaf); closure = rcAlloc(16);
// closure.code = addEnv; closure.capturedRc = leaf; r = callRcClosure(closure, 5);
// rcReleaseClosure(closure); v = *leaf; rcRelease(leaf); ret i32 (r + v) }` — the retain before
// capture keeps `leaf` alive independently of the closure's own reference, so releasing the
// closure (which drops its own reference) must NOT free `leaf`: `*leaf` is still readable
// afterward, and only the final, separate `rcRelease(leaf)` actually frees it.
let buildRcClosureModule name context =
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
                            let rcClosureType = structType(context)([ptr, ptr])(2u32)(false)
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
                                                                        match defineAddEnvFunction(module_)(context)(Some(releaseBuilder)) with
                                                                            | (addEnvFunction, envFnType, addEnvBuilder) ->
                                                                                match defineRcReleaseClosureFunction(module_)(context)(Some(addEnvBuilder))(headerType)(i8)(ptr)(
                                                                                    rcClosureType
                                                                                )(freeType)(freeFn)(rcReleaseType)(rcReleaseFunction) with
                                                                                    | (rcReleaseClosureFunction, rcReleaseClosureType, releaseClosureBuilder) ->
                                                                                        match defineCallRcClosureFunction(module_)(context)(Some(releaseClosureBuilder))(
                                                                                            rcClosureType
                                                                                        )(envFnType) with
                                                                                            | (callRcClosureFunction, callRcClosureType, callBuilder) ->
                                                                                                match beginFunction(module_)(context)(Some(callBuilder))("rcClosureLifecycle")(i32)(
                                                                                                    []
                                                                                                )(0u32) with
                                                                                                    | (_, _, builder) ->
                                                                                                        let leaf =
                                                                                                            buildCall(builder)(rcAllocType)(rcAllocFunction)(
                                                                                                                [constInt(i64)(4u64)(false)]
                                                                                                            )(1u32)("leaf")
                                                                                                        in
                                                                                                            let _ =
                                                                                                                buildStore(builder)(constInt(i32)(50u64)(false))(leaf)
                                                                                                            in
                                                                                                                let retainedLeaf =
                                                                                                                    buildCall(builder)(rcRetainType)(rcRetainFunction)([leaf])(1u32)(
                                                                                                                        "retainedLeaf"
                                                                                                                    )
                                                                                                                in
                                                                                                                    let closure =
                                                                                                                        buildCall(builder)(rcAllocType)(rcAllocFunction)(
                                                                                                                            [constInt(i64)(16u64)(false)]
                                                                                                                        )(1u32)("closure")
                                                                                                                    in
                                                                                                                        let zeroIndex = constInt(i32)(0u64)(false)
                                                                                                                        in
                                                                                                                            let codeFieldPtr =
                                                                                                                                buildGEP(builder)(rcClosureType)(closure)(
                                                                                                                                    [zeroIndex, zeroIndex]
                                                                                                                                )(2u32)("codeFieldPtr")
                                                                                                                            in
                                                                                                                                let capturedFieldPtr =
                                                                                                                                    buildGEP(builder)(rcClosureType)(closure)(
                                                                                                                                        [zeroIndex, constInt(i32)(1u64)(false)]
                                                                                                                                    )(2u32)("capturedFieldPtr")
                                                                                                                                in
                                                                                                                                    let _ =
                                                                                                                                        Unit
                                                                                                                                        |> (given (_) ->
                                                                                                                                            buildStore(builder)(addEnvFunction)(
                                                                                                                                                codeFieldPtr
                                                                                                                                            ))
                                                                                                                                        |> (given (_) ->
                                                                                                                                            buildStore(builder)(retainedLeaf)(
                                                                                                                                                capturedFieldPtr
                                                                                                                                            ))
                                                                                                                                    in
                                                                                                                                        let result =
                                                                                                                                            buildCall(builder)(callRcClosureType)(
                                                                                                                                                callRcClosureFunction
                                                                                                                                            )([closure, constInt(i32)(5u64)(false)])(
                                                                                                                                                2u32
                                                                                                                                            )("result")
                                                                                                                                        in
                                                                                                                                            let _ =
                                                                                                                                                buildCall(builder)(
                                                                                                                                                    rcReleaseClosureType
                                                                                                                                                )(rcReleaseClosureFunction)([closure])(
                                                                                                                                                    1u32
                                                                                                                                                )("")
                                                                                                                                            in
                                                                                                                                                let finalLeafValue =
                                                                                                                                                    buildLoad(builder)(i32)(leaf)(
                                                                                                                                                        "finalLeafValue"
                                                                                                                                                    )
                                                                                                                                                in
                                                                                                                                                    let _ =
                                                                                                                                                        buildCall(builder)(
                                                                                                                                                            rcReleaseType
                                                                                                                                                        )(rcReleaseFunction)([leaf])(
                                                                                                                                                            1u32
                                                                                                                                                        )("")
                                                                                                                                                    in
                                                                                                                                                        let combined =
                                                                                                                                                            buildAdd(builder)(result)(
                                                                                                                                                                finalLeafValue
                                                                                                                                                            )("combined")
                                                                                                                                                        in
                                                                                                                                                            let _ =
                                                                                                                                                                buildRet(builder)(
                                                                                                                                                                    combined
                                                                                                                                                                )
                                                                                                                                                            in (module_, builder))

// A genuine 3-arm ADT dispatched through a real LLVM `switch`, not chained `buildICmp`/
// `buildCondBr` pairs — architecture.md's "one tag switch per match" for real, not simulated.
// `triType` is `{i32 tag, ptr fieldA, ptr fieldB}`: tag `0` (`Leaf`) owns neither field, tag `1`
// (`OneChild`) owns only `fieldA`, tag `2` (`TwoChildren`, the default/unrecognized-tag arm too,
// matching a real switch's fallthrough default) owns both — three genuinely different drop
// behaviors selected by one dispatch instruction, composing every RC drop shape in this arc
// (zero fields, one field, two fields) into a single ADT.
let defineRcReleaseTriFunction module_ context existingBuilder headerType i8 ptr triType freeType freeFn rcReleaseType rcReleaseFn =
    (let i64 = int64Type(context)
    in
        let i32 = int32Type(context)
        in
            match beginFunction(module_)(context)(existingBuilder)("rcReleaseTri")(voidType(context))([ptr])(1u32) with
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
                                                                        let tagPtr = buildGEP(builder)(triType)(value)([zeroIndex, zeroIndex])(2u32)("tagPtr")
                                                                        in
                                                                            let tag = buildLoad(builder)(i32)(tagPtr)("tag")
                                                                            in
                                                                                let leafBlock = appendBasicBlock(context)(function)("leaf")
                                                                                in
                                                                                    let oneChildBlock = appendBasicBlock(context)(function)("oneChild")
                                                                                    in
                                                                                        let twoChildrenBlock = appendBasicBlock(context)(function)("twoChildren")
                                                                                        in
                                                                                            let switchInst = buildSwitch(builder)(tag)(twoChildrenBlock)(2u32)
                                                                                            in
                                                                                                let _ =
                                                                                                    addCase(switchInst)(constInt(i32)(0u64)(false))(leafBlock)
                                                                                                in
                                                                                                    let _ =
                                                                                                        addCase(switchInst)(constInt(i32)(1u64)(false))(oneChildBlock)
                                                                                                    in
                                                                                                        let _ =
                                                                                                            Unit
                                                                                                            |> (given (_) -> positionBuilderAtEnd(builder)(leafBlock))
                                                                                                            |> (given (_) -> buildCall(builder)(freeType)(freeFn)([headerPtr])(1u32)(""))
                                                                                                            |> (given (_) -> buildRetVoid(builder))
                                                                                                        in
                                                                                                            let _ =
                                                                                                                Unit
                                                                                                                |> (given (_) -> positionBuilderAtEnd(builder)(oneChildBlock))
                                                                                                                |> (given (_) ->
                                                                                                                    buildGEP(builder)(triType)(value)([zeroIndex, constInt(i32)(1u64)(false)])(
                                                                                                                        2u32
                                                                                                                    )("fieldAFieldPtr"))
                                                                                                                |> (given (fieldAFieldPtr) -> buildLoad(builder)(ptr)(fieldAFieldPtr)("fieldA"))
                                                                                                                |> (given (fieldA) -> buildCall(builder)(rcReleaseType)(rcReleaseFn)([fieldA])(1u32)(""))
                                                                                                                |> (given (_) -> buildCall(builder)(freeType)(freeFn)([headerPtr])(1u32)(""))
                                                                                                                |> (given (_) -> buildRetVoid(builder))
                                                                                                            in
                                                                                                                let _ = positionBuilderAtEnd(builder)(twoChildrenBlock)
                                                                                                                in
                                                                                                                    let fieldAFieldPtr =
                                                                                                                        buildGEP(builder)(triType)(value)([zeroIndex, constInt(i32)(1u64)(false)])(
                                                                                                                            2u32
                                                                                                                        )("fieldAFieldPtr")
                                                                                                                    in
                                                                                                                        let fieldBFieldPtr =
                                                                                                                            buildGEP(builder)(triType)(value)([zeroIndex, constInt(i32)(2u64)(false)])(
                                                                                                                                2u32
                                                                                                                            )("fieldBFieldPtr")
                                                                                                                        in
                                                                                                                            let fieldA = buildLoad(builder)(ptr)(fieldAFieldPtr)("fieldA")
                                                                                                                            in
                                                                                                                                let fieldB = buildLoad(builder)(ptr)(fieldBFieldPtr)("fieldB")
                                                                                                                                in
                                                                                                                                    let _ =
                                                                                                                                        Unit
                                                                                                                                        |> (given (_) ->
                                                                                                                                            buildCall(builder)(rcReleaseType)(rcReleaseFn)([fieldA])(
                                                                                                                                                1u32
                                                                                                                                            )(""))
                                                                                                                                        |> (given (_) ->
                                                                                                                                            buildCall(builder)(rcReleaseType)(rcReleaseFn)([fieldB])(
                                                                                                                                                1u32
                                                                                                                                            )(""))
                                                                                                                                        |> (given (_) -> buildCall(builder)(freeType)(freeFn)([headerPtr])(1u32)(""))
                                                                                                                                        |> (given (_) -> buildRetVoid(builder))
                                                                                                                                    in
                                                                                                                                        let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                                                                                        in
                                                                                                                                            let _ = buildRetVoid(builder)
                                                                                                                                            in (function, fnType, builder))

// Proves the `TwoChildren` arm (tag `2`) is reached by the real `switch` above (as the DEFAULT
// case, matching a genuine switch's fallthrough behavior for any tag with no explicit case) and
// drops both owned fields: two leaf `i32` cells (10 and 20) owned by one tri value. Releasing it
// once must cascade into `rcRelease` twice — the `Leaf` (tag `0`) and `OneChild` (tag `1`) arms
// are proven at the function level, per this arc's established discipline, not exercised again at
// a second call site here.
let buildRcTriReleaseModule name context =
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
                            let triType = structType(context)([i32, ptr, ptr])(3u32)(false)
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
                                                                match defineRcReleaseTriFunction(module_)(context)(Some(releaseBuilder))(headerType)(i8)(ptr)(triType)(freeType)(
                                                                    freeFn
                                                                )(rcReleaseType)(rcReleaseFunction) with
                                                                    | (rcReleaseTriFunction, rcReleaseTriType, releaseTriBuilder) ->
                                                                        match beginFunction(module_)(context)(Some(releaseTriBuilder))("rcTriLifecycle")(i32)([])(0u32) with
                                                                            | (_, _, builder) ->
                                                                                let leftLeaf =
                                                                                    buildCall(builder)(rcAllocType)(rcAllocFunction)([constInt(i64)(4u64)(false)])(1u32)(
                                                                                        "leftLeaf"
                                                                                    )
                                                                                in
                                                                                    let _ =
                                                                                        buildStore(builder)(constInt(i32)(10u64)(false))(leftLeaf)
                                                                                    in
                                                                                        let rightLeaf =
                                                                                            buildCall(builder)(rcAllocType)(rcAllocFunction)([constInt(i64)(4u64)(false)])(1u32)(
                                                                                                "rightLeaf"
                                                                                            )
                                                                                        in
                                                                                            let _ =
                                                                                                buildStore(builder)(constInt(i32)(20u64)(false))(rightLeaf)
                                                                                            in
                                                                                                let tri =
                                                                                                    buildCall(builder)(rcAllocType)(rcAllocFunction)([constInt(i64)(24u64)(false)])(
                                                                                                        1u32
                                                                                                    )("tri")
                                                                                                in
                                                                                                    let zeroIndex = constInt(i32)(0u64)(false)
                                                                                                    in
                                                                                                        let tagPtr = buildGEP(builder)(triType)(tri)([zeroIndex, zeroIndex])(2u32)("tagPtr")
                                                                                                        in
                                                                                                            let fieldAFieldPtr =
                                                                                                                buildGEP(builder)(triType)(tri)([zeroIndex, constInt(i32)(1u64)(false)])(2u32)(
                                                                                                                    "fieldAFieldPtr"
                                                                                                                )
                                                                                                            in
                                                                                                                let fieldBFieldPtr =
                                                                                                                    buildGEP(builder)(triType)(tri)([zeroIndex, constInt(i32)(2u64)(false)])(2u32)(
                                                                                                                        "fieldBFieldPtr"
                                                                                                                    )
                                                                                                                in
                                                                                                                    let _ =
                                                                                                                        Unit
                                                                                                                        |> (given (_) ->
                                                                                                                            buildStore(builder)(constInt(i32)(2u64)(false))(tagPtr))
                                                                                                                        |> (given (_) -> buildStore(builder)(leftLeaf)(fieldAFieldPtr))
                                                                                                                        |> (given (_) -> buildStore(builder)(rightLeaf)(fieldBFieldPtr))
                                                                                                                    in
                                                                                                                        let leftValue = buildLoad(builder)(i32)(leftLeaf)("leftValue")
                                                                                                                        in
                                                                                                                            let rightValue = buildLoad(builder)(i32)(rightLeaf)("rightValue")
                                                                                                                            in
                                                                                                                                let sum = buildAdd(builder)(leftValue)(rightValue)("sum")
                                                                                                                                in
                                                                                                                                    let _ =
                                                                                                                                        buildCall(builder)(rcReleaseTriType)(
                                                                                                                                            rcReleaseTriFunction
                                                                                                                                        )([tri])(1u32)("")
                                                                                                                                    in
                                                                                                                                        let _ = buildRet(builder)(sum)
                                                                                                                                        in (module_, builder))

// The first genuinely IR-DRIVEN tests in this arc: every module builder above hand-encodes the
// LLVM calls a human decided represent some IR shape. This one instead runs real source through
// the REAL self-hosted `AshesCompiler.Frontend.Parser.parseProgram` +
// `AshesCompiler.Semantics.CoreLowering.lowerCoreProgramWithSource` pipeline (the same one
// `selfhost/tests/ir-program-parity` already trusts against stage 0's own IR text output for both
// fixtures used below) and hands the resulting REAL `IrFunction` to
// `AshesCompiler.Backend.IrCodegen.codegenEntryFunction`. If the shape `IrProgram`/`IrFunction`
// actually produce didn't match what a codegen walker expects, this is where it would show up —
// no earlier test in this arc could ever catch that, since they all supplied the IR shape by hand.
let lowerRealSource source name =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } ->
            match lowerCoreProgramWithSource(name + ".ash")(source)(program) with
                | CoreLoweringResult { program = Some(lowered), error = None } -> lowered
                | CoreLoweringResult { error = Some(error) } -> test.fail("lowering failed: " + Ashes.Trait.Show.show(error))
                | _ -> test.fail("lowering produced no program")
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let codegenRealSource source name context =
    name
    |> lowerRealSource(source)
    |> codegenProgram(name)(context)

// The same pipeline with `IrOptimizer` run in between, as the real compile pipeline does —
// compile-time evaluation disabled so a program whose result is a constant is not folded away to
// a bare `PrintInt` of a literal, leaving the optimizer's structural rewrites (known-closure
// devirtualization to `CallKnown`, closure-environment scalarization) as the thing under test.
let optimizerOptionsWithoutCompileTimeEval =
    IrOptimizerOptions(
        enableCompileTimeEval = false,
        enableInlining = true,
        enableDeadCodeElision = true,
        enableIdentityReduction = true
    )

let codegenOptimizedRealSource source name context =
    name
    |> lowerRealSource(source)
    |> optimizeIrProgramWithOptions(optimizerOptionsWithoutCompileTimeEval)
    |> codegenProgram(name)(context)

let isAshSourceName name = Ashes.Text.length(name) > 4 && Ashes.Text.substring(name)(Ashes.Text.length(name) - 4)(4) == ".ash"

// Every `<Module.Path>.ash` under the shipped standard-library root (`lib/Ashes` in a checkout,
// whose file names encode the module path under the implicit `Ashes.` prefix), as the in-memory
// texts `stitchWithShippedModules` resolves `import Ashes.*` against. Read once per run; only
// the modules a program actually reaches are ever parsed.
let recursive readShippedModules root names =
    match names with
        | [] -> []
        | name :: rest ->
            if isAshSourceName(name) == false
            then readShippedModules(root)(rest)
            else
                let path = root + "/" + name
                in
                    match Ashes.IO.File.readText(path) with
                        | Error(message) -> test.fail("could not read shipped module " + path + ": " + message)
                        | Ok(source) ->
                            ShippedModuleText(
                                moduleName = "Ashes." + Ashes.Text.substring(name)(0)(Ashes.Text.length(name) - 4),
                                sourcePath = path,
                                source = source
                            ) :: readShippedModules(root)(rest)

let loadShippedModules root =
    match Ashes.IO.Directory.entries(root) with
        | Error(message) -> test.fail("could not list shipped modules under " + root + ": " + message)
        | Ok(names) -> readShippedModules(root)(names)

// The same pipeline as `codegenRealSource`, with the program's `import Ashes.*` header resolved
// against the shipped modules and stitched in first — the path any program using the standard
// library takes.
let codegenShippedSource shipped source name context =
    match stitchWithShippedModules(name)(name + ".ash")(source)(shipped) with
        | Error(error) -> test.fail("shipped-module stitching failed: " + Ashes.Trait.Show.show(error))
        | Ok(StitchedSyntaxProject { program = program }) ->
            match lowerCoreProgramWithSource(name + ".ash")(source)(program) with
                | CoreLoweringResult { program = Some(lowered), error = None } -> codegenProgram(name)(context)(lowered)
                | CoreLoweringResult { error = Some(error) } -> test.fail("lowering failed: " + Ashes.Trait.Show.show(error))
                | _ -> test.fail("lowering produced no program")

// `simple_arith`'s own fixture source: `LoadConstInt` x3, `MulInt`, `AddInt`, `Return` — no
// top-level `let`, so no arena bracketing at all.
let buildRealIrArithmeticModule name context = codegenRealSource("1 + 2 * 3")(name)(context)

// `let_bindings`'s own fixture source: adds `StoreLocal`/`LoadLocal` AND arena bracketing (every
// top-level `let` scope gets one, even this provably-scalar case — see the fixture's own lowered
// IR) around each of the two `let`s. `10 * (10 + 5) = 150`.
let buildRealIrLetBindingsModule name context = codegenRealSource("let x = 10\nlet y = x + 5\nx * y")(name)(context)

// A plain `if`/`then`/`else` — not yet one of `ir-program-parity`'s own trusted fixtures (`if`
// doesn't need the constructor-layout/closure machinery that suite's excluded fixtures do, only
// real self-hosted lowering to succeed for this shape, confirmed directly). Its lowered IR is
// exactly the same no-`phi` slot pattern this arc's earlier hand-built tests already used
// (`buildMaxModule` et al.): `CmpIntGt`, `JumpIfFalse`, a `StoreLocal` into a shared result slot in
// each arm, `Jump`/`Label` joining them, and a final `LoadLocal`. `1 > 0` is true, so this always
// evaluates to `42`.
let buildRealIrConditionalModule name context = codegenRealSource("if 1 > 0 then 42 else 99")(name)(context)

// Exercises `PrintInt` and the zero-field `AllocAdt` case together — the first genuinely
// user-observable real-IR module in this arc: `codegenEntryFunction` walks IR containing a real
// builtin call, not just arithmetic/locals/control-flow. `42 - 84 = -42`, deliberately negative to
// exercise `emitPrintInt`'s sign-handling path, not just its common case. Uses the plain
// `codegenRealSource` (no hand-built layouts): `Ashes.IO.print` is intrinsic now
// (`CoreLowering.ash`'s `standardBuiltinLayouts`/`standardConstructorLayouts`, matching
// language.md's "qualified access (no import required)"), proving the real language semantics —
// no `import`, no caller-supplied glue — not just a case this test file happened to wire up.
let buildRealIrPrintModule name context = codegenRealSource("Ashes.IO.print(42 - 84)")(name)(context)

// Exercises the new `&&`/`||` short-circuit operators through the same real-IR path: each desugars
// to `lowerIf` (see CoreLowering.ash's `ExprLogicalAnd`/`ExprLogicalOr` cases), so this proves that
// desugaring produces the exact same JumpIfFalse/Jump/StoreLocal/LoadLocal shape the codegen already
// handles for a plain `if` — including the new `LoadConstBool`/zero-extend/truncate plumbing this
// needed in `IrCodegen.ash` and `Llvm.ash`, not just that it lowers without error. `true && false ||
// true` is `(true && false) || true = false || true = true`, so the outer `if` always takes its
// `then` arm and prints `1`.
let buildRealIrLogicalOperatorsModule name context = codegenRealSource("Ashes.IO.print(if true && false || true then 1 else 0)")(name)(context)

// The first real string literal this compiler has taken from source to a running executable.
// `CoreBuiltinLowering.ash`'s `printValue` already dispatches `Ashes.IO.print` to `PrintStr` for a
// `SemString`-typed argument (confirmed by reading it — this was not new lowering work, only its
// codegen was missing), so a plain string literal print needed nothing beyond `LoadConstStr` (a
// `.rodata`-shaped global per literal, matching `EmitHeapStringLiteral`'s exact layout) and
// `PrintStr` (write the value's own `[len][bytes]` payload via the raw `write` syscall, then a
// newline) in `IrCodegen`. Prints `hello`.
let buildRealIrStringLiteralModule name context = codegenRealSource("Ashes.IO.print(\"hello\")")(name)(context)

// `Some(42)` allocates via a field-carrying constructor, and `x` is never referenced again, so
// lowering also releases it with a single `RcDrop` immediately. `x` is never read back (no
// `match`/field-read codegen exists), so `Ashes.IO.print(1)` is the only observable output; the
// object's real `call malloc@PLT`/`call free@PLT` pair makes `linkLinuxExecutable` choose its
// dynamic path automatically. Field-store/drop correctness is not observable through real IR until
// reads exist, so it is verified separately by dumping the module's assembly text and checking the
// `malloc`/GEP/store/decrement/`free` byte offsets against `AllocAdt`'s and `RcDrop`'s documented
// layouts.
let buildRealIrSomeConstructorModule name context = codegenRealSource("let x = Some(42)\nAshes.IO.print(1)")(name)(context)

// Combines `buildRealIrSomeConstructorModule`'s heap allocation (`Some(42)`'s `AllocAdt`/
// `SetAdtField`/`RcDrop`, calling real `malloc`/`free`) with `buildRealIrStringLiteralModule`'s
// string literal (`LoadConstStr`/`PrintStr`, a `.rodata` reference) in one object — the exact
// combination `AshesCompiler.Backend.ElfLinker.linkLinuxExecutable` used to reject outright
// (`"an object with both external-symbol calls (malloc/free) and .rodata references (a string
// literal) is not yet supported together"`). Any program that both allocates a record and prints a
// string literal hits this shape, so it is not a corner case. Prints `hello`.
let buildRealIrAllocAndStringLiteralModule name context = codegenRealSource("let x = Some(42)\nAshes.IO.print(\"hello\")")(name)(context)

// Two DISTINCT string literals in one object, printed by two separate calls — the
// "multi-string-literal objects" gap the `LoadConstStr`/`PrintStr` item once flagged as still
// open. Already worked with no further codegen or linker change needed:
// `buildStringLiteralGlobalsFromIndex` already builds one `.rodata`-shaped global per entry in the
// whole `stringLiterals` list (not just the first), `LoadConstStr` already looks its own global up
// by `label` rather than assuming a single fixed one, and the collected relocations against
// `.rodata`'s SECTION symbol already carry a distinct byte-offset addend per literal, so copying
// the whole section and patching every collected offset independently already generalizes.
// Verified only when actually run: this was checked by direct probe, not inferred from reading the
// code. Prints `hello` then `world`.
let buildRealIrTwoStringLiteralsModule name context = codegenRealSource("let _ = Ashes.IO.print(\"hello\")\nAshes.IO.print(\"world\")")(name)(context)

// A string literal reached through a `let` binding rather than passed directly as `print`'s own
// argument — the other half of the "strings reached through anything other than a direct
// `Ashes.IO.print` argument" gap. This one did NOT already work: `objdump -dr` on the emitted
// object showed a THIRD relocation shape neither prior fix (absolute `R_X86_64_32`/`32S`,
// PC-relative `R_X86_64_PC32`) had ever produced — an 8-byte absolute `R_X86_64_64`
// (`movabs $imm64, reg`, addend `0`, the header's own address with `+16` to the payload computed
// by a separate runtime `add` instead of folded into the addend the way an immediately-used
// literal's load does). Fixed in `AshesCompiler.Backend.ElfLinker`: `DataRelocationPatch` gained
// `dataPatchWidth`, `isRodataRelocationType` accepts type `1` alongside `2`/`10`/`11`, and
// `applyDataPatches` writes the full 64-bit virtual address (`putU64`) rather than truncating to 32
// bits for this one type. Prints `hello`.
let buildRealIrLetBoundStringModule name context = codegenRealSource("let s = \"hello\"\nAshes.IO.print(s)")(name)(context)

// A string literal stored in a user-defined record field and read back through `match` — the
// "stored in a field" half of the same still-open gap. Already worked with no further change:
// a `Str` value shares the exact same 16-byte-RC-header layout as any other runtime-managed value,
// so the `SetAdtField`/`GetAdtField`/`match` codegen already proven for `Int` fields needed nothing
// string-specific. Verified only when actually run. Prints `hello`.
let buildRealIrRecordFieldStringModule name context = codegenRealSource("type Box = | value: Str\nlet b = Box(value = \"hello\")\nmatch b with | Box(value) -> Ashes.IO.print(value)")(name)(context)

// `Ashes.IO.panic` is already an intrinsic builtin (`CoreBuiltinLowering.ash`'s `standardBuiltinLayout`)
// lowering to the `PanicStr` IR instruction (`CorePanic` -> `never(start)(value)(PanicStr)`) — only
// its codegen was missing. Prints `boom` then exits `1`, matching `LlvmCodegenExpressions.cs`'s own
// `EmitPanic` (message via the same helper `print` uses, then `exit(1)` rather than `exit(0)`).
let buildRealIrPanicModule name context = codegenRealSource("Ashes.IO.panic(\"boom\")")(name)(context)

// `==`/`!=` on `Str` (`CoreLowering.ash`'s `emitResolvedCoreEquality` desugars both to
// `CmpStrEq`/`CmpStrNe`) exercises the linker's new `memcmp` dynamic import alongside the
// length-mismatch fast path `emitStringEquals` takes without ever calling it: `"hello" == "hello"`
// (equal, calls `memcmp`), `"hello" != "world"` (same length, `memcmp` returns nonzero), and
// `"ab" != "abc"` (different length, short-circuits before any `memcmp` call) all combined through
// `&&`, so all three code paths must succeed for this to print `1` rather than `0`.
let buildRealIrStringEqualityModule name context = codegenRealSource("Ashes.IO.print(if \"hello\" == \"hello\" && \"hello\" != \"world\" && \"ab\" != \"abc\" then 1 else 0)")(name)(context)

// `+` on two `Str` operands (`CoreLowering.ash`'s `emitResolvedCoreAdd` dispatches `SemString`/
// `SemString` to `emitCoreConcat` -> `ConcatStr`) exercises the new real `malloc`+`memcpy`
// concatenation codegen. Three literals chained left-nested (`("hel" + "lo ") + "world"`) also
// exercises `IrOptimizer.ash`'s `foldConcatStrChains`, which runs as the very last optimization
// pass and folds the whole chain into one `ConcatStrN` before codegen ever sees it — so this proves
// the N-ary path real source actually takes, not just the two-operand `ConcatStr` case. Prints
// `hello world`.
let buildRealIrStringConcatModule name context = codegenRealSource("Ashes.IO.print(\"hel\" + \"lo \" + \"world\")")(name)(context)

// `GetAdtField`/`GetAdtTag` read what `AllocAdt`/`SetAdtField` already write, but no real source
// reaches them yet: extracting an ADT field requires `match`, and a real `match` on `Maybe`/`Result`
// (the only constructors `standardConstructorLayouts` registers) also needs a null-representable-
// type check and per-arm RC cleanup that are not implemented — a user-defined record type would
// avoid that, but `CoreLowering.ash` has no lowering path for a top-level `type` declaration at all
// yet. This builds the `IrFunction` by hand instead of through `codegenRealSource`, the one
// exception to this file's usual real-lowering-only rule (necessary here, not a step backward):
// allocate a 2-field RC cell, set both fields, read field 0 and the tag back, add them, and print
// the sum — proving both instructions against every other one they compose with real-execution
// output rather than a hand-simulated shape.
let buildAdtFieldTagReadModule name context =
    (let instructions =
        [
            IrInstruction(instruction = LoadConstInt(0)(3), location = None),
            IrInstruction(instruction = LoadConstInt(1)(4), location = None),
            IrInstruction(instruction = AllocAdt(2)(0)(2)(true), location = None),
            IrInstruction(instruction = SetAdtField(2)(0)(0), location = None),
            IrInstruction(instruction = SetAdtField(2)(1)(1), location = None),
            IrInstruction(instruction = GetAdtField(3)(2)(0), location = None),
            IrInstruction(instruction = GetAdtTag(4)(2), location = None),
            IrInstruction(instruction = AddInt(5)(3)(4), location = None),
            IrInstruction(instruction = PrintInt(5), location = None),
            IrInstruction(instruction = Return(5), location = None)
        ]
    in
        let irFunction =
            IrFunction(
                label = name,
                instructions = instructions,
                localCount = 0,
                tempCount = 6,
                hasEnvAndArgParams = false,
                coroutine = None,
                localNames = [],
                localTypes = [],
                origin = None,
                lifetimesPlaced = false
            )
        in codegenEntryFunction(name)(context)(irFunction)([]))

// Proves `SwitchTag`/`IrSwitchCase` codegen directly, via a hand-built `IrFunction` — the same
// "prove the primitive first" precedent `AllocAdt`/`GetAdtField`/`GetAdtTag` used before any real
// source could drive them. A real `match` compiles down to `SwitchTag` once tag-group dispatch
// actually groups arms together (a plain sequential match never needs it — already proven end to
// end by `buildRealIrMatchSomeModule` below).
//
// A first attempt at this instruction crashed/corrupted memory: `IrCodegen.ash`'s own
// `addSwitchCases` interleaved a `labelBlocks` lookup (`lookupIndexed`) with the `LLVMAddCase` FFI
// call for each case, in a single recursive pass. Direct experiment confirmed a `labelBlocks`
// lookup performed AFTER an `addCase` FFI call reads back wrong data for later entries in the
// same list — reproduced with static string literals, no real lowering involved. The actual fix
// (`resolveSwitchCases`/`addResolvedSwitchCases` in `IrCodegen.ash`) resolves every case's block
// FIRST, in a pass with no FFI calls at all, THEN calls `addCase` for each already-resolved pair —
// no lookup ever follows an FFI call. Dispatches a tag value of `1` against three explicit cases
// plus a default; only the `1` arm's `PrintInt` should run.
let buildSwitchTagModule name context =
    (let instructions =
        [
            IrInstruction(instruction = LoadConstInt(0)(1), location = None),
            IrInstruction(
                instruction = SwitchTag(0)(
                    [
                        IrSwitchCase(tag = 0, label = "case_zero"),
                        IrSwitchCase(tag = 1, label = "case_one"),
                        IrSwitchCase(tag = 2, label = "case_two")
                    ]
                )("case_default"),
                location = None
            ),
            IrInstruction(instruction = Label("case_zero"), location = None),
            IrInstruction(instruction = LoadConstInt(1)(100), location = None),
            IrInstruction(instruction = PrintInt(1), location = None),
            IrInstruction(instruction = Jump("case_end"), location = None),
            IrInstruction(instruction = Label("case_one"), location = None),
            IrInstruction(instruction = LoadConstInt(2)(200), location = None),
            IrInstruction(instruction = PrintInt(2), location = None),
            IrInstruction(instruction = Jump("case_end"), location = None),
            IrInstruction(instruction = Label("case_two"), location = None),
            IrInstruction(instruction = LoadConstInt(3)(300), location = None),
            IrInstruction(instruction = PrintInt(3), location = None),
            IrInstruction(instruction = Jump("case_end"), location = None),
            IrInstruction(instruction = Label("case_default"), location = None),
            IrInstruction(instruction = LoadConstInt(4)(-1), location = None),
            IrInstruction(instruction = PrintInt(4), location = None),
            IrInstruction(instruction = Jump("case_end"), location = None),
            IrInstruction(instruction = Label("case_end"), location = None),
            IrInstruction(instruction = LoadConstInt(5)(0), location = None),
            IrInstruction(instruction = Return(5), location = None)
        ]
    in
        let irFunction =
            IrFunction(
                label = name,
                instructions = instructions,
                localCount = 0,
                tempCount = 6,
                hasEnvAndArgParams = false,
                coroutine = None,
                localNames = [],
                localTypes = [],
                origin = None,
                lifetimesPlaced = false
            )
        in codegenEntryFunction(name)(context)(irFunction)([]))

// The first real-IR module driven by a USER-DEFINED type declaration, not one of the intrinsic
// `Unit`/`Maybe`/`Result` constructors: `CoreLowering.ash`'s `registerTopLevelTypeDeclaration`
// resolves `Point`'s two `Int` fields and registers a constructor layout for it, after which
// `Point(x=3, y=4)`'s construction and `p.x`'s field access lower through the exact same
// `lowerRecord`/`emitRecordFieldLoad` paths `Some(42)` and pattern-matched field access already
// use — no new lowering or codegen was needed for this, only the registration step. Prints `3`
// (field `x`), proving `GetAdtField` reads back the exact word `SetAdtField` wrote for a
// record-style, named-field constructor, not just the positional single-field one from
// `buildAdtFieldTagReadModule`.
let buildRealIrRecordFieldModule name context = codegenRealSource("type Point =\n    | x: Int\n    | y: Int\n\nlet p = Point(x = 3, y = 4)\nAshes.IO.print(p.x)")(name)(context)

// A multi-constructor, positional (non-record) user-defined type: `registerTopLevelTypeDeclaration`
// tags constructors by their declared position, the same convention `Maybe`'s `None`/`Some` and
// `Result`'s `Ok`/`Error` already use, so `Circle` gets tag `0` and `Square` tag `1` with no
// constructor-specific code. Both bindings are provably dead, so each also gets its own `RcDrop` —
// two distinct constructors of the same type dropping independently in one program. No positional
// field can be read back without `match` (unlike the record's named-field access above), so the
// only observable output is the independent `Ashes.IO.print(1)`; tag/field correctness for this
// shape is verified manually via an IR/assembly dump instead, the same split this session used for
// `buildRealIrSomeConstructorModule`.
let buildRealIrMultiConstructorModule name context = codegenRealSource("type Shape =\n    | Circle(Int)\n    | Square(Int)\n\nlet a = Circle(7)\nlet b = Square(9)\nAshes.IO.print(1)")(name)(context)

// A generic user-defined type: `registerTopLevelTypeDeclaration` assigns `Box`'s own type
// parameter `a` a fresh id drawn from the live `typeSupply` and quantifies it in the constructor's
// scheme — the exact same `TypeScheme`/`instantiate` mechanism `print`'s `forall a. a -> Unit` and
// `Some`'s `forall a. a -> Maybe(a)` already prove works at every call site, just built from a
// user's own declaration instead of a static intrinsic one. `Box(value = 5)` instantiates the
// scheme's `a` as `Int`; a second real probe (not part of this test) instantiated the same
// constructor as `Str` in the same program, confirming genuine polymorphism rather than an
// accidentally-monomorphic scheme. Prints `5`, the field read back through `GetAdtField`.
let buildRealIrGenericTypeModule name context = codegenRealSource("type Box(a) =\n    | value: a\n\nlet b = Box(value = 5)\nAshes.IO.print(b.value)")(name)(context)

// A generic type with TWO distinct type parameters: `assignTypeParameterIds` mints one fresh id
// per declared parameter regardless of count, so this needed no code changes at all to already
// work — this test exists to verify that, not to add support for it. Both fields happen to be
// instantiated as `Int` here (a genuinely `Str`-instantiated field was tried first and hit a real,
// separate, already-known gap: `IrCodegen.ash` has no `LoadConstStr` case at all yet — its own
// header comment already names "strings" as unimplemented — so this test deliberately stays within
// already-covered instruction territory rather than exercising that unrelated gap). Prints `7`
// (`p.second`, read back through `GetAdtField` at field index `1`, not `0`), confirming the second
// type parameter's field lands at its own correctly-ordered offset rather than aliasing the first.
let buildRealIrMultiParamGenericTypeModule name context = codegenRealSource("type Pair(a, b) =\n    | first: a\n    | second: b\n\nlet p = Pair(first = 5, second = 7)\nAshes.IO.print(p.second)")(name)(context)

// The first REAL `match` this compiler has ever taken from source to a running executable —
// reading a positional constructor's field back through pattern matching, which
// `buildRealIrMultiConstructorModule` above explicitly could not do yet (it only proved both
// constructors allocate correctly, never reading either back). Needed no new lowering: the
// self-hosted `CoreLowering.ash` already emits a complete `match` compilation (arena-bracketed
// arms, a null-pointer guard before each `GetAdtTag`, per-arm `RcDrop`, `Borrow`/`CopyOutArena`
// bookkeeping) for any constructor already in `standardConstructorLayouts`, `Maybe` included — only
// `IrCodegen.ash` was missing the instruction cases that shape of IR actually uses (`CmpIntEq`/
// `CmpIntNe` and the `Borrow`/`CopyOutArena` aliases below). Prints `42`, the payload read back out
// of `Some(42)` through its own arm.
let buildRealIrMatchSomeModule name context = codegenRealSource("let x = Some(42)\n\nmatch x with\n    | Some(v) -> Ashes.IO.print(v)\n    | None -> Ashes.IO.print(0)")(name)(context)

// The same `match` compilation exercised on a user-defined, non-null-representable multi-
// constructor type rather than the intrinsic `Maybe` — confirming the null-guard/tag-dispatch
// pattern above is a uniform part of `match`'s lowering strategy, not special-cased for `Maybe`.
// Prints `7`, `Circle`'s own field read back through its own arm (`Square`'s arm is never taken).
let buildRealIrMatchMultiConstructorModule name context = codegenRealSource("type Shape =\n    | Circle(Int)\n    | Square(Int)\n\nlet x = Circle(7)\n\nmatch x with\n    | Circle(r) -> Ashes.IO.print(r)\n    | Square(s) -> Ashes.IO.print(s)")(name)(context)

// A NESTED pattern — two arms (`Some(Some(v))`/`Some(None)`) sharing the outer `Some` tag — is the
// real trigger for `CoreLowering.ash`'s `lowerMatchArmsViaTagGroups`, which compiles down to a
// `SwitchTag` instruction (confirmed via `--emit-ir`) rather than the sequential if-chain the
// two-arm cases above use. This is the exact shape that surfaced the `IrCodegen.ash` `SwitchTag`
// bug documented on `buildSwitchTagModule` above; this test exercises it through real lowering,
// not just a hand-built `IrFunction`. Prints `7`, `Some(Some(7))`'s payload read back through the
// `Some(Some(v))` arm specifically (proving the grouped dispatch reached the right nested arm, not
// just the right outer tag).
let buildRealIrMatchNestedModule name context = codegenRealSource("let x = Some(Some(7))\n\nmatch x with\n    | Some(Some(v)) -> Ashes.IO.print(v)\n    | Some(None) -> Ashes.IO.print(-1)\n    | None -> Ashes.IO.print(0)")(name)(context)

// The first program with a lifted helper function beyond the entry: unoptimized, `inc` is a
// `MakeClosure` (zero-size environment) stored in a local, and the call is an indirect
// `CallClosure` through it into `lambda_0`, whose `Return` is a real `ret`.
let buildRealIrHelperFunctionModule name context = codegenRealSource("let inc x = x + 1\nAshes.IO.print(inc(41))")(name)(context)

// Currying: `add(40)` returns a closure capturing `x` (an `Alloc`'d 8-byte environment written
// via `StoreMemOffset`, read back in `lambda_1` via `LoadEnv`), and the second `CallClosure`
// applies it — a closure object genuinely outliving the function that built it.
let buildRealIrCurriedHelperModule name context = codegenRealSource("let add x y = x + y\nAshes.IO.print(add(40)(2))")(name)(context)

// Self-recursion through the environment word: `fact`'s body rebuilds its own closure from the
// env it received (`LoadLocal Slot=0` → `MakeClosure`) and calls it.
let buildRealIrRecursiveHelperModule name context = codegenRealSource("let recursive fact n = if n > 1 then n * fact(n - 1) else 1\nAshes.IO.print(fact(5))")(name)(context)

// The optimized shapes of the same two programs: the curried call devirtualizes to one direct
// `CallKnown` of a scalarized-environment clone (`lambda_1__scalarenv0`, reading its capture
// from local slot `0` — the env parameter itself), and the recursive call becomes a direct
// `CallKnown` of `lambda_0` from within `lambda_0`.
let buildOptimizedIrCurriedHelperModule name context = codegenOptimizedRealSource("let add x y = x + y\nAshes.IO.print(add(40)(2))")(name)(context)

// `Int.min` is the one value whose negation overflows back onto itself, so `PrintInt`'s digit
// loop must divide unsigned — see `printIntDigitLoopBody`. `-9223372036854775807 - 1` rather than
// `1 << 63` only because this codegen has no `ShlInt` case yet.
let buildRealIrPrintIntMinModule name context = codegenRealSource("Ashes.IO.print(-9223372036854775807 - 1)")(name)(context)

// Every remaining integer operator in one expression: `<<`/`>>` (`ShlInt`/`ShrInt`, amount masked
// to 0..63, logical right shift), `/` (`DivInt`), `&`/`|`/`^` (`AndInt`/`OrInt`/`XorInt`):
// 16 + 64 + 3 + 2 + 7 + 5 = 97.
let buildRealIrIntegerOperatorsModule name context = codegenRealSource("Ashes.IO.print((1 << 4) + (256 >> 2) + (10 / 3) + (6 & 3) + (6 | 1) + (6 ^ 3))")(name)(context)

// The four signed comparisons that had no case (`>=`, `<`, `<=`, plus `>` again) and an unsigned
// one (`CmpUIntLt` via `u64` operands), all true, joined by `&&`.
let buildRealIrIntegerComparisonsModule name context = codegenRealSource("Ashes.IO.print(if 3 >= 3 && 2 < 3 && 2 <= 2 && 4 > 3 && 1u64 < 2u64 then 1 else 0)")(name)(context)

let buildRealIrPrintBoolTrueModule name context = codegenRealSource("Ashes.IO.print(true)")(name)(context)

let buildRealIrPrintBoolFalseModule name context = codegenRealSource("Ashes.IO.print(1 > 2)")(name)(context)

let buildOptimizedIrRecursiveHelperModule name context = codegenOptimizedRealSource("let recursive fact n = if n > 1 then n * fact(n - 1) else 1\nAshes.IO.print(fact(5))")(name)(context)

let buildOptimizedIrDeepTailLoopModule name context = codegenOptimizedRealSource("let recursive loop n acc = if n == 0 then acc else loop(n - 1)(acc + 1)\nAshes.IO.print(loop(2000000)(0))")(name)(context)

let buildOptimizedIrDeepMutualRecursionModule name context = codegenOptimizedRealSource("let recursive isEven n = if n == 0 then true else isOdd(n - 1) and isOdd n = if n == 0 then false else isEven(n - 1)\nAshes.IO.print(isEven(2000000))")(name)(context)

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

// Proves the entry-function-can't-just-`ret` fix in `AshesCompiler.Backend.IrCodegen` beyond just
// "the label is present": the compiled entry function must genuinely end in the `exit` syscall,
// not a bare `ret` — a real, separate claim from `assertLooksLikeAssembly`'s label-only check.
let assertEndsInSyscallExit bytes =
    (let text =
        bytes
        |> Ashes.Byte.length
        |> Ashes.Byte.subText(bytes)(0)
    in
        Unit
        |> (given (_) ->
            "syscall"
            |> Ashes.Text.contains(text)
            |> test.assertEqual(true))
        |> (given (_) ->
            "retq"
            |> Ashes.Text.contains(text)
            |> test.assertEqual(false)))

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

let testEmitAssemblyForMultiCaptureClosureModule unit =
    match emitModule(buildMultiCaptureClosureModule)("selfhost-backend-multi-capture-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("callMultiCaptureClosure")

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

let testEmitAssemblyForRcTreeReleaseModule unit =
    match emitModule(buildRcTreeReleaseModule)("selfhost-backend-rc-tree-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("rcTreeLifecycle")

let testEmitAssemblyForRcReuseModule unit =
    match emitModule(buildRcReuseModule)("selfhost-backend-rc-reuse-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("rcReuseLifecycle")

let testEmitAssemblyForRcClosureModule unit =
    match emitModule(buildRcClosureModule)("selfhost-backend-rc-closure-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("rcClosureLifecycle")

let testEmitAssemblyForRcTriReleaseModule unit =
    match emitModule(buildRcTriReleaseModule)("selfhost-backend-rc-tri-test")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("rcTriLifecycle")

let testEmitAssemblyForRealIrArithmeticModule unit =
    match emitModule(buildRealIrArithmeticModule)("selfhostBackendRealIrArith")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) ->
            let _ = assertLooksLikeAssembly(bytes)("selfhostBackendRealIrArith")
            in assertEndsInSyscallExit(bytes)

let testEmitAssemblyForRealIrLetBindingsModule unit =
    match emitModule(buildRealIrLetBindingsModule)("selfhostBackendRealIrLetBindings")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("selfhostBackendRealIrLetBindings")

let testEmitAssemblyForRealIrConditionalModule unit =
    match emitModule(buildRealIrConditionalModule)("selfhostBackendRealIrConditional")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("selfhostBackendRealIrConditional")

// `PrintInt`'s own `write` syscalls go through the exact same inline-assembly mechanism `Return`'s
// `exit` syscall does, so `assertLooksLikeAssembly`'s `"syscall"`-independent label check is
// enough here — there is no separate "ends in syscall, not ret" claim to make for a non-entry
// instruction the way there was for `Return` itself.
let testEmitAssemblyForRealIrPrintModule unit =
    match emitModule(buildRealIrPrintModule)("selfhostBackendRealIrPrint")(assemblyFileType) with
        | Error(message) -> test.fail(message)
        | Ok(bytes) -> assertLooksLikeAssembly(bytes)("selfhostBackendRealIrPrint")

// Proves `AshesCompiler.Backend.ElfLinker`'s static-only linker end to end: emit the real IR
// arithmetic module as an OBJECT (not assembly), link it into a static executable, and check the
// result is a genuine ET_EXEC ELF64 file (magic, 64-bit class, `e_type`/`e_machine`, one `PT_LOAD`
// program header) — not just "some bytes came back".
let assertLooksLikeStaticExecutable bytes =
    Unit
    |> (given (_) -> test.assertEqual(true)(Ashes.Byte.length(bytes) > 4096))
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
    |> (given (_) ->
        4
        |> Ashes.Byte.get(bytes)
        |> test.assertEqual(2u8))
    |> (given (_) ->
        16
        |> Ashes.Byte.getU16Le(bytes)
        |> test.assertEqual(2u16))
    |> (given (_) ->
        18
        |> Ashes.Byte.getU16Le(bytes)
        |> test.assertEqual(62u16))
    |> (given (_) ->
        56
        |> Ashes.Byte.getU16Le(bytes)
        |> test.assertEqual(1u16))

let testLinkStaticExecutableForRealIrArithmeticModule unit =
    match emitModule(buildRealIrArithmeticModule)("selfhostBackendLinkArith")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendLinkArith") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) -> assertLooksLikeStaticExecutable(executableBytes)

let testLinkStaticExecutableForRealIrPrintModule unit =
    match emitModule(buildRealIrPrintModule)("selfhostBackendLinkPrint")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendLinkPrint") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) -> assertLooksLikeStaticExecutable(executableBytes)

// THE genuine end-to-end proof this arc has been building toward: write the self-hosted linker's
// own output to a real file, make it executable, and actually run it — checking real stdout
// (`"-42"`, from `42 - 84` — the negative-sign path) and a real `0` exit code, not just that the
// bytes look ELF-shaped. Every earlier codegen/linker slice in this arc could only be checked this
// way OUTSIDE the automated suite (a scratch project + manual `chmod`/execute); this is the first
// self-hosted-produced executable proven correct BY the suite itself.
let testRunStaticExecutableForRealIrPrintModule unit =
    match emitModule(buildRealIrPrintModule)("selfhostBackendRunPrint")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunPrint") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_print_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_print_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_print_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("-42")(line)
                                                        in test.assertEqual(0)(exitCode)

// Same end-to-end proof as testRunStaticExecutableForRealIrPrintModule, for the `&&`/`||`
// desugaring instead of a plain `if`: writes, links, chmods, and runs a real executable, checking
// its actual stdout (`"1"`) and exit code.
let testRunStaticExecutableForRealIrLogicalOperatorsModule unit =
    match emitModule(buildRealIrLogicalOperatorsModule)("selfhostBackendRunLogicalOperators")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunLogicalOperators") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_logical_ops_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_logical_ops_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_logical_ops_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("1")(line)
                                                        in test.assertEqual(0)(exitCode)

// Runs `buildRealIrStringLiteralModule`'s executable end to end: `LoadConstStr`'s global and
// `PrintStr`'s syscall-based write, proven on a real Linux process, not just a hand-inspected IR
// shape.
let testRunStaticExecutableForRealIrStringLiteralModule unit =
    match emitModule(buildRealIrStringLiteralModule)("selfhostBackendRunStringLit")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunStringLit") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_string_lit_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_string_lit_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_string_lit_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("hello")(line)
                                                        in test.assertEqual(0)(exitCode)

// Runs `buildRealIrSomeConstructorModule`'s executable end to end, same shape as
// `testRunStaticExecutableForRealIrPrintModule` above: proves the RC-managed `AllocAdt`/
// `SetAdtField`/`RcDrop` codegen links (dynamically, since it calls real `malloc`/`free`) and runs
// without crashing on a genuine Linux process.
let testRunStaticExecutableForRealIrSomeConstructorModule unit =
    match emitModule(buildRealIrSomeConstructorModule)("selfhostBackendRunSomeCtor")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunSomeCtor") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_some_ctor_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_some_ctor_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_some_ctor_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("1")(line)
                                                        in test.assertEqual(0)(exitCode)

// Runs `buildRealIrAllocAndStringLiteralModule`'s executable end to end: proves
// `linkLinuxExecutable` can produce a single working executable for an object that needs BOTH the
// dynamic-import path (real `malloc`/`free` calls) AND a `.rodata` segment (the string literal) —
// the combination that previously failed to link at all.
let testRunStaticExecutableForRealIrAllocAndStringLiteralModule unit =
    match emitModule(buildRealIrAllocAndStringLiteralModule)("selfhostBackendRunAllocAndStringLit")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunAllocAndStringLit") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_alloc_and_string_lit_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_alloc_and_string_lit_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_alloc_and_string_lit_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("hello")(line)
                                                        in test.assertEqual(0)(exitCode)

// Runs `buildAdtFieldTagReadModule`'s executable end to end: allocates a 2-field RC cell (tag 0,
// fields 3 and 4), reads field 0 and the tag back through `GetAdtField`/`GetAdtTag`, and prints
// their sum. `3 + 0 = 3` confirms both reads land on the exact words `AllocAdt`/`SetAdtField`
// wrote, not just that the process didn't crash.
let testRunStaticExecutableForAdtFieldTagReadModule unit =
    match emitModule(buildAdtFieldTagReadModule)("selfhostBackendRunAdtFieldTagRead")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunAdtFieldTagRead") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_adt_field_tag_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_adt_field_tag_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_adt_field_tag_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("3")(line)
                                                        in test.assertEqual(0)(exitCode)

let testRunStaticExecutableForSwitchTagModule unit =
    match emitModule(buildSwitchTagModule)("selfhostBackendRunSwitchTag")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunSwitchTag") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_switch_tag_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_switch_tag_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_switch_tag_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("200")(line)
                                                        in test.assertEqual(0)(exitCode)

// Runs `buildRealIrRecordFieldModule`'s executable end to end: a user-defined `type Point`
// declaration, constructed with named fields and read back through `.x`, compiled through the
// complete self-hosted pipeline and executed on a real Linux process.
let testRunStaticExecutableForRealIrRecordFieldModule unit =
    match emitModule(buildRealIrRecordFieldModule)("selfhostBackendRunRecordField")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunRecordField") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_record_field_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_record_field_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_record_field_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("3")(line)
                                                        in test.assertEqual(0)(exitCode)

// Runs `buildRealIrMultiConstructorModule`'s executable end to end: two different constructors of
// the same multi-constructor type, each allocating and immediately dropping its own RC cell,
// compiled and run on a real Linux process without crashing.
let testRunStaticExecutableForRealIrMultiConstructorModule unit =
    match emitModule(buildRealIrMultiConstructorModule)("selfhostBackendRunMultiCtor")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunMultiCtor") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_multi_ctor_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_multi_ctor_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_multi_ctor_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("1")(line)
                                                        in test.assertEqual(0)(exitCode)

// Runs `buildRealIrGenericTypeModule`'s executable end to end: a generic user-defined type
// constructed and read back through the same real self-hosted pipeline, printing `5`.
let testRunStaticExecutableForRealIrGenericTypeModule unit =
    match emitModule(buildRealIrGenericTypeModule)("selfhostBackendRunGenericType")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunGenericType") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_generic_type_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_generic_type_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_generic_type_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("5")(line)
                                                        in test.assertEqual(0)(exitCode)

// Runs `buildRealIrMultiParamGenericTypeModule`'s executable end to end: a two-type-parameter
// generic type, each parameter instantiated at a different concrete type, printing `5`.
let testRunStaticExecutableForRealIrMultiParamGenericTypeModule unit =
    match emitModule(buildRealIrMultiParamGenericTypeModule)("selfhostBackendRunMultiParamGeneric")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunMultiParamGeneric") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_multi_param_generic_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_multi_param_generic_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_multi_param_generic_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("7")(line)
                                                        in test.assertEqual(0)(exitCode)

let testRunStaticExecutableForRealIrMatchSomeModule unit =
    match emitModule(buildRealIrMatchSomeModule)("selfhostBackendRunMatchSome")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunMatchSome") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_match_some_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_match_some_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_match_some_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("42")(line)
                                                        in test.assertEqual(0)(exitCode)

let testRunStaticExecutableForRealIrMatchMultiConstructorModule unit =
    match emitModule(buildRealIrMatchMultiConstructorModule)("selfhostBackendRunMatchMultiCtor")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunMatchMultiCtor") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_match_multi_ctor_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_match_multi_ctor_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_match_multi_ctor_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("7")(line)
                                                        in test.assertEqual(0)(exitCode)

let testRunStaticExecutableForRealIrMatchNestedModule unit =
    match emitModule(buildRealIrMatchNestedModule)("selfhostBackendRunMatchNested")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunMatchNested") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_match_nested_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_match_nested_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_match_nested_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("7")(line)
                                                        in test.assertEqual(0)(exitCode)

// Compiles `buildModule`'s program through the complete pipeline (codegen, object emission,
// linking), writes and runs the executable, and asserts its single line of stdout and a `0` exit —
// the same steps every run test above performs inline.
let assertProgramPrints buildModule name executablePath expectedLine =
    match emitModule(buildModule)(name)(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)(name) with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes(executablePath)(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable(executablePath) with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./" + executablePath)([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual(expectedLine)(line)
                                                        in test.assertEqual(0)(exitCode)

let testRunStaticExecutableForRealIrHelperFunctionModule unit = assertProgramPrints(buildRealIrHelperFunctionModule)("selfhostBackendRunHelperFunction")("selfhost_backend_helper_function_e2e")("42")

let testRunStaticExecutableForRealIrCurriedHelperModule unit = assertProgramPrints(buildRealIrCurriedHelperModule)("selfhostBackendRunCurriedHelper")("selfhost_backend_curried_helper_e2e")("42")

let testRunStaticExecutableForRealIrRecursiveHelperModule unit = assertProgramPrints(buildRealIrRecursiveHelperModule)("selfhostBackendRunRecursiveHelper")("selfhost_backend_recursive_helper_e2e")("120")

let testRunStaticExecutableForOptimizedIrCurriedHelperModule unit = assertProgramPrints(buildOptimizedIrCurriedHelperModule)("selfhostBackendRunOptimizedCurriedHelper")("selfhost_backend_optimized_curried_helper_e2e")("42")

let testRunStaticExecutableForRealIrPrintIntMinModule unit = assertProgramPrints(buildRealIrPrintIntMinModule)("selfhostBackendRunPrintIntMin")("selfhost_backend_print_int_min_e2e")("-9223372036854775808")

let testRunStaticExecutableForRealIrIntegerOperatorsModule unit = assertProgramPrints(buildRealIrIntegerOperatorsModule)("selfhostBackendRunIntegerOperators")("selfhost_backend_integer_operators_e2e")("97")

let testRunStaticExecutableForRealIrIntegerComparisonsModule unit = assertProgramPrints(buildRealIrIntegerComparisonsModule)("selfhostBackendRunIntegerComparisons")("selfhost_backend_integer_comparisons_e2e")("1")

let testRunStaticExecutableForRealIrPrintBoolTrueModule unit = assertProgramPrints(buildRealIrPrintBoolTrueModule)("selfhostBackendRunPrintBoolTrue")("selfhost_backend_print_bool_true_e2e")("true")

let testRunStaticExecutableForRealIrPrintBoolFalseModule unit = assertProgramPrints(buildRealIrPrintBoolFalseModule)("selfhostBackendRunPrintBoolFalse")("selfhost_backend_print_bool_false_e2e")("false")

// The first programs reaching a shipped standard-library module: `Ashes.Collection.List.length`
// (a pure-Ashes stdlib function, not an intrinsic) through a whole-module import and a
// qualified reference, and through a selector import under a local alias.
let buildShippedListLengthModule shipped name context = codegenShippedSource(shipped)("import Ashes.Collection.List\nAshes.IO.print(Ashes.Collection.List.length([1, 2, 3]))")(name)(context)

let buildShippedListLengthSelectorModule shipped name context = codegenShippedSource(shipped)("import Ashes.Collection.List.length as len\nAshes.IO.print(len([4, 5, 6, 7]))")(name)(context)

let buildIntrinsicModuleImportModule shipped name context = codegenShippedSource(shipped)("import Ashes.IO\nAshes.IO.print(42)")(name)(context)

let buildIntrinsicAliasImportModule shipped name context = codegenShippedSource(shipped)("import Ashes.IO as io\nio.print(43)")(name)(context)

let buildShippedTextJoinModule shipped name context = codegenShippedSource(shipped)("import Ashes.Text\nAshes.IO.print(Ashes.Text.join(\", \")([\"a\", \"b\", \"c\"]))")(name)(context)

let buildTextFromIntModule name context = codegenOptimizedRealSource("Ashes.IO.print(Ashes.Text.fromInt(0 - 42) + \"|\" + Ashes.Text.fromInt(0) + \"|\" + Ashes.Text.fromInt(9007))")(name)(context)

let buildTextByteLengthModule name context = codegenOptimizedRealSource("Ashes.IO.print(Ashes.Text.byteLength(\"hello, world\" + \"!\"))")(name)(context)

let buildBytesSliceModule name context = codegenOptimizedRealSource("let bytes = Ashes.Byte.fromText(\"hello world\")\nAshes.IO.print(Ashes.Byte.subText(bytes)(6)(5) + \"/\" + Ashes.Byte.subView(bytes)(0)(5))")(name)(context)

let buildBytesScalarOpsModule name context = codegenOptimizedRealSource("let bytes = Ashes.Byte.fromText(\"hello\")\nAshes.IO.print(Ashes.Text.fromInt(Ashes.Byte.indexOf(bytes)(108)(0)) + Ashes.Text.fromInt(Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(1))) + Ashes.Text.fromInt(Ashes.Byte.compare(bytes)(Ashes.Byte.fromText(\"hellp\"))))")(name)(context)

let buildTextUnconsTextModule name context = codegenOptimizedRealSource("let split text =\n    match Ashes.Text.unconsText(text) with\n        | Some((head, rest)) -> head + \"|\" + rest\n        | None -> \"<empty>\"\nAshes.IO.print(split(\"hello\") + split(\"\"))")(name)(context)

let buildRuneToTextModule name context = codegenOptimizedRealSource("Ashes.IO.print(Ashes.Rune.toText('A') + Ashes.Rune.toText('é') + Ashes.Rune.toText('€'))")(name)(context)

let buildStringAccumulatorDefaultModule name context = codegenOptimizedRealSource("let recursive go remaining output =\n    match remaining with\n        | [] -> output\n        | _head :: tail -> go(tail)(output + \"x\")\nAshes.IO.print(go([1, 2, 3])(\"\"))")(name)(context)

let buildRecursiveAdtFieldModule name context = codegenOptimizedRealSource("type Tree =\n    | Leaf(Int)\n    | Node(Tree, Tree)\nlet recursive sumTree tree =\n    match tree with\n        | Leaf(value) -> value\n        | Node(left, right) -> sumTree(left) + sumTree(right)\nAshes.IO.print(sumTree(Node(Node(Leaf(1))(Leaf(2)))(Leaf(3))))")(name)(context)

let buildDeepMatchJoinLoopModule name context = codegenOptimizedRealSource("type Step =\n    | Done\n    | Continue(Int, Int)\nlet recursive next n = if n <= 0 then Done else Continue(n)(n)\nlet recursive loop n acc =\n    match next(n) with\n        | Done -> acc\n        | Continue(m, r) -> loop(m - 1)(acc + r)\nAshes.IO.print(loop(200000)(0))")(name)(context)

let buildFloatScalarOpsModule name context = codegenOptimizedRealSource("Ashes.IO.print(if 1.0 + 2.0 * 3.0 == 7.0 && 8.0 / 2.0 - 1.0 >= 3.0 && 1.5 < 2.5 && 2.0 != 3.0 && 3.0 > 2.5 && 2.0 <= 2.0 then 1 else 0)")(name)(context)

let testRunStaticExecutableForFloatScalarOpsModule unit = assertProgramPrints(buildFloatScalarOpsModule)("selfhostBackendRunFloatScalarOps")("selfhost_backend_float_scalar_ops_e2e")("1")

let testRunStaticExecutableForShippedListLengthModule shipped unit =
    assertProgramPrints(buildShippedListLengthModule(shipped))("selfhostBackendRunShippedListLength")("selfhost_backend_shipped_list_length_e2e")("3")

let testRunStaticExecutableForShippedListLengthSelectorModule shipped unit =
    assertProgramPrints(buildShippedListLengthSelectorModule(shipped))("selfhostBackendRunShippedListLengthSelector")("selfhost_backend_shipped_list_length_selector_e2e")("4")

let testRunStaticExecutableForIntrinsicModuleImportModule shipped unit =
    assertProgramPrints(buildIntrinsicModuleImportModule(shipped))("selfhostBackendRunIntrinsicModuleImport")("selfhost_backend_intrinsic_module_import_e2e")("42")

let testRunStaticExecutableForIntrinsicAliasImportModule shipped unit =
    assertProgramPrints(buildIntrinsicAliasImportModule(shipped))("selfhostBackendRunIntrinsicAliasImport")("selfhost_backend_intrinsic_alias_import_e2e")("43")

let testRunStaticExecutableForShippedTextJoinModule shipped unit =
    assertProgramPrints(buildShippedTextJoinModule(shipped))("selfhostBackendRunShippedTextJoin")("selfhost_backend_shipped_text_join_e2e")("a, b, c")

let testRunStaticExecutableForTextFromIntModule unit = assertProgramPrints(buildTextFromIntModule)("selfhostBackendRunTextFromInt")("selfhost_backend_text_from_int_e2e")("-42|0|9007")

let testRunStaticExecutableForTextByteLengthModule unit = assertProgramPrints(buildTextByteLengthModule)("selfhostBackendRunTextByteLength")("selfhost_backend_text_byte_length_e2e")("13")

let testRunStaticExecutableForBytesSliceModule unit = assertProgramPrints(buildBytesSliceModule)("selfhostBackendRunBytesSlice")("selfhost_backend_bytes_slice_e2e")("world/hello")

let testRunStaticExecutableForBytesScalarOpsModule unit = assertProgramPrints(buildBytesScalarOpsModule)("selfhostBackendRunBytesScalarOps")("selfhost_backend_bytes_scalar_ops_e2e")("2101-1")

let buildTextParseUnconsModule name context = codegenOptimizedRealSource("Ashes.IO.print((match Ashes.Text.parseInt(\"-42\") with | Ok(v) -> Ashes.Text.fromInt(v) | Error(m) -> m) + \"/\" + (match Ashes.Text.parseInt(\"4x\") with | Ok(v2) -> Ashes.Text.fromInt(v2) | Error(m2) -> m2) + \"/\" + (match Ashes.Text.uncons(\"hi\") with | Some(pair) -> (match pair with | (r, rest) -> Ashes.Rune.toText(r) + rest) | None -> \"none\"))")(name)(context)

let testRunStaticExecutableForTextParseUnconsModule unit = assertProgramPrints(buildTextParseUnconsModule)("selfhostBackendRunTextParseUncons")("selfhost_backend_text_parse_uncons_e2e")("-42/Ashes.Text.parseInt() invalid input/hi")

let buildBytesBuilderOpsModule name context = codegenOptimizedRealSource("let grown = Ashes.Byte.appendByte(Ashes.Byte.singleton(65u8))(66u8)\nlet listed = Ashes.Byte.fromList([65u8, 66u8])\nlet zeros = Ashes.Byte.allocate(3)\nAshes.IO.print(Ashes.Text.fromInt(Ashes.Byte.length(grown)) + Ashes.Text.fromInt(Ashes.Number.UInt.toInt(Ashes.Byte.get(zeros)(2))) + (if Ashes.Byte.hash(grown) == Ashes.Byte.hash(listed) then \"eq\" else \"ne\"))")(name)(context)

let testRunStaticExecutableForBytesBuilderOpsModule unit = assertProgramPrints(buildBytesBuilderOpsModule)("selfhostBackendRunBytesBuilderOps")("selfhost_backend_bytes_builder_ops_e2e")("20eq")

let buildBytesRangeOpsModule name context = codegenOptimizedRealSource("let joined = Ashes.Byte.append(Ashes.Byte.empty(Unit))(Ashes.Byte.u32Le(16909060u32))\nlet patched = Ashes.Byte.setU16Le(Ashes.Byte.allocate(4))(1)(4660u16)\nlet shifted = Ashes.Byte.copyRange(Ashes.Byte.fromList([1u8, 2u8, 3u8, 4u8, 5u8]))(1)(Ashes.Byte.fromList([9u8, 8u8]))(0)(2)\nAshes.IO.print(Ashes.Text.fromInt(Ashes.Byte.getU16Le(joined)(0)) + \"|\" + Ashes.Text.fromInt(Ashes.Byte.getU16Le(patched)(1)) + \"|\" + Ashes.Text.toHex(255) + \"|\" + Ashes.Text.fromInt(Ashes.Byte.get(shifted)(1)) + \"|\" + (match Ashes.Byte.scanHash(Ashes.Byte.fromList([65u8, 59u8]))(59)(0) with | (idx, _h) -> Ashes.Text.fromInt(idx)))")(name)(context)

let testRunStaticExecutableForBytesRangeOpsModule unit = assertProgramPrints(buildBytesRangeOpsModule)("selfhostBackendRunBytesRangeOps")("selfhost_backend_bytes_range_ops_e2e")("772|4660|0xff|9|1")

let testRunStaticExecutableForTextUnconsTextModule unit = assertProgramPrints(buildTextUnconsTextModule)("selfhostBackendRunTextUnconsText")("selfhost_backend_text_uncons_text_e2e")("h|ello<empty>")

let testRunStaticExecutableForRuneToTextModule unit = assertProgramPrints(buildRuneToTextModule)("selfhostBackendRunRuneToText")("selfhost_backend_rune_to_text_e2e")("Aé€")

let testRunStaticExecutableForStringAccumulatorDefaultModule unit = assertProgramPrints(buildStringAccumulatorDefaultModule)("selfhostBackendRunStringAccumulatorDefault")("selfhost_backend_string_accumulator_default_e2e")("xxx")

let testRunStaticExecutableForRecursiveAdtFieldModule unit = assertProgramPrints(buildRecursiveAdtFieldModule)("selfhostBackendRunRecursiveAdtField")("selfhost_backend_recursive_adt_field_e2e")("6")

let testRunStaticExecutableForDeepMatchJoinLoopModule unit = assertProgramPrints(buildDeepMatchJoinLoopModule)("selfhostBackendRunDeepMatchJoinLoop")("selfhost_backend_deep_match_join_loop_e2e")("20000100000")

let testRunStaticExecutableForOptimizedIrRecursiveHelperModule unit = assertProgramPrints(buildOptimizedIrRecursiveHelperModule)("selfhostBackendRunOptimizedRecursiveHelper")("selfhost_backend_optimized_recursive_helper_e2e")("120")

let testRunStaticExecutableForOptimizedIrDeepTailLoopModule unit = assertProgramPrints(buildOptimizedIrDeepTailLoopModule)("selfhostBackendRunOptimizedDeepTailLoop")("selfhost_backend_deep_tail_loop_e2e")("2000000")

let testRunStaticExecutableForOptimizedIrDeepMutualRecursionModule unit = assertProgramPrints(buildOptimizedIrDeepMutualRecursionModule)("selfhostBackendRunOptimizedDeepMutualRecursion")("selfhost_backend_deep_mutual_recursion_e2e")("true")

// A six-constructor `match` lowers to one `SwitchTag`, which LLVM's x86-64 selection turns into a
// jump table in `.rodata`: absolute `.text` block addresses carried by `.rela.rodata` entries. The
// linker must apply those to the `.rodata` bytes (`collectRodataPatches`), not only the `.text`
// relocations; before it did, the dispatch jumped through zeroed entries to address 0.
let buildRealIrJumpTableDispatchModule name context =
    codegenRealSource(
        "type Color =\n    | Red(Int)\n    | Green(Int)\n    | Blue(Int)\n    | Yellow(Int)\n    | Purple(Int)\n    | Orange(Int)\n\nlet color = Orange(0)\n\nAshes.IO.print(match color with\n    | Red(_) -> \"red\"\n    | Green(_) -> \"green\"\n    | Blue(_) -> \"blue\"\n    | Yellow(_) -> \"yellow\"\n    | Purple(_) -> \"purple\"\n    | Orange(_) -> \"orange\")\n"
    )(name)(context)

let testRunStaticExecutableForRealIrJumpTableDispatchModule unit = assertProgramPrints(buildRealIrJumpTableDispatchModule)("selfhostBackendRunJumpTableDispatch")("selfhost_backend_jump_table_dispatch_e2e")("orange")

// THE dynamic-linking proof: `buildMallocFreeEntryModule`'s object has real
// `R_X86_64_PLT32` relocations against `malloc`/`free`, so `linkLinuxExecutable` must produce a
// genuinely dynamically-linked executable (`e_phnum = 4`: text `PT_LOAD`, data `PT_LOAD`,
// `PT_INTERP`, `PT_DYNAMIC` — checked structurally) that the REAL Linux dynamic loader can load
// and run. Verified independently outside this assertion (via `strace`) that the kernel loads
// `ld-linux-x86-64.so.2`, which loads real glibc and calls its actual `malloc` (observable as a
// real `brk` syscall extending the heap) before this program's own `exit(0)` syscall fires.
let testLinkAndRunDynamicMallocFreeModule unit =
    match emitModule(buildMallocFreeEntryModule)("selfhostBackendDynamicMallocFree")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendDynamicMallocFree") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    let _ =
                        Unit
                        |> (given (_) -> test.assertEqual(true)(Ashes.Byte.length(executableBytes) > 4096))
                        |> (given (_) ->
                            0
                            |> Ashes.Byte.get(executableBytes)
                            |> test.assertEqual(127u8))
                        |> (given (_) ->
                            16
                            |> Ashes.Byte.getU16Le(executableBytes)
                            |> test.assertEqual(2u16))
                        |> (given (_) ->
                            56
                            |> Ashes.Byte.getU16Le(executableBytes)
                            |> test.assertEqual(4u16))
                    in
                        match Ashes.IO.File.writeBytes("selfhost_backend_dynamic_mallocfree_e2e")(executableBytes) with
                            | Error(message) -> test.fail(message)
                            | Ok(_) ->
                                match Ashes.IO.File.makeExecutable("selfhost_backend_dynamic_mallocfree_e2e") with
                                    | Error(message) -> test.fail(message)
                                    | Ok(_) ->
                                        match Ashes.IO.Process.spawn("./selfhost_backend_dynamic_mallocfree_e2e")([]) with
                                            | Error(message) -> test.fail(message)
                                            | Ok(process) ->
                                                process
                                                |> Ashes.IO.Process.waitForExit
                                                |> test.assertEqual(0)

// Runs `buildRealIrTwoStringLiteralsModule`'s executable end to end: proves two distinct string
// literals in one object are both laid out and printed correctly.
let testRunStaticExecutableForRealIrTwoStringLiteralsModule unit =
    match emitModule(buildRealIrTwoStringLiteralsModule)("selfhostBackendRunTwoStringLits")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunTwoStringLits") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_two_string_lits_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_two_string_lits_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_two_string_lits_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected two lines of stdout from the linked executable, got none")
                                                | Some(firstLine) ->
                                                    match Ashes.IO.Process.readStdoutLine(process) with
                                                        | None -> test.fail("expected a second line of stdout from the linked executable, got only one")
                                                        | Some(secondLine) ->
                                                            let exitCode = Ashes.IO.Process.waitForExit(process)
                                                            in
                                                                let _ = test.assertEqual("hello")(firstLine)
                                                                in
                                                                    let _ = test.assertEqual("world")(secondLine)
                                                                    in test.assertEqual(0)(exitCode)

// Runs `buildRealIrLetBoundStringModule`'s executable end to end: proves the linker's new 8-byte
// absolute `R_X86_64_64` support (`DataRelocationPatch.dataPatchWidth`) is correct, not just that
// it compiles — a string reached through a `let` binding, not `print`'s own direct argument.
let testRunStaticExecutableForRealIrLetBoundStringModule unit =
    match emitModule(buildRealIrLetBoundStringModule)("selfhostBackendRunLetBoundString")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunLetBoundString") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_let_bound_string_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_let_bound_string_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_let_bound_string_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("hello")(line)
                                                        in test.assertEqual(0)(exitCode)

// Runs `buildRealIrRecordFieldStringModule`'s executable end to end: proves a `Str`-typed record
// field round-trips through `SetAdtField`/`GetAdtField`/`match` exactly like any other field type.
let testRunStaticExecutableForRealIrRecordFieldStringModule unit =
    match emitModule(buildRealIrRecordFieldStringModule)("selfhostBackendRunRecordFieldString")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunRecordFieldString") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_record_field_string_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_record_field_string_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_record_field_string_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("hello")(line)
                                                        in test.assertEqual(0)(exitCode)

// Runs `buildRealIrPanicModule`'s executable end to end: proves `PanicStr` prints its message and
// exits `1`, not just that it compiles.
let testRunStaticExecutableForRealIrPanicModule unit =
    match emitModule(buildRealIrPanicModule)("selfhostBackendRunPanic")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunPanic") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_panic_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_panic_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_panic_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("boom")(line)
                                                        in test.assertEqual(1)(exitCode)

// Runs `buildRealIrStringEqualityModule`'s executable end to end: proves `CmpStrEq`/`CmpStrNe`'s
// length-mismatch fast path AND its real `memcmp` dynamic-import call both produce the correct
// result, not just that the object links.
let testRunStaticExecutableForRealIrStringEqualityModule unit =
    match emitModule(buildRealIrStringEqualityModule)("selfhostBackendRunStringEquality")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunStringEquality") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_string_equality_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_string_equality_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_string_equality_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("1")(line)
                                                        in test.assertEqual(0)(exitCode)

// Runs `buildRealIrStringConcatModule`'s executable end to end: proves the real `malloc`+`memcpy`
// `ConcatStrN` codegen produces the correct combined length and byte content, not just that the
// object links.
let testRunStaticExecutableForRealIrStringConcatModule unit =
    match emitModule(buildRealIrStringConcatModule)("selfhostBackendRunStringConcat")(objectFileType) with
        | Error(message) -> test.fail(message)
        | Ok(objectBytes) ->
            match linkLinuxExecutable(objectBytes)("selfhostBackendRunStringConcat") with
                | Error(message) -> test.fail(message)
                | Ok(executableBytes) ->
                    match Ashes.IO.File.writeBytes("selfhost_backend_string_concat_e2e")(executableBytes) with
                        | Error(message) -> test.fail(message)
                        | Ok(_) ->
                            match Ashes.IO.File.makeExecutable("selfhost_backend_string_concat_e2e") with
                                | Error(message) -> test.fail(message)
                                | Ok(_) ->
                                    match Ashes.IO.Process.spawn("./selfhost_backend_string_concat_e2e")([]) with
                                        | Error(message) -> test.fail(message)
                                        | Ok(process) ->
                                            match Ashes.IO.Process.readStdoutLine(process) with
                                                | None -> test.fail("expected one line of stdout from the linked executable, got none")
                                                | Some(line) ->
                                                    let exitCode = Ashes.IO.Process.waitForExit(process)
                                                    in
                                                        let _ = test.assertEqual("hello world")(line)
                                                        in test.assertEqual(0)(exitCode)

let run shipped =
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
    |> testEmitAssemblyForMultiCaptureClosureModule
    |> testEmitAssemblyForRcCellLifecycleModule
    |> testEmitAssemblyForRcNodeReleaseModule
    |> testEmitAssemblyForRcOptionReleaseModule
    |> testEmitAssemblyForRcTreeReleaseModule
    |> testEmitAssemblyForRcReuseModule
    |> testEmitAssemblyForRcClosureModule
    |> testEmitAssemblyForRcTriReleaseModule
    |> testEmitAssemblyForRealIrArithmeticModule
    |> testEmitAssemblyForRealIrLetBindingsModule
    |> testEmitAssemblyForRealIrConditionalModule
    |> testEmitAssemblyForRealIrPrintModule
    |> testLinkStaticExecutableForRealIrArithmeticModule
    |> testLinkStaticExecutableForRealIrPrintModule
    |> testRunStaticExecutableForRealIrPrintModule
    |> testRunStaticExecutableForRealIrLogicalOperatorsModule
    |> testRunStaticExecutableForRealIrStringLiteralModule
    |> testRunStaticExecutableForRealIrSomeConstructorModule
    |> testRunStaticExecutableForRealIrAllocAndStringLiteralModule
    |> testRunStaticExecutableForRealIrTwoStringLiteralsModule
    |> testRunStaticExecutableForRealIrLetBoundStringModule
    |> testRunStaticExecutableForRealIrRecordFieldStringModule
    |> testRunStaticExecutableForRealIrPanicModule
    |> testRunStaticExecutableForRealIrStringEqualityModule
    |> testRunStaticExecutableForRealIrStringConcatModule
    |> testRunStaticExecutableForAdtFieldTagReadModule
    |> testRunStaticExecutableForSwitchTagModule
    |> testRunStaticExecutableForRealIrRecordFieldModule
    |> testRunStaticExecutableForRealIrMultiConstructorModule
    |> testRunStaticExecutableForRealIrGenericTypeModule
    |> testRunStaticExecutableForRealIrMultiParamGenericTypeModule
    |> testRunStaticExecutableForRealIrMatchSomeModule
    |> testRunStaticExecutableForRealIrMatchMultiConstructorModule
    |> testRunStaticExecutableForRealIrMatchNestedModule
    |> testRunStaticExecutableForRealIrHelperFunctionModule
    |> testRunStaticExecutableForRealIrCurriedHelperModule
    |> testRunStaticExecutableForRealIrRecursiveHelperModule
    |> testRunStaticExecutableForOptimizedIrCurriedHelperModule
    |> testRunStaticExecutableForOptimizedIrRecursiveHelperModule
    |> testRunStaticExecutableForOptimizedIrDeepTailLoopModule
    |> testRunStaticExecutableForOptimizedIrDeepMutualRecursionModule
    |> testRunStaticExecutableForRealIrPrintIntMinModule
    |> testRunStaticExecutableForRealIrIntegerOperatorsModule
    |> testRunStaticExecutableForRealIrIntegerComparisonsModule
    |> testRunStaticExecutableForRealIrPrintBoolTrueModule
    |> testRunStaticExecutableForRealIrPrintBoolFalseModule
    |> testRunStaticExecutableForShippedListLengthModule(shipped)
    |> testRunStaticExecutableForShippedListLengthSelectorModule(shipped)
    |> testRunStaticExecutableForIntrinsicModuleImportModule(shipped)
    |> testRunStaticExecutableForIntrinsicAliasImportModule(shipped)
    |> testRunStaticExecutableForShippedTextJoinModule(shipped)
    |> testRunStaticExecutableForTextFromIntModule
    |> testRunStaticExecutableForTextByteLengthModule
    |> testRunStaticExecutableForBytesSliceModule
    |> testRunStaticExecutableForBytesScalarOpsModule
    |> testRunStaticExecutableForTextUnconsTextModule
    |> testRunStaticExecutableForRuneToTextModule
    |> testRunStaticExecutableForStringAccumulatorDefaultModule
    |> testRunStaticExecutableForRecursiveAdtFieldModule
    |> testRunStaticExecutableForDeepMatchJoinLoopModule
    |> testRunStaticExecutableForFloatScalarOpsModule
    |> testRunStaticExecutableForTextParseUnconsModule
    |> testRunStaticExecutableForBytesBuilderOpsModule
    |> testRunStaticExecutableForBytesRangeOpsModule
    |> testRunStaticExecutableForRealIrJumpTableDispatchModule
    |> testLinkAndRunDynamicMallocFreeModule
    |> (given (_) -> Ashes.IO.print("all self-hosted backend tests passed"))

match Ashes.IO.args with
    | root :: [] ->
        root
        |> loadShippedModules
        |> run
    | _ -> Ashes.IO.panic("usage: backend-tests <shipped-library-root>")
