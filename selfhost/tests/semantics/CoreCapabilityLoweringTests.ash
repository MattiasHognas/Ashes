import Ashes.Test as test
import Ashes.Collection.List.append
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreCapabilityLowering
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.Types
export (
    value runCoreCapabilityLoweringTests,
)

let recursive containsLoadCapabilityHandler index instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = LoadCapabilityHandler(_target, candidate) } :: rest ->
            if index == candidate
            then true
            else containsLoadCapabilityHandler(index)(rest)
        | _ :: rest -> containsLoadCapabilityHandler(index)(rest)

let recursive containsStoreCapabilityHandler index instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = StoreCapabilityHandler(candidate, _source) } :: rest ->
            if index == candidate
            then true
            else containsStoreCapabilityHandler(index)(rest)
        | _ :: rest -> containsStoreCapabilityHandler(index)(rest)

let recursive containsPanicStr instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = PanicStr(_) } :: _ -> true
        | _ :: rest -> containsPanicStr(rest)

let recursive containsAddInt instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = AddInt(_, _, _) } :: _ -> true
        | _ :: rest -> containsAddInt(rest)

let recursive containsCallClosure instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = CallClosure(_, _, _, _) } :: _ -> true
        | _ :: rest -> containsCallClosure(rest)

let recursive containsStoreMemOffset offset instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = StoreMemOffset(_target, candidate, _source) } :: rest ->
            if offset == candidate
            then true
            else containsStoreMemOffset(offset)(rest)
        | _ :: rest -> containsStoreMemOffset(offset)(rest)

let recursive containsAlloc size instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = Alloc(_target, candidate, _zeroed) } :: rest ->
            if size == candidate
            then true
            else containsAlloc(size)(rest)
        | _ :: rest -> containsAlloc(size)(rest)

let recursive containsAllocStack size instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = AllocStack(_target, candidate) } :: rest ->
            if size == candidate
            then true
            else containsAllocStack(size)(rest)
        | _ :: rest -> containsAllocStack(size)(rest)

let recursive appendAllFunctionInstructions functions collected =
    match functions with
        | [] -> collected
        | IrFunction { instructions = instrs } :: rest ->
            instrs
            |> append(collected)
            |> appendAllFunctionInstructions(rest)

// Closure bodies (an operation arm, a post continuation) lower into their own separate IrFunction,
// not into the entry function's own instruction list — a check that only looked at the entry
// function would never see what a closure's body actually does.
let allProgramInstructions program =
    match program with
        | IrProgram { entryFunction = IrFunction { instructions = entryInstrs }, functions = functions } -> appendAllFunctionInstructions(functions)(entryInstrs)

let recursive containsRequestServerStop instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = RequestServerStop(_) } :: _ -> true
        | _ :: rest -> containsRequestServerStop(rest)

let testDynamicPerformEmission unit =
    (let emission =
        emitDynamicPerform(
            "State",
            "get",
            0,
            0,
            2,
            10,
            0,
            [1],
            SemInt
        )
    in
        match emission with
            | CoreCapabilityPerformEmission { instructions = instrs, resultTemp = resTemp, semanticType = semType, error = None } ->
                semType
                |> test.assertEqual(SemInt)
                |> (given (_) ->
                    let hasLoad =
                        match instrs with
                            | LoadCapabilityHandler(t, 0) :: _ -> t == 10
                            | _ -> false
                    in test.assertEqual(true)(hasLoad))
            | _ -> test.fail("dynamic perform emission failed"))

let testStaticProviderCall unit =
    (let provider =
        CoreStaticProviderLayout(
            capabilityName = "Log",
            typeArguments = [],
            operations = [("log", ExprVar("my_log"))]
        )
    in
        let emission =
            emitStaticProviderCall(
                provider,
                "log",
                5,
                10,
                0,
                [6],
                SemNamed(0)("Unit")([])
            )
        in
            match emission with
                | CoreCapabilityPerformEmission { instructions = instrs, resultTemp = resTemp, error = None } ->
                    match instrs with
                        | CallClosure(target, 5, 6, -1) :: [] -> test.assertEqual(10)(target)
                        | _ -> test.fail("static provider call should emit single CallClosure")
                | _ -> test.fail("static provider call emission failed"))

