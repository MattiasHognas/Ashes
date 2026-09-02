// Pure-Ashes tests for compile-time evaluation and IR optimization pipeline.

import Ashes.IO
import Ashes.Test as test
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrCompileTimeEval
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOptimizer
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.Types
export (
    value runIrOptimizerTests,
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

let entryInstructions (program: IrProgram) =
    (let entry = program.entryFunction
    in entry.instructions)

let testConstantFolding unit =
    (let fn =
        makeFunction("_start_main")(
            [
                10
                |> LoadConstInt(0)
                |> makeInstruction,
                20
                |> LoadConstInt(1)
                |> makeInstruction,
                1
                |> AddInt(2)(0)
                |> makeInstruction,
                2
                |> LoadConstInt(3)
                |> makeInstruction,
                3
                |> MulInt(4)(2)
                |> makeInstruction,
                makeInstruction(Return(4))
            ]
        )(
            0
        )(
            5
        )(
            false
        )
    in
        let optFn = optimizeIrFunction(fn)
        in
            match optFn.instructions with
                | IrInstruction { instruction = LoadConstInt(4, 60) } :: IrInstruction { instruction = Return(4) } :: [] -> Unit
                | _ -> test.fail("testConstantFolding failed"))

let testIdentityReduction unit =
    (let fn =
        makeFunction("_start_main")(
            [
                0
                |> LoadConstInt(0)
                |> makeInstruction,
                42
                |> LoadConstInt(1)
                |> makeInstruction,
                0
                |> AddInt(2)(1)
                |> makeInstruction,
                1
                |> LoadConstInt(3)
                |> makeInstruction,
                3
                |> MulInt(4)(2)
                |> makeInstruction,
                2
                |> LoadConstInt(5)
                |> makeInstruction,
                5
                |> MulInt(6)(4)
                |> makeInstruction,
                makeInstruction(Return(6))
            ]
        )(
            0
        )(
            7
        )(
            false
        )
    in
        let optFn = optimizeIrFunction(fn)
        in
            match optFn.instructions with
                | IrInstruction { instruction = LoadConstInt(6, 84) } :: IrInstruction { instruction = Return(6) } :: [] -> Unit
                | _ -> test.fail("testIdentityReduction failed"))

let testUnreachableCodeElision unit =
    (let fn =
        makeFunction("_start_main")(
            [
                100
                |> LoadConstInt(0)
                |> makeInstruction,
                makeInstruction(Return(0)),
                999
                |> LoadConstInt(1)
                |> makeInstruction,
                makeInstruction(Return(1))
            ]
        )(
            0
        )(
            2
        )(
            false
        )
    in
        let optFn = optimizeIrFunction(fn)
        in
            match optFn.instructions with
                | IrInstruction { instruction = LoadConstInt(0, 100) } :: IrInstruction { instruction = Return(0) } :: [] -> Unit
                | _ -> test.fail("testUnreachableCodeElision failed"))

let testDeadCodeElision unit =
    (let fn =
        makeFunction("_start_main")(
            [
                123
                |> LoadConstInt(0)
                |> makeInstruction,
                456
                |> LoadConstInt(1)
                |> makeInstruction,
                789
                |> LoadConstInt(2)
                |> makeInstruction,
                makeInstruction(Return(1))
            ]
        )(
            0
        )(
            3
        )(
            false
        )
    in
        let optFn = optimizeIrFunction(fn)
        in
            match optFn.instructions with
                | IrInstruction { instruction = LoadConstInt(1, 456) } :: IrInstruction { instruction = Return(1) } :: [] -> Unit
                | _ -> test.fail("testDeadCodeElision failed"))

let testDevirtualizeClosure unit =
    (let fn =
        makeFunction("_start_main")(
            [
                0
                |> LoadConstInt(0)
                |> makeInstruction,
                false
                |> MakeClosure(1)("helper_target")(0)(0)(false)(false)
                |> makeInstruction,
                5
                |> LoadConstInt(2)
                |> makeInstruction,
                -1
                |> CallClosure(3)(1)(2)
                |> makeInstruction,
                makeInstruction(Return(3))
            ]
        )(
            0
        )(
            4
        )(
            false
        )
    in
        let optFn = optimizeIrFunction(fn)
        in
            match optFn.instructions with
                | IrInstruction { instruction = LoadConstInt(0, 0) } :: IrInstruction { instruction = LoadConstInt(2, 5) } :: IrInstruction { instruction = CallKnown(3, "helper_target", 0, 2, -1, false) } :: IrInstruction { instruction = Return(3) } :: [] -> Unit
                | _ -> test.fail("testDevirtualizeClosure failed"))

let testRedundantArenaBrackets unit =
    (let fn =
        makeFunction("_start_main")(
            [
                false
                |> SaveArenaState(0)(1)
                |> makeInstruction,
                77
                |> LoadConstInt(0)
                |> makeInstruction,
                false
                |> RestoreArenaState(0)(1)(2)
                |> makeInstruction,
                false
                |> ReclaimArenaChunks(1)(2)
                |> makeInstruction,
                makeInstruction(Return(0))
            ]
        )(
            3
        )(
            1
        )(
            false
        )
    in
        let program = makeProgram(fn)([])([])(0)
        in
            let optProg = optimizeIrProgram(program)
            in
                match entryInstructions(optProg) with
                    | IrInstruction { instruction = LoadConstInt(0, 77) } :: IrInstruction { instruction = Return(0) } :: [] -> Unit
                    | _ -> test.fail("testRedundantArenaBrackets failed"))

let testCompileTimeEvaluation unit =
    (let helper =
        makeFunction("add_ten")(
            [
                1
                |> LoadLocal(0)
                |> makeInstruction,
                10
                |> LoadConstInt(1)
                |> makeInstruction,
                1
                |> AddInt(2)(0)
                |> makeInstruction,
                makeInstruction(Return(2))
            ]
        )(
            2
        )(
            3
        )(
            true
        )
    in
        let entry =
            makeFunction("_start_main")(
                [
                    32
                    |> LoadConstInt(0)
                    |> makeInstruction,
                    false
                    |> CallKnown(1)("add_ten")(0)(0)(-1)
                    |> makeInstruction,
                    makeInstruction(Return(1))
                ]
            )(
                0
            )(
                2
            )(
                false
            )
        in
            let program = makeProgram(entry)([helper])([])(0)
            in
                let optProg = optimizeIrProgram(program)
                in
                    match entryInstructions(optProg) with
                        | IrInstruction { instruction = LoadConstInt(1, 42) } :: IrInstruction { instruction = Return(1) } :: [] -> Unit
                        | _ -> test.fail("testCompileTimeEvaluation failed"))

let recursive hasLoadConstInt instructions dest value =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = LoadConstInt(d, v) } :: tail ->
            if d == dest
            then
                if v == value
                then true
                else hasLoadConstInt(tail)(dest)(value)
            else hasLoadConstInt(tail)(dest)(value)
        | _ :: tail -> hasLoadConstInt(tail)(dest)(value)

let recursive hasAddInt instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = AddInt(_, _, _) } :: _ -> true
        | _ :: tail -> hasAddInt(tail)

let recursive hasLoadLocal instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = LoadLocal(_, _) } :: _ -> true
        | _ :: tail -> hasLoadLocal(tail)

let recursive hasJumpIfFalse instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = JumpIfFalse(_, _) } :: _ -> true
        | _ :: tail -> hasJumpIfFalse(tail)

let recursive hasSwitchTag instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = SwitchTag(_, _, _) } :: _ -> true
        | _ :: tail -> hasSwitchTag(tail)

let recursive hasLabel instructions name =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = Label(candidate) } :: tail ->
            if candidate == name
            then true
            else hasLabel(tail)(name)
        | _ :: tail -> hasLabel(tail)(name)

let recursive firstJumpTarget instructions =
    match instructions with
        | [] -> "<none>"
        | IrInstruction { instruction = Jump(candidate) } :: _ -> candidate
        | IrInstruction { instruction = SwitchTag(_, _, _) } :: _ -> "<switch>"
        | _ :: tail -> firstJumpTarget(tail)

let recursive hasJumpTo instructions name =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = Jump(candidate) } :: tail ->
            if candidate == name
            then true
            else hasJumpTo(tail)(name)
        | _ :: tail -> hasJumpTo(tail)(name)

let optimizeInstructions instructions localCount tempCount =
    (let optimized =
        false
        |> makeFunction("_start_main")(instructions)(localCount)(tempCount)
        |> optimizeIrFunction
    in optimized.instructions)

let testKnownTrueBranchDropsElseArm unit =
    (let optimized =
        optimizeInstructions(
            [
                true
                |> LoadConstBool(0)
                |> makeInstruction,
                "else"
                |> JumpIfFalse(0)
                |> makeInstruction,
                1
                |> LoadConstInt(1)
                |> makeInstruction,
                makeInstruction(Return(1)),
                makeInstruction(Label("else")),
                2
                |> LoadConstInt(2)
                |> makeInstruction,
                makeInstruction(Return(2))
            ]
        )(
            0
        )(
            3
        )
    in
        if hasJumpIfFalse(optimized)
        then test.fail("testKnownTrueBranchDropsElseArm: a branch on a known-true condition must be dropped")
        else
            if hasLabel(optimized)("else")
            then test.fail("testKnownTrueBranchDropsElseArm: the orphaned else label must vanish with its body")
            else
                if hasLoadConstInt(optimized)(2)(2)
                then test.fail("testKnownTrueBranchDropsElseArm: the else body survived")
                else Unit)

