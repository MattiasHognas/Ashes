// Pure-Ashes tests for control-flow precise placement of ordinary-value lifetime markers.

import Ashes.Collection.List.map
import Ashes.Test as test
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrText
import AshesCompiler.Semantics.PerceusLifetimePlacement
export (
    value runPerceusLifetimePlacementTests,
)

let instruction kind = IrInstruction(instruction = kind, location = None)

let anchor slot =
    None
    |> RcDrop(5)("Option")(slot)(false)(false)
    |> instruction

let formatted (placed: PlacedInstructions) = map(formatIrInstruction)(placed.placedInstructions)

let makeFunction instructions placed =
    IrFunction(
        label = "lambda_0",
        instructions = instructions,
        localCount = 3,
        tempCount = 12,
        hasEnvAndArgParams = false,
        coroutine = None,
        localNames = [],
        localTypes = [],
        origin = None,
        lifetimesPlaced = placed
    )

// The anchor and the owner load feeding it disappear; the drop, now naming the definition's source
// temp, lands right after the alias's last use.
let expectAnchorMovesToLastUse unit =
    [
        1
        |> StoreLocal(2)
        |> instruction,
        2
        |> LoadLocal(2)
        |> instruction,
        2
        |> Borrow(3)
        |> instruction,
        3
        |> GetAdtTag(4)
        |> instruction,
        2
        |> LoadLocal(5)
        |> instruction,
        anchor(2),
        0
        |> LoadConstInt(6)
        |> instruction,
        instruction(Return(6))
    ]
    |> (given (instructions) -> placeInstructionLifetimes(instructions)(12))
    |> formatted
    |> test.assertEqual([
        "    StoreLocal            Slot=2 Source=1",
        "    LoadLocal             Target=2 Slot=2",
        "    Borrow                Target=3 SourceTemp=2",
        "    GetAdtTag             Target=4 Ptr=3",
        "    RcDrop                SourceTemp=1 TypeName=Option OwnerSlot=2",
        "    LoadConstInt          Target=6 Value=0",
        "    Return                Source=6"
    ])

// An owner that is never read is released right after its definition.
let expectUnusedOwnerDropsAtDefinition unit =
    [
        1
        |> StoreLocal(2)
        |> instruction,
        0
        |> LoadConstInt(6)
        |> instruction,
        2
        |> LoadLocal(5)
        |> instruction,
        anchor(2),
        instruction(Return(6))
    ]
    |> (given (instructions) -> placeInstructionLifetimes(instructions)(12))
    |> formatted
    |> test.assertEqual([
        "    StoreLocal            Slot=2 Source=1",
        "    RcDrop                SourceTemp=1 TypeName=Option OwnerSlot=2",
        "    LoadConstInt          Target=6 Value=0",
        "    Return                Source=6"
    ])

// A branch that reads the owner releases it after that read; the branch that never reads it
// releases it at its entry, since it is reached from a block where the owner was still live.
let expectLiveBranchPredecessorGetsEntryDrop unit =
    [
        1
        |> StoreLocal(2)
        |> instruction,
        1
        |> LoadConstInt(7)
        |> instruction,
        "else_0"
        |> JumpIfFalse(7)
        |> instruction,
        2
        |> LoadLocal(2)
        |> instruction,
        2
        |> GetAdtTag(4)
        |> instruction,
        instruction(Jump("end_0")),
        instruction(Label("else_0")),
        instruction(Jump("end_0")),
        instruction(Label("end_0")),
        2
        |> LoadLocal(5)
        |> instruction,
        anchor(2),
        0
        |> LoadConstInt(6)
        |> instruction,
        instruction(Return(6))
    ]
    |> (given (instructions) -> placeInstructionLifetimes(instructions)(12))
    |> formatted
    |> test.assertEqual([
        "    StoreLocal            Slot=2 Source=1",
        "    LoadConstInt          Target=7 Value=1",
        "    JumpIfFalse           CondTemp=7 Target=else_0",
        "    LoadLocal             Target=2 Slot=2",
        "    GetAdtTag             Target=4 Ptr=2",
        "    RcDrop                SourceTemp=1 TypeName=Option OwnerSlot=2",
        "    Jump                  Target=end_0",
        "  else_0:",
        "    RcDrop                SourceTemp=1 TypeName=Option OwnerSlot=2",
        "    Jump                  Target=end_0",
        "  end_0:",
        "    LoadConstInt          Target=6 Value=0",
        "    Return                Source=6"
    ])