let testSplitHandlerArms unit =
    (let arms =
        [
            (Some("State"), "get", [PatternVar("u")], ExprInt(42)),
            (None, "return", [PatternVar("x")], ExprVar("x"))
        ]
    in
        match splitHandlerArms(arms) with
            | ParsedHandlerArms { opArms = ops, returnArm = ret } ->
                (match ops with
                    | _ :: [] -> 1
                    | _ -> 0)
                |> test.assertEqual(1)
                |> (given (_) ->
                    match ret with
                        | Some(_) -> Unit
                        | None -> test.fail("expected return arm")))

let testFindCapabilityLayout unit =
    (let op = CoreCapabilityOperationLayout(name = "get", index = 0)
    in
        let cap =
            CoreCapabilityLayout(
                name = "State",
                index = 0,
                operations = [op]
            )
        in
            match findCapabilityLayout("State")([cap]) with
                | Some(found) -> test.assertEqual(cap)(found)
                | None -> test.fail("expected capability layout"))

let testFindCapabilityOperationIndex unit =
    (let op = CoreCapabilityOperationLayout(name = "put", index = 1)
    in
        match findCapabilityOperationIndex("put")([op]) with
            | Some(idx) -> test.assertEqual(1)(idx)
            | None -> test.fail("expected operation index"))

let testStopCapabilityLowering unit =
    (let stopCall =
        ExprPerform(
            ExprCall(
                ExprQualifiedVar("Stop")("stop"),
                ExprInt(0),
                false,
                callArgumentsInline
            )
        )
    in
        match lowerCoreExpression(stopCall) with
            | CoreLoweringResult { program = Some(program), error = None } ->
                match program with
                    | IrProgram { entryFunction = IrFunction { instructions = instrs } } ->
                        instrs
                        |> containsRequestServerStop
                        |> test.assertEqual(true)
            | _ -> test.fail("stop capability lowering failed"))

let testHandleExpressionLowering unit =
    (let op = CoreCapabilityOperationLayout(name = "get", index = 0)
    in
        let cap =
            CoreCapabilityLayout(
                name = "State",
                index = 0,
                operations = [op]
            )
        in
            let handleExpr =
                ExprHandle(
                    ExprInt(42),
                    [
                        (Some("State"), "get", [PatternVar("u")], ExprCall(ExprVar("resume"))(ExprInt(100))(false)(callArgumentsInline))
                    ]
                )
            in
                match lowerCoreExpressionWithCompleteContext([])([])([])([])([])([cap])([])(1)(handleExpr) with
                    | CoreLoweringResult { program = Some(program), error = None } ->
                        match program with
                            | IrProgram { entryFunction = IrFunction { instructions = instrs } } ->
                                instrs
                                |> containsStoreCapabilityHandler(0)
                                |> test.assertEqual(true)
                                |> (given (_) ->
                                    instrs
                                    |> containsStoreMemOffset((1 + 1 + 0) * 8)
                                    |> test.assertEqual(true))
                    | CoreLoweringResult { error = Some(error) } -> test.fail("handle expression lowering failed: " + Ashes.Trait.Show.show(error))
                    | _ -> test.fail("handle expression lowering produced no program"))

let testDynamicPerformViaExpression unit =
    (let op = CoreCapabilityOperationLayout(name = "get", index = 0)
    in
        let cap =
            CoreCapabilityLayout(
                name = "State",
                index = 0,
                operations = [op]
            )
        in
            let performExpr =
                ExprPerform(
                    ExprCall(
                        ExprQualifiedVar("State")("get"),
                        ExprInt(0),
                        false,
                        callArgumentsInline
                    )
                )
            in
                match lowerCoreExpressionWithCompleteContext([])([])([])([])([])([cap])([])(1)(performExpr) with
                    | CoreLoweringResult { program = Some(program), error = None } ->
                        match program with
                            | IrProgram { entryFunction = IrFunction { instructions = instrs } } ->
                                instrs
                                |> containsLoadCapabilityHandler(0)
                                |> test.assertEqual(true)
                                |> (given (_) ->
                                    instrs
                                    |> containsPanicStr
                                    |> test.assertEqual(true))
                    | _ -> test.fail("dynamic perform lowering failed"))

