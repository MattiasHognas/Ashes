// Unit tests for OPT-42's reset-safety gate (`AshesCompiler.Semantics.ReuseResetSafety`): the
// verdict stage 0's `IsFullyReusing` reaches on a specialized body before its driving loop is
// allowed to reclaim its arena, plus the two accumulator-shape predicates that decide whether a
// call may be routed to a specialization at all. Exercised against hand-built instruction lists,
// the same idiom as IrValidationTests.ash.

import Ashes.Test as test
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.ReuseDecision
import AshesCompiler.Semantics.ReuseResetSafety
import AshesCompiler.Semantics.Types
export (
    value runReuseResetSafetyTests,
)

let at kind = IrInstruction(instruction = kind, location = None)

let located kind line = IrInstruction(instruction = kind, location = Some(IrSourceLocation(filePath = "spec.ash", line = line, column = 3)))

let verdict (instructions: List(IrInstruction)) =
    match reuseResetSafety(instructions) with
        | ReuseResetSafety { accepted = accepted, reason = reason } -> (accepted, reuseDecisionReasonName(reason))

let rejectionLine (instructions: List(IrInstruction)) =
    match reuseResetSafety(instructions) with
        | ReuseResetSafety { location = Some(IrSourceLocation { line = line }) } -> Some(line)
        | _ -> None

// A body that allocates nothing at all rewrites its accumulator in place and returns only cells
// that already existed, so its loop may reset.
let testEmptyBodyAcceptsReset unit =
    []
    |> verdict
    |> test.assertEqual((true, "NoResetInvalidatingAllocation"))

let testInPlaceRebuildAcceptsReset unit =
    [0
    |> LoadLocal(1)
    |> at, false
    |> GetAdtField(2)(1)(0)
    |> at, false
    |> AllocReusing(3)(0)(2)(1)(false)(false)
    |> at, at(Return(3))]
    |> verdict
    |> test.assertEqual((true, "NoResetInvalidatingAllocation"))

let testFreshAdtAllocationRejectsReset unit =
    [0
    |> LoadLocal(1)
    |> at, false
    |> AllocAdt(2)(0)(2)(false)
    |> at]
    |> verdict
    |> test.assertEqual((false, "FreshAdtAllocation"))

let testFreshAdtAllocationReportsItsLocation unit =
    [0
    |> LoadLocal(1)
    |> at, located(AllocAdt(2)(0)(2)(false)(false))(41)]
    |> rejectionLine
    |> test.assertEqual(Some(41))

let testStackAdtAllocationRejectsReset unit =
    [false
    |> AllocAdtStack(1)(0)(1)
    |> at]
    |> verdict
    |> test.assertEqual((false, "FreshStackAdtAllocation"))

let testStackAllocationRejectsReset unit =
    [16
    |> AllocStack(1)
    |> at]
    |> verdict
    |> test.assertEqual((false, "FreshStackAllocation"))

let testStringConcatenationRejectsReset unit =
    [false
    |> ConcatStr(3)(1)(2)
    |> at]
    |> verdict
    |> test.assertEqual((false, "StringConcatenationAllocation"))

let testArenaCopyOutRejectsReset unit =
    [None
    |> CopyOutArena(2)(1)(24)(false)(ArenaResultBoundary)
    |> at]
    |> verdict
    |> test.assertEqual((false, "ArenaCopyOut"))

let testListCopyOutRejectsReset unit =
    [ArenaResultBoundary
    |> CopyOutList(2)(1)(InlineListHead)(false)
    |> at]
    |> verdict
    |> test.assertEqual((false, "ListCopyOut"))

let testClosureCopyOutRejectsReset unit =
    [ArenaResultBoundary
    |> CopyOutClosure(2)(1)(false)
    |> at]
    |> verdict
    |> test.assertEqual((false, "ClosureCopyOut"))

let testTcoListCellCopyOutRejectsReset unit =
    [ArenaTcoCompaction
    |> CopyOutTcoListCell(2)(1)(InlineListHead)
    |> at]
    |> verdict
    |> test.assertEqual((false, "TcoListCellCopyOut"))

