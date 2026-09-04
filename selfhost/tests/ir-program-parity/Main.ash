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
// match arm, each window reset only when its result survives the reset); ownerless_match adds an
// arena-placed constructor scrutinee with a null guard before each tag test, pattern_match the
// owned-binding borrow and its control-flow precise release, and closure_capture the source-named
// function origins, the closure environment normalizer, the per-call windows, and the stack
// closure of a let-bound lambda used only as a callee, down to its source locations.
// tag_group_arm_brackets adds the per-arm brackets on the SwitchTag dispatch path (a cleanup
// block per linearly tested group case, none for a trivial single-case group) and
// match_arm_copy_out the pattern-owned binding's release and the copy-out of a record arm result
// past the arm's reset. mutual_recursion still needs recursive-binding lowering parity and
// remains deliberately excluded until that is ported.
// call_result_copy_out adds the call window's conditional copy-out of a result whose placement
// only the callee's returns bit knows, and call_argument_retain the retain of a fresh
// reference-counted argument under the callee's accepts bit with its release after the call.
// mutual_recursion still needs recursive-binding lowering parity and remains deliberately
// excluded until that is ported.
// owned_let_list_drop adds a `let`-owned runtime list of fresh strings (the list request, the
// runtime-managed cells and heads, and the inline unique-spine walk at the scope exit) and
// aggregate_children_retain the escaping tuple, list literal, and cons cell that retain the owned
// bindings they store, the runtime tuple and list cells, and the shared-spine walk of an owned
// list. lambda_returns_record (a lambda returning a fresh record tree, owned by a top-level `let`
// and released through its field walk) stays out of the runner: its `_start_main` still copies
// the match result out at the scope exit where stage 0 knows every arm produced a runtime value.
match Ashes.IO.args with
    | root :: [] ->
        Unit
        |> (given (_) -> checkFixture(root)("simple_arith"))
        |> (given (_) -> checkFixture(root)("let_bindings"))
        |> (given (_) -> checkFixture(root)("nested_let_scopes"))
        |> (given (_) -> checkFixture(root)("scalar_match"))
        |> (given (_) -> checkFixture(root)("ownerless_match"))
        |> (given (_) -> checkFixture(root)("pattern_match"))
        |> (given (_) -> checkFixture(root)("closure_capture"))
        |> (given (_) -> checkFixture(root)("heap_result_builtin"))
        |> (given (_) -> checkFixture(root)("heap_result_let"))
        |> (given (_) -> checkFixture(root)("heap_result_list"))
        |> (given (_) -> checkFixture(root)("record_pattern"))
        |> (given (_) -> checkFixture(root)("tag_group_arm_brackets"))
        |> (given (_) -> checkFixture(root)("match_arm_copy_out"))
        |> (given (_) -> checkFixture(root)("call_result_copy_out"))
        |> (given (_) -> checkFixture(root)("call_argument_retain"))
        |> (given (_) -> checkFixture(root)("owned_let_list_drop"))
        |> (given (_) -> checkFixture(root)("aggregate_children_retain"))
        |> (given (_) -> Ashes.IO.print("all self-hosted whole-program IR parity fixtures passed"))
    | _ -> Ashes.IO.panic("usage: ir-program-parity <fixture-directory>")
