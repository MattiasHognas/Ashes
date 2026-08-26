import Ashes.IO
import Ashes.Collection.List.length
import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.CoroutineFrame
import AshesCompiler.Semantics.DecisionSnapshot
import AshesCompiler.Semantics.FunctionOrigins
import AshesCompiler.Semantics.HoverTypeInfo
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.SourceContext
import AshesCompiler.Semantics.Types
// Tests source maps, definition/hover identities, diagnostic locations, function origins, and explanation metadata.
export (
    value runMetadataAndOriginsTests,
)

let testSourceContextSingleFile unit =
    (let source = "let add x y =\n    x + y\n\nadd(1)(2)"
    in
        let ctx = createSourceContext("/path/to/test.ash")(source)
        in
            let loc0 = resolveOffsetLocation(ctx)(0)
            in
                let _ =
                    match loc0 with
                        | None -> test.fail("expected location at offset 0")
                        | Some(IrSourceLocation(path, line, col)) ->
                            let _ = test.assertEqual("/path/to/test.ash")(path)
                            in
                                let _ = test.assertEqual(1)(line)
                                in
                                    let _ = test.assertEqual(1)(col)
                                    in Unit
                in
                    let loc18 = resolveOffsetLocation(ctx)(18)
                    in
                        let _ =
                            match loc18 with
                                | None -> test.fail("expected location at offset 18")
                                | Some(IrSourceLocation(_p, l2, c2)) ->
                                    let _ = test.assertEqual(2)(l2)
                                    in
                                        let _ = test.assertEqual(5)(c2)
                                        in Unit
                        in Unit)

let testSourceContextMultiFile unit =
    (let combinedSource = "let x = 1\nlet y = 2\nlet entry = x + y"
    in
        let regions =
            [
                ModuleRegion(regionFilePath = "ModuleA.ash", startOffset = 0, endOffset = 10),
                ModuleRegion(regionFilePath = "ModuleB.ash", startOffset = 10, endOffset = 20),
                ModuleRegion(regionFilePath = "Main.ash", startOffset = 20, endOffset = 37)
            ]
        in
            let ctx = createMultiFileSourceContext(combinedSource)(regions)("Main.ash")
            in
                let locA = resolveOffsetLocation(ctx)(4)
                in
                    let _ =
                        match locA with
                            | None -> test.fail("expected location in ModuleA")
                            | Some(IrSourceLocation(path, line, col)) ->
                                let _ = test.assertEqual("ModuleA.ash")(path)
                                in
                                    let _ = test.assertEqual(1)(line)
                                    in
                                        let _ = test.assertEqual(5)(col)
                                        in Unit
                    in
                        let locB = resolveOffsetLocation(ctx)(14)
                        in
                            let _ =
                                match locB with
                                    | None -> test.fail("expected location in ModuleB")
                                    | Some(IrSourceLocation(path, line, col)) ->
                                        let _ = test.assertEqual("ModuleB.ash")(path)
                                        in
                                            let _ = test.assertEqual(1)(line)
                                            in
                                                let _ = test.assertEqual(5)(col)
                                                in Unit
                            in
                                let locOut = resolveOffsetLocation(ctx)(100)
                                in
                                    let _ = test.assertEqual(true)(locOut == None)
                                    in Unit)

// A dependency module with a header comment and an export block (exactly what a text-rendering
// stitcher would strip) next to the entry module; the stitched context resolves each module's
// spans against that module's own file, keyed by the combined item the span belongs to.
let tokSource = "// A dependency module.\nexport (\n    value describe,\n)\n\nlet describe (kind: Kind) =\n    match kind with\n        | Alpha -> \"alpha\"\n"

let mainSource = "import Repro.Tok\n\nlet render (text: Str) =\n    describe(text)\n"

let stitchedContext unit =
    createStitchedSourceContext([("Tok.ash", tokSource), ("Main.ash", mainSource)])(
        [
            StitchedItemRegion(itemFilePath = "Tok.ash", itemStart = 0, itemEnd = 1),
            StitchedItemRegion(itemFilePath = "Main.ash", itemStart = 1, itemEnd = 2)
        ]
    )(
        "Main.ash"
    )

