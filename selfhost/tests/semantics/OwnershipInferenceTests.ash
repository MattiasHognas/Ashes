// Unit tests for self-hosted parameter ownership, result reachability, moves, borrows, and SCC provenance.

import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.OwnershipProvenance
import AshesCompiler.Semantics.OwnershipInference
import Ashes.Collection.List.length
export (
    value runOwnershipInferenceTests,
)

let dummyOrigin name =
    SourceFunctionOrigin(
        functionSourceName = name,
        functionQualifiedName = None,
        declarationLocation = None,
        declarationOffset = 0
    )

// 1. Borrow vs Consumed classification tests
let testBorrowReadResourceParameter unit =
    (let callExpr =
        ExprCall(
            ExprQualifiedVar("Ashes.IO.File")("readLine")
        )(
            ExprVar("h")
        )(
            false
        )(
            callArgumentsInline
        )
    in
        let isBorrow = isParamUsedOnlyAsBorrowRead(callExpr)("h")
        in test.assertEqual(true)(isBorrow))

// The same read through a parsed tree, where the root and the argument carry source spans.
let testBorrowReadResourceParameterThroughSpans unit =
    (let callExpr =
        ExprAt(
            TextSpan(start = 0, end = 30),
            ExprCall(
                ExprAt(
                    TextSpan(start = 0, end = 26),
                    ExprQualifiedVar("Ashes.IO.File")("readChunk")
                )
            )(
                ExprAt(TextSpan(start = 27, end = 28))(ExprVar("h"))
            )(
                false
            )(
                callArgumentsInline
            )
        )
    in
        let isBorrow = isParamUsedOnlyAsBorrowRead(callExpr)("h")
        in test.assertEqual(true)(isBorrow))

let testUnusedParameterIsBorrow unit =
    (let body = ExprInt(42)
    in
        let isBorrow = isParamUsedOnlyAsBorrowRead(body)("x")
        in test.assertEqual(true)(isBorrow))

let testConsumedArithmeticParameter unit =
    (let body = ExprAdd(ExprVar("x"))(ExprInt(1))
    in
        let isBorrow = isParamUsedOnlyAsBorrowRead(body)("x")
        in test.assertEqual(false)(isBorrow))

let testConsumedReturnedParameter unit =
    (let body = ExprVar("x")
    in
        let isBorrow = isParamUsedOnlyAsBorrowRead(body)("x")
        in test.assertEqual(false)(isBorrow))

let testConsumedConstructorParameter unit =
    (let body = ExprList([ExprVar("x")])(false)
    in
        let isBorrow = isParamUsedOnlyAsBorrowRead(body)("x")
        in test.assertEqual(false)(isBorrow))

let testMixedParameters unit =
    (let readCall =
        ExprCall(
            ExprQualifiedVar("Ashes.IO.File")("readLine")
        )(
            ExprVar("h")
        )(
            false
        )(
            callArgumentsInline
        )
    in
        let body =
            ExprIf(
                ExprGreaterThan(ExprVar("b"))(ExprInt(0))
            )(
                readCall
            )(
                ExprString("")
            )
        in
            let hBorrow = isParamUsedOnlyAsBorrowRead(body)("h")
            in
                let bBorrow = isParamUsedOnlyAsBorrowRead(body)("b")
                in
                    let _ = test.assertEqual(true)(hBorrow)
                    in test.assertEqual(false)(bBorrow))

// 2. Result reachability and freshness tests
let testFreshConstantResult unit =
    (let sig = FunctionSignature(name = "f", origin = dummyOrigin("f"), parameters = ["x"], body = ExprInt(42))
    in
        let summary = inferFunctionOwnership(sig)([])
        in
            match summary with
                | FunctionOwnershipSummary { resultReachFacts = facts } ->
                    let _ =
                        facts
                        |> isResultFresh
                        |> test.assertEqual(true)
                    in
                        facts
                        |> isResultPoisoned
                        |> test.assertEqual(false))