let testKnownFalseBranchBecomesJump unit =
    (let optimized =
        optimizeInstructions(
            [
                false
                |> LoadConstBool(0)
                |> makeInstruction,
                "else"
                |> JumpIfFalse(0)
                |> makeInstruction,
                1
                |> LoadConstInt(1)
                |> makeInstruction,
                makeInstruction(Return(1)),
                makeInstruction(Label("else")),
                2
                |> LoadConstInt(2)
                |> makeInstruction,
                makeInstruction(Return(2))
            ]
        )(
            0
        )(
            3
        )
    in
        if hasJumpIfFalse(optimized)
        then test.fail("testKnownFalseBranchBecomesJump: a branch on a known-false condition must fold away")
        else
            if hasLoadConstInt(optimized)(1)(1)
            then test.fail("testKnownFalseBranchBecomesJump: the then body after the folded jump must be unreachable")
            else
                if hasLoadConstInt(optimized)(2)(2)
                then Unit
                else test.fail("testKnownFalseBranchBecomesJump: the else body must survive"))

let testKnownSwitchTagBecomesJump unit =
    (let optimized =
        optimizeInstructions(
            [
                1
                |> LoadConstInt(0)
                |> makeInstruction,
                "other"
                |> SwitchTag(0)([IrSwitchCase(tag = 0, label = "zero"), IrSwitchCase(tag = 1, label = "one")])
                |> makeInstruction,
                makeInstruction(Label("zero")),
                10
                |> LoadConstInt(1)
                |> makeInstruction,
                makeInstruction(Return(1)),
                makeInstruction(Label("one")),
                11
                |> LoadConstInt(2)
                |> makeInstruction,
                makeInstruction(Return(2)),
                makeInstruction(Label("other")),
                12
                |> LoadConstInt(3)
                |> makeInstruction,
                makeInstruction(Return(3))
            ]
        )(
            0
        )(
            4
        )
    in
        if hasSwitchTag(optimized)
        then test.fail("testKnownSwitchTagBecomesJump: a switch on a known tag must fold to a jump")
        else
            if hasLabel(optimized)("zero")
            then test.fail("testKnownSwitchTagBecomesJump: the orphaned zero case must vanish")
            else
                if hasLabel(optimized)("other")
                then test.fail("testKnownSwitchTagBecomesJump: the orphaned default case must vanish")
                else Unit)

let testUnreferencedLabelInUnreachableRegionIsDropped unit =
    (let optimized =
        optimizeInstructions(
            [
                1
                |> LoadConstInt(0)
                |> makeInstruction,
                makeInstruction(Return(0)),
                makeInstruction(Label("orphan")),
                2
                |> LoadConstInt(1)
                |> makeInstruction,
                makeInstruction(Return(1)),
                makeInstruction(Label("target")),
                3
                |> LoadConstInt(2)
                |> makeInstruction,
                makeInstruction(Jump("target"))
            ]
        )(
            0
        )(
            3
        )
    in
        if hasLabel(optimized)("orphan")
        then test.fail("testUnreferencedLabelInUnreachableRegionIsDropped: a label nothing branches to cannot re-establish reachability")
        else
            if hasLabel(optimized)("target")
            then Unit
            else test.fail("testUnreferencedLabelInUnreachableRegionIsDropped: a label a branch still targets must survive"))

let testMeetOverPathsAgreeingEdges unit =
    (let optimized =
        optimizeInstructions(
            [
                7
                |> LoadConstInt(0)
                |> makeInstruction,
                0
                |> LoadLocal(1)
                |> makeInstruction,
                "else"
                |> JumpIfFalse(1)
                |> makeInstruction,
                makeInstruction(Jump("join")),
                makeInstruction(Label("else")),
                makeInstruction(Jump("join")),
                makeInstruction(Label("join")),
                1
                |> LoadConstInt(2)
                |> makeInstruction,
                2
                |> AddInt(3)(0)
                |> makeInstruction,
                makeInstruction(Return(3))
            ]
        )(
            1
        )(
            4
        )
    in
        if hasLoadConstInt(optimized)(3)(8)
        then
            if hasAddInt(optimized)
            then test.fail("testMeetOverPathsAgreeingEdges: the folded add survived")
            else Unit
        else test.fail("testMeetOverPathsAgreeingEdges: a fact agreed on by both edges must survive the join"))

let testMeetOverPathsLocalSlotAgreeing unit =
    (let optimized =
        optimizeInstructions(
            [
                1
                |> LoadLocal(0)
                |> makeInstruction,
                "else"
                |> JumpIfFalse(0)
                |> makeInstruction,
                7
                |> LoadConstInt(1)
                |> makeInstruction,
                1
                |> StoreLocal(0)
                |> makeInstruction,
                makeInstruction(Jump("join")),
                makeInstruction(Label("else")),
                7
                |> LoadConstInt(2)
                |> makeInstruction,
                2
                |> StoreLocal(0)
                |> makeInstruction,
                makeInstruction(Label("join")),
                0
                |> LoadLocal(3)
                |> makeInstruction,
                1
                |> LoadConstInt(4)
                |> makeInstruction,
                4
                |> AddInt(5)(3)
                |> makeInstruction,
                makeInstruction(Return(5))
            ]
        )(
            2
        )(
            6
        )
    in
        if hasLoadConstInt(optimized)(5)(8)
        then Unit
        else test.fail("testMeetOverPathsLocalSlotAgreeing: a slot stored the same constant on every edge must fold its load"))

let testMeetOverPathsLocalSlotDisagreeing unit =
    (let optimized =
        optimizeInstructions(
            [
                1
                |> LoadLocal(0)
                |> makeInstruction,
                "else"
                |> JumpIfFalse(0)
                |> makeInstruction,
                7
                |> LoadConstInt(1)
                |> makeInstruction,
                1
                |> StoreLocal(0)
                |> makeInstruction,
                makeInstruction(Jump("join")),
                makeInstruction(Label("else")),
                9
                |> LoadConstInt(2)
                |> makeInstruction,
                2
                |> StoreLocal(0)
                |> makeInstruction,
                makeInstruction(Label("join")),
                0
                |> LoadLocal(3)
                |> makeInstruction,
                1
                |> LoadConstInt(4)
                |> makeInstruction,
                4
                |> AddInt(5)(3)
                |> makeInstruction,
                makeInstruction(Return(5))
            ]
        )(
            2
        )(
            6
        )
    in
        if hasLoadLocal(optimized)
        then
            if hasAddInt(optimized)
            then Unit
            else test.fail("testMeetOverPathsLocalSlotDisagreeing: the add over a disagreeing slot was folded")
        else test.fail("testMeetOverPathsLocalSlotDisagreeing: a slot stored differently on each edge must keep its load"))

let testLocalSlotConstantFolds unit =
    (let optimized =
        optimizeInstructions(
            [
                5
                |> LoadConstInt(0)
                |> makeInstruction,
                0
                |> StoreLocal(0)
                |> makeInstruction,
                0
                |> LoadLocal(1)
                |> makeInstruction,
                2
                |> LoadConstInt(2)
                |> makeInstruction,
                2
                |> AddInt(3)(1)
                |> makeInstruction,
                makeInstruction(Return(3))
            ]
        )(
            1
        )(
            4
        )
    in
        if hasLoadConstInt(optimized)(3)(7)
        then Unit
        else test.fail("testLocalSlotConstantFolds: a load of a slot holding a known constant must fold"))

let testLocalSlotUnknownStoreKills unit =
    (let optimized =
        optimizeInstructions(
            [
                5
                |> LoadConstInt(0)
                |> makeInstruction,
                0
                |> StoreLocal(0)
                |> makeInstruction,
                makeInstruction(LoadProgramArgs(1)),
                1
                |> StoreLocal(0)
                |> makeInstruction,
                0
                |> LoadLocal(2)
                |> makeInstruction,
                2
                |> LoadConstInt(3)
                |> makeInstruction,
                3
                |> AddInt(4)(2)
                |> makeInstruction,
                makeInstruction(Return(4))
            ]
        )(
            1
        )(
            5
        )
    in
        if hasLoadLocal(optimized)
        then Unit
        else test.fail("testLocalSlotUnknownStoreKills: a store of an unknown value must kill the slot's fact"))

let testLoopHeaderClearsFacts unit =
    (let optimized =
        optimizeInstructions(
            [
                3
                |> LoadConstInt(0)
                |> makeInstruction,
                0
                |> StoreLocal(0)
                |> makeInstruction,
                makeInstruction(Label("head")),
                0
                |> LoadLocal(1)
                |> makeInstruction,
                1
                |> LoadConstInt(2)
                |> makeInstruction,
                2
                |> AddInt(3)(1)
                |> makeInstruction,
                3
                |> StoreLocal(0)
                |> makeInstruction,
                makeInstruction(Jump("head"))
            ]
        )(
            1
        )(
            4
        )
    in
        if hasLoadLocal(optimized)
        then
            if hasAddInt(optimized)
            then Unit
            else test.fail("testLoopHeaderClearsFacts: the loop body add was folded")
        else test.fail("testLoopHeaderClearsFacts: a label with an unobserved back edge must clear every fact"))

let recursive hasBorrow instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = Borrow(_, _) } :: _ -> true
        | _ :: tail -> hasBorrow(tail)

let testIdentityCopyErasedAfterReduction unit =
    (let optimized =
        optimizeInstructions(
            [
                0
                |> LoadLocal(0)
                |> makeInstruction,
                0
                |> LoadConstInt(1)
                |> makeInstruction,
                1
                |> AddInt(2)(0)
                |> makeInstruction,
                makeInstruction(Return(2))
            ]
        )(
            1
        )(
            3
        )
    in
        if hasAddInt(optimized)
        then test.fail("testIdentityCopyErasedAfterReduction: x + 0 must reduce to a copy")
        else
            if hasBorrow(optimized)
            then test.fail("testIdentityCopyErasedAfterReduction: the copy introduced by identity reduction must be erased by the second elision")
            else Unit)

