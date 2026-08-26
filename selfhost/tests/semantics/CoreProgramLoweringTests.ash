import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
export (
    value runCoreProgramLoweringTests,
)

let parsedProgram source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let loweredProgramSource source =
    match source
    |> parsedProgram
    |> lowerCoreProgram with
        | CoreLoweringResult { program = Some(program), error = None } -> program
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> Ashes.Trait.Show.show
            |> (given (text) -> test.fail("program lowering failed: " + text))
        | _ -> test.fail("program lowering produced no program")

let dumpSource source =
    formatIr(loweredProgramSource(source))(LoweredIr)(None)

let loweringErrorFor source =
    match source
    |> parsedProgram
    |> lowerCoreProgram with
        | CoreLoweringResult { error = Some(error) } -> error
        | CoreLoweringResult { error = None } -> test.fail("expected program lowering to fail, but it produced a program")

let expectPlainTopLevelLetsProduceIr unit =
    "let a = 1\nlet b = 2\na + b"
    |> loweredProgramSource
    |> (given (_) -> Unit)

let expectSelfRecursiveTopLevelLetLowers unit =
    "let recursive fact n = if n <= 1 then 1 else n * fact(n - 1)\nfact(5)"
    |> loweredProgramSource
    |> (given (_) -> Unit)

let expectMutualRecursionGroupLowers unit =
    "let recursive isEven n = if n == 0 then true else isOdd(n - 1)\nand isOdd n = if n == 0 then false else isEven(n - 1)\nisEven(10)"
    |> loweredProgramSource
    |> (given (_) -> Unit)

let expectMixedPlainAndRecursiveLettersLower unit =
    "let base = 10\nlet recursive countDown n = if n <= 0 then base else countDown(n - 1)\nlet doubled = base * 2\ncountDown(3) + doubled"
    |> loweredProgramSource
    |> (given (_) -> Unit)

let expectDuplicateTopLevelBindingIsRejected unit =
    match loweringErrorFor("let a = 1\nlet a = 2\na") with
        | DuplicateTopLevelBinding(name) -> test.assertEqual("a")(name)
        | other -> test.fail("expected DuplicateTopLevelBinding, got " + Ashes.Trait.Show.show(other))

let expectDuplicateInsideRecursiveGroupIsRejected unit =
    match loweringErrorFor("let recursive f n = f(n)\nand f n = f(n)\nf(0)") with
        | DuplicateTopLevelBinding(name) -> test.assertEqual("f")(name)
        | other -> test.fail("expected DuplicateTopLevelBinding, got " + Ashes.Trait.Show.show(other))

let expectDuplicateAcrossPlainAndRecursiveIsRejected unit =
    match loweringErrorFor("let a = 1\nlet recursive a n = a(n)\na(0)") with
        | DuplicateTopLevelBinding(name) -> test.assertEqual("a")(name)
        | other -> test.fail("expected DuplicateTopLevelBinding, got " + Ashes.Trait.Show.show(other))

let expectPlainTopLevelLetsProduceExpectedIr unit =
    "let a = 1\nlet b = 2\na + b"
    |> dumpSource
    |> test.assertEqual([
        "IR (lowered)",
        "============",
        "",
        "function _start_main  [ProgramEntry]",
        "  locals=2 temps=5",
        "    LoadConstInt          Target=0 Value=1",
        "    StoreLocal            Slot=0 Source=0",
        "    LoadConstInt          Target=1 Value=2",
        "    StoreLocal            Slot=1 Source=1",
        "    LoadLocal             Target=2 Slot=0",
        "    LoadLocal             Target=3 Slot=1",
        "    AddInt                Target=4 Left=2 Right=3",
        "    Return                Source=4",
        ""
    ])

let runCoreProgramLoweringTests unit =
    unit
    |> expectPlainTopLevelLetsProduceIr
    |> expectPlainTopLevelLetsProduceExpectedIr
    |> expectSelfRecursiveTopLevelLetLowers
    |> expectMutualRecursionGroupLowers
    |> expectMixedPlainAndRecursiveLettersLower
    |> expectDuplicateTopLevelBindingIsRejected
    |> expectDuplicateInsideRecursiveGroupIsRejected
    |> expectDuplicateAcrossPlainAndRecursiveIsRejected
    |> (given (_) -> Ashes.IO.print("all self-hosted core program lowering tests passed"))