// The recursion scaffolding a specialized body legitimately builds: an environment that only a
// closure reads, and a closure that is only ever called.
let testClosureEnvironmentScaffoldingAcceptsReset unit =
    [false
    |> Alloc(1)(8)
    |> at, 9
    |> StoreMemOffset(1)(0)
    |> at, false
    |> MakeClosure(2)("go")(1)(8)(false)(false)
    |> at, 5
    |> CallClosure(3)(2)(4)
    |> at]
    |> verdict
    |> test.assertEqual((true, "NoResetInvalidatingAllocation"))

let testRawAllocationStoredAsNodeFieldRejectsReset unit =
    [false
    |> Alloc(1)(8)
    |> at, false
    |> SetAdtField(7)(0)(1)
    |> at]
    |> verdict
    |> test.assertEqual((false, "RawAllocationMayEscape"))

let testRawAllocationReturnedRejectsReset unit =
    [false
    |> Alloc(1)(8)
    |> at, at(Return(1))]
    |> verdict
    |> test.assertEqual((false, "RawAllocationMayEscape"))

let testClosurePassedAsArgumentRejectsReset unit =
    [false
    |> Alloc(1)(8)
    |> at, false
    |> MakeClosure(2)("go")(1)(8)(false)(false)
    |> at, 5
    |> CallClosure(3)(9)(2)
    |> at]
    |> verdict
    |> test.assertEqual((false, "ClosureMayEscape"))

let testStackClosurePassedAsArgumentRejectsReset unit =
    [false
    |> Alloc(1)(8)
    |> at, false
    |> MakeClosureStack(2)("go")(1)(8)(false)
    |> at, 5
    |> CallClosure(3)(9)(2)
    |> at]
    |> verdict
    |> test.assertEqual((false, "StackClosureMayEscape"))

// A closure moved through a slot written exactly once and called from its load is still a call
// target; a slot written twice loses that identity and is rejected.
let testClosureThroughSingleStoreSlotAcceptsReset unit =
    [false
    |> Alloc(1)(8)
    |> at, false
    |> MakeClosure(2)("go")(1)(8)(false)(false)
    |> at, 2
    |> StoreLocal(0)
    |> at, 0
    |> LoadLocal(3)
    |> at, 6
    |> CallClosure(4)(3)(5)
    |> at]
    |> verdict
    |> test.assertEqual((true, "NoResetInvalidatingAllocation"))

let testClosureThroughRewrittenSlotRejectsReset unit =
    [false
    |> Alloc(1)(8)
    |> at, false
    |> MakeClosure(2)("go")(1)(8)(false)(false)
    |> at, 2
    |> StoreLocal(0)
    |> at, 9
    |> StoreLocal(0)
    |> at, 0
    |> LoadLocal(3)
    |> at, 6
    |> CallClosure(4)(3)(5)
    |> at]
    |> verdict
    |> test.assertEqual((false, "ClosureMayEscape"))

let testBorrowedAllocationFieldReadAcceptsReset unit =
    [false
    |> Alloc(1)(8)
    |> at, 1
    |> Borrow(2)
    |> at, false
    |> GetAdtField(3)(2)(0)
    |> at]
    |> verdict
    |> test.assertEqual((true, "NoResetInvalidatingAllocation"))

let testBorrowedAllocationReturnedRejectsReset unit =
    [false
    |> Alloc(1)(8)
    |> at, 1
    |> Borrow(2)
    |> at, at(Return(2))]
    |> verdict
    |> test.assertEqual((false, "RawAllocationMayEscape"))

let testAllocationMaterializedToSpaceAcceptsReset unit =
    [false
    |> Alloc(1)(8)
    |> at, 8
    |> CopyOutArenaToSpace(2)(1)
    |> at]
    |> verdict
    |> test.assertEqual((true, "NoResetInvalidatingAllocation"))

let listOfInt unit = SemList(SemInt)

let mapTree (name: Str) = SemNamed(7)(name)([SemInt])