let recursive bytesSubTextSource instructions =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = BytesSubText(_target, bytesTemp, _start, _length, _runtimeManaged) } :: _ -> Some(bytesTemp)
        | _ :: tail -> bytesSubTextSource(tail)

// Eliding a single-use Borrow remaps its use for every instruction kind: the borrowed operand of
// a builtin such as BytesSubText must end up naming the borrowed source, never the erased temp.
let testBorrowElisionRemapsBytesOperand unit =
    (let optimized =
        optimizeInstructions(
            [
                "hello world"
                |> LoadConstStr(0)
                |> makeInstruction,
                0
                |> StoreLocal(0)
                |> makeInstruction,
                0
                |> LoadLocal(1)
                |> makeInstruction,
                1
                |> Borrow(2)
                |> makeInstruction,
                6
                |> LoadConstInt(3)
                |> makeInstruction,
                5
                |> LoadConstInt(4)
                |> makeInstruction,
                false
                |> BytesSubText(5)(2)(3)(4)
                |> makeInstruction,
                makeInstruction(Return(5))
            ]
        )(
            1
        )(
            6
        )
    in
        if hasBorrow(optimized)
        then test.fail("testBorrowElisionRemapsBytesOperand: a single-use Borrow must be elided")
        else
            match bytesSubTextSource(optimized) with
                | Some(source) ->
                    if source == 2
                    then test.fail("testBorrowElisionRemapsBytesOperand: the BytesSubText operand still names the erased Borrow temp")
                    else Unit
                | None -> test.fail("testBorrowElisionRemapsBytesOperand: the BytesSubText must survive optimization"))

let recursive hasCallClosure instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = CallClosure(_, _, _, _) } :: _ -> true
        | _ :: tail -> hasCallClosure(tail)

let recursive hasCallKnownTo instructions label =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = CallKnown(_, candidate, _, _, _, _) } :: tail ->
            if candidate == label
            then true
            else hasCallKnownTo(tail)(label)
        | _ :: tail -> hasCallKnownTo(tail)(label)

let recursive hasLoadMemOffset instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = LoadMemOffset(_, _, _) } :: _ -> true
        | _ :: tail -> hasLoadMemOffset(tail)

let noCompileTimeEvalOptions =
    IrOptimizerOptions(
        enableCompileTimeEval = false,
        enableInlining = true,
        enableDeadCodeElision = true,
        enableIdentityReduction = true
    )

let closureReturningFunction label inner =
    makeFunction(label)(
        [
            0
            |> LoadConstInt(0)
            |> makeInstruction,
            false
            |> MakeClosure(1)(inner)(0)(0)(false)(false)
            |> makeInstruction,
            makeInstruction(Return(1))
        ]
    )(
        2
    )(
        2
    )(
        true
    )

let stackClosureReturningFunction label inner =
    makeFunction(label)(
        [
            0
            |> LoadConstInt(0)
            |> makeInstruction,
            false
            |> MakeClosureStack(1)(inner)(0)(0)(false)
            |> makeInstruction,
            makeInstruction(Return(1))
        ]
    )(
        2
    )(
        2
    )(
        true
    )

let constantFunction label value =
    makeFunction(label)([value
    |> LoadConstInt(0)
    |> makeInstruction, makeInstruction(Return(0))])(2)(1)(true)

let curriedCallEntry outer =
    makeFunction("_start_main")(
        [
            0
            |> LoadConstInt(0)
            |> makeInstruction,
            10
            |> LoadConstInt(1)
            |> makeInstruction,
            false
            |> CallKnown(2)(outer)(0)(1)(-1)
            |> makeInstruction,
            32
            |> LoadConstInt(3)
            |> makeInstruction,
            -1
            |> CallClosure(4)(2)(3)
            |> makeInstruction,
            makeInstruction(Return(4))
        ]
    )(
        0
    )(
        5
    )(
        false
    )

let testReturnedClosureCallDevirtualizes unit =
    (let program =
        makeProgram(curriedCallEntry("add_outer"))([closureReturningFunction("add_outer")("add_inner"), constantFunction("add_inner")(42)])([])(0)
    in
        let entry =
            program
            |> optimizeIrProgramWithOptions(noCompileTimeEvalOptions)
            |> entryInstructions
        in
            if hasCallClosure(entry)
            then test.fail("testReturnedClosureCallDevirtualizes: the second application of a curried call must become direct")
            else
                if hasCallKnownTo(entry)("add_inner")
                then
                    if hasLoadMemOffset(entry)
                    then Unit
                    else test.fail("testReturnedClosureCallDevirtualizes: the direct call must read the closure's environment word")
                else test.fail("testReturnedClosureCallDevirtualizes: the direct call must target the returned closure's label"))

let testReturnedStackClosureKeepsIndirectCall unit =
    (let program =
        makeProgram(curriedCallEntry("add_outer"))([stackClosureReturningFunction("add_outer")("add_inner"), constantFunction("add_inner")(42)])([])(0)
    in
        let entry =
            program
            |> optimizeIrProgramWithOptions(noCompileTimeEvalOptions)
            |> entryInstructions
        in
            if hasCallClosure(entry)
            then Unit
            else test.fail("testReturnedStackClosureKeepsIndirectCall: a stack closure's environment dies with its frame, so its label must never be a known returned label"))

let transitiveReturningFunction label callee =
    makeFunction(label)(
        [
            0
            |> LoadConstInt(0)
            |> makeInstruction,
            0
            |> LoadConstInt(1)
            |> makeInstruction,
            false
            |> CallKnown(2)(callee)(0)(1)(-1)
            |> makeInstruction,
            makeInstruction(Return(2))
        ]
    )(
        2
    )(
        3
    )(
        true
    )

let testTransitivelyReturnedClosureCallDevirtualizes unit =
    (let program =
        makeProgram(curriedCallEntry("forward"))(
            [
                transitiveReturningFunction("forward")("build"),
                closureReturningFunction("build")("leaf"),
                constantFunction("leaf")(9)
            ]
        )(
            []
        )(
            0
        )
    in
        let entry =
            program
            |> optimizeIrProgramWithOptions(noCompileTimeEvalOptions)
            |> entryInstructions
        in
            if hasCallKnownTo(entry)("leaf")
            then Unit
            else test.fail("testTransitivelyReturnedClosureCallDevirtualizes: a function returning another proven function's result must resolve in a later fixpoint pass"))

let recursive countGetAdtField instructions =
    match instructions with
        | [] -> 0
        | IrInstruction { instruction = GetAdtField(_, _, _) } :: tail -> 1 + countGetAdtField(tail)
        | _ :: tail -> countGetAdtField(tail)

let recursive countCallKnownTo instructions label =
    match instructions with
        | [] -> 0
        | IrInstruction { instruction = CallKnown(_, candidate, _, _, _, _) } :: tail ->
            if candidate == label
            then 1 + countCallKnownTo(tail)(label)
            else countCallKnownTo(tail)(label)
        | _ :: tail -> countCallKnownTo(tail)(label)

let optimizeArgumentFunction instructions =
    (let optimized =
        true
        |> makeFunction("field_reader")(instructions)(6)(12)
        |> optimizeIrFunction
    in optimized.instructions)

let readFieldTwiceThroughSlot =
    [
        1
        |> LoadLocal(0)
        |> makeInstruction,
        0
        |> GetAdtField(1)(0)
        |> makeInstruction,
        1
        |> StoreLocal(2)
        |> makeInstruction,
        1
        |> LoadLocal(3)
        |> makeInstruction,
        0
        |> GetAdtField(4)(3)
        |> makeInstruction,
        4
        |> AddInt(5)(1)
        |> makeInstruction,
        makeInstruction(Return(5))
    ]

let testDuplicateFieldReadThroughLocalSlotForwards unit =
    (let optimized = optimizeArgumentFunction(readFieldTwiceThroughSlot)
    in
        if countGetAdtField(optimized) == 1
        then Unit
        else test.fail("testDuplicateFieldReadThroughLocalSlotForwards: the second read of the same field of the function's own argument must forward to the first"))

let testFieldReadNotMergedAcrossLabel unit =
    (let optimized =
        optimizeArgumentFunction(
            [
                1
                |> LoadLocal(0)
                |> makeInstruction,
                0
                |> GetAdtField(1)(0)
                |> makeInstruction,
                4
                |> LoadLocal(6)
                |> makeInstruction,
                "next"
                |> JumpIfFalse(6)
                |> makeInstruction,
                makeInstruction(Label("next")),
                1
                |> LoadLocal(3)
                |> makeInstruction,
                0
                |> GetAdtField(4)(3)
                |> makeInstruction,
                4
                |> AddInt(5)(1)
                |> makeInstruction,
                makeInstruction(Return(5))
            ]
        )
    in
        if countGetAdtField(optimized) == 2
        then Unit
        else test.fail("testFieldReadNotMergedAcrossLabel: a label starts a new block, so no fact may cross it"))

let testFieldReadNotMergedAcrossAliasingWrite unit =
    (let optimized =
        optimizeArgumentFunction(
            [
                1
                |> LoadLocal(0)
                |> makeInstruction,
                0
                |> GetAdtField(1)(0)
                |> makeInstruction,
                0
                |> LoadLocal(6)
                |> makeInstruction,
                1
                |> SetAdtField(6)(0)
                |> makeInstruction,
                1
                |> LoadLocal(3)
                |> makeInstruction,
                0
                |> GetAdtField(4)(3)
                |> makeInstruction,
                4
                |> AddInt(5)(1)
                |> makeInstruction,
                makeInstruction(Return(5))
            ]
        )
    in
        if countGetAdtField(optimized) == 2
        then Unit
        else test.fail("testFieldReadNotMergedAcrossAliasingWrite: a write through a pointer that may alias the read must invalidate the cache"))