// `x + y` is a copy-typed scalar: it reaches neither parameter, so the result is fresh even though
// both parameters are read.
let testScalarOperatorResultIsFresh unit =
    (let body = ExprAdd(ExprVar("x"))(ExprVar("y"))
    in
        let sig = FunctionSignature(name = "add", origin = dummyOrigin("add"), parameters = ["x", "y"], body = body)
        in
            match inferFunctionOwnership(sig)([]) with
                | FunctionOwnershipSummary { resultReachFacts = facts } ->
                    facts
                    |> isResultFresh
                    |> test.assertEqual(true)
                    |> (given (_) ->
                        "x"
                        |> resultReachesParameter(facts)
                        |> test.assertEqual(false))
                    |> (given (_) ->
                        "y"
                        |> resultReachesParameter(facts)
                        |> test.assertEqual(false)))

let testParameterReachingResult unit =
    (let sig = FunctionSignature(name = "id", origin = dummyOrigin("id"), parameters = ["x"], body = ExprVar("x"))
    in
        let summary = inferFunctionOwnership(sig)([])
        in
            match summary with
                | FunctionOwnershipSummary { resultReachFacts = facts } ->
                    let _ =
                        facts
                        |> isResultFresh
                        |> test.assertEqual(false)
                    in
                        let _ =
                            "x"
                            |> resultReachesParameter(facts)
                            |> test.assertEqual(true)
                        in
                            let _ =
                                "x"
                                |> resultReachesParameterWhole(facts)
                                |> test.assertEqual(true)
                            in
                                facts
                                |> isResultPoisoned
                                |> test.assertEqual(false))

// `reverse`'s shape: the result re-conses the matched head onto the accumulator and returns the
// accumulator on the empty arm. The accumulator is reached whole; the list parameter is reached
// only through its destructured head and tail, which this walk does not track at all (arm bodies
// are analyzed under the unchanged environment), so it is neither reached nor reached whole.
let testDestructuredParameterIsNotReachedWhole unit =
    (let consArm = (PatternCons(PatternVar("head"))(PatternVar("tail")), ExprCons(ExprVar("head"))(ExprVar("reversed")), None)
    in
        let emptyArm = (PatternEmptyList, ExprVar("reversed"), None)
        in
            let body = ExprMatch(ExprVar("values"))([consArm, emptyArm])(None)
            in
                let sig = FunctionSignature(name = "reverse", origin = dummyOrigin("reverse"), parameters = ["values", "reversed"], body = body)
                in
                    let summary = inferFunctionOwnership(sig)([])
                    in
                        match summary with
                            | FunctionOwnershipSummary { resultReachFacts = facts } ->
                                let _ =
                                    "reversed"
                                    |> resultReachesParameterWhole(facts)
                                    |> test.assertEqual(true)
                                in
                                    let _ =
                                        "values"
                                        |> resultReachesParameterWhole(facts)
                                        |> test.assertEqual(false)
                                    in
                                        "values"
                                        |> resultReachesParameter(facts)
                                        |> test.assertEqual(false))

let testConditionalBranchReachingResult unit =
    (let body = ExprIf(ExprVar("cond"))(ExprVar("a"))(ExprVar("b"))
    in
        let sig = FunctionSignature(name = "pick", origin = dummyOrigin("pick"), parameters = ["cond", "a", "b"], body = body)
        in
            let summary = inferFunctionOwnership(sig)([])
            in
                match summary with
                    | FunctionOwnershipSummary { resultReachFacts = facts } ->
                        let reachesA = resultReachesParameter(facts)("a")
                        in
                            let reachesB = resultReachesParameter(facts)("b")
                            in
                                let reachesCond = resultReachesParameter(facts)("cond")
                                in
                                    let _ = test.assertEqual(true)(reachesA)
                                    in
                                        let _ = test.assertEqual(true)(reachesB)
                                        in test.assertEqual(false)(reachesCond))

let testInternalSharingPoisoning unit =
    (let body = ExprTuple([ExprVar("x"), ExprVar("x")])
    in
        let sig = FunctionSignature(name = "pair", origin = dummyOrigin("pair"), parameters = ["x"], body = body)
        in
            let summary = inferFunctionOwnership(sig)([])
            in
                match summary with
                    | FunctionOwnershipSummary { resultReachFacts = facts } ->
                        let _ =
                            facts
                            |> isResultPoisoned
                            |> test.assertEqual(true)
                        in
                            facts
                            |> isResultFresh
                            |> test.assertEqual(false))