// Only a span's start takes part in resolution, so the end is one past it.
let spanAt (source: Str) (text: Str) =
    (let start = Ashes.Text.indexOf(source)(text)
    in
        if start < 0
        then test.fail("test source must contain " + text)
        else TextSpan(start)(start + 1))

let expectLocation (label: Str) (path: Str) (line: Int) (column: Int) (location: Maybe(IrSourceLocation)) =
    match location with
        | Some(IrSourceLocation(actualPath, actualLine, actualColumn)) ->
            let _ = test.assertEqual(path)(actualPath)
            in
                let _ = test.assertEqual(line)(actualLine)
                in
                    let _ = test.assertEqual(column)(actualColumn)
                    in Unit
        | None -> test.fail(label + ": expected a location")

let testStitchedSourceContext unit =
    (let ctx = stitchedContext(Unit)
    in
        let _ =
            "match kind with"
            |> spanAt(tokSource)
            |> resolveItemSpanLocation(ctx)(0)
            |> expectLocation("dependency item")("Tok.ash")(7)(5)
        in
            let _ =
                "describe(text)"
                |> spanAt(mainSource)
                |> resolveItemSpanLocation(ctx)(1)
                |> expectLocation("entry item")("Main.ash")(4)(5)
            in
                let glue =
                    "match kind with"
                    |> spanAt(tokSource)
                    |> resolveItemSpanLocation(ctx)(5)
                in
                    let _ = test.assertEqual(true)(glue == None)
                    in
                        let empty =
                            0
                            |> TextSpan(0)
                            |> resolveItemSpanLocation(ctx)(0)
                        in
                            let _ = test.assertEqual(true)(empty == None)
                            in Unit)

let recursive locatedInstructionLines instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { location = Some(IrSourceLocation(path, line, _column)) } :: tail -> locatedInstructionLines(tail)((path, line) :: acc)
        | _ :: tail -> locatedInstructionLines(tail)(acc)

let recursive allLocatedIn (path: Str) (located: List((Str, Int))) =
    match located with
        | [] -> true
        | (candidate, _) :: tail ->
            if candidate == path
            then allLocatedIn(path)(tail)
            else false

let recursive anyLocatedAtLine (line: Int) (located: List((Str, Int))) =
    match located with
        | [] -> false
        | (_, candidate) :: tail ->
            if candidate == line
            then true
            else anyLocatedAtLine(line)(tail)

// Lowering an expression of the dependency item tags every emitted instruction with that
// module's file and the line of the innermost enclosing span (the entry Return stays unlocated).
let testLoweredDependencyItemCarriesItsOwnFileLines unit =
    (let ctx = stitchedContext(Unit)
    in
        let expression =
            ExprInt(2)
            |> ExprAdd(ExprAt(spanAt(tokSource)("Alpha"))(ExprInt(1)))
            |> ExprAt(spanAt(tokSource)("match kind with"))
        in
            match lowerCoreExpressionLocated(ctx)(0)(expression) with
                | CoreLoweringResult { program = Some(IrProgram { entryFunction = IrFunction { instructions = instructions } }), error = None } ->
                    let located = locatedInstructionLines(instructions)([])
                    in
                        let _ =
                            located
                            |> length
                            |> test.assertEqual(length(instructions) - 1)
                        in
                            let _ =
                                located
                                |> allLocatedIn("Tok.ash")
                                |> test.assertEqual(true)
                            in
                                let _ =
                                    located
                                    |> anyLocatedAtLine(7)
                                    |> test.assertEqual(true)
                                in
                                    let _ =
                                        located
                                        |> anyLocatedAtLine(8)
                                        |> test.assertEqual(true)
                                    in Unit
                | _ -> test.fail("located core lowering should produce a program"))

let testRuntimeMachineryTagging unit =
    (let ctx = createSourceContext("Test.ash")("let v = 42")
    in
        let span =
            5
            |> TextSpan(4)
            |> Some
        in
            let returnInst = tagInstruction(Return(0))(span)(Some(ctx))
            in
                let _ =
                    match returnInst with
                        | IrInstruction(Return(temp), Some(IrSourceLocation(_path, line, col))) ->
                            let _ = test.assertEqual(0)(temp)
                            in
                                let _ = test.assertEqual(1)(line)
                                in
                                    let _ = test.assertEqual(5)(col)
                                    in Unit
                        | _ -> test.fail("expected tagged return instruction")
                in
                    let dropInst =
                        tagInstruction(RcDrop(0)("Int")(0)(false)(false)(None))(span)(Some(ctx))
                    in
                        let _ =
                            match dropInst with
                                | IrInstruction(RcDrop(temp, _tn, _sl, _res, _str, _dest), loc) ->
                                    let _ = test.assertEqual(0)(temp)
                                    in
                                        let _ = test.assertEqual(true)(loc == None)
                                        in Unit
                                | _ -> test.fail("expected unlocated drop instruction")
                        in Unit)