let testFieldReadMergedAcrossArenaBracket unit =
    (let optimized =
        optimizeArgumentFunction(
            [
                1
                |> LoadLocal(0)
                |> makeInstruction,
                0
                |> GetAdtField(1)(0)
                |> makeInstruction,
                false
                |> SaveArenaState(2)(3)
                |> makeInstruction,
                false
                |> RestoreArenaState(2)(3)(4)
                |> makeInstruction,
                false
                |> ReclaimArenaChunks(3)(4)
                |> makeInstruction,
                1
                |> LoadLocal(3)
                |> makeInstruction,
                0
                |> GetAdtField(4)(3)
                |> makeInstruction,
                4
                |> AddInt(5)(1)
                |> makeInstruction,
                makeInstruction(Return(5))
            ]
        )
    in
        if countGetAdtField(optimized) == 1
        then Unit
        else test.fail("testFieldReadMergedAcrossArenaBracket: arena bookkeeping moves a cursor and never writes through a pointer, so it must not invalidate the cache"))

let pureDoublingFunction label =
    makeFunction(label)(
        [
            1
            |> LoadLocal(0)
            |> makeInstruction,
            0
            |> AddInt(1)(0)
            |> makeInstruction,
            makeInstruction(Return(1))
        ]
    )(
        2
    )(
        2
    )(
        true
    )

let callTwiceEntry callee =
    makeFunction("_start_main")(
        [
            0
            |> LoadConstInt(0)
            |> makeInstruction,
            21
            |> LoadConstInt(1)
            |> makeInstruction,
            false
            |> CallKnown(2)(callee)(0)(1)(-1)
            |> makeInstruction,
            false
            |> CallKnown(3)(callee)(0)(1)(-1)
            |> makeInstruction,
            3
            |> AddInt(4)(2)
            |> makeInstruction,
            makeInstruction(Return(4))
        ]
    )(
        0
    )(
        5
    )(
        false
    )

let testDuplicatePureKnownCallMerges unit =
    (let entry =
        0
        |> makeProgram(callTwiceEntry("double"))([pureDoublingFunction("double")])([])
        |> optimizeIrProgramWithOptions(noCompileTimeEvalOptions)
        |> entryInstructions
    in
        if countCallKnownTo(entry)("double") == 1
        then Unit
        else test.fail("testDuplicatePureKnownCallMerges: the second call of a proven-pure function with the same operands must forward to the first"))

let testDuplicateUnprovenCallKeepsBothCalls unit =
    (let entry =
        0
        |> makeProgram(callTwiceEntry("opaque"))([])([])
        |> optimizeIrProgramWithOptions(noCompileTimeEvalOptions)
        |> entryInstructions
    in
        if countCallKnownTo(entry)("opaque") == 2
        then Unit
        else test.fail("testDuplicateUnprovenCallKeepsBothCalls: a call the purity oracle cannot prove must never be merged"))

let recursive hasNegativeTempReference instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = AddInt(_, left, right) } :: tail ->
            if left < 0
            then true
            else
                if right < 0
                then true
                else hasNegativeTempReference(tail)
        | IrInstruction { instruction = Borrow(_, source) } :: tail ->
            if source < 0
            then true
            else hasNegativeTempReference(tail)
        | IrInstruction { instruction = Return(source) } :: tail ->
            if source < 0
            then true
            else hasNegativeTempReference(tail)
        | _ :: tail -> hasNegativeTempReference(tail)

let recursive returnsTemp instructions temp =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = Return(source) } :: tail ->
            if source == temp
            then true
            else returnsTemp(tail)(temp)
        | _ :: tail -> returnsTemp(tail)(temp)

let testStoreToLoadForwardsThroughFreshRecord unit =
    (let optimized =
        optimizeArgumentFunction(
            [
                1
                |> LoadLocal(0)
                |> makeInstruction,
                false
                |> AllocAdt(1)(0)(2)
                |> makeInstruction,
                0
                |> SetAdtField(1)(0)
                |> makeInstruction,
                0
                |> GetAdtField(2)(1)
                |> makeInstruction,
                2
                |> AddInt(3)(2)
                |> makeInstruction,
                makeInstruction(Return(3))
            ]
        )
    in
        if countGetAdtField(optimized) == 0
        then
            if hasNegativeTempReference(optimized)
            then test.fail("testStoreToLoadForwardsThroughFreshRecord: the forwarded value must be the write's raw source temp, never the env/arg slot sentinel")
            else Unit
        else test.fail("testStoreToLoadForwardsThroughFreshRecord: a read of a field just written through a fresh allocation must forward the stored value"))

let testLaterStoreThroughFreshRecordForwardsTheNewerValue unit =
    (let optimized =
        optimizeInstructions(
            [
                5
                |> LoadConstInt(0)
                |> makeInstruction,
                7
                |> LoadConstInt(1)
                |> makeInstruction,
                false
                |> AllocAdt(2)(0)(1)
                |> makeInstruction,
                0
                |> SetAdtField(2)(0)
                |> makeInstruction,
                0
                |> GetAdtField(3)(2)
                |> makeInstruction,
                1
                |> SetAdtField(2)(0)
                |> makeInstruction,
                0
                |> GetAdtField(4)(2)
                |> makeInstruction,
                makeInstruction(Return(4))
            ]
        )(
            2
        )(
            8
        )
    in
        if countGetAdtField(optimized) == 0
        then
            if returnsTemp(optimized)(1)
            then Unit
            else test.fail("testLaterStoreThroughFreshRecordForwardsTheNewerValue: the read after the second write must forward the second value")
        else test.fail("testLaterStoreThroughFreshRecordForwardsTheNewerValue: both reads of the fresh record must forward"))

let testStoreThroughUnknownPointerDoesNotForward unit =
    (let optimized =
        optimizeArgumentFunction(
            [
                1
                |> LoadLocal(0)
                |> makeInstruction,
                0
                |> LoadLocal(1)
                |> makeInstruction,
                0
                |> SetAdtField(1)(0)
                |> makeInstruction,
                0
                |> GetAdtField(2)(1)
                |> makeInstruction,
                2
                |> AddInt(3)(2)
                |> makeInstruction,
                makeInstruction(Return(3))
            ]
        )
    in
        if countGetAdtField(optimized) == 1
        then Unit
        else test.fail("testStoreThroughUnknownPointerDoesNotForward: a write through a pointer not allocated in this block may alias anything, so its read must stay a load"))

let recursive hasAllocStack instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = AllocStack(_, _) } :: _ -> true
        | _ :: tail -> hasAllocStack(tail)

let recursive hasLoadEnv instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = LoadEnv(_, _) } :: _ -> true
        | _ :: tail -> hasLoadEnv(tail)

let recursive hasLoadLocalOfSlot instructions slot =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = LoadLocal(_, candidate) } :: tail ->
            if candidate == slot
            then true
            else hasLoadLocalOfSlot(tail)(slot)
        | _ :: tail -> hasLoadLocalOfSlot(tail)(slot)

let recursive findFunction (functions: List(IrFunction)) label =
    match functions with
        | [] -> None
        | (IrFunction { label = candidate } as fn) :: tail ->
            if candidate == label
            then Some(fn)
            else findFunction(tail)(label)

let recursive callKnownEnvTemp instructions label =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = CallKnown(_, candidate, envTemp, _, _, _) } :: tail ->
            if candidate == label
            then Some(envTemp)
            else callKnownEnvTemp(tail)(label)
        | _ :: tail -> callKnownEnvTemp(tail)(label)

let captureAddingCallee label =
    makeFunction(label)(
        [
            0
            |> LoadEnv(0)
            |> makeInstruction,
            1
            |> LoadLocal(1)
            |> makeInstruction,
            1
            |> AddInt(2)(0)
            |> makeInstruction,
            makeInstruction(Return(2))
        ]
    )(
        2
    )(
        3
    )(
        true
    )

let rawEnvReadingCallee label =
    makeFunction(label)(
        [
            0
            |> LoadEnv(0)
            |> makeInstruction,
            0
            |> LoadLocal(1)
            |> makeInstruction,
            1
            |> AddInt(2)(0)
            |> makeInstruction,
            makeInstruction(Return(2))
        ]
    )(
        2
    )(
        3
    )(
        true
    )

let singleCaptureEntry callee envSize extraRead =
    makeFunction("_start_main")(
        Ashes.Collection.List.append(
            [
                7
                |> LoadConstInt(0)
                |> makeInstruction,
                envSize
                |> AllocStack(1)
                |> makeInstruction,
                0
                |> StoreMemOffset(1)(0)
                |> makeInstruction,
                35
                |> LoadConstInt(2)
                |> makeInstruction
            ]
        )(
            Ashes.Collection.List.append(
                if extraRead
                then
                    [0
                    |> LoadMemOffset(5)(1)
                    |> makeInstruction]
                else []
            )(
                [
                    true
                    |> CallKnown(3)(callee)(1)(2)(-1)
                    |> makeInstruction,
                    makeInstruction(Return(3))
                ]
            )
        )
    )(
        0
    )(
        6
    )(
        false
    )

let optimizeSingleCaptureProgram callee entry =
    0
    |> makeProgram(entry)([callee])([])
    |> optimizeIrProgramWithOptions(noCompileTimeEvalOptions)