// 3. Provenance and SCC fixpoint tests
let testDirectRcConstruction unit =
    (let node = buildProvenanceNode("makeList")(true)(false)(1)([])(None)([])(false)
    in
        let results = resolveResultProvenances([node])
        in
            match results with
                | pair :: tail ->
                    match tail with
                        | [] ->
                            match pair with
                                | (name, prov) ->
                                    match prov with
                                        | FunctionResultProvenance { rcEligible = rc } ->
                                            let _ = test.assertEqual("makeList")(name)
                                            in test.assertEqual(true)(rc)
                        | _ -> test.fail("expected 1 provenance result")
                | [] -> test.fail("expected non-empty provenance results"))

let testForwardingChainProvenance unit =
    (let nodeG =
        ProvenanceFunctionNode(
            functionName = "g",
            hasDirectEligibleResult = false,
            hasRejectedResult = false,
            consideredArmCount = 1,
            forwardTargets = ["makeList"],
            unambiguousForwardTarget = Some("makeList"),
            directBytesProvenances = [],
            hasUnknownBytesResult = false
        )
    in
        let nodeBase =
            ProvenanceFunctionNode(
                functionName = "makeList",
                hasDirectEligibleResult = true,
                hasRejectedResult = false,
                consideredArmCount = 1,
                forwardTargets = [],
                unambiguousForwardTarget = None,
                directBytesProvenances = [],
                hasUnknownBytesResult = false
            )
        in
            let results = resolveResultProvenances([nodeG, nodeBase])
            in
                match results with
                    | r1 :: t1 ->
                        match t1 with
                            | r2 :: t2 ->
                                match t2 with
                                    | [] ->
                                        match r1 with
                                            | (nameG, provG) ->
                                                match provG with
                                                    | FunctionResultProvenance { rcEligible = rcG, forwardsTo = fwdG } ->
                                                        match r2 with
                                                            | (nameBase, provBase) ->
                                                                match provBase with
                                                                    | FunctionResultProvenance { rcEligible = rcBase } ->
                                                                        let _ = test.assertEqual("g")(nameG)
                                                                        in
                                                                            let _ = test.assertEqual(true)(rcG)
                                                                            in
                                                                                let _ =
                                                                                    match fwdG with
                                                                                        | Some(t) -> test.assertEqual("makeList")(t)
                                                                                        | None -> test.fail("expected Some(makeList)")
                                                                                in
                                                                                    let _ = test.assertEqual("makeList")(nameBase)
                                                                                    in test.assertEqual(true)(rcBase)
                                    | _ -> test.fail("expected 2 provenance results")
                            | [] -> test.fail("expected 2 provenance results")
                    | [] -> test.fail("expected non-empty provenance results"))

let testMutualRecursionProvenanceConvergence unit =
    (let nodeEven = buildProvenanceNode("even")(true)(false)(2)(["odd"])(None)([])(false)
    in
        let nodeOdd = buildProvenanceNode("odd")(false)(false)(1)(["even"])(Some("even"))([])(false)
        in
            let results = resolveResultProvenances([nodeEven, nodeOdd])
            in
                match results with
                    | r1 :: t1 ->
                        match t1 with
                            | r2 :: t2 ->
                                match t2 with
                                    | [] ->
                                        match r1 with
                                            | (nameEven, provEven) ->
                                                match provEven with
                                                    | FunctionResultProvenance { rcEligible = rcEven } ->
                                                        match r2 with
                                                            | (nameOdd, provOdd) ->
                                                                match provOdd with
                                                                    | FunctionResultProvenance { rcEligible = rcOdd } ->
                                                                        let _ = test.assertEqual(true)(rcEven)
                                                                        in test.assertEqual(true)(rcOdd)
                                    | _ -> test.fail("expected 2 mutual provenance results")
                            | [] -> test.fail("expected 2 mutual provenance results")
                    | [] -> test.fail("expected non-empty provenance results"))

