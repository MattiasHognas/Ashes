import Ashes.Test as test
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.IrValidation
import AshesCompiler.Semantics.Types
export (
    value runIrValidationTests,
)

let makeInstruction kind =
    IrInstruction(
        instruction = kind,
        location = None
    )

let makeFunction label instructions localCount tempCount hasEnv =
    IrFunction(
        label = label,
        instructions = instructions,
        localCount = localCount,
        tempCount = tempCount,
        hasEnvAndArgParams = hasEnv,
        coroutine = None,
        localNames = [],
        localTypes = [],
        origin = None,
        lifetimesPlaced = false
    )

let makeProgram entryFunction functions stringLiterals capGlobals =
    IrProgram(
        entryFunction = entryFunction,
        functions = functions,
        stringLiterals = stringLiterals,
        externalFunctions = [],
        externalOpaqueTypes = [],
        usesPrintInt = false,
        usesPrintStr = false,
        usesPrintBool = false,
        usesConcatStr = false,
        usesClosures = false,
        usesAsync = false,
        capabilityHandlerGlobals = capGlobals,
        traitEvidence = emptyTraitEvidenceAnnotations
    )

let testValidProgram unit =
    (let entry =
        makeFunction("_start_main")(
            [
                10
                |> LoadConstInt(0)
                |> makeInstruction,
                0
                |> StoreLocal(0)
                |> makeInstruction,
                0
                |> LoadLocal(1)
                |> makeInstruction,
                makeInstruction(Return(1))
            ]
        )(
            2
        )(
            3
        )(
            false
        )
    in
        let helper =
            makeFunction("helper_fn")(
                [
                    makeInstruction(Label("entry_label")),
                    "str0"
                    |> LoadConstStr(0)
                    |> makeInstruction,
                    makeInstruction(Jump("entry_label")),
                    makeInstruction(Return(0))
                ]
            )(
                1
            )(
                2
            )(
                true
            )
        in
            let stringLit = IrStringLiteral(label = "str0", value = "hello")
            in
                let program = makeProgram(entry)([helper])([stringLit])(0)
                in
                    let report = validateIrProgram(program)
                    in
                        match report with
                            | IrValidationReport { isValid = valid, issues = issues } ->
                                valid
                                |> test.assertEqual(true)
                                |> (given (_) ->
                                    issues
                                    |> Ashes.Collection.List.length
                                    |> test.assertEqual(0)))

let testEmptyEntryLabel unit =
    (let entry = makeFunction("")([makeInstruction(Return(0))])(0)(1)(false)
    in
        let program = makeProgram(entry)([])([])(0)
        in
            let report = validateIrProgram(program)
            in
                match report with
                    | IrValidationReport { isValid = valid, issues = issues } ->
                        valid
                        |> test.assertEqual(false)
                        |> (given (_) -> test.assertEqual(true)(Ashes.Collection.List.length(issues) > 0)))

let testEntryFunctionHasEnvParams unit =
    (let entry = makeFunction("_start_main")([makeInstruction(Return(0))])(0)(1)(true)
    in
        let program = makeProgram(entry)([])([])(0)
        in
            let report = validateIrProgram(program)
            in
                match report with
                    | IrValidationReport { isValid = valid, issues = issues } ->
                        valid
                        |> test.assertEqual(false)
                        |> (given (_) ->
                            match issues with
                                | IrValidationIssue { message = msg } :: _ -> test.assertEqual("entry function cannot have env and arg params")(msg)
                                | [] -> test.fail("expected issue")))

let testDuplicateFunctionLabel unit =
    (let entry = makeFunction("_start_main")([makeInstruction(Return(0))])(0)(1)(false)
    in
        let f1 = makeFunction("foo")([makeInstruction(Return(0))])(0)(1)(true)
        in
            let f2 = makeFunction("foo")([makeInstruction(Return(0))])(0)(1)(true)
            in
                let program = makeProgram(entry)([f1, f2])([])(0)
                in
                    let report = validateIrProgram(program)
                    in
                        match report with
                            | IrValidationReport { isValid = valid, issues = issues } ->
                                valid
                                |> test.assertEqual(false)
                                |> (given (_) ->
                                    match issues with
                                        | IrValidationIssue { message = msg } :: _ -> test.assertEqual("duplicate function label 'foo' in IR program")(msg)
                                        | [] -> test.fail("expected issue")))