let testSingleCaptureStackClosureScalarizes unit =
    (let optimized =
        false
        |> singleCaptureEntry("adder")(8)
        |> optimizeSingleCaptureProgram(captureAddingCallee("adder"))
    in
        let entry = entryInstructions(optimized)
        in
            if hasAllocStack(entry)
            then test.fail("testSingleCaptureStackClosureScalarizes: the environment allocation must disappear")
            else
                match callKnownEnvTemp(entry)("adder__scalarenv0") with
                    | Some(0) ->
                        match findFunction(optimized.functions)("adder__scalarenv0") with
                            | None -> test.fail("testSingleCaptureStackClosureScalarizes: the generated variant must be appended to the program")
                            | Some(variant) ->
                                if hasLoadEnv(variant.instructions)
                                then test.fail("testSingleCaptureStackClosureScalarizes: the variant must read the capture as a raw parameter, not through LoadEnv")
                                else
                                    if hasLoadLocalOfSlot(variant.instructions)(0)
                                    then
                                        match findFunction(optimized.functions)("adder") with
                                            | Some(original) ->
                                                if hasLoadEnv(original.instructions)
                                                then Unit
                                                else test.fail("testSingleCaptureStackClosureScalarizes: the original callee must be left untouched")
                                            | None -> test.fail("testSingleCaptureStackClosureScalarizes: the original callee must remain in the program")
                                    else test.fail("testSingleCaptureStackClosureScalarizes: the variant must read slot 0 directly")
                    | _ -> test.fail("testSingleCaptureStackClosureScalarizes: the call must target the variant and pass the captured word as its env argument"))

let testCalleeReadingEnvPointerRawKeepsEnvironment unit =
    (let optimized =
        false
        |> singleCaptureEntry("adder")(8)
        |> optimizeSingleCaptureProgram(rawEnvReadingCallee("adder"))
    in
        if optimized
        |> entryInstructions
        |> hasAllocStack
        then Unit
        else test.fail("testCalleeReadingEnvPointerRawKeepsEnvironment: a callee that reads slot 0 as a raw pointer must keep its environment"))

let testTwoWordEnvironmentKeepsEnvironment unit =
    (let optimized =
        false
        |> singleCaptureEntry("adder")(16)
        |> optimizeSingleCaptureProgram(captureAddingCallee("adder"))
    in
        if optimized
        |> entryInstructions
        |> hasAllocStack
        then Unit
        else test.fail("testTwoWordEnvironmentKeepsEnvironment: only a one-word environment is scalarized here"))

let testEnvironmentReadElsewhereKeepsEnvironment unit =
    (let optimized =
        true
        |> singleCaptureEntry("adder")(8)
        |> optimizeSingleCaptureProgram(captureAddingCallee("adder"))
    in
        if optimized
        |> entryInstructions
        |> hasAllocStack
        then Unit
        else test.fail("testEnvironmentReadElsewhereKeepsEnvironment: an environment read anywhere but its store and its call must be kept"))

let recursive hasLoadArgumentOwnership instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = LoadArgumentOwnership(_) } :: _ -> true
        | _ :: tail -> hasLoadArgumentOwnership(tail)

let recursive hasCleanupResource instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = CleanupResource(_, _, _) } :: _ -> true
        | _ :: tail -> hasCleanupResource(tail)

let recursive hasStoreLocal instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = StoreLocal(_, _) } :: _ -> true
        | _ :: tail -> hasStoreLocal(tail)

let recursive callKnownFlagTemp instructions label =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = CallKnown(_, candidate, _, _, flagTemp, _) } :: tail ->
            if candidate == label
            then Some(flagTemp)
            else callKnownFlagTemp(tail)(label)
        | _ :: tail -> callKnownFlagTemp(tail)(label)

let twoCaptureAddingCallee label =
    makeFunction(label)(
        [
            0
            |> LoadEnv(0)
            |> makeInstruction,
            1
            |> LoadEnv(1)
            |> makeInstruction,
            1
            |> AddInt(2)(0)
            |> makeInstruction,
            1
            |> LoadLocal(3)
            |> makeInstruction,
            3
            |> AddInt(4)(2)
            |> makeInstruction,
            makeInstruction(Return(4))
        ]
    )(
        2
    )(
        5
    )(
        true
    )

let flagReadingTwoCaptureCallee label =
    makeFunction(label)(
        [
            0
            |> LoadEnv(0)
            |> makeInstruction,
            1
            |> LoadEnv(1)
            |> makeInstruction,
            makeInstruction(LoadArgumentOwnership(2)),
            1
            |> AddInt(3)(0)
            |> makeInstruction,
            makeInstruction(Return(3))
        ]
    )(
        2
    )(
        4
    )(
        true
    )

let twoCaptureEntry callee flagTemp =
    makeFunction("_start_main")(
        [
            7
            |> LoadConstInt(0)
            |> makeInstruction,
            9
            |> LoadConstInt(1)
            |> makeInstruction,
            16
            |> AllocStack(2)
            |> makeInstruction,
            0
            |> StoreMemOffset(2)(0)
            |> makeInstruction,
            1
            |> StoreMemOffset(2)(8)
            |> makeInstruction,
            35
            |> LoadConstInt(3)
            |> makeInstruction,
            true
            |> CallKnown(4)(callee)(2)(3)(flagTemp)
            |> makeInstruction,
            makeInstruction(Return(4))
        ]
    )(
        0
    )(
        6
    )(
        false
    )

let testTwoCaptureStackClosureScalarizes unit =
    (let optimized =
        -1
        |> twoCaptureEntry("adder2")
        |> optimizeSingleCaptureProgram(twoCaptureAddingCallee("adder2"))
    in
        let entry = entryInstructions(optimized)
        in
            if hasAllocStack(entry)
            then test.fail("testTwoCaptureStackClosureScalarizes: the two-word environment allocation must disappear")
            else
                match (callKnownEnvTemp(entry)("adder2__scalarenv0"), callKnownFlagTemp(entry)("adder2__scalarenv0")) with
                    | (Some(0), Some(1)) ->
                        match findFunction(optimized.functions)("adder2__scalarenv0") with
                            | None -> test.fail("testTwoCaptureStackClosureScalarizes: the generated variant must be appended to the program")
                            | Some(variant) ->
                                if hasLoadEnv(variant.instructions)
                                then test.fail("testTwoCaptureStackClosureScalarizes: the variant must not read its environment")
                                else
                                    if hasLoadArgumentOwnership(variant.instructions)
                                    then Unit
                                    else test.fail("testTwoCaptureStackClosureScalarizes: the variant must read the second capture through the flag word")
                    | _ -> test.fail("testTwoCaptureStackClosureScalarizes: the call must pass the first capture as env and the second as the flag word"))

let testTwoCaptureCallWithOwnershipFlagKeepsEnvironment unit =
    (let optimized =
        3
        |> twoCaptureEntry("adder2")
        |> optimizeSingleCaptureProgram(twoCaptureAddingCallee("adder2"))
    in
        if optimized
        |> entryInstructions
        |> hasAllocStack
        then Unit
        else test.fail("testTwoCaptureCallWithOwnershipFlagKeepsEnvironment: a call that already passes an ownership flag has no free word for a second capture"))

let testFlagReadingCalleeKeepsTwoCaptureEnvironment unit =
    (let optimized =
        -1
        |> twoCaptureEntry("adder2")
        |> optimizeSingleCaptureProgram(flagReadingTwoCaptureCallee("adder2"))
    in
        if optimized
        |> entryInstructions
        |> hasAllocStack
        then Unit
        else test.fail("testFlagReadingCalleeKeepsTwoCaptureEnvironment: a callee that reads the ownership flag cannot receive a capture in it"))

let letBoundHelperInstructions =
    [
        0
        |> LoadConstInt(0)
        |> makeInstruction,
        true
        |> MakeClosureStack(1)("helper")(0)(0)(false)
        |> makeInstruction,
        1
        |> StoreLocal(2)
        |> makeInstruction,
        2
        |> LoadLocal(2)
        |> makeInstruction,
        5
        |> LoadConstInt(3)
        |> makeInstruction,
        -1
        |> CallClosure(4)(2)(3)
        |> makeInstruction,
        2
        |> LoadLocal(5)
        |> makeInstruction,
        None
        |> CleanupResource(5)("Function")
        |> makeInstruction,
        makeInstruction(Return(4))
    ]

let testLetBoundHelperCallDevirtualizesThroughItsSlot unit =
    (let optimized = optimizeInstructions(letBoundHelperInstructions)(3)(6)
    in
        if hasCallClosure(optimized)
        then test.fail("testLetBoundHelperCallDevirtualizesThroughItsSlot: a call through a single-store slot must resolve to its closure construction")
        else
            if hasCallKnownTo(optimized)("helper")
            then
                if hasCleanupResource(optimized)
                then test.fail("testLetBoundHelperCallDevirtualizesThroughItsSlot: the scope-exit cleanup of a dropper-free stack closure is a runtime no-op and must go")
                else
                    if hasStoreLocal(optimized)
                    then test.fail("testLetBoundHelperCallDevirtualizesThroughItsSlot: once every load is gone the slot store must die in the dead-code sweep")
                    else Unit
            else test.fail("testLetBoundHelperCallDevirtualizesThroughItsSlot: the direct call must target the helper's label"))