let testUngroundedCycleRejection unit =
    (let nodeF = buildProvenanceNode("f")(false)(false)(1)(["g"])(Some("g"))([])(false)
    in
        let nodeG = buildProvenanceNode("g")(false)(false)(1)(["f"])(Some("f"))([])(false)
        in
            let results = resolveResultProvenances([nodeF, nodeG])
            in
                match results with
                    | r1 :: t1 ->
                        match t1 with
                            | r2 :: t2 ->
                                match t2 with
                                    | [] ->
                                        match r1 with
                                            | (nameF, provF) ->
                                                match provF with
                                                    | FunctionResultProvenance { rcEligible = rcF } ->
                                                        match r2 with
                                                            | (nameG, provG) ->
                                                                match provG with
                                                                    | FunctionResultProvenance { rcEligible = rcG } ->
                                                                        let _ = test.assertEqual(false)(rcF)
                                                                        in test.assertEqual(false)(rcG)
                                    | _ -> test.fail("expected 2 ungrounded provenance results")
                            | [] -> test.fail("expected 2 ungrounded provenance results")
                    | [] -> test.fail("expected non-empty provenance results"))

// 4. Program ownership inference integration
let testProgramOwnershipInference unit =
    (let readCall =
        ExprCall(
            ExprQualifiedVar("Ashes.IO.File")("readLine")
        )(
            ExprVar("h")
        )(
            false
        )(
            callArgumentsInline
        )
    in
        let body1 =
            ExprIf(
                ExprGreaterThan(ExprVar("x"))(ExprInt(0))
            )(
                readCall
            )(
                ExprString("")
            )
        in
            let sig1 = FunctionSignature(name = "readIfPos", origin = dummyOrigin("readIfPos"), parameters = ["h", "x"], body = body1)
            in
                let sig2 = FunctionSignature(name = "id", origin = dummyOrigin("id"), parameters = ["x"], body = ExprVar("x"))
                in
                    let prov1 = buildProvenanceNode("readIfPos")(false)(false)(1)([])(None)([])(false)
                    in
                        let prov2 = buildProvenanceNode("id")(false)(false)(1)([])(None)([])(false)
                        in
                            let summaries = inferProgramOwnership([sig1, sig2])([prov1, prov2])([])(None)([])
                            in
                                match summaries with
                                    | s1 :: t1 ->
                                        match t1 with
                                            | s2 :: t2 ->
                                                match t2 with
                                                    | [] ->
                                                        match s1 with
                                                            | FunctionOwnershipSummary { functionName = n1, borrowedParameters = b1, consumedParameters = c1 } ->
                                                                match s2 with
                                                                    | FunctionOwnershipSummary { functionName = n2, borrowedParameters = b2, consumedParameters = c2 } ->
                                                                        let _ = test.assertEqual("readIfPos")(n1)
                                                                        in
                                                                            let _ = test.assertEqual(["h"])(b1)
                                                                            in
                                                                                let _ = test.assertEqual(["x"])(c1)
                                                                                in
                                                                                    let _ = test.assertEqual("id")(n2)
                                                                                    in
                                                                                        let _ = test.assertEqual([])(b2)
                                                                                        in test.assertEqual(["x"])(c2)
                                                    | _ -> test.fail("expected 2 program summaries")
                                            | [] -> test.fail("expected 2 program summaries")
                                    | [] -> test.fail("expected non-empty program summaries"))

// 5. Capture analysis tests
let testDirectCaptureOfFreeVariable unit =
    (let body = ExprAdd(ExprVar("x"))(ExprVar("y"))
    in
        let captures = computeCaptures(body)(["x"])
        in test.assertEqual(["y"])(captures))

let testProgramLevelCaptureExcludesOtherFunctions unit =
    (let callOther =
        ExprCall(
            ExprVar("other")
        )(
            ExprVar("x")
        )(
            false
        )(
            callArgumentsInline
        )
    in
        let body = ExprAdd(callOther)(ExprVar("y"))
        in
            let sigHelper = FunctionSignature(name = "helper", origin = dummyOrigin("helper"), parameters = ["x"], body = body)
            in
                let sigOther = FunctionSignature(name = "other", origin = dummyOrigin("other"), parameters = ["z"], body = ExprVar("z"))
                in
                    let provHelper = buildProvenanceNode("helper")(false)(false)(1)([])(None)([])(false)
                    in
                        let provOther = buildProvenanceNode("other")(false)(false)(1)([])(None)([])(false)
                        in
                            let summaries = inferProgramOwnership([sigHelper, sigOther])([provHelper, provOther])([])(None)([])
                            in
                                match summaries with
                                    | s1 :: t1 ->
                                        match t1 with
                                            | _s2 :: t2 ->
                                                match t2 with
                                                    | [] ->
                                                        match s1 with
                                                            | FunctionOwnershipSummary { functionName = n1, capturedValues = caps1 } ->
                                                                let _ = test.assertEqual("helper")(n1)
                                                                in test.assertEqual(["y"])(caps1)
                                                    | _ -> test.fail("expected 2 program summaries")
                                            | [] -> test.fail("expected 2 program summaries")
                                    | [] -> test.fail("expected non-empty program summaries"))