let testFunctionOriginsCreation unit =
    (let entry = createProgramEntryOrigin(Unit)
    in
        let _ =
            match entry with
                | IrFunctionOrigin(label, originKind, _src, _pLabel, Some(CompilerFunctionOwner(ownerKind, ownerName)), _disc, _loc) ->
                    let _ = test.assertEqual("_start_main")(label)
                    in
                        let _ = test.assertEqual(true)(originKind == ProgramEntryOrigin)
                        in
                            let _ = test.assertEqual(true)(ownerKind == ProgramFunctionOwner)
                            in
                                let _ = test.assertEqual("program entry")(ownerName)
                                in Unit
                | _ -> test.fail("unexpected program entry origin")
        in
            let srcOrigin =
                createSourceFunctionOrigin(
                    "myFunc",
                    Some("Module.myFunc"),
                    Some(
                        IrSourceLocation(
                            filePath = "Module.ash",
                            line = 10,
                            column = 5
                        )
                    ),
                    45
                )
            in
                let _ = test.assertEqual("myFunc")(srcOrigin.functionSourceName)
                in
                    let lambdaOrigin =
                        createLambdaOrigin(
                            "__closure_1",
                            "y",
                            TextSpan(50)(60),
                            None,
                            Some(entry),
                            Some(
                                IrSourceLocation(
                                    filePath = "Module.ash",
                                    line = 11,
                                    column = 1
                                )
                            )
                        )
                    in
                        let _ =
                            match lambdaOrigin with
                                | IrFunctionOrigin(label, originKind, _src, Some(pLabel), _owner, Some(disc), _loc) ->
                                    let _ = test.assertEqual("__closure_1")(label)
                                    in
                                        let _ = test.assertEqual(true)(originKind == ClosureHelperOrigin)
                                        in
                                            let _ = test.assertEqual("_start_main")(pLabel)
                                            in
                                                let _ = test.assertEqual("lambda:50:10:y")(disc)
                                                in Unit
                                | _ -> test.fail("unexpected lambda origin")
                        in Unit)

let testFunctionOriginsDiscovery unit =
    (let lambdaExpr = ExprLambda("x")(ExprVar("x"))(None)
    in
        let ast =
            ExprAt(
                TextSpan(0)(50),
                ExprLet(
                    "helper",
                    ExprAt(TextSpan(9)(25))(lambdaExpr),
                    ExprVar("helper"),
                    [],
                    None,
                    []
                )
            )
        in
            let ctx = createSourceContext("Main.ash")("let helper x = x in helper")
            in
                let origins = discoverSourceFunctionOrigins(ast)(None)(Some(ctx))
                in
                    let _ =
                        origins
                        |> length
                        |> test.assertEqual(1)
                    in
                        let _ =
                            match origins with
                                | (name, SourceFunctionOrigin(srcName, _q, Some(IrSourceLocation(_p, line, col)), offset)) :: _ ->
                                    let _ = test.assertEqual("helper")(name)
                                    in
                                        let _ = test.assertEqual("helper")(srcName)
                                        in
                                            let _ = test.assertEqual(1)(line)
                                            in
                                                let _ = test.assertEqual(10)(col)
                                                in
                                                    let _ = test.assertEqual(9)(offset)
                                                    in Unit
                                | _ -> test.fail("unexpected discovered origin structure")
                        in Unit)