let testLetBoundHelperWithDropperKeepsCleanup unit =
    (let optimized =
        optimizeInstructions(
            [
                0
                |> LoadConstInt(0)
                |> makeInstruction,
                true
                |> MakeClosureStack(1)("helper")(0)(0)(false)
                |> makeInstruction,
                0
                |> StoreMemOffset(1)(24)
                |> makeInstruction,
                1
                |> StoreLocal(2)
                |> makeInstruction,
                2
                |> LoadLocal(2)
                |> makeInstruction,
                5
                |> LoadConstInt(3)
                |> makeInstruction,
                -1
                |> CallClosure(4)(2)(3)
                |> makeInstruction,
                2
                |> LoadLocal(5)
                |> makeInstruction,
                None
                |> CleanupResource(5)("Function")
                |> makeInstruction,
                makeInstruction(Return(4))
            ]
        )(
            3
        )(
            6
        )
    in
        if hasCleanupResource(optimized)
        then Unit
        else test.fail("testLetBoundHelperWithDropperKeepsCleanup: a closure that received a dropper still needs its scope-exit cleanup"))

let twoStoreSlotInstructions =
    [
        0
        |> LoadConstInt(0)
        |> makeInstruction,
        true
        |> MakeClosureStack(1)("helper")(0)(0)(false)
        |> makeInstruction,
        1
        |> StoreLocal(2)
        |> makeInstruction,
        true
        |> MakeClosureStack(6)("other")(0)(0)(false)
        |> makeInstruction,
        6
        |> StoreLocal(2)
        |> makeInstruction,
        2
        |> LoadLocal(2)
        |> makeInstruction,
        5
        |> LoadConstInt(3)
        |> makeInstruction,
        -1
        |> CallClosure(4)(2)(3)
        |> makeInstruction,
        makeInstruction(Return(4))
    ]

let testTwiceStoredSlotKeepsIndirectCall unit =
    (let optimized = optimizeInstructions(twoStoreSlotInstructions)(3)(7)
    in
        if hasCallClosure(optimized)
        then Unit
        else test.fail("testTwiceStoredSlotKeepsIndirectCall: a slot written twice holds no single known closure"))

let recursive hasConcatStr instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = ConcatStr(_, _, _, _) } :: _ -> true
        | _ :: tail -> hasConcatStr(tail)

let recursive concatStrNParts instructions =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = ConcatStrN(_, parts, _) } :: _ -> Some(parts)
        | _ :: tail -> concatStrNParts(tail)

let fourLiterals =
    [
        "str_0"
        |> LoadConstStr(0)
        |> makeInstruction,
        "str_1"
        |> LoadConstStr(1)
        |> makeInstruction,
        "str_2"
        |> LoadConstStr(2)
        |> makeInstruction,
        "str_3"
        |> LoadConstStr(3)
        |> makeInstruction
    ]

let optimizeEntryProgram instructions tempCount =
    0
    |> makeProgram(makeFunction("_start_main")(instructions)(4)(tempCount)(false))([])([])
    |> optimizeIrProgramWithOptions(noCompileTimeEvalOptions)
    |> entryInstructions

let testLeftNestedConcatChainFoldsIntoOneAllocation unit =
    (let optimized =
        optimizeEntryProgram(
            Ashes.Collection.List.append(fourLiterals)(
                [
                    false
                    |> ConcatStr(4)(0)(1)
                    |> makeInstruction,
                    false
                    |> ConcatStr(5)(4)(2)
                    |> makeInstruction,
                    false
                    |> ConcatStr(6)(5)(3)
                    |> makeInstruction,
                    makeInstruction(Return(6))
                ]
            )
        )(
            7
        )
    in
        if hasConcatStr(optimized)
        then test.fail("testLeftNestedConcatChainFoldsIntoOneAllocation: every link of the chain must be absorbed")
        else
            match concatStrNParts(optimized) with
                | Some(parts) ->
                    if parts == [0, 1, 2, 3]
                    then Unit
                    else test.fail("testLeftNestedConcatChainFoldsIntoOneAllocation: the folded concatenation must list every part in order")
                | None -> test.fail("testLeftNestedConcatChainFoldsIntoOneAllocation: the chain must fold into one concatenation"))

let testConcatIntermediateUsedTwiceKeepsChain unit =
    (let optimized =
        optimizeEntryProgram(
            Ashes.Collection.List.append(fourLiterals)(
                [
                    false
                    |> ConcatStr(4)(0)(1)
                    |> makeInstruction,
                    false
                    |> ConcatStr(5)(4)(2)
                    |> makeInstruction,
                    false
                    |> ConcatStr(6)(4)(3)
                    |> makeInstruction,
                    false
                    |> ConcatStr(7)(5)(6)
                    |> makeInstruction,
                    makeInstruction(Return(7))
                ]
            )
        )(
            8
        )
    in
        match concatStrNParts(optimized) with
            | Some(parts) ->
                if parts == [4, 2, 6]
                then
                    if hasConcatStr(optimized)
                    then Unit
                    else test.fail("testConcatIntermediateUsedTwiceKeepsChain: the twice-read intermediate must keep its own concatenation")
                else test.fail("testConcatIntermediateUsedTwiceKeepsChain: the chain must stop at an intermediate read by two links and fold only the single-use link")
            | None -> test.fail("testConcatIntermediateUsedTwiceKeepsChain: the single-use link above the shared intermediate must still fold"))

let testConcatChainAcrossArenaBracketIsDeclined unit =
    (let optimized =
        optimizeEntryProgram(
            [
                "str_0"
                |> LoadConstStr(0)
                |> makeInstruction,
                false
                |> SaveArenaState(0)(1)
                |> makeInstruction,
                false
                |> AllocAdt(7)(0)(1)
                |> makeInstruction,
                "str_1"
                |> LoadConstStr(1)
                |> makeInstruction,
                false
                |> RestoreArenaState(0)(1)(2)
                |> makeInstruction,
                false
                |> ReclaimArenaChunks(1)(2)
                |> makeInstruction,
                "str_2"
                |> LoadConstStr(2)
                |> makeInstruction,
                false
                |> ConcatStr(3)(0)(1)
                |> makeInstruction,
                false
                |> ConcatStr(4)(3)(2)
                |> makeInstruction,
                makeInstruction(Return(4))
            ]
        )(
            8
        )
    in
        match concatStrNParts(optimized) with
            | None -> Unit
            | Some(_) -> test.fail("testConcatChainAcrossArenaBracketIsDeclined: a reclaim between an earlier part and the fold point may free that part's memory"))

let testMixedRuntimeManagedFlagsSplitTheChain unit =
    (let optimized =
        optimizeEntryProgram(
            Ashes.Collection.List.append(fourLiterals)(
                [
                    true
                    |> ConcatStr(4)(0)(1)
                    |> makeInstruction,
                    false
                    |> ConcatStr(5)(4)(2)
                    |> makeInstruction,
                    false
                    |> ConcatStr(6)(5)(3)
                    |> makeInstruction,
                    makeInstruction(Return(6))
                ]
            )
        )(
            7
        )
    in
        match concatStrNParts(optimized) with
            | Some(parts) ->
                if parts == [4, 2, 3]
                then
                    if hasConcatStr(optimized)
                    then Unit
                    else test.fail("testMixedRuntimeManagedFlagsSplitTheChain: the differently flagged link must survive as a plain concatenation")
                else test.fail("testMixedRuntimeManagedFlagsSplitTheChain: the chain must stop at a link with a different runtime-managed flag")
            | None -> test.fail("testMixedRuntimeManagedFlagsSplitTheChain: the outer links must still fold"))

let recursive hasAnyJump instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = Jump(_) } :: _ -> true
        | _ :: tail -> hasAnyJump(tail)

let recursive hasAnyLabel instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = Label(_) } :: _ -> true
        | _ :: tail -> hasAnyLabel(tail)

let recursive casesAllTarget (cases: List(IrSwitchCase)) (label: Str) =
    match cases with
        | [] -> true
        | IrSwitchCase { label = candidate } :: tail ->
            if candidate == label
            then casesAllTarget(tail)(label)
            else false

let recursive switchTargetsOnly instructions label =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = SwitchTag(_, cases, defaultLabel) } :: _ ->
            if defaultLabel == label
            then casesAllTarget(cases)(label)
            else false
        | _ :: tail -> switchTargetsOnly(tail)(label)

let testJumpChainCollapsesToFallthrough unit =
    (let optimized =
        optimizeInstructions(
            [
                makeInstruction(Jump("hop_a")),
                makeInstruction(Label("hop_a")),
                makeInstruction(Jump("hop_b")),
                makeInstruction(Label("hop_b")),
                makeInstruction(Jump("hop_c")),
                makeInstruction(Label("hop_c")),
                1
                |> LoadConstInt(0)
                |> makeInstruction,
                makeInstruction(Return(0))
            ]
        )(
            0
        )(
            1
        )
    in
        if hasAnyJump(optimized)
        then test.fail("testJumpChainCollapsesToFallthrough: every hop of an empty-label chain must be threaded away and the final redundant jump elided")
        else
            if hasAnyLabel(optimized)
            then test.fail("testJumpChainCollapsesToFallthrough: a label nothing branches to must be dropped")
            else
                if hasLoadConstInt(optimized)(0)(1)
                then Unit
                else test.fail("testJumpChainCollapsesToFallthrough: the code the chain led to must survive"))

let testConditionalBranchThreadsThroughEmptyLabel unit =
    (let optimized =
        optimizeInstructions(
            [
                0
                |> LoadLocal(0)
                |> makeInstruction,
                "skip"
                |> JumpIfFalse(0)
                |> makeInstruction,
                1
                |> LoadConstInt(1)
                |> makeInstruction,
                makeInstruction(Jump("end")),
                makeInstruction(Label("skip")),
                makeInstruction(Jump("other")),
                makeInstruction(Label("other")),
                2
                |> LoadConstInt(1)
                |> makeInstruction,
                makeInstruction(Label("end")),
                makeInstruction(Return(1))
            ]
        )(
            1
        )(
            2
        )
    in
        if hasLabel(optimized)("skip")
        then test.fail("testConditionalBranchThreadsThroughEmptyLabel: the empty label must be threaded away")
        else
            if hasLabel(optimized)("other")
            then
                if hasJumpTo(optimized)("end")
                then Unit
                else test.fail("testConditionalBranchThreadsThroughEmptyLabel: the branch past the else code must stay")
            else test.fail("testConditionalBranchThreadsThroughEmptyLabel: the conditional branch must be redirected to the chain's destination, which keeps that label alive"))

