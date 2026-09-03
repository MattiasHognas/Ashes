import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreExternalLowering
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.ExternalTyping
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.Types
export (
    value runCoreExternalLoweringTests,
)

let emptyTyping =
    ExternalFunctionTyping(
        name = "stub",
        symbolName = "stub",
        libraryName = None,
        parameters = [],
        returnSyntax = ParsedNamed("void"),
        sourceResult = SemNamed(0)("Unit")([]),
        directType = SemNamed(0)("Unit")([]),
        firstClassType = None,
        runtimeCapabilities = []
    )

let dummyParamSource =
    ExternalParameterTyping(
        syntax = ParsedNamed("void"),
        sourceArgument = None,
        sourceOutput = None,
        ownership = ExternalOwnershipUnspecified,
        directOnly = false
    )

let makeExternalFunctionAbi name symbolName libraryName parameters returnType directOnly =
    ExternalFunctionAbi(
        name = name,
        symbol = ExternalSymbolReference(
            symbolName = symbolName,
            libraryName = libraryName
        ),
        parameters = parameters,
        returnType = returnType,
        sourceTyping = emptyTyping,
        destructorForResource = None,
        runtimeCapabilities = [],
        directOnly = directOnly
    )

let makeLayout abi =
    CoreExternalFunctionLayout(
        name = match abi with
            | ExternalFunctionAbi { name = n } -> n,
        abi = abi,
        scheme = TypeScheme(
            quantified = [],
            body = SemNamed(0)("Unit")([]),
            constraints = []
        )
    )

let loweredProgramWithExternal layouts functions opaqueTypes expression =
    match lowerCoreExpressionWithFullContext([])([])(layouts)(functions)(opaqueTypes)(expression) with
        | CoreLoweringResult { program = Some(program), error = None } -> program
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> Ashes.Trait.Show.show
            |> (given (text) -> test.fail("core external lowering failed: " + text))
        | _ -> test.fail("core external lowering produced no program")

let loweredResultWithExternal layouts functions opaqueTypes expression = lowerCoreExpressionWithFullContext([])([])(layouts)(functions)(opaqueTypes)(expression)

let entryInstructions program =
    match program with
        | IrProgram { entryFunction = IrFunction { instructions = instructions } } -> instructions

let recursive containsCallExternal symbolName libraryName instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = CallExternal(_target, candidateSym, candidateLib, _args, _paramTypes, _retType) } :: rest ->
            let matches =
                if symbolName == candidateSym
                then libraryName == candidateLib
                else false
            in
                if matches
                then true
                else containsCallExternal(symbolName)(libraryName)(rest)
        | _ :: rest -> containsCallExternal(symbolName)(libraryName)(rest)

let recursive containsToCString instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = ToCString(_, _) } :: _ -> true
        | _ :: rest -> containsToCString(rest)

let recursive containsCopyFfiString instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = CopyFfiString(_, _, _) } :: _ -> true
        | _ :: rest -> containsCopyFfiString(rest)

let recursive containsAllocFfiOut instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = AllocFfiOut(_, _) } :: _ -> true
        | _ :: rest -> containsAllocFfiOut(rest)

let recursive containsLoadFfiOut instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = LoadFfiOut(_, _, _) } :: _ -> true
        | _ :: rest -> containsLoadFfiOut(rest)

let recursive containsAlloc size instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = Alloc(_target, candidate, _managed) } :: rest ->
            if size == candidate
            then true
            else containsAlloc(size)(rest)
        | _ :: rest -> containsAlloc(size)(rest)

let testExternalParameterCount unit =
    (let params =
        [
            ExternalParameterAbi(parameterIndex = 0, abiType = ExternalAbiFloat64, source = dummyParamSource),
            ExternalParameterAbi(parameterIndex = 1, abiType = ExternalAbiOut(ExternalAbiInt), source = dummyParamSource),
            ExternalParameterAbi(parameterIndex = 2, abiType = ExternalAbiString, source = dummyParamSource),
            ExternalParameterAbi(parameterIndex = 3, abiType = None
            |> ExternalAbiNativeString(true)(ExternalNativeStringBorrowed)
            |> ExternalAbiOut, source = dummyParamSource)
        ]
    in
        params
        |> externalInputParameterCount
        |> test.assertEqual(2))