let testHandleReturnArmLowering unit =
    (let op = CoreCapabilityOperationLayout(name = "get", index = 0)
    in
        let cap =
            CoreCapabilityLayout(
                name = "State",
                index = 0,
                operations = [op]
            )
        in
            let handleExpr =
                ExprHandle(
                    ExprInt(42),
                    [
                        (Some("State"), "get", [PatternVar("u")], ExprCall(ExprVar("resume"))(ExprInt(100))(false)(callArgumentsInline)),
                        (None, "return", [PatternVar("x")], ExprAdd(ExprVar("x"))(ExprInt(1)))
                    ]
                )
            in
                match lowerCoreExpressionWithCompleteContext([])([])([])([])([])([cap])([])(1)(handleExpr) with
                    | CoreLoweringResult { program = Some(program), error = None } ->
                        match program with
                            | IrProgram { entryFunction = IrFunction { instructions = instrs } } ->
                                instrs
                                |> containsAddInt
                                |> test.assertEqual(true)
                    | CoreLoweringResult { error = Some(error) } -> test.fail("handle return-arm lowering failed: " + Ashes.Trait.Show.show(error))
                    | _ -> test.fail("handle return-arm lowering produced no program"))

let testHandleArmWithoutResumeIsRejected unit =
    (let op = CoreCapabilityOperationLayout(name = "get", index = 0)
    in
        let cap =
            CoreCapabilityLayout(
                name = "State",
                index = 0,
                operations = [op]
            )
        in
            let handleExpr =
                ExprHandle(
                    ExprInt(42),
                    [
                        (Some("State"), "get", [PatternVar("u")], ExprInt(100))
                    ]
                )
            in
                match lowerCoreExpressionWithCompleteContext([])([])([])([])([])([cap])([])(1)(handleExpr) with
                    | CoreLoweringResult { error = Some(UnsupportedOperationArmResume("State", "get")) } -> Unit
                    | CoreLoweringResult { error = Some(other) } -> test.fail("expected UnsupportedOperationArmResume, got " + Ashes.Trait.Show.show(other))
                    | _ -> test.fail("expected an operation arm without resume to be rejected"))

// A non-resuming `let` prefix before a bare tail resume (`let y = u + 1 in resume(y * 2)`) is an
// ordinary way to write an operation arm — do some work, then resume — that oneShotLetResume's
// exact-shape check alone would previously reject: neither "value IS a resume call" (one-shot) nor
// "body unwraps directly to a resume call" (bare tail) held for the let-wrapped whole. Proves
// resolveOperationArmBody's non-resuming ExprLet branch actually lowers the prefix and recurses
// into the still-tail-position resume underneath, rather than mistaking the let for something
// unsupported.
let testHandleLetPrefixBeforeTailResumeLowering unit =
    (let op = CoreCapabilityOperationLayout(name = "get", index = 0)
    in
        let cap =
            CoreCapabilityLayout(
                name = "State",
                index = 0,
                operations = [op]
            )
        in
            let letPrefixArmBody =
                ExprLet(
                    "y",
                    ExprAdd(ExprVar("u"))(ExprInt(1)),
                    ExprCall(ExprVar("resume"))(ExprMultiply(ExprVar("y"))(ExprInt(2)))(false)(callArgumentsInline),
                    [],
                    None,
                    []
                )
            in
                let handleExpr =
                    ExprHandle(
                        ExprInt(42),
                        [
                            (Some("State"), "get", [PatternVar("u")], letPrefixArmBody)
                        ]
                    )
                in
                    match lowerCoreExpressionWithCompleteContext([])([])([])([])([])([cap])([])(1)(handleExpr) with
                        | CoreLoweringResult { program = Some(program), error = None } ->
                            program
                            |> allProgramInstructions
                            |> containsAddInt
                            |> test.assertEqual(true)
                        | CoreLoweringResult { error = Some(error) } -> test.fail("let-prefix tail resume lowering failed: " + Ashes.Trait.Show.show(error))
                        | _ -> test.fail("let-prefix tail resume lowering produced no program"))

