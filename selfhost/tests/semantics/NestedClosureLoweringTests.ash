import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Types
export (
    value runNestedClosureLoweringTests,
)

let parsedProgram source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

// A curried function of twelve parameters is twelve nested lambdas, and its body is lowered a
// second time once its operator requirement closes the scope. The trailing identity function's
// type variable is drawn from the supply after all of that, and a discarded lowering hands its
// supply on to the next one, so the variable's number counts the lowerings that ran.
let curriedProgramSource = "let helper x = x + 1\n" + "let many p1 p2 p3 p4 p5 p6 p7 p8 p9 p10 p11 p12 =\n" + "    helper(p1 + p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9 + p10 + p11 + p12)\n" + "    |> (given y -> y + helper(p1))\n" + "given z -> z\n"

let identityVariable source =
    match source
    |> parsedProgram
    |> lowerCoreProgram with
        | CoreLoweringResult { semanticType = SemFunction(SemVariable(variable), _result, _row), error = None } -> variable
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> Ashes.Trait.Show.show
            |> (given (text) -> test.fail("program lowering failed: " + text))
        | _ -> test.fail("the program's result should be the identity function over a type variable")

// Lowering each nested closure twice at every level costs 2^12 lowerings of the innermost body and
// draws 8197 variables; lowering the nested closures once while the enclosing body is lowered
// again draws 97.
let expectNestedClosuresLoweredOnceUnderSecondLowering unit =
    (let variable = identityVariable(curriedProgramSource)
    in
        if variable < 1000
        then Unit
        else
            test.fail(
                "a twelve-parameter curried function drew " + Ashes.Text.fromInt(variable) + " type variables: its nested closures are being lowered again at every level"
            ))

let runNestedClosureLoweringTests unit =
    unit
    |> expectNestedClosuresLoweredOnceUnderSecondLowering
    |> (given (_) -> Ashes.IO.print("nested closure lowering tests passed"))