let testHoverTypeInfo unit =
    (let semType =
        SemFunction(
            SemInt,
            SemInt,
            Some(
                SemRow(
                    [
                        SemCapability("Clock")([]),
                        SemCapability("FileSystem")([SemString])
                    ],
                    None
                )
            )
        )
    in
        let hover1 =
            HoverTypeInfo(
                span = TextSpan(0)(10),
                name = Some("getTime"),
                inferredType = semType,
                constraints = [],
                parameterNames = ["unit"],
                isParameter = false
            )
        in
            let hoverList = [hover1]
            in
                let found = findHoverAtOffset(5)(hoverList)
                in
                    let _ =
                        match found with
                            | Some(HoverTypeInfo(_s, Some(name), _t, _c, _p, _param)) -> test.assertEqual("getTime")(name)
                            | _ -> test.fail("expected to find hover info at offset 5")
                    in
                        let notFound = findHoverAtOffset(20)(hoverList)
                        in
                            let _ = test.assertEqual(true)(notFound == None)
                            in
                                let caps = collectTypeCapabilities(semType)
                                in
                                    let _ =
                                        caps
                                        |> length
                                        |> test.assertEqual(2)
                                    in
                                        let authority = capturePublicAuthority(["getTime"])(hoverList)
                                        in
                                            let _ =
                                                match authority with
                                                    | PublicAuthorityRecord(name, authCaps) :: [] ->
                                                        let _ = test.assertEqual("getTime")(name)
                                                        in
                                                            authCaps
                                                            |> length
                                                            |> test.assertEqual(2)
                                                    | _ -> test.fail("unexpected public authority record")
                                            in Unit)

let testDecisionSnapshot unit =
    (let srcOrigin = createSourceFunctionOrigin("compute")(None)(None)(0)
    in
        let funcOrigin =
            IrFunctionOrigin(
                generatedLabel = "compute_0",
                originKind = SourceFunctionOriginKind,
                sourceOrigin = Some(srcOrigin),
                parentGeneratedLabel = None,
                compilerOwner = None,
                stableDiscriminator = None,
                generationLocation = None
            )
        in
            let ownershipRec =
                FunctionOwnershipRecord(
                    ordinal = 0,
                    origin = srcOrigin,
                    functionName = "compute",
                    parameters = ["x"],
                    borrowedParameters = ["x"],
                    consumedParameters = [],
                    uniqueParameters = [],
                    capturedValues = [],
                    resultProvenance = "Fresh",
                    resultAliases = [],
                    resultFresh = true,
                    resultPoisoned = false,
                    mayExecuteUnderLiveHandlerPost = false
                )
            in
                let placementRec =
                    ValuePlacementRecord(
                        ordinal = 0,
                        functionOrigin = Some(funcOrigin),
                        temp = 0,
                        placement = RuntimeRc,
                        ownership = TempOwned,
                        producer = ProducerCall,
                        dropKind = DropRc,
                        reason = ReasonInitialValue,
                        typeName = Some("List(Int)"),
                        location = None
                    )
                in
                    let snapshot =
                        CompilationDecisionSnapshot(
                            functionOwnership = [ownershipRec],
                            valuePlacements = [placementRec],
                            coroutineRepresentations = [],
                            patternBindings = [],
                            externalResources = [],
                            publicAuthority = [],
                            externalAuthority = []
                        )
                    in
                        let funcOwnerships = filterOwnershipFor(srcOrigin)(snapshot)
                        in
                            let _ =
                                funcOwnerships
                                |> length
                                |> test.assertEqual(1)
                            in
                                let placements = filterPlacementsIn("compute_0")(snapshot)
                                in
                                    let _ =
                                        placements
                                        |> length
                                        |> test.assertEqual(1)
                                    in
                                        let cat = classifyValuePlacement(false)(false)(false)(true)(false)(false)
                                        in
                                            let _ = test.assertEqual(true)(cat == RuntimeRc)
                                            in Unit)

let runMetadataAndOriginsTests unit =
    (let _ = testSourceContextSingleFile(Unit)
    in
        let _ = testSourceContextMultiFile(Unit)
        in
            let _ = testStitchedSourceContext(Unit)
            in
                let _ = testLoweredDependencyItemCarriesItsOwnFileLines(Unit)
                in
                    let _ = testRuntimeMachineryTagging(Unit)
                    in
                        let _ = testFunctionOriginsCreation(Unit)
                        in
                            let _ = testFunctionOriginsDiscovery(Unit)
                            in
                                let _ = testHoverTypeInfo(Unit)
                                in
                                    let _ = testDecisionSnapshot(Unit)
                                    in
                                        let _ = Ashes.IO.print("all self-hosted metadata and origins tests passed")
                                        in Unit)