// A non-resuming `let recursive` prefix before a one-shot resume (`let recursive fact = ... in let
// x = resume(fact(u)) in x + 1`) proves resolveOperationArmBody's ExprLetRecursive branch, and that
// a one-shot resume still reachable underneath a recursive prefix installs its post closure the
// same as when there is no prefix at all.
let testHandleLetRecursivePrefixBeforeOneShotResumeLowering unit =
    (let op = CoreCapabilityOperationLayout(name = "get", index = 0)
    in
        let cap =
            CoreCapabilityLayout(
                name = "State",
                index = 0,
                operations = [op]
            )
        in
            let factValue =
                ExprLambda(
                    "n",
                    ExprIf(
                        ExprEqual(ExprVar("n"))(ExprInt(0)),
                        ExprInt(1),
                        callArgumentsInline
                        |> ExprCall(ExprVar("fact"))(ExprSubtract(ExprVar("n"))(ExprInt(1)))(false)
                        |> ExprMultiply(ExprVar("n"))
                    ),
                    None
                )
            in
                let oneShotBody =
                    ExprLet(
                        "x",
                        ExprCall(ExprVar("resume"))(ExprCall(ExprVar("fact"))(ExprVar("u"))(false)(callArgumentsInline))(false)(callArgumentsInline),
                        ExprAdd(ExprVar("x"))(ExprInt(1)),
                        [],
                        None,
                        []
                    )
                in
                    let letRecursivePrefixArmBody = ExprLetRecursive("fact")(factValue)(oneShotBody)([])(None)([])
                    in
                        let handleExpr =
                            ExprHandle(
                                ExprInt(42),
                                [
                                    (Some("State"), "get", [PatternVar("u")], letRecursivePrefixArmBody)
                                ]
                            )
                        in
                            match lowerCoreExpressionWithCompleteContext([])([])([])([])([])([cap])([])(1)(handleExpr) with
                                | CoreLoweringResult { program = Some(program), error = None } ->
                                    let instrs = allProgramInstructions(program)
                                    in
                                        instrs
                                        |> containsStoreCapabilityHandler(1)
                                        |> test.assertEqual(true)
                                        |> (given (_) ->
                                            instrs
                                            |> containsCallClosure
                                            |> test.assertEqual(true))
                                | CoreLoweringResult { error = Some(error) } -> test.fail("let-recursive-prefix one-shot resume lowering failed: " + Ashes.Trait.Show.show(error))
                                | _ -> test.fail("let-recursive-prefix one-shot resume lowering produced no program"))