let testDuplicateStringLiteralLabel unit =
    (let entry = makeFunction("_start_main")([makeInstruction(Return(0))])(0)(1)(false)
    in
        let s1 = IrStringLiteral(label = "str0", value = "a")
        in
            let s2 = IrStringLiteral(label = "str0", value = "b")
            in
                let program = makeProgram(entry)([])([s1, s2])(0)
                in
                    let report = validateIrProgram(program)
                    in
                        match report with
                            | IrValidationReport { isValid = valid, issues = issues } ->
                                valid
                                |> test.assertEqual(false)
                                |> (given (_) ->
                                    match issues with
                                        | IrValidationIssue { message = msg } :: _ -> test.assertEqual("duplicate string literal label 'str0' in IR program")(msg)
                                        | [] -> test.fail("expected issue")))

let testNegativeCounts unit =
    (let entry = makeFunction("_start_main")([makeInstruction(FlushStdout)])(-1)(-1)(false)
    in
        let program = makeProgram(entry)([])([])(-2)
        in
            let report = validateIrProgram(program)
            in
                match report with
                    | IrValidationReport { isValid = valid, issues = issues } ->
                        valid
                        |> test.assertEqual(false)
                        |> (given (_) ->
                            issues
                            |> Ashes.Collection.List.length
                            |> test.assertEqual(3)))

let testDuplicateInstructionLabel unit =
    (let entry =
        makeFunction("_start_main")(
            [
                makeInstruction(Label("loop")),
                makeInstruction(Label("loop")),
                makeInstruction(Return(0))
            ]
        )(
            0
        )(
            1
        )(
            false
        )
    in
        let program = makeProgram(entry)([])([])(0)
        in
            let report = validateIrProgram(program)
            in
                match report with
                    | IrValidationReport { isValid = valid, issues = issues } ->
                        valid
                        |> test.assertEqual(false)
                        |> (given (_) ->
                            match issues with
                                | IrValidationIssue { message = msg } :: _ -> test.assertEqual("duplicate instruction label 'loop' in function '_start_main'")(msg)
                                | [] -> test.fail("expected issue")))

let testUndefinedJumpTarget unit =
    (let entry =
        makeFunction("_start_main")(
            [
                makeInstruction(Jump("nonexistent_label")),
                makeInstruction(Return(0))
            ]
        )(
            0
        )(
            1
        )(
            false
        )
    in
        let program = makeProgram(entry)([])([])(0)
        in
            let report = validateIrProgram(program)
            in
                match report with
                    | IrValidationReport { isValid = valid, issues = issues } ->
                        valid
                        |> test.assertEqual(false)
                        |> (given (_) ->
                            match issues with
                                | IrValidationIssue { message = msg } :: _ -> test.assertEqual("undefined jump target label 'nonexistent_label'")(msg)
                                | [] -> test.fail("expected issue")))

let testLocalSlotOutOfBounds unit =
    (let entry =
        makeFunction("_start_main")(
            [
                0
                |> StoreLocal(5)
                |> makeInstruction,
                makeInstruction(Return(0))
            ]
        )(
            2
        )(
            1
        )(
            false
        )
    in
        let program = makeProgram(entry)([])([])(0)
        in
            let report = validateIrProgram(program)
            in
                match report with
                    | IrValidationReport { isValid = valid, issues = issues } ->
                        valid
                        |> test.assertEqual(false)
                        |> (given (_) ->
                            match issues with
                                | IrValidationIssue { message = msg } :: _ -> test.assertEqual("local slot 5 out of bounds [0, 2)")(msg)
                                | [] -> test.fail("expected issue")))