let testFromExternalAbiType unit =
    ExternalAbiInt
    |> fromExternalAbiType
    |> test.assertEqual(SemInt)
    |> (given (_) ->
        ExternalAbiFloat64
        |> fromExternalAbiType
        |> test.assertEqual(SemFloat))
    |> (given (_) ->
        ExternalAbiBool
        |> fromExternalAbiType
        |> test.assertEqual(SemBool))
    |> (given (_) ->
        ExternalAbiString
        |> fromExternalAbiType
        |> test.assertEqual(SemString))
    |> (given (_) ->
        ExternalAbiOpaque("FileHandle")
        |> fromExternalAbiType
        |> test.assertEqual(SemOpaque("FileHandle")))
    |> (given (_) ->
        None
        |> ExternalAbiNativeString(false)(ExternalNativeStringBorrowed)
        |> fromExternalAbiType
        |> test.assertEqual(SemString))
    |> (given (_) ->
        None
        |> ExternalAbiNativeString(true)(ExternalNativeStringBorrowed)
        |> fromExternalAbiType
        |> test.assertEqual(SemNamed(0)("Maybe")([SemString])))
    |> (given (_) ->
        ExternalAbiVoid
        |> fromExternalAbiType
        |> test.assertEqual(SemNamed(0)("Unit")([])))

let testDirectScalarExternalCall unit =
    (let abi =
        makeExternalFunctionAbi(
            "c_sin",
            "sin",
            Some("libm.so"),
            [
                ExternalParameterAbi(parameterIndex = 0, abiType = ExternalAbiFloat64, source = dummyParamSource)
            ],
            ExternalAbiFloat64,
            false
        )
    in
        let layouts = [makeLayout(abi)]
        in
            let callExpr =
                ExprCall(
                    ExprVar("c_sin"),
                    ExprFloat(1.5)("1.5"),
                    false,
                    callArgumentsInline
                )
            in
                let program = loweredProgramWithExternal(layouts)([abi])([])(callExpr)
                in
                    program
                    |> entryInstructions
                    |> containsCallExternal("sin")(Some("libm.so"))
                    |> test.assertEqual(true))

let testDirectStringExternalCall unit =
    (let abi =
        makeExternalFunctionAbi(
            "c_puts",
            "puts",
            None,
            [
                ExternalParameterAbi(parameterIndex = 0, abiType = ExternalAbiString, source = dummyParamSource)
            ],
            ExternalAbiInt,
            false
        )
    in
        let layouts = [makeLayout(abi)]
        in
            let callExpr =
                ExprCall(
                    ExprVar("c_puts"),
                    ExprString("hello"),
                    false,
                    callArgumentsInline
                )
            in
                let program = loweredProgramWithExternal(layouts)([abi])([])(callExpr)
                in
                    let instrs = entryInstructions(program)
                    in
                        instrs
                        |> containsToCString
                        |> test.assertEqual(true)
                        |> (given (_) ->
                            instrs
                            |> containsCallExternal("puts")(None)
                            |> test.assertEqual(true)))

let testNativeStringReturn unit =
    (let abi =
        makeExternalFunctionAbi(
            "c_get_name",
            "get_name",
            None,
            [],
            ExternalAbiNativeString(false)(ExternalNativeStringBorrowed)(None),
            false
        )
    in
        let layouts = [makeLayout(abi)]
        in
            let callExpr =
                ExprCall(
                    ExprVar("c_get_name"),
                    ExprVar("Unit"),
                    false,
                    callArgumentsInline
                )
            in
                let program = loweredProgramWithExternal(layouts)([abi])([])(callExpr)
                in
                    let instrs = entryInstructions(program)
                    in
                        instrs
                        |> containsCallExternal("get_name")(None)
                        |> test.assertEqual(true)
                        |> (given (_) ->
                            instrs
                            |> containsCopyFfiString
                            |> test.assertEqual(true)))

let testOutParameterAndReturnPackaging unit =
    (let abi =
        makeExternalFunctionAbi(
            "c_split",
            "split_native",
            None,
            [
                ExternalParameterAbi(parameterIndex = 0, abiType = ExternalAbiString, source = dummyParamSource),
                ExternalParameterAbi(parameterIndex = 1, abiType = None
                |> ExternalAbiNativeString(true)(ExternalNativeStringBorrowed)
                |> ExternalAbiOut, source = dummyParamSource),
                ExternalParameterAbi(parameterIndex = 2, abiType = None
                |> ExternalAbiNativeString(true)(ExternalNativeStringBorrowed)
                |> ExternalAbiOut, source = dummyParamSource)
            ],
            ExternalAbiVoid,
            false
        )
    in
        let layouts = [makeLayout(abi)]
        in
            let callExpr =
                ExprCall(
                    ExprVar("c_split"),
                    ExprString("input"),
                    false,
                    callArgumentsInline
                )
            in
                let program = loweredProgramWithExternal(layouts)([abi])([])(callExpr)
                in
                    let instrs = entryInstructions(program)
                    in
                        instrs
                        |> containsAllocFfiOut
                        |> test.assertEqual(true)
                        |> (given (_) ->
                            instrs
                            |> containsLoadFfiOut
                            |> test.assertEqual(true))
                        |> (given (_) ->
                            instrs
                            |> containsCopyFfiString
                            |> test.assertEqual(true))
                        |> (given (_) ->
                            instrs
                            |> containsAlloc(16)
                            |> test.assertEqual(true)))