// An `if` whose branches resolve to different resume shapes — bare tail in one arm, one-shot
// `let`-resume in the other — proves resolveOperationArmBody's ExprIf case handles each branch
// independently (not forcing both down the same path) while reusing the ordinary
// lowerIfThenBranch/finishIfElseBranch join machinery unchanged.
let testHandleIfBranchesWithDifferentResumeShapesLowering unit =
    (let op = CoreCapabilityOperationLayout(name = "get", index = 0)
    in
        let cap =
            CoreCapabilityLayout(
                name = "State",
                index = 0,
                operations = [op]
            )
        in
            let ifArmBody =
                ExprIf(
                    ExprGreaterThan(ExprVar("u"))(ExprInt(0)),
                    ExprCall(ExprVar("resume"))(ExprAdd(ExprVar("u"))(ExprInt(1)))(false)(callArgumentsInline),
                    ExprLet(
                        "y",
                        ExprCall(ExprVar("resume"))(ExprInt(0))(false)(callArgumentsInline),
                        ExprSubtract(ExprVar("y"))(ExprInt(1)),
                        [],
                        None,
                        []
                    )
                )
            in
                let handleExpr =
                    ExprHandle(
                        ExprInt(42),
                        [
                            (Some("State"), "get", [PatternVar("u")], ifArmBody)
                        ]
                    )
                in
                    match lowerCoreExpressionWithCompleteContext([])([])([])([])([])([cap])([])(1)(handleExpr) with
                        | CoreLoweringResult { program = Some(program), error = None } ->
                            let instrs = allProgramInstructions(program)
                            in
                                instrs
                                |> containsAddInt
                                |> test.assertEqual(true)
                                |> (given (_) ->
                                    instrs
                                    |> containsStoreCapabilityHandler(1)
                                    |> test.assertEqual(true))
                                |> (given (_) ->
                                    instrs
                                    |> containsCallClosure
                                    |> test.assertEqual(true))
                        | CoreLoweringResult { error = Some(error) } -> test.fail("if-branch mixed resume shape lowering failed: " + Ashes.Trait.Show.show(error))
                        | _ -> test.fail("if-branch mixed resume shape lowering produced no program"))

// resume in an if's own condition is rejected outright, mirroring stage-0's TryRewriteResumeIf —
// there is no one-shot if-condition-resume shape (unlike match, which has one for its scrutinee).
let testHandleIfConditionResumeIsRejected unit =
    (let op = CoreCapabilityOperationLayout(name = "get", index = 0)
    in
        let cap =
            CoreCapabilityLayout(
                name = "State",
                index = 0,
                operations = [op]
            )
        in
            let ifArmBody =
                ExprIf(
                    ExprGreaterThan(ExprCall(ExprVar("resume"))(ExprVar("u"))(false)(callArgumentsInline))(ExprInt(0)),
                    ExprInt(1),
                    ExprInt(2)
                )
            in
                let handleExpr =
                    ExprHandle(
                        ExprInt(42),
                        [
                            (Some("State"), "get", [PatternVar("u")], ifArmBody)
                        ]
                    )
                in
                    match lowerCoreExpressionWithCompleteContext([])([])([])([])([])([cap])([])(1)(handleExpr) with
                        | CoreLoweringResult { error = Some(UnsupportedOperationArmResume("State", "get")) } -> Unit
                        | CoreLoweringResult { error = Some(other) } -> test.fail("expected UnsupportedOperationArmResume, got " + Ashes.Trait.Show.show(other))
                        | _ -> test.fail("expected resume in an if condition to be rejected"))

// A match whose case bodies resolve to different resume shapes — bare tail in one case, one-shot
// let-resume in another — proves resolveOperationArmMatchArm(s) handles each case independently,
// the same independence testHandleIfBranchesWithDifferentResumeShapesLowering already proved for
// if branches, and reuses the ordinary lowerMatchGuard/finishMatchArm/matchFailLabel machinery for
// the surrounding pattern-test/fail-label/result-join structure.
let testHandleMatchCasesWithDifferentResumeShapesLowering unit =
    (let op = CoreCapabilityOperationLayout(name = "get", index = 0)
    in
        let cap =
            CoreCapabilityLayout(
                name = "State",
                index = 0,
                operations = [op]
            )
        in
            let matchArmBody =
                ExprMatch(
                    ExprVar("u"),
                    [
                        (PatternInt(0), ExprCall(ExprVar("resume"))(ExprInt(1))(false)(callArgumentsInline), None),
                        (PatternWildcard, ExprLet(
                            "y",
                            ExprCall(ExprVar("resume"))(ExprInt(0))(false)(callArgumentsInline),
                            ExprSubtract(ExprVar("y"))(ExprInt(1)),
                            [],
                            None,
                            []
                        ), None)
                    ],
                    None
                )
            in
                let handleExpr =
                    ExprHandle(
                        ExprInt(42),
                        [
                            (Some("State"), "get", [PatternVar("u")], matchArmBody)
                        ]
                    )
                in
                    match lowerCoreExpressionWithCompleteContext([])([])([])([])([])([cap])([])(1)(handleExpr) with
                        | CoreLoweringResult { program = Some(program), error = None } ->
                            let instrs = allProgramInstructions(program)
                            in
                                instrs
                                |> containsStoreCapabilityHandler(1)
                                |> test.assertEqual(true)
                                |> (given (_) ->
                                    instrs
                                    |> containsCallClosure
                                    |> test.assertEqual(true))
                        | CoreLoweringResult { error = Some(error) } -> test.fail("match-case mixed resume shape lowering failed: " + Ashes.Trait.Show.show(error))
                        | _ -> test.fail("match-case mixed resume shape lowering produced no program"))