let testSwitchCasesThreadThroughEmptyLabels unit =
    (let optimized =
        optimizeInstructions(
            [
                0
                |> LoadLocal(0)
                |> makeInstruction,
                "arm_default"
                |> SwitchTag(0)([IrSwitchCase(tag = 0, label = "arm_0"), IrSwitchCase(tag = 1, label = "arm_1")])
                |> makeInstruction,
                makeInstruction(Label("arm_0")),
                makeInstruction(Jump("merge")),
                makeInstruction(Label("arm_1")),
                makeInstruction(Jump("merge")),
                makeInstruction(Label("arm_default")),
                makeInstruction(Jump("merge")),
                makeInstruction(Label("merge")),
                7
                |> LoadConstInt(1)
                |> makeInstruction,
                makeInstruction(Return(1))
            ]
        )(
            1
        )(
            2
        )
    in
        if switchTargetsOnly(optimized)("merge")
        then
            if hasLabel(optimized)("arm_0")
            then test.fail("testSwitchCasesThreadThroughEmptyLabels: an arm label nothing branches to any more must be dropped")
            else Unit
        else test.fail("testSwitchCasesThreadThroughEmptyLabels: every case and the default must be redirected to the merge label"))

let testJumpToItsOwnNextLabelIsElided unit =
    (let optimized =
        optimizeInstructions(
            [
                1
                |> LoadConstInt(0)
                |> makeInstruction,
                makeInstruction(Jump("next")),
                makeInstruction(Label("next")),
                makeInstruction(Return(0))
            ]
        )(
            0
        )(
            1
        )
    in
        if hasAnyJump(optimized)
        then test.fail("testJumpToItsOwnNextLabelIsElided: a jump reached only by fall-through to its own target is a no-op")
        else
            if hasAnyLabel(optimized)
            then test.fail("testJumpToItsOwnNextLabelIsElided: the label the elided jump targeted has no reference left")
            else Unit)

let testSelfLoopingJumpIsLeftAlone unit =
    (let optimized =
        optimizeInstructions(
            [
                0
                |> LoadLocal(0)
                |> makeInstruction,
                "exit"
                |> JumpIfFalse(0)
                |> makeInstruction,
                makeInstruction(Label("spin")),
                makeInstruction(Jump("spin")),
                makeInstruction(Label("exit")),
                makeInstruction(Return(0))
            ]
        )(
            1
        )(
            1
        )
    in
        if hasJumpTo(optimized)("spin")
        then Unit
        else test.fail("testSelfLoopingJumpIsLeftAlone: a label followed by a jump to itself is not an empty hop"))

let recursive countCallKnown instructions =
    match instructions with
        | [] -> 0
        | IrInstruction { instruction = CallKnown(_, _, _, _, _, _) } :: tail -> 1 + countCallKnown(tail)
        | _ :: tail -> countCallKnown(tail)

let recursive hasHeapAlloc instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = Alloc(_, _, _) } :: _ -> true
        | _ :: tail -> hasHeapAlloc(tail)

let recursive callKnownStackFlag instructions label =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = CallKnown(_, candidate, _, _, _, stackAllocated) } :: tail ->
            if candidate == label
            then Some(stackAllocated)
            else callKnownStackFlag(tail)(label)
        | _ :: tail -> callKnownStackFlag(tail)(label)

let recursive allocStackSizeOf instructions target =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = AllocStack(candidate, size) } :: tail ->
            if candidate == target
            then Some(size)
            else allocStackSizeOf(tail)(target)
        | _ :: tail -> allocStackSizeOf(tail)(target)

let recursive countStoresThrough instructions basePtr =
    match instructions with
        | [] -> 0
        | IrInstruction { instruction = StoreMemOffset(candidate, _, _) } :: tail ->
            if candidate == basePtr
            then 1 + countStoresThrough(tail)(basePtr)
            else countStoresThrough(tail)(basePtr)
        | _ :: tail -> countStoresThrough(tail)(basePtr)

let recursive loadsEnvironmentWordInto instructions target =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = LoadMemOffset(candidate, _, 8) } :: tail ->
            if candidate == target
            then true
            else loadsEnvironmentWordInto(tail)(target)
        | _ :: tail -> loadsEnvironmentWordInto(tail)(target)

// outer calls the closure it captured in its only environment word; inner returns its argument.
let capturedCallingFunction label =
    makeFunction(label)(
        [
            0
            |> LoadEnv(0)
            |> makeInstruction,
            1
            |> LoadLocal(1)
            |> makeInstruction,
            -1
            |> CallClosure(2)(0)(1)
            |> makeInstruction,
            makeInstruction(Return(2))
        ]
    )(
        2
    )(
        3
    )(
        true
    )

let argumentReturningFunction label =
    makeFunction(label)([1
    |> LoadLocal(0)
    |> makeInstruction, makeInstruction(Return(0))])(2)(1)(true)

// The entry creates outer's closure once, storing inner's closure into its single environment
// word, and calls it.
let singleCreationSiteEntry =
    makeFunction("entry")(
        [
            0
            |> LoadConstInt(0)
            |> makeInstruction,
            false
            |> MakeClosure(1)("inner")(0)(0)(false)(false)
            |> makeInstruction,
            8
            |> AllocStack(2)
            |> makeInstruction,
            1
            |> StoreMemOffset(2)(0)
            |> makeInstruction,
            false
            |> MakeClosureStack(3)("outer")(2)(8)(false)
            |> makeInstruction,
            1
            |> LoadLocal(4)
            |> makeInstruction,
            -1
            |> CallClosure(5)(3)(4)
            |> makeInstruction,
            makeInstruction(Return(5))
        ]
    )(
        2
    )(
        6
    )(
        true
    )

let testCapturedClosureCallDevirtualizes unit =
    (let optimized =
        0
        |> makeProgram(singleCreationSiteEntry)([capturedCallingFunction("outer"), argumentReturningFunction("inner")])([])
        |> optimizeIrProgramWithOptions(noCompileTimeEvalOptions)
    in
        match findFunction(optimized.functions)("outer") with
            | None -> test.fail("testCapturedClosureCallDevirtualizes: outer must remain in the program")
            | Some(outer) ->
                if hasCallClosure(outer.instructions)
                then test.fail("testCapturedClosureCallDevirtualizes: the call through the captured closure must become direct")
                else
                    if countCallKnownTo(outer.instructions)("inner") == 1
                    then
                        match callKnownEnvTemp(outer.instructions)("inner") with
                            | Some(envTemp) ->
                                if loadsEnvironmentWordInto(outer.instructions)(envTemp)
                                then Unit
                                else test.fail("testCapturedClosureCallDevirtualizes: the direct call must read the captured closure object's environment word")
                            | None -> test.fail("testCapturedClosureCallDevirtualizes: the direct call must target inner")
                    else test.fail("testCapturedClosureCallDevirtualizes: exactly one direct call to inner is expected"))

// Two creation sites store different closures into the same word of outer: the call inside outer
// must stay indirect.
let disagreeingCreationSitesEntry =
    makeFunction("entry")(
        [
            0
            |> LoadConstInt(0)
            |> makeInstruction,
            false
            |> MakeClosure(1)("inner")(0)(0)(false)(false)
            |> makeInstruction,
            8
            |> AllocStack(2)
            |> makeInstruction,
            1
            |> StoreMemOffset(2)(0)
            |> makeInstruction,
            false
            |> MakeClosureStack(3)("outer")(2)(8)(false)
            |> makeInstruction,
            false
            |> MakeClosure(4)("other")(0)(0)(false)(false)
            |> makeInstruction,
            8
            |> AllocStack(5)
            |> makeInstruction,
            4
            |> StoreMemOffset(5)(0)
            |> makeInstruction,
            false
            |> MakeClosureStack(6)("outer")(5)(8)(false)
            |> makeInstruction,
            1
            |> LoadLocal(7)
            |> makeInstruction,
            -1
            |> CallClosure(8)(3)(7)
            |> makeInstruction,
            -1
            |> CallClosure(9)(6)(7)
            |> makeInstruction,
            9
            |> AddInt(10)(8)
            |> makeInstruction,
            makeInstruction(Return(10))
        ]
    )(
        2
    )(
        11
    )(
        true
    )

let testDisagreeingCreationSitesKeepIndirectCall unit =
    (let optimized =
        0
        |> makeProgram(disagreeingCreationSitesEntry)([capturedCallingFunction("outer"), argumentReturningFunction("inner"), constantFunction("other")(1)])([])
        |> optimizeIrProgramWithOptions(noCompileTimeEvalOptions)
    in
        match findFunction(optimized.functions)("outer") with
            | None -> test.fail("testDisagreeingCreationSitesKeepIndirectCall: outer must remain in the program")
            | Some(outer) ->
                if hasCallClosure(outer.instructions)
                then Unit
                else test.fail("testDisagreeingCreationSitesKeepIndirectCall: a word whose creation sites disagree must not be devirtualized"))