let testFirstClassExternalFunctionReference unit =
    (let abi =
        makeExternalFunctionAbi(
            "c_sin",
            "sin",
            Some("libm.so"),
            [
                ExternalParameterAbi(parameterIndex = 0, abiType = ExternalAbiFloat64, source = dummyParamSource)
            ],
            ExternalAbiFloat64,
            false
        )
    in
        let layouts = [makeLayout(abi)]
        in
            let refExpr =
                ExprLet(
                    "f",
                    ExprVar("c_sin"),
                    ExprCall(ExprVar("f"))(ExprFloat(2.0)("2.0"))(false)(callArgumentsInline),
                    [],
                    None,
                    []
                )
            in
                let program = loweredProgramWithExternal(layouts)([abi])([])(refExpr)
                in
                    match program with
                        | IrProgram { functions = liftedFunctions } ->
                            test.assertEqual(1)(match liftedFunctions with
                                | _ :: [] -> 1
                                | _ -> 0))

let testDirectOnlyRejection unit =
    (let abi =
        makeExternalFunctionAbi(
            "c_write_buf",
            "write_buf",
            None,
            [
                ExternalParameterAbi(parameterIndex = 0, abiType = ExternalAbiBuffer(ExternalAbiUInt(8)), source = dummyParamSource)
            ],
            ExternalAbiVoid,
            true
        )
    in
        let layouts = [makeLayout(abi)]
        in
            let refExpr =
                ExprLet(
                    "f",
                    ExprVar("c_write_buf"),
                    ExprVar("f"),
                    [],
                    None,
                    []
                )
            in
                match loweredResultWithExternal(layouts)([abi])([])(refExpr) with
                    | CoreLoweringResult { error = Some(CoreExternalDirectOnlyViolation("c_write_buf")) } -> Unit
                    | _ -> test.fail("direct-only external function reference should be rejected"))

let testExternalProgramMetadata unit =
    (let abi =
        makeExternalFunctionAbi(
            "c_time",
            "time",
            None,
            [],
            ExternalAbiInt,
            false
        )
    in
        let layouts = [makeLayout(abi)]
        in
            let program = loweredProgramWithExternal(layouts)([abi])(["FileHandle"])(ExprInt(0))
            in
                match program with
                    | IrProgram { externalFunctions = extFuncs, externalOpaqueTypes = opaqueTypes } ->
                        extFuncs
                        |> test.assertEqual([abi])
                        |> (given (_) -> test.assertEqual(["FileHandle"])(opaqueTypes)))

let testCleanupResourceEmission unit =
    (let cleanupInst = emitCleanupResource(5)("Resource")(None)
    in
        match cleanupInst with
            | CleanupResource(5, "Resource", None) -> Unit
            | _ -> test.fail("unexpected cleanup resource instruction"))

// A declared external resource `Counted` with an opening function, a borrowing reader, a
// consuming transfer, and its destructor.
let countedParameter ownership =
    ExternalParameterAbi(
        parameterIndex = 0,
        abiType = ExternalAbiOpaque("Counted"),
        source = dummyParamSource with ownership = ownership
    )

let countedAbi name symbolName parameters returnType destructorFor = makeExternalFunctionAbi(name)(symbolName)(Some("libcounted.so"))(parameters)(returnType)(false) with destructorForResource = destructorFor

let countedAbis =
    [
        countedAbi("openCounted")("counted_open")([ExternalParameterAbi(parameterIndex = 0, abiType = ExternalAbiInt, source = dummyParamSource)])(ExternalAbiOpaque("Counted"))(None),
        countedAbi("readCounted")("counted_read")([countedParameter(ExternalOwnershipBorrow)])(ExternalAbiInt)(None),
        countedAbi("transferCounted")("counted_transfer")([countedParameter(ExternalOwnershipConsume)])(ExternalAbiVoid)(None),
        countedAbi("closeCounted")("counted_close")([countedParameter(ExternalOwnershipConsume)])(ExternalAbiVoid)(Some("Counted"))
    ]

let recursive layoutsOf abis =
    match abis with
        | [] -> []
        | abi :: rest -> makeLayout(abi) :: layoutsOf(rest)

let parsedBody source =
    match parseProgram(source) with
        | ProgramParseResult { program = ProgramSyntax { body = Some(body) }, diagnostics = [] } -> body
        | _ -> test.fail("expected a cleanly parsed trailing expression in: " + source)