// resume in a match's scrutinee is rejected outright, since the scrutinee doesn't unwrap to
// exactly a resume call in this arm — this also covers stage-0's distinct one-shot-scrutinee
// shape (`match resume(v) with | ...`), which is not yet ported and rejected the same way as any
// other resume reference in the scrutinee.
let testHandleMatchScrutineeResumeIsRejected unit =
    (let op = CoreCapabilityOperationLayout(name = "get", index = 0)
    in
        let cap =
            CoreCapabilityLayout(
                name = "State",
                index = 0,
                operations = [op]
            )
        in
            let matchArmBody =
                ExprMatch(
                    ExprCall(ExprVar("resume"))(ExprVar("u"))(false)(callArgumentsInline),
                    [
                        (PatternWildcard, ExprInt(1), None)
                    ],
                    None
                )
            in
                let handleExpr =
                    ExprHandle(
                        ExprInt(42),
                        [
                            (Some("State"), "get", [PatternVar("u")], matchArmBody)
                        ]
                    )
                in
                    match lowerCoreExpressionWithCompleteContext([])([])([])([])([])([cap])([])(1)(handleExpr) with
                        | CoreLoweringResult { error = Some(UnsupportedOperationArmResume("State", "get")) } -> Unit
                        | CoreLoweringResult { error = Some(other) } -> test.fail("expected UnsupportedOperationArmResume, got " + Ashes.Trait.Show.show(other))
                        | _ -> test.fail("expected resume in a match scrutinee to be rejected"))

// resume in a match case's guard is rejected outright, mirroring stage-0's
// TryRewriteResumeMatchCases guard check.
let testHandleMatchGuardResumeIsRejected unit =
    (let op = CoreCapabilityOperationLayout(name = "get", index = 0)
    in
        let cap =
            CoreCapabilityLayout(
                name = "State",
                index = 0,
                operations = [op]
            )
        in
            let matchArmBody =
                ExprMatch(
                    ExprVar("u"),
                    [
                        (PatternVar("x"), ExprInt(1), ExprInt(0)
                        |> ExprGreaterThan(ExprCall(ExprVar("resume"))(ExprVar("x"))(false)(callArgumentsInline))
                        |> Some)
                    ],
                    None
                )
            in
                let handleExpr =
                    ExprHandle(
                        ExprInt(42),
                        [
                            (Some("State"), "get", [PatternVar("u")], matchArmBody)
                        ]
                    )
                in
                    match lowerCoreExpressionWithCompleteContext([])([])([])([])([])([cap])([])(1)(handleExpr) with
                        | CoreLoweringResult { error = Some(UnsupportedOperationArmResume("State", "get")) } -> Unit
                        | CoreLoweringResult { error = Some(other) } -> test.fail("expected UnsupportedOperationArmResume, got " + Ashes.Trait.Show.show(other))
                        | _ -> test.fail("expected resume in a match guard to be rejected"))

