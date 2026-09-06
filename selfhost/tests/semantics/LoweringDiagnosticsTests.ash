// A call argument that fails to meet its parameter type is located at the call, under the
// argument's context, and renders as the diagnostic stage 0 reports for it.
import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.LoweringDiagnostics
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification
export (
    value runLoweringDiagnosticsTests,
)

let callMismatchSource = "let add (x: Int) (y: Int) = x + y\n\nadd(1)(\"two\")\n"

let tailSelfCallMismatchSource = "let recursive f (xs: List(Int)) (n: Int) =\n    match xs with\n        | [] -> n\n        | _ :: rest -> f(rest)(rest)\n\nAshes.IO.print(f([1])(0))\n"

// A failed comparison names the check and shows the value it got.
let expectEqual label expected actual =
    if expected == actual
    then Unit
    else test.fail(label + ": got " + Ashes.Trait.Show.show(actual))

let loweringError source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } ->
            match lowerCoreProgramWithSource("probe.ash")(source)(program) with
                | CoreLoweringResult { error = Some(error) } -> error
                | _ -> test.fail("expected lowering to reject the program")
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let expectCallArgumentMismatchLocatedAtTheCall unit =
    callMismatchSource
    |> loweringError
    |> expectEqual("expectCallArgumentMismatchLocatedAtTheCall")(
        CoreCallTypeMismatch(
            TypeMismatch(SemInt)(SemString),
            CoreMismatchSite(
                span = Some(TextSpan(start = 35, end = 48)),
                location = Some(IrSourceLocation(filePath = "probe.ash", line = 3, column = 1)),
                argument = Some(CoreCallArgument(ordinal = 2, callee = Some("add")))
            )
        )
    )

let expectTailSelfCallArgumentMismatchLocatedAtTheCall unit =
    tailSelfCallMismatchSource
    |> loweringError
    |> expectEqual("expectTailSelfCallArgumentMismatchLocatedAtTheCall")(
        CoreCallTypeMismatch(
            TypeMismatch(SemInt)(SemList(SemInt)),
            CoreMismatchSite(
                span = Some(TextSpan(start = 102, end = 115)),
                location = Some(IrSourceLocation(filePath = "probe.ash", line = 4, column = 24)),
                argument = Some(CoreCallArgument(ordinal = 2, callee = Some("f")))
            )
        )
    )

let expectMismatchRendersStageZeroDiagnostic unit =
    callMismatchSource
    |> loweringError
    |> loweringErrorDiagnostic
    |> expectEqual("expectMismatchRendersStageZeroDiagnostic")(
        Some(
            DiagnosticEntry(
                span = TextSpan(start = 35, end = 48),
                message = "Type mismatch: Int vs Str. Context: in argument #2 of call to 'add'.",
                code = Some("ASH002")
            )
        )
    )

let expectMismatchLocationIsTheCallLocation unit =
    tailSelfCallMismatchSource
    |> loweringError
    |> loweringErrorLocation
    |> expectEqual("expectMismatchLocationIsTheCallLocation")(Some(IrSourceLocation(filePath = "probe.ash", line = 4, column = 24)))

let expectUnsitedMismatchRendersWithoutContext unit =
    CoreMismatchSite(span = None, location = None, argument = None)
    |> CoreCallTypeMismatch(TypeMismatch(SemBool)(SemFloat))
    |> loweringErrorDiagnostic
    |> expectEqual("expectUnsitedMismatchRendersWithoutContext")(
        Some(
            DiagnosticEntry(
                span = TextSpan(start = 0, end = 0),
                message = "Type mismatch: Bool vs Float.",
                code = Some("ASH002")
            )
        )
    )

let expectTypeTextMatchesStageZero unit =
    Unit
    |> (given (_) ->
        SemList(SemInt)
        |> diagnosticTypeText
        |> expectEqual("expectTypeTextMatchesStageZero")("List<Int>"))
    |> (given (_) ->
        SemTuple([SemInt, SemUInt(8)])
        |> diagnosticTypeText
        |> expectEqual("expectTypeTextMatchesStageZero")("(Int, u8)"))
    |> (given (_) ->
        None
        |> SemFunction(SemFunction(SemVariable(4))(SemVariable(9))(None))(SemList(SemVariable(4)))
        |> diagnosticTypeText
        |> expectEqual("expectTypeTextMatchesStageZero")("(a -> b) -> List<a>"))
    |> (given (_) ->
        [SemString]
        |> SemNamed(1)("Maybe")
        |> diagnosticTypeText
        |> expectEqual("expectTypeTextMatchesStageZero")("Maybe<Str>"))
    |> (given (_) ->
        Some(SemRow([SemCapability("Log")([SemString]), SemCapability("Console")([])])(None))
        |> SemFunction(SemInt)(SemNamed(2)("Unit")([]))
        |> diagnosticTypeText
        |> expectEqual("expectTypeTextMatchesStageZero")("Int -> Unit needs {Console, Log(Str)}"))

let expectPairTextSharesVariableNames unit =
    SemTuple([SemVariable(3), SemVariable(7)])
    |> diagnosticTypePairText(SemFunction(SemVariable(7))(SemList(SemVariable(3)))(None))
    |> expectEqual("expectPairTextSharesVariableNames")(("a -> List<b>", "(b, a)"))

let runLoweringDiagnosticsTests unit =
    unit
    |> expectCallArgumentMismatchLocatedAtTheCall
    |> expectTailSelfCallArgumentMismatchLocatedAtTheCall
    |> expectMismatchRendersStageZeroDiagnostic
    |> expectMismatchLocationIsTheCallLocation
    |> expectUnsitedMismatchRendersWithoutContext
    |> expectTypeTextMatchesStageZero
    |> expectPairTextSharesVariableNames