let testTempRegisterOutOfBounds unit =
    (let entry =
        makeFunction("_start_main")(
            [
                1
                |> AddInt(5)(0)
                |> makeInstruction,
                makeInstruction(Return(0))
            ]
        )(
            0
        )(
            2
        )(
            false
        )
    in
        let program = makeProgram(entry)([])([])(0)
        in
            let report = validateIrProgram(program)
            in
                match report with
                    | IrValidationReport { isValid = valid, issues = issues } ->
                        valid
                        |> test.assertEqual(false)
                        |> (given (_) ->
                            match issues with
                                | IrValidationIssue { message = msg } :: _ -> test.assertEqual("temp register 5 out of bounds [0, 2)")(msg)
                                | [] -> test.fail("expected issue")))

let testUnknownStringLiteral unit =
    (let entry =
        makeFunction("_start_main")(
            [
                "missing_str"
                |> LoadConstStr(0)
                |> makeInstruction,
                makeInstruction(Return(0))
            ]
        )(
            0
        )(
            1
        )(
            false
        )
    in
        let program = makeProgram(entry)([])([])(0)
        in
            let report = validateIrProgram(program)
            in
                match report with
                    | IrValidationReport { isValid = valid, issues = issues } ->
                        valid
                        |> test.assertEqual(false)
                        |> (given (_) ->
                            match issues with
                                | IrValidationIssue { message = msg } :: _ -> test.assertEqual("unknown string literal label 'missing_str'")(msg)
                                | [] -> test.fail("expected issue")))

let testCoroutineAndDebugValidation unit =
    (let badCoroutine = CoroutineInfo(stateCount = -1, stateStructSize = -8, captureCount = -1)
    in
        let entry =
            IrFunction(
                label = "_start_main",
                instructions = [makeInstruction(Return(0))],
                localCount = 1,
                tempCount = 1,
                hasEnvAndArgParams = false,
                coroutine = Some(badCoroutine),
                localNames = [(5, "name")],
                localTypes = [(5, SemInt)],
                origin = None,
                lifetimesPlaced = false
            )
        in
            let program = makeProgram(entry)([])([])(0)
            in
                let report = validateIrProgram(program)
                in
                    match report with
                        | IrValidationReport { isValid = valid, issues = issues } ->
                            valid
                            |> test.assertEqual(false)
                            |> (given (_) ->
                                issues
                                |> Ashes.Collection.List.length
                                |> test.assertEqual(5)))

let testAssertAndHelperValidation unit =
    (let validEntry = makeFunction("_start_main")([makeInstruction(Return(0))])(0)(1)(false)
    in
        let validProg = makeProgram(validEntry)([])([])(0)
        in
            validProg
            |> isIrProgramValid
            |> test.assertEqual(true)
            |> (given (_) ->
                match assertValidIrProgram(validProg) with
                    | Ok(_) -> Unit
                    | Error(_) -> test.fail("expected valid program"))
            |> (given (_) ->
                let badEntry = makeFunction("_start_main")([makeInstruction(Return(5))])(0)(1)(false)
                in
                    let badProg = makeProgram(badEntry)([])([])(0)
                    in
                        badProg
                        |> isIrProgramValid
                        |> test.assertEqual(false)
                        |> (given (_) ->
                            match assertValidIrProgram(badProg) with
                                | Ok(_) -> test.fail("expected invalid program")
                                | Error(_) -> Unit)))

let runIrValidationTests unit =
    (let _ = testValidProgram(Unit)
    in
        let _ = testEmptyEntryLabel(Unit)
        in
            let _ = testEntryFunctionHasEnvParams(Unit)
            in
                let _ = testDuplicateFunctionLabel(Unit)
                in
                    let _ = testDuplicateStringLiteralLabel(Unit)
                    in
                        let _ = testNegativeCounts(Unit)
                        in
                            let _ = testDuplicateInstructionLabel(Unit)
                            in
                                let _ = testUndefinedJumpTarget(Unit)
                                in
                                    let _ = testLocalSlotOutOfBounds(Unit)
                                    in
                                        let _ = testTempRegisterOutOfBounds(Unit)
                                        in
                                            let _ = testUnknownStringLiteral(Unit)
                                            in
                                                let _ = testCoroutineAndDebugValidation(Unit)
                                                in
                                                    let _ = testAssertAndHelperValidation(Unit)
                                                    in
                                                        let _ = Ashes.IO.print("all self-hosted IR validation tests passed")
                                                        in Unit)
