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

let optimizeInstructions instructions localCount tempCount =
    (let optimized =
        false
        |> makeFunction("_start_main")(instructions)(localCount)(tempCount)
        |> optimizeIrFunction
    in optimized.instructions)

let testMeetOverPathsAgreeingEdges unit =
    (let optimized =
        optimizeInstructions(
            [
                7
                |> LoadConstInt(0)
                |> makeInstruction,
                true
                |> LoadConstBool(1)
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
            0
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
                true
                |> LoadConstBool(0)
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
            1
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
                true
                |> LoadConstBool(0)
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
            1
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

let runIrOptimizerTests unit =
    unit
    |> testConstantFolding
    |> (given (_) -> testMeetOverPathsAgreeingEdges(Unit))
    |> (given (_) -> testMeetOverPathsLocalSlotAgreeing(Unit))
    |> (given (_) -> testMeetOverPathsLocalSlotDisagreeing(Unit))
    |> (given (_) -> testLocalSlotConstantFolds(Unit))
    |> (given (_) -> testLocalSlotUnknownStoreKills(Unit))
    |> (given (_) -> testLoopHeaderClearsFacts(Unit))
    |> (given (_) -> testIdentityReduction(Unit))
    |> (given (_) -> testUnreachableCodeElision(Unit))
    |> (given (_) -> testDeadCodeElision(Unit))
    |> (given (_) -> testDevirtualizeClosure(Unit))
    |> (given (_) -> testRedundantArenaBrackets(Unit))
    |> (given (_) -> testCompileTimeEvaluation(Unit))
    |> (given (_) -> Ashes.IO.print("all self-hosted IR optimizer tests passed"))