let testHandleOneShotResumeLowering unit =
    (let op = CoreCapabilityOperationLayout(name = "get", index = 0)
    in
        let cap =
            CoreCapabilityLayout(
                name = "State",
                index = 0,
                operations = [op]
            )
        in
            let oneShotArmBody =
                ExprLet(
                    "x",
                    ExprCall(ExprVar("resume"))(ExprAdd(ExprVar("u"))(ExprInt(1)))(false)(callArgumentsInline),
                    ExprMultiply(ExprVar("x"))(ExprInt(2)),
                    [],
                    None,
                    []
                )
            in
                let handleExpr =
                    ExprHandle(
                        ExprInt(42),
                        [
                            (Some("State"), "get", [PatternVar("u")], oneShotArmBody)
                        ]
                    )
                in
                    match lowerCoreExpressionWithCompleteContext([])([])([])([])([])([cap])([])(1)(handleExpr) with
                        | CoreLoweringResult { program = Some(program), error = None } ->
                            let instrs = allProgramInstructions(program)
                            in
                                instrs
                                |> containsStoreCapabilityHandler(1)
                                |> test.assertEqual(true)
                                |> (given (_) ->
                                    instrs
                                    |> containsCallClosure
                                    |> test.assertEqual(true))
                        | CoreLoweringResult { error = Some(error) } -> test.fail("one-shot resume lowering failed: " + Ashes.Trait.Show.show(error))
                        | _ -> test.fail("one-shot resume lowering produced no program"))

// End-to-end: a perform inside the handled body reaching an operation arm that resumes in
// one-shot-let position. Proves collectCapabilityPost (the perform-site half) runs, not just the
// arm-closure and fold-loop halves testHandleOneShotResumeLowering already covers.
let testHandleOneShotResumeWithPerformLowering unit =
    (let op = CoreCapabilityOperationLayout(name = "get", index = 0)
    in
        let cap =
            CoreCapabilityLayout(
                name = "State",
                index = 0,
                operations = [op]
            )
        in
            let oneShotArmBody =
                ExprLet(
                    "x",
                    ExprCall(ExprVar("resume"))(ExprAdd(ExprVar("u"))(ExprInt(1)))(false)(callArgumentsInline),
                    ExprMultiply(ExprVar("x"))(ExprInt(2)),
                    [],
                    None,
                    []
                )
            in
                let performBody =
                    ExprPerform(
                        ExprCall(
                            ExprQualifiedVar("State")("get"),
                            ExprInt(0),
                            false,
                            callArgumentsInline
                        )
                    )
                in
                    let handleExpr =
                        ExprHandle(
                            performBody,
                            [
                                (Some("State"), "get", [PatternVar("u")], oneShotArmBody)
                            ]
                        )
                    in
                        match lowerCoreExpressionWithCompleteContext([])([])([])([])([])([cap])([])(1)(handleExpr) with
                            | CoreLoweringResult { program = Some(program), error = None } ->
                                program
                                |> allProgramInstructions
                                |> containsAlloc(16)
                                |> test.assertEqual(true)
                            | CoreLoweringResult { error = Some(error) } -> test.fail("one-shot resume with perform lowering failed: " + Ashes.Trait.Show.show(error))
                            | _ -> test.fail("one-shot resume with perform lowering produced no program"))

let runCoreCapabilityLoweringTests unit =
    Unit
    |> testDynamicPerformEmission
    |> testStaticProviderCall
    |> testSplitHandlerArms
    |> testFindCapabilityLayout
    |> testFindCapabilityOperationIndex
    |> testStopCapabilityLowering
    |> testHandleExpressionLowering
    |> testHandleReturnArmLowering
    |> testHandleArmWithoutResumeIsRejected
    |> testHandleLetPrefixBeforeTailResumeLowering
    |> testHandleLetRecursivePrefixBeforeOneShotResumeLowering
    |> testHandleIfBranchesWithDifferentResumeShapesLowering
    |> testHandleIfConditionResumeIsRejected
    |> testHandleMatchCasesWithDifferentResumeShapesLowering
    |> testHandleMatchScrutineeResumeIsRejected
    |> testHandleMatchGuardResumeIsRejected
    |> testHandleOneShotResumeLowering
    |> testHandleOneShotResumeWithPerformLowering
    |> testDynamicPerformViaExpression
