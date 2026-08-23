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

let testConstantFolding unit =
    (let fn =
        makeFunction("_start_main")(
            [
                makeInstruction(LoadConstInt(0)(10)),
                makeInstruction(LoadConstInt(1)(20)),
                makeInstruction(AddInt(2)(0)(1)),
                makeInstruction(LoadConstInt(3)(2)),
                makeInstruction(MulInt(4)(2)(3)),
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
                makeInstruction(LoadConstInt(0)(0)),
                makeInstruction(LoadConstInt(1)(42)),
                makeInstruction(AddInt(2)(1)(0)),
                makeInstruction(LoadConstInt(3)(1)),
                makeInstruction(MulInt(4)(2)(3)),
                makeInstruction(LoadConstInt(5)(2)),
                makeInstruction(MulInt(6)(4)(5)),
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
                makeInstruction(LoadConstInt(0)(100)),
                makeInstruction(Return(0)),
                makeInstruction(LoadConstInt(1)(999)),
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
                makeInstruction(LoadConstInt(0)(123)),
                makeInstruction(LoadConstInt(1)(456)),
                makeInstruction(LoadConstInt(2)(789)),
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
                makeInstruction(LoadConstInt(0)(0)),
                makeInstruction(MakeClosure(1)("helper_target")(0)(0)(false)(false)(false)),
                makeInstruction(LoadConstInt(2)(5)),
                makeInstruction(CallClosure(3)(1)(2)(-1)),
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
                makeInstruction(SaveArenaState(0)(1)(false)),
                makeInstruction(LoadConstInt(0)(77)),
                makeInstruction(RestoreArenaState(0)(1)(2)(false)),
                makeInstruction(ReclaimArenaChunks(1)(2)(false)),
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
                match optProg.entryFunction.instructions with
                    | IrInstruction { instruction = LoadConstInt(0, 77) } :: IrInstruction { instruction = Return(0) } :: [] -> Unit
                    | _ -> test.fail("testRedundantArenaBrackets failed"))

let testCompileTimeEvaluation unit =
    (let helper =
        makeFunction("add_ten")(
            [
                makeInstruction(LoadLocal(0)(1)),
                makeInstruction(LoadConstInt(1)(10)),
                makeInstruction(AddInt(2)(0)(1)),
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
                    makeInstruction(LoadConstInt(0)(32)),
                    makeInstruction(CallKnown(1)("add_ten")(0)(0)(-1)(false)),
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
                    match optProg.entryFunction.instructions with
                        | IrInstruction { instruction = LoadConstInt(1, 42) } :: IrInstruction { instruction = Return(1) } :: [] -> Unit
                        | _ -> test.fail("testCompileTimeEvaluation failed"))

let runIrOptimizerTests unit =
    (let _ = testConstantFolding(Unit)
    in
        let _ = testIdentityReduction(Unit)
        in
            let _ = testUnreachableCodeElision(Unit)
            in
                let _ = testDeadCodeElision(Unit)
                in
                    let _ = testDevirtualizeClosure(Unit)
                    in
                        let _ = testRedundantArenaBrackets(Unit)
                        in
                            let _ = testCompileTimeEvaluation(Unit)
                            in
                                let _ = Ashes.IO.print("all self-hosted IR optimizer tests passed")
                                in Unit)