// The entry applies stage(x, 3)(0): the stage copies its two captures and its argument into a
// fresh heap environment for body, so the whole chain can collapse into one direct call over a
// caller-frame environment. Three captures keep the result out of reach of scalarization so the
// assertions see the inlining alone.
let curryingStageEntry =
    makeFunction("entry")(
        [
            1
            |> LoadLocal(0)
            |> makeInstruction,
            16
            |> AllocStack(1)
            |> makeInstruction,
            0
            |> StoreMemOffset(1)(0)
            |> makeInstruction,
            3
            |> LoadConstInt(7)
            |> makeInstruction,
            7
            |> StoreMemOffset(1)(8)
            |> makeInstruction,
            2
            |> LoadConstInt(2)
            |> makeInstruction,
            true
            |> CallKnown(3)("stage")(1)(2)(-1)
            |> makeInstruction,
            8
            |> LoadMemOffset(4)(3)
            |> makeInstruction,
            0
            |> LoadConstInt(5)
            |> makeInstruction,
            false
            |> CallKnown(6)("body")(4)(5)(-1)
            |> makeInstruction,
            makeInstruction(Return(6))
        ]
    )(
        2
    )(
        8
    )(
        true
    )

// A stage that only copies, or one that also retains its first capture first (which makes it more
// than a pure copy).
let curryingStageFunction retainCapture =
    (let capturedTemp =
        if retainCapture
        then 6
        else 1
    in
        let retain =
            if retainCapture
            then
                [false
                |> RcDup(6)(1)(true)
                |> makeInstruction]
            else []
        in
            makeFunction("stage")(
                Ashes.Collection.List.append(
                    [
                        false
                        |> Alloc(0)(24)
                        |> makeInstruction,
                        0
                        |> LoadEnv(1)
                        |> makeInstruction
                    ]
                )(
                    Ashes.Collection.List.append(retain)(
                        [
                            capturedTemp
                            |> StoreMemOffset(0)(0)
                            |> makeInstruction,
                            1
                            |> LoadEnv(5)
                            |> makeInstruction,
                            5
                            |> StoreMemOffset(0)(8)
                            |> makeInstruction,
                            1
                            |> LoadLocal(2)
                            |> makeInstruction,
                            2
                            |> StoreMemOffset(0)(16)
                            |> makeInstruction,
                            false
                            |> MakeClosure(3)("body")(0)(24)(false)(false)
                            |> makeInstruction,
                            makeInstruction(Return(3))
                        ]
                    )
                )
            )(
                2
            )(
                7
            )(
                true
            ))

let threeCaptureBody =
    makeFunction("body")(
        [
            0
            |> LoadEnv(0)
            |> makeInstruction,
            1
            |> LoadEnv(1)
            |> makeInstruction,
            2
            |> LoadEnv(3)
            |> makeInstruction,
            1
            |> AddInt(2)(0)
            |> makeInstruction,
            3
            |> AddInt(4)(2)
            |> makeInstruction,
            makeInstruction(Return(4))
        ]
    )(
        2
    )(
        5
    )(
        true
    )

let optimizeCurryingStageProgram retainCapture =
    0
    |> makeProgram(curryingStageEntry)([curryingStageFunction(retainCapture), threeCaptureBody])([])
    |> optimizeIrProgramWithOptions(noCompileTimeEvalOptions)

let testPureCurryingStageInlinesIntoStackEnvironment unit =
    (let entry =
        false
        |> optimizeCurryingStageProgram
        |> entryInstructions
    in
        if countCallKnown(entry) != 1
        then test.fail("testPureCurryingStageInlinesIntoStackEnvironment: the stage must not be called at all, leaving only the call of body")
        else
            match callKnownStackFlag(entry)("body") with
                | Some(true) ->
                    match callKnownEnvTemp(entry)("body") with
                        | Some(envTemp) ->
                            if allocStackSizeOf(entry)(envTemp) == Some(24)
                            then
                                if countStoresThrough(entry)(envTemp) == 3
                                then
                                    if hasHeapAlloc(entry)
                                    then test.fail("testPureCurryingStageInlinesIntoStackEnvironment: no heap environment may remain")
                                    else Unit
                                else test.fail("testPureCurryingStageInlinesIntoStackEnvironment: every environment word must be stored by the caller")
                            else test.fail("testPureCurryingStageInlinesIntoStackEnvironment: the caller-frame environment must have the stage's environment size")
                        | None -> test.fail("testPureCurryingStageInlinesIntoStackEnvironment: body must be called directly")
                | _ -> test.fail("testPureCurryingStageInlinesIntoStackEnvironment: the environment must live in the caller's frame"))

// Scalarization may still rename the stage call, so the count covers the stage under any label.
let testRetainingStageIsNotInlined unit =
    (let entry =
        true
        |> optimizeCurryingStageProgram
        |> entryInstructions
    in
        if countCallKnown(entry) != 2
        then test.fail("testRetainingStageIsNotInlined: a stage with a retain is not a pure copy, so its call must remain")
        else
            match callKnownStackFlag(entry)("body") with
                | Some(false) -> Unit
                | _ -> test.fail("testRetainingStageIsNotInlined: body must still receive the stage's heap environment"))

let runIrOptimizerTests unit =
    unit
    |> testConstantFolding
    |> (given (_) -> testCapturedClosureCallDevirtualizes(Unit))
    |> (given (_) -> testDisagreeingCreationSitesKeepIndirectCall(Unit))
    |> (given (_) -> testPureCurryingStageInlinesIntoStackEnvironment(Unit))
    |> (given (_) -> testRetainingStageIsNotInlined(Unit))
    |> (given (_) -> testMeetOverPathsAgreeingEdges(Unit))
    |> (given (_) -> testMeetOverPathsLocalSlotAgreeing(Unit))
    |> (given (_) -> testMeetOverPathsLocalSlotDisagreeing(Unit))
    |> (given (_) -> testLocalSlotConstantFolds(Unit))
    |> (given (_) -> testLocalSlotUnknownStoreKills(Unit))
    |> (given (_) -> testLoopHeaderClearsFacts(Unit))
    |> (given (_) -> testKnownTrueBranchDropsElseArm(Unit))
    |> (given (_) -> testKnownFalseBranchBecomesJump(Unit))
    |> (given (_) -> testKnownSwitchTagBecomesJump(Unit))
    |> (given (_) -> testUnreferencedLabelInUnreachableRegionIsDropped(Unit))
    |> (given (_) -> testIdentityCopyErasedAfterReduction(Unit))
    |> (given (_) -> testReturnedClosureCallDevirtualizes(Unit))
    |> (given (_) -> testReturnedStackClosureKeepsIndirectCall(Unit))
    |> (given (_) -> testTransitivelyReturnedClosureCallDevirtualizes(Unit))
    |> (given (_) -> testDuplicateFieldReadThroughLocalSlotForwards(Unit))
    |> (given (_) -> testFieldReadNotMergedAcrossLabel(Unit))
    |> (given (_) -> testFieldReadNotMergedAcrossAliasingWrite(Unit))
    |> (given (_) -> testFieldReadMergedAcrossArenaBracket(Unit))
    |> (given (_) -> testDuplicatePureKnownCallMerges(Unit))
    |> (given (_) -> testDuplicateUnprovenCallKeepsBothCalls(Unit))
    |> (given (_) -> testStoreToLoadForwardsThroughFreshRecord(Unit))
    |> (given (_) -> testLaterStoreThroughFreshRecordForwardsTheNewerValue(Unit))
    |> (given (_) -> testStoreThroughUnknownPointerDoesNotForward(Unit))
    |> (given (_) -> testSingleCaptureStackClosureScalarizes(Unit))
    |> (given (_) -> testCalleeReadingEnvPointerRawKeepsEnvironment(Unit))
    |> (given (_) -> testTwoWordEnvironmentKeepsEnvironment(Unit))
    |> (given (_) -> testEnvironmentReadElsewhereKeepsEnvironment(Unit))
    |> (given (_) -> testTwoCaptureStackClosureScalarizes(Unit))
    |> (given (_) -> testTwoCaptureCallWithOwnershipFlagKeepsEnvironment(Unit))
    |> (given (_) -> testFlagReadingCalleeKeepsTwoCaptureEnvironment(Unit))
    |> (given (_) -> testLetBoundHelperCallDevirtualizesThroughItsSlot(Unit))
    |> (given (_) -> testLetBoundHelperWithDropperKeepsCleanup(Unit))
    |> (given (_) -> testTwiceStoredSlotKeepsIndirectCall(Unit))
    |> (given (_) -> testLeftNestedConcatChainFoldsIntoOneAllocation(Unit))
    |> (given (_) -> testConcatIntermediateUsedTwiceKeepsChain(Unit))
    |> (given (_) -> testConcatChainAcrossArenaBracketIsDeclined(Unit))
    |> (given (_) -> testMixedRuntimeManagedFlagsSplitTheChain(Unit))
    |> (given (_) -> testJumpChainCollapsesToFallthrough(Unit))
    |> (given (_) -> testConditionalBranchThreadsThroughEmptyLabel(Unit))
    |> (given (_) -> testSwitchCasesThreadThroughEmptyLabels(Unit))
    |> (given (_) -> testJumpToItsOwnNextLabelIsElided(Unit))
    |> (given (_) -> testSelfLoopingJumpIsLeftAlone(Unit))
    |> (given (_) -> testIdentityReduction(Unit))
    |> (given (_) -> testUnreachableCodeElision(Unit))
    |> (given (_) -> testDeadCodeElision(Unit))
    |> (given (_) -> testDevirtualizeClosure(Unit))
    |> (given (_) -> testRedundantArenaBrackets(Unit))
    |> (given (_) -> testCompileTimeEvaluation(Unit))
    |> (given (_) -> testBorrowElisionRemapsBytesOperand(Unit))
    |> (given (_) -> Ashes.IO.print("all self-hosted IR optimizer tests passed"))
