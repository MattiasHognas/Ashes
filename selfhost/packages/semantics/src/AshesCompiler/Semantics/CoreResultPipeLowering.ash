// Emits the branches of the Result success pipe `left |?> f`: the left operand's tag is compared
// with `Ok`'s, the success payload is passed through the mapper closure, and the mapped value is
// rewrapped in `Ok` (or stored as it is when the mapper itself returns a `Result`); an `Error`
// operand flows through unchanged. The two branches meet in one local slot read once after the
// join label.
//
// Invariants:
// - Temps and the result local are allocated contiguously from the caller's counters, and the
//   emission reports where they end so the caller can advance its own supply.
// - The labels are the caller's: they are allocated from the lowering's label supply so their
//   numbering matches the surrounding IR.

import Ashes.Collection.List.append
import AshesCompiler.Semantics.IrInstructions
export (
    type CoreResultPipeEmission(..),
    value emitResultPipeBranches,
)

type CoreResultPipeEmission =
    | instructions: List(IrInstructionKind)
    | nextTemp: Int
    | nextLocal: Int
    | resultTemp: Int

let okWrapInstructions (rhsResultTemp: Int) (wrappedTemp: Int) (resultSlot: Int) (okTag: Int) (okTagless: Bool) =
    [
        AllocAdt(wrappedTemp)(okTag)(1)(false)(okTagless),
        SetAdtField(wrappedTemp)(0)(rhsResultTemp)(okTagless),
        StoreLocal(resultSlot)(wrappedTemp)
    ]

let emitResultPipeBranches (leftTemp: Int) (funcTemp: Int) (okTag: Int) (okTagless: Bool) (isFlatMap: Bool) (errorLabel: Str) (endLabel: Str) (startTemp: Int) (startLocal: Int) =
    (let resultSlot = startLocal
    in
        let tagTemp = startTemp
        in
            let expectedOkTagTemp = startTemp + 1
            in
                let isOkTemp = startTemp + 2
                in
                    let payloadTemp = startTemp + 3
                    in
                        let rhsResultTemp = startTemp + 4
                        in
                            let storeInstructions =
                                if isFlatMap
                                then [StoreLocal(resultSlot)(rhsResultTemp)]
                                else okWrapInstructions(rhsResultTemp)(startTemp + 5)(resultSlot)(okTag)(okTagless)
                            in
                                let resultTemp =
                                    if isFlatMap
                                    then startTemp + 5
                                    else startTemp + 6
                                in
                                    let dispatchInstructions =
                                        [
                                            GetAdtTag(tagTemp)(leftTemp),
                                            LoadConstInt(expectedOkTagTemp)(okTag),
                                            CmpIntEq(isOkTemp)(tagTemp)(expectedOkTagTemp),
                                            JumpIfFalse(isOkTemp)(errorLabel),
                                            GetAdtField(payloadTemp)(leftTemp)(0)(okTagless),
                                            CallClosure(rhsResultTemp)(funcTemp)(payloadTemp)(-1)
                                        ]
                                    in
                                        let joinInstructions =
                                            [
                                                Jump(endLabel),
                                                Label(errorLabel),
                                                StoreLocal(resultSlot)(leftTemp),
                                                Label(endLabel),
                                                LoadLocal(resultTemp)(resultSlot)
                                            ]
                                        in
                                            CoreResultPipeEmission(
                                                instructions = joinInstructions
                                                |> append(storeInstructions)
                                                |> append(dispatchInstructions),
                                                nextTemp = resultTemp + 1,
                                                nextLocal = startLocal + 1,
                                                resultTemp = resultTemp
                                            ))