let countedProgram source =
    source
    |> parsedBody
    |> loweredProgramWithExternal(layoutsOf(countedAbis))(countedAbis)(["Counted"])

let countedError source =
    match source
    |> parsedBody
    |> loweredResultWithExternal(layoutsOf(countedAbis))(countedAbis)(["Counted"]) with
        | CoreLoweringResult { error = Some(error) } -> error
        | _ -> test.fail("expected lowering to fail for: " + source)

let recursive countCountedCleanups instructions =
    match instructions with
        | [] -> 0
        | IrInstruction { instruction = CleanupResource(_temp, "Counted", Some(ExternalFunctionAbi { name = "closeCounted" })) } :: rest -> 1 + countCountedCleanups(rest)
        | _ :: rest -> countCountedCleanups(rest)

// A live declared resource is closed at its let's exit by a `CleanupResource` carrying the
// destructor; a borrowing read leaves it live.
let testDeclaredResourceIsCleanedUpWithItsDestructor unit =
    "let r = openCounted(1) in readCounted(r)"
    |> countedProgram
    |> entryInstructions
    |> countCountedCleanups
    |> test.assertEqual(1)

// Closing through the destructor releases the binding: no scope-exit cleanup, and a later read
// is use-after-close.
let testDestructorCloseReleasesTheBinding unit =
    Unit
    |> (given (_) ->
        "let r = openCounted(1) in let _ = closeCounted(r) in 0"
        |> countedProgram
        |> entryInstructions
        |> countCountedCleanups
        |> test.assertEqual(0))
    |> (given (_) ->
        match countedError("let r = openCounted(1) in let _ = closeCounted(r) in readCounted(r)") with
            | ResourceUseAfterClose(message) -> test.assertEqual("Resource 'r' has already been closed. Using a resource after it has been closed is not allowed.")(message)
            | other -> test.fail("expected ResourceUseAfterClose, got " + Ashes.Trait.Show.show(other)))

// Closing twice through the destructor is a double close.
let testDestructorDoubleCloseIsRejected unit =
    match countedError("let r = openCounted(1) in let _ = closeCounted(r) in closeCounted(r)") with
        | ResourceDoubleClose(message) -> test.assertEqual("Resource 'r' has already been closed. Closing a resource twice is not allowed.")(message)
        | other -> test.fail("expected ResourceDoubleClose, got " + Ashes.Trait.Show.show(other))

// A consuming external that is not the destructor moves the resource: no cleanup, and a later
// read or close is use-after-move.
let testConsumingExternalMovesTheResource unit =
    Unit
    |> (given (_) ->
        "let r = openCounted(1) in let _ = transferCounted(r) in 0"
        |> countedProgram
        |> entryInstructions
        |> countCountedCleanups
        |> test.assertEqual(0))
    |> (given (_) ->
        match countedError("let r = openCounted(1) in let _ = transferCounted(r) in readCounted(r)") with
            | ResourceUseAfterMove(_message) -> Unit
            | other -> test.fail("expected ResourceUseAfterMove, got " + Ashes.Trait.Show.show(other)))
    |> (given (_) ->
        match countedError("let r = openCounted(1) in let _ = transferCounted(r) in closeCounted(r)") with
            | ResourceUseAfterMove(message) -> test.assertEqual("Resource 'r' has been moved and can no longer be closed here. Ownership was transferred when it was passed to a function or stored in a data structure.")(message)
            | other -> test.fail("expected ResourceUseAfterMove, got " + Ashes.Trait.Show.show(other)))

// A resource a closure captures moves into the closure rather than being closed at the scope
// exit the closure outlives.
let testCapturedResourceMovesIntoTheClosure unit =
    "let r = openCounted(1) in let f = given (x) -> readCounted(r) + x in f(1)"
    |> countedProgram
    |> entryInstructions
    |> countCountedCleanups
    |> test.assertEqual(0)

let runCoreExternalLoweringTests unit =
    Unit
    |> testExternalParameterCount
    |> testFromExternalAbiType
    |> testDirectScalarExternalCall
    |> testDirectStringExternalCall
    |> testNativeStringReturn
    |> testOutParameterAndReturnPackaging
    |> testFirstClassExternalFunctionReference
    |> testDirectOnlyRejection
    |> testExternalProgramMetadata
    |> testCleanupResourceEmission
    |> testDeclaredResourceIsCleanedUpWithItsDestructor
    |> testDestructorCloseReleasesTheBinding
    |> testDestructorDoubleCloseIsRejected
    |> testConsumingExternalMovesTheResource
    |> testCapturedResourceMovesIntoTheClosure