// A closure call that receives an alias while the owner is still read afterwards gets a
// compensating duplicate before the call, on a fresh temp.
let expectBorrowedCallArgumentIsDuplicated unit =
    [
        1
        |> StoreLocal(2)
        |> instruction,
        2
        |> LoadLocal(2)
        |> instruction,
        2
        |> Borrow(3)
        |> instruction,
        10
        |> CallClosure(8)(9)(3)
        |> instruction,
        2
        |> LoadLocal(4)
        |> instruction,
        4
        |> GetAdtTag(11)
        |> instruction,
        2
        |> LoadLocal(5)
        |> instruction,
        anchor(2),
        0
        |> LoadConstInt(6)
        |> instruction,
        instruction(Return(6))
    ]
    |> (given (instructions) -> placeInstructionLifetimes(instructions)(12))
    |> (given (placed) ->
        placed
        |> formatted
        |> test.assertEqual([
            "    StoreLocal            Slot=2 Source=1",
            "    LoadLocal             Target=2 Slot=2",
            "    Borrow                Target=3 SourceTemp=2",
            "    RcDup                 Target=12 SourceTemp=3",
            "    CallClosure           Target=8 ClosureTemp=9 ArgTemp=3 RuntimeManagedArgumentFlagTemp=10",
            "    LoadLocal             Target=4 Slot=2",
            "    GetAdtTag             Target=11 Ptr=4",
            "    RcDrop                SourceTemp=1 TypeName=Option OwnerSlot=2",
            "    LoadConstInt          Target=6 Value=0",
            "    Return                Source=6"
        ])
        |> (given (_) -> test.assertEqual(13)(placed.placedTempCount)))

// A slot with two anchors is left alone: the pass only moves a single lexical anchor.
let expectTwoAnchorsLeaveInstructionsUnchanged unit =
    ((given (instructions) ->
        instructions
        |> (given (original) -> placeInstructionLifetimes(original)(12))
        |> formatted
        |> test.assertEqual(map(formatIrInstruction)(instructions))))([
        1
        |> StoreLocal(2)
        |> instruction,
        2
        |> LoadLocal(2)
        |> instruction,
        anchor(2),
        2
        |> LoadLocal(5)
        |> instruction,
        anchor(2),
        0
        |> LoadConstInt(6)
        |> instruction,
        instruction(Return(6))
    ])

// A function whose lifetimes were already placed is returned unchanged.
let expectPlacedFunctionIsNotPlacedAgain unit =
    [
        1
        |> StoreLocal(2)
        |> instruction,
        2
        |> LoadLocal(5)
        |> instruction,
        anchor(2),
        0
        |> LoadConstInt(6)
        |> instruction,
        instruction(Return(6))
    ]
    |> (given (instructions) ->
        true
        |> makeFunction(instructions)
        |> placeFunctionLifetimes)
    |> (given (function_: IrFunction) -> map(formatIrInstruction)(function_.instructions))
    |> test.assertEqual([
        "    StoreLocal            Slot=2 Source=1",
        "    LoadLocal             Target=5 Slot=2",
        "    RcDrop                SourceTemp=5 TypeName=Option OwnerSlot=2",
        "    LoadConstInt          Target=6 Value=0",
        "    Return                Source=6"
    ])

let expectFunctionPlacementMarksTheFunction unit =
    [
        1
        |> StoreLocal(2)
        |> instruction,
        2
        |> LoadLocal(5)
        |> instruction,
        anchor(2),
        0
        |> LoadConstInt(6)
        |> instruction,
        instruction(Return(6))
    ]
    |> (given (instructions) ->
        false
        |> makeFunction(instructions)
        |> placeFunctionLifetimes)
    |> (given (function_: IrFunction) -> test.assertEqual(true)(function_.lifetimesPlaced))

let reportPlacementSuccess unit = Ashes.IO.print("all self-hosted lifetime placement tests passed")

let runPerceusLifetimePlacementTests unit =
    unit
    |> expectAnchorMovesToLastUse
    |> expectUnusedOwnerDropsAtDefinition
    |> expectLiveBranchPredecessorGetsEntryDrop
    |> expectBorrowedCallArgumentIsDuplicated
    |> expectTwoAnchorsLeaveInstructionsUnchanged
    |> expectPlacedFunctionIsNotPlacedAgain
    |> expectFunctionPlacementMarksTheFunction
    |> reportPlacementSuccess
