// `receiver.field` parses as a qualified name, so a lambda body that reads a record only that way
// mentions its receiver nowhere the free-variable walk would see as a variable. The walk has to
// count the receiver anyway, or the closure captures nothing and lowering the field access finds
// no binding — stage 0's `FreeVarsVisitQualifiedVar` carries the same rule.
import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
export (
    value runQualifiedReceiverCaptureTests,
)

// `facts` is the outer parameter of a curried function and is read only as `facts.consumedTail`,
// inside the inner parameter's lambda, so only the qualified form mentions it.
let capturedReceiverSource = "type Facts =\n    | consumedTail: Str\n    | borrowOnly: Str\n\nlet evaluate facts include =\n    if include\n    then facts.consumedTail\n    else facts.borrowOnly\n\nAshes.IO.print(evaluate(Facts(consumedTail = \"tail\", borrowOnly = \"borrow\"))(true))\n"

let parsedProgram (source: Str) =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let expectCapturedReceiverLowers unit =
    match capturedReceiverSource
    |> parsedProgram
    |> lowerCoreProgram with
        | CoreLoweringResult { program = Some(_program), error = None } -> Unit
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> Ashes.Trait.Show.show
            |> (given (text) -> test.fail("a captured record receiver read only as `receiver.field` should lower: " + text))
        | _ -> test.fail("program lowering produced no program")

let runQualifiedReceiverCaptureTests unit =
    Unit
    |> expectCapturedReceiverLowers
    |> (given (_) -> Ashes.IO.print("qualified receiver capture tests passed"))
