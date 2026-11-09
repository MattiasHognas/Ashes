// The taken-over record (stage 0's `GRC_NO_TAKENOVERRECORD`): a loop or a recursive group member
// hands the list a call returned to a generic helper that conses its elements into tuples of the
// accumulator, frees the list's cells where it leaves the list, and the caller that knows the
// tuples' first field releases the references they took over. Each function is compared with stage
// 0 instruction for instruction, without locations and with labels numbered within the function.
import Ashes.Test as test
import LoweredIrFixtures.loweredFixtureLines
import LoweredIrFixtures.stageZeroFixtureLines
import LoweredIrFixtures.functionLines
import LoweredIrFixtures.withoutLocations
import LoweredIrFixtures.withFunctionLocalLabels
import LoweredIrFixtures.expectSameLines
export (
    value runTakenOverRecordLoweringTests,
)

let expectFunctionMatchesStageZero (fixture: Str) (origin: Str) =
    fixture
    |> loweredFixtureLines
    |> functionLines(origin)
    |> withoutLocations
    |> withFunctionLocalLabels
    |> expectSameLines(fixture + " " + origin)(fixture
    |> stageZeroFixtureLines
    |> functionLines(origin)
    |> withoutLocations
    |> withFunctionLocalLabels)

let recursive withoutLastPart (parts: List(Str)) =
    match parts with
        | [] -> []
        | _last :: [] -> []
        | head :: rest -> head :: withoutLastPart(rest)

// `FuncLabel=__rcdrop_general_33` -> `FuncLabel=__rcdrop_general`: a generated helper's program-wide
// number, which an unrelated difference earlier in the program shifts.
let helperLabelBase (token: Str) =
    if Ashes.Text.startsWith(token)("FuncLabel=__rc")
    then
        "_"
        |> Ashes.Text.split(token)
        |> withoutLastPart
        |> Ashes.Text.join("_")
    else token

let withoutHelperNumbers (lines: List(Str)) =
    Ashes.Collection.List.map(given (line: Str) ->
        " "
        |> Ashes.Text.split(line)
        |> Ashes.Collection.List.map(helperLabelBase)
        |> Ashes.Text.join(" "))(lines)

// The comparison above with generated helpers named without their number: stage 0 specializes a
// generic helper for its elements in a function lowered earlier, which stage 1 does not yet, so
// every later helper's number differs while the compared function's instructions are the same.
let expectFunctionMatchesStageZeroUpToHelperNumbers (fixture: Str) (origin: Str) =
    fixture
    |> loweredFixtureLines
    |> functionLines(origin)
    |> withoutLocations
    |> withFunctionLocalLabels
    |> withoutHelperNumbers
    |> expectSameLines(fixture + " " + origin)(fixture
    |> stageZeroFixtureLines
    |> functionLines(origin)
    |> withoutLocations
    |> withFunctionLocalLabels
    |> withoutHelperNumbers)

// The loop frees the expanded list's cells at its back edge, and the caller releases the
// patterns behind its copy of the result.
let expectLoopSettledByCaller unit =
    Unit
    |> (given (_) -> expectFunctionMatchesStageZero("taken_over_record_loop_settled_by_caller")("[ClosureHelper from expandCases]"))
    |> (given (_) -> expectFunctionMatchesStageZero("taken_over_record_loop_settled_by_caller")("[SourceFunction from probe]"))

// A recursive group member has no back edge: the list's cells are freed at its return.
let expectGroupSettledByCaller unit =
    Unit
    |> (given (_) -> expectFunctionMatchesStageZero("taken_over_record_group_settled_by_caller")("[ClosureHelper from expandCases]"))
    |> (given (_) -> expectFunctionMatchesStageZeroUpToHelperNumbers("taken_over_record_group_settled_by_caller")("[SourceFunction from probe]"))

// A generic function holding the result in a let settles at the let's end.
let expectGenericLetHolder unit = expectFunctionMatchesStageZero("taken_over_record_generic_let_holder")("[ClosureHelper from check]")

// An arm consing a borrowed case tuple onto the accumulator: the record is declined, so the caller
// releases nothing.
let expectBorrowedArmDeclines unit =
    Unit
    |> (given (_) -> expectFunctionMatchesStageZero("taken_over_record_borrowed_arm_declines")("[ClosureHelper from expandCases]"))
    |> (given (_) -> expectFunctionMatchesStageZero("taken_over_record_borrowed_arm_declines")("[SourceFunction from probe]"))

// A let holder whose result returns a pattern out of the value settles nothing.
let expectEscapingLetHolderDeclines unit = expectFunctionMatchesStageZero("taken_over_record_escaping_let_holder_declines")("[ClosureHelper from firstPattern]")

// The own-field record: the caller of a loop returning `Some` of its own retained parameter
// releases the arena `Some`'s field at the end of the match on it.
let expectOwnFieldSettledAtMatchEnd unit =
    Unit |> (given (_) -> expectFunctionMatchesStageZero("loop_result_some_holding_the_loop_parameter")("[ClosureHelper from stageLength]"))

let runTakenOverRecordLoweringTests unit =
    Unit
    |> expectLoopSettledByCaller
    |> expectOwnFieldSettledAtMatchEnd
    |> expectGroupSettledByCaller
    |> expectGenericLetHolder
    |> expectBorrowedArmDeclines
    |> expectEscapingLetHolderDeclines