// 6. Whole-program inspect-only fixpoint over parsed programs
let parsedProgram source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let programOwnershipTable source =
    source
    |> parsedProgram
    |> topLevelFunctions
    |> inferProgramParameterOwnership

let ownershipOf (table: ProgramParameterOwnership) (name: Str) =
    match lookupProgramParameterOwnership(name)(table) with
        | Some(ownership) -> ownership
        | None -> test.fail("expected a table entry for " + name)

// Each assertion hands the table on so a test reads as one pipeline over it.
let assertBorrowed (name: Str) (expected: List(Str)) (table: ProgramParameterOwnership) =
    (let _ =
        name
        |> ownershipOf(table)
        |> getBorrowedParameters
        |> test.assertEqual(expected)
    in table)

let assertConsumed (name: Str) (expected: List(Str)) (table: ProgramParameterOwnership) =
    (let _ =
        name
        |> ownershipOf(table)
        |> getConsumedParameters
        |> test.assertEqual(expected)
    in table)

let done (_table: ProgramParameterOwnership) = Unit

let peekSource = "let peek h = Ashes.IO.File.readChunk(h)(2)\n"

let peekTwiceSource = peekSource + "let peekTwice h = (let a = peek(h) in peek(h))\n"

// A helper that only reads its handle, and a wrapper that only hands the handle to that helper.
let testInspectingHelperAndWrapperAreBorrowed unit =
    peekTwiceSource
    |> programOwnershipTable
    |> assertBorrowed("peek")(["h"])
    |> assertBorrowed("peekTwice")(["h"])
    |> done

// Alone, the wrapper cannot see through `peek`: the single-function verdict stays consumed.
let testSingleFunctionVerdictForWrapperStaysConsumed unit =
    match "let peekTwice h = (let a = peek(h) in peek(h))\n"
    |> parsedProgram
    |> topLevelFunctions with
        | (_name, parameters, body) :: [] ->
            []
            |> classifyParameterOwnership(parameters)(body)
            |> getConsumedParameters
            |> test.assertEqual(["h"])
        | _ -> test.fail("expected exactly one registered function")

// A wrapper that also stores the handle in its result consumes it; the helper stays borrowed.
let testWrapperStoringParameterIsConsumed unit =
    peekSource + "let keep h = (peek(h), h)\n"
    |> programOwnershipTable
    |> assertBorrowed("peek")(["h"])
    |> assertConsumed("keep")(["h"])
    |> done

// Two mutually recursive functions that hand the handle to each other: neither is proven before the
// other is checked, so the cycle never converges to borrowed (stage 0's outcome).
let testMutuallyRecursiveInspectorsStayConsumed unit =
    "let recursive ping h n = if n == 0 then Ashes.IO.File.readChunk(h)(1) else pong(h)(n - 1)\nand pong h n = if n == 0 then Ashes.IO.File.readChunk(h)(2) else ping(h)(n - 1)\n"
    |> programOwnershipTable
    |> assertConsumed("ping")(["h", "n"])
    |> assertConsumed("pong")(["h", "n"])
    |> done

// One member of the cycle returns the handle, so the handle flowing through the other is consumed.
let testCycleWithRetainingMemberIsConsumed unit =
    "let recursive ping h n = if n == 0 then h else pong(h)(n - 1)\nand pong h n = if n == 0 then Ashes.IO.File.readChunk(h)(2) else ping(h)(n - 1)\n"
    |> programOwnershipTable
    |> assertConsumed("ping")(["h", "n"])
    |> assertConsumed("pong")(["h", "n"])
    |> done

