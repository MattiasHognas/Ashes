import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
let fixturePath root name extension = root + "/" + name + extension

let readFixture path =
    match Ashes.IO.File.readText(path) with
        | Ok(value) -> value
        | Error(message) -> test.fail("could not read parity fixture " + path + ": " + message)

let checkFixture root name =
    (let source =
        ".source"
        |> fixturePath(root)(name)
        |> readFixture
    in
        let expected =
            ".ir"
            |> fixturePath(root)(name)
            |> readFixture
        in
            match parseProgram(source) with
                | ProgramParseResult { program = program, diagnostics = [] } ->
                    match lowerCoreProgramWithSource(name + ".ash")(source)(program) with
                        | CoreLoweringResult { program = Some(lowered), error = None } ->
                            let actual =
                                Ashes.Text.join("\n")(formatIr(lowered)(LoweredIr)(None)) + "\n"
                            in
                                if actual == expected
                                then Unit
                                else
                                    test.fail(
                                        "IR parity mismatch for " + name + "\nexpected:\n" + expected + "actual:\n" + actual
                                    )
                        | CoreLoweringResult { error = Some(error) } -> test.fail("lowering failed for " + name + ": " + Ashes.Trait.Show.show(error))
                        | _ -> test.fail("lowering produced no program for " + name)
                | ProgramParseResult { diagnostics = diagnostics } -> test.fail(name + " should parse cleanly: " + Ashes.Trait.Show.show(diagnostics)))

// let_bindings, nested_let_scopes, and scalar_match need only arena bracketing (SaveArenaState/
// RestoreArenaState/ReclaimArenaChunks around flat top-level lets, nested let chains, and each
// match arm), ported for the provably-scalar case (CoreLowering.ash's isProvablyArenaSafeExpr/
// topLevelItemsProvablyArenaSafe/lowerArenaBracketedNestedLet/lowerMatchArms); ownerless_match
// adds an arena-placed constructor scrutinee with a null guard before each tag test. closure_capture,
// mutual_recursion, and pattern_match still need constructor-layout registration,
// closure/resource cleanup, or recursive bindings — none of which lowerCoreProgram runs yet —
// and remain deliberately excluded until that machinery is ported.
match Ashes.IO.args with
    | root :: [] ->
        Unit
        |> (given (_) -> checkFixture(root)("simple_arith"))
        |> (given (_) -> checkFixture(root)("let_bindings"))
        |> (given (_) -> checkFixture(root)("nested_let_scopes"))
        |> (given (_) -> checkFixture(root)("scalar_match"))
        |> (given (_) -> checkFixture(root)("ownerless_match"))
        |> (given (_) -> Ashes.IO.print("all self-hosted whole-program IR parity fixtures passed"))
    | _ -> Ashes.IO.panic("usage: ir-program-parity <fixture-directory>")
