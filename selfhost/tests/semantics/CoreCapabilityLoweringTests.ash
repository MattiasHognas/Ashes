import Ashes.Test as test
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

let recursive containsAllocStack size instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = AllocStack(_target, candidate) } :: rest ->
            if size == candidate
            then true
            else containsAllocStack(size)(rest)
        | _ :: rest -> containsAllocStack(size)(rest)

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
                        (Some("State"), "get", [PatternVar("u")], ExprInt(100))
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
                    | _ -> test.fail("handle expression lowering failed"))

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

let runCoreCapabilityLoweringTests unit =
    Unit
    |> testDynamicPerformEmission
    |> testStaticProviderCall
    |> testSplitHandlerArms
    |> testFindCapabilityLayout
    |> testFindCapabilityOperationIndex
    |> testStopCapabilityLowering
    |> testHandleExpressionLowering
    |> testDynamicPerformViaExpression