// A hand-off to a function outside the registered set is a consuming use.
let testUnregisteredCalleeIsConsumed unit =
    peekSource + "let wrap h = unknown(h)\n"
    |> programOwnershipTable
    |> assertConsumed("wrap")(["h"])
    |> done

// A local binding that shadows the registered helper's name is not the helper.
let testShadowedCalleeIsConsumed unit =
    peekSource + "let local h = (let peek = given (x) -> x in peek(h))\n"
    |> programOwnershipTable
    |> assertConsumed("local")(["h"])
    |> done

// A partial application of an inspecting helper captures the handle in a closure.
let testPartialHandOffIsConsumed unit =
    "let peekAt h n = Ashes.IO.File.readChunk(h)(n)\nlet part h = peekAt(h)\n"
    |> programOwnershipTable
    |> assertBorrowed("peekAt")(["h"])
    |> assertConsumed("part")(["h"])
    |> done

let recursive signaturesOf (funcs: List((Str, List(Str), Expr))) =
    match funcs with
        | [] -> []
        | (name, parameters, body) :: rest -> FunctionSignature(name = name, origin = dummyOrigin(name), parameters = parameters, body = body) :: signaturesOf(rest)

let recursive provenanceNodesOf (funcs: List((Str, List(Str), Expr))) =
    match funcs with
        | [] -> []
        | (name, _parameters, _body) :: rest -> buildProvenanceNode(name)(false)(false)(1)([])(None)([])(false) :: provenanceNodesOf(rest)

let recursive borrowedInSummaries (summaries: List(FunctionOwnershipSummary)) (name: Str) =
    match summaries with
        | [] -> test.fail("expected a summary for " + name)
        | FunctionOwnershipSummary { functionName = candidate, borrowedParameters = borrowed } :: rest ->
            if candidate == name
            then borrowed
            else borrowedInSummaries(rest)(name)

let assertSummaryBorrowed (name: Str) (expected: List(Str)) (summaries: List(FunctionOwnershipSummary)) =
    (let _ =
        name
        |> borrowedInSummaries(summaries)
        |> test.assertEqual(expected)
    in summaries)

// The program summaries carry the fixpoint verdict, not the single-function one.
let testProgramSummariesSeeThroughHandOff unit =
    (let funcs =
        peekTwiceSource
        |> parsedProgram
        |> topLevelFunctions
    in
        funcs
        |> provenanceNodesOf
        |> (given (provNodes) ->
            inferProgramOwnership(signaturesOf(funcs))(provNodes)([])(None)(funcs))
        |> assertSummaryBorrowed("peek")(["h"])
        |> assertSummaryBorrowed("peekTwice")(["h"])
        |> (given (_summaries) -> Unit))

let reportOwnershipInferenceSuccess unit = Ashes.IO.print("all self-hosted ownership inference and provenance tests passed")

let runOwnershipInferenceTests unit =
    unit
    |> testBorrowReadResourceParameter
    |> testBorrowReadResourceParameterThroughSpans
    |> testUnusedParameterIsBorrow
    |> testConsumedArithmeticParameter
    |> testConsumedReturnedParameter
    |> testConsumedConstructorParameter
    |> testMixedParameters
    |> testFreshConstantResult
    |> testScalarOperatorResultIsFresh
    |> testParameterReachingResult
    |> testDestructuredParameterIsNotReachedWhole
    |> testConditionalBranchReachingResult
    |> testInternalSharingPoisoning
    |> testDirectRcConstruction
    |> testForwardingChainProvenance
    |> testMutualRecursionProvenanceConvergence
    |> testUngroundedCycleRejection
    |> testProgramOwnershipInference
    |> testDirectCaptureOfFreeVariable
    |> testProgramLevelCaptureExcludesOtherFunctions
    |> testInspectingHelperAndWrapperAreBorrowed
    |> testSingleFunctionVerdictForWrapperStaysConsumed
    |> testWrapperStoringParameterIsConsumed
    |> testMutuallyRecursiveInspectorsStayConsumed
    |> testCycleWithRetainingMemberIsConsumed
    |> testUnregisteredCalleeIsConsumed
    |> testShadowedCalleeIsConsumed
    |> testPartialHandOffIsConsumed
    |> testProgramSummariesSeeThroughHandOff
    |> reportOwnershipInferenceSuccess