let rebuildsAccumulatorAfter (argumentCount: Int) (functionType: SemanticType) = specializationRebuildsAccumulator(functionType)(argumentCount)

let testRebuildsAccumulatorForListRewriter unit =
    None
    |> SemFunction(SemInt)(SemFunction(listOfInt(Unit))(listOfInt(Unit))(None))
    |> rebuildsAccumulatorAfter(2)
    |> test.assertEqual(true)

let testRebuildsAccumulatorForSameNamedType unit =
    None
    |> SemFunction(mapTree("MapTree"))(mapTree("MapTree"))
    |> rebuildsAccumulatorAfter(1)
    |> test.assertEqual(true)

let testRebuildsAccumulatorDeclinesDifferentNamedType unit =
    None
    |> SemFunction(mapTree("MapTree"))(mapTree("Maybe"))
    |> rebuildsAccumulatorAfter(1)
    |> test.assertEqual(false)

let testRebuildsAccumulatorDeclinesReader unit =
    None
    |> SemFunction(listOfInt(Unit))(SemInt)
    |> rebuildsAccumulatorAfter(1)
    |> test.assertEqual(false)

let testRebuildsAccumulatorDeclinesTooFewParameters unit =
    None
    |> SemFunction(listOfInt(Unit))(listOfInt(Unit))
    |> rebuildsAccumulatorAfter(2)
    |> test.assertEqual(false)

let testRebuildsAccumulatorDeclinesZeroArguments unit =
    None
    |> SemFunction(listOfInt(Unit))(listOfInt(Unit))
    |> rebuildsAccumulatorAfter(0)
    |> test.assertEqual(false)

let testCopyElementListIsFullyPersistent unit =
    SemInt
    |> SemList
    |> accumulatorIsFullyPersistent
    |> test.assertEqual(true)

let testHeapElementListIsNotFullyPersistent unit =
    SemString
    |> SemList
    |> accumulatorIsFullyPersistent
    |> test.assertEqual(false)

let testNamedAccumulatorIsNotFullyPersistent unit =
    "MapTree"
    |> mapTree
    |> accumulatorIsFullyPersistent
    |> test.assertEqual(false)

let reportSuccess unit = Ashes.IO.print("all self-hosted reuse reset safety tests passed")

let runReuseResetSafetyTests unit =
    unit
    |> testEmptyBodyAcceptsReset
    |> testInPlaceRebuildAcceptsReset
    |> testFreshAdtAllocationRejectsReset
    |> testFreshAdtAllocationReportsItsLocation
    |> testStackAdtAllocationRejectsReset
    |> testStackAllocationRejectsReset
    |> testStringConcatenationRejectsReset
    |> testArenaCopyOutRejectsReset
    |> testListCopyOutRejectsReset
    |> testClosureCopyOutRejectsReset
    |> testTcoListCellCopyOutRejectsReset
    |> testClosureEnvironmentScaffoldingAcceptsReset
    |> testRawAllocationStoredAsNodeFieldRejectsReset
    |> testRawAllocationReturnedRejectsReset
    |> testClosurePassedAsArgumentRejectsReset
    |> testStackClosurePassedAsArgumentRejectsReset
    |> testClosureThroughSingleStoreSlotAcceptsReset
    |> testClosureThroughRewrittenSlotRejectsReset
    |> testBorrowedAllocationFieldReadAcceptsReset
    |> testBorrowedAllocationReturnedRejectsReset
    |> testAllocationMaterializedToSpaceAcceptsReset
    |> testRebuildsAccumulatorForListRewriter
    |> testRebuildsAccumulatorForSameNamedType
    |> testRebuildsAccumulatorDeclinesDifferentNamedType
    |> testRebuildsAccumulatorDeclinesReader
    |> testRebuildsAccumulatorDeclinesTooFewParameters
    |> testRebuildsAccumulatorDeclinesZeroArguments
    |> testCopyElementListIsFullyPersistent
    |> testHeapElementListIsNotFullyPersistent
    |> testNamedAccumulatorIsNotFullyPersistent
    |> reportSuccess
