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

// Stage 0 prints the trait evidence it resolved (an `Int` operand of a bound variable's type)
// ahead of the functions; trait lowering is not ported, so the section is dropped from the
// oracle together with the blank line that closes it.
let recursive dropTraitEvidence (inSection: Bool) (lines: List(Str)) =
    match lines with
        | [] -> []
        | line :: rest ->
            if inSection
            then
                if line == ""
                then dropTraitEvidence(false)(rest)
                else dropTraitEvidence(true)(rest)
            else
                if line == "trait evidence"
                then dropTraitEvidence(true)(rest)
                else line :: dropTraitEvidence(false)(rest)

let withoutTraitEvidence (text: Str) =
    "\n"
    |> Ashes.Text.split(text)
    |> dropTraitEvidence(false)
    |> Ashes.Text.join("\n")

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
            |> withoutTraitEvidence
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
// consumed_list_argument adds the inline list walk releasing a fresh list argument the callee
// did not take, element heads included; consumed_string_list_copied_release the release of a
// fresh string-list argument the callee's result keeps parts of, branching on the result's
// returns bit: spine-only where the result stayed reference-counted, strings included where the
// conditional copy-out copied its heads. non_tail_self_call_list_result adds a self call outside
// tail position (`bang(head) :: stamp(tail)`): it reads the returns bit and copies its string-list
// result out like any other call, so placement keeps the pattern-owned tail alive across it, and
// the closure the self reference rebuilds carries its environment size in bytes.
// match_fresh_scrutinee_owner_release adds the owner every arm makes for a fresh reference-counted
// list scrutinee whose pattern binds a string head, released at the arm exit through the inline
// walk; match_fresh_scrutinee_head_returned the arm that returns the bound head: the head keeps
// a reference of its own and the owner is released before the result through its structural
// dropper, synthesized under the match's location.
// self_call_operand_string_result adds a self call under a string concatenation (`"a" + go(n - 1)`)
// whose result type only the concatenation resolves: the call reads the callee's returns bit and
// copies its result out like a call to any other function, instead of asking for an arena result
// because its type was still a variable when the call was lowered.
// unannotated_parameter_record adds a record builder whose parameter carries no annotation
// (`let toEntry n = Item(name = n, flag = true)`): the parameter's type is seeded from the field
// it is stored into before the body is lowered, so the entry normalization decided ahead of the
// body places the record on the reference-counted heap as it does for an annotated parameter.
// tco_non_tail_self_call_in_operator_operand adds the loop functions whose self call sits under
// an operator (`1 + countEvens(tail)`): the scalar loops read the callee's returns bit like any
// call, and the list loops pass a pattern binding of a loop parameter whose placement finalize
// still decides through the pending argument-retain skeleton, its ownership flag zeroed at
// finalize when the frame keeps the parameter in the arena.
// tco_list_walk adds the curried stages of a loop function capturing its runtime-managed list
// parameter: each stage's `lambda_N$env_normalize` copies the captured list out for a closure
// that escapes, and the shared `__rc_cdrop_N` closure dropper walks it, both synthesized when
// the stage's closure is emitted.
// mutual_recursion still needs recursive-binding lowering parity and remains deliberately
// excluded until that is ported.
// match_rc_scrutinee adds the owner each arm makes for a fresh reference-counted scrutinee
// (stored to its own slot, released at the arm exit), the all-arms runtime-managed join, and
// the literal string arm copied to the reference-counted heap beside a fresh-string arm;
// match_list_scrutinee_drop the owner of a nested match result over a list of scalars, whose
// release walks the spine inline at the arm exit. handle_match_arm_reset stays out: the
// single-file lowering does not take capability declarations, so its live-posts guards are
// covered at the expression level in MatchArmScopeTests.
// tco_scalar_loop, tco_scalar_owned_let, and tco_unused_chain_parameter add the TCO loop of a
// self-recursive function over scalar parameters: the chain parameters installed in local slots,
// the fixed and per-iteration watermarks and the stack pointer saved around the `lambda_N_body`
// label, the affine accumulator's reservation slots, and the back edge's argument temps, old
// parameter loads, parameter stores, deferred owner releases and arena reset, stack restore,
// and jump, with the unread chain parameter's synthetic slot.
// owned_let_list_drop adds a `let`-owned runtime list of fresh strings (the list request, the
// runtime-managed cells and heads, and the inline unique-spine walk at the scope exit) and
// aggregate_children_retain the escaping tuple, list literal, and cons cell that retain the owned
// bindings they store, the runtime tuple and list cells, and the shared-spine walk of an owned
// list. lambda_returns_record (a lambda returning a fresh record tree, owned by a top-level `let`
// and released through its field walk) stays out of the runner: its `_start_main` still copies
// the match result out at the scope exit where stage 0 knows every arm produced a runtime value.
// reuse_record_update and reuse_list_map add OPT-42's runtime reuse: the `let`-owned scrutinee
// released into each arm's reuse token, the same-constructor rebuild consuming it in place with
// its transferred pointer child guarded by the token's uniqueness, and the nullary rebuild reusing
// the dead cell; reuse_shared_falls_back keeps a scrutinee aliased by a second `let` on the arena
// path with no token at all.
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
        |> (given (_) -> checkFixture(root)("consumed_list_argument"))
        |> (given (_) -> checkFixture(root)("consumed_string_list_copied_release"))
        |> (given (_) -> checkFixture(root)("non_tail_self_call_list_result"))
        |> (given (_) -> checkFixture(root)("match_fresh_scrutinee_owner_release"))
        |> (given (_) -> checkFixture(root)("match_fresh_scrutinee_head_returned"))
        |> (given (_) -> checkFixture(root)("self_call_operand_string_result"))
        |> (given (_) -> checkFixture(root)("unannotated_parameter_record"))
        |> (given (_) -> checkFixture(root)("tco_non_tail_self_call_in_operator_operand"))
        |> (given (_) -> checkFixture(root)("tco_list_walk"))
        |> (given (_) -> checkFixture(root)("match_rc_scrutinee"))
        |> (given (_) -> checkFixture(root)("match_list_scrutinee_drop"))
        |> (given (_) -> checkFixture(root)("tco_scalar_loop"))
        |> (given (_) -> checkFixture(root)("tco_scalar_owned_let"))
        |> (given (_) -> checkFixture(root)("tco_unused_chain_parameter"))
        |> (given (_) -> checkFixture(root)("owned_let_list_drop"))
        |> (given (_) -> checkFixture(root)("aggregate_children_retain"))
        |> (given (_) -> checkFixture(root)("reuse_record_update"))
        |> (given (_) -> checkFixture(root)("reuse_list_map"))
        |> (given (_) -> checkFixture(root)("reuse_shared_falls_back"))
        |> (given (_) -> Ashes.IO.print("all self-hosted whole-program IR parity fixtures passed"))
    | _ -> Ashes.IO.panic("usage: ir-program-parity <fixture-directory>")
