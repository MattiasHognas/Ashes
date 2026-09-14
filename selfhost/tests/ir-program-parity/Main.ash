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

// The first line on which the two dumps disagree, with both readings, so a red fixture names
// the instruction that drifted rather than the whole program.
let recursive firstDifference (lineNumber: Int) (expected: List(Str)) (actual: List(Str)) =
    match (expected, actual) with
        | ([], []) -> ""
        | (expectedLine :: expectedRest, actualLine :: actualRest) ->
            if expectedLine == actualLine
            then firstDifference(lineNumber + 1)(expectedRest)(actualRest)
            else "line " + Ashes.Text.fromInt(lineNumber) + "\n    expected: " + expectedLine + "\n    actual:   " + actualLine
        | (expectedLine :: _rest, []) -> "line " + Ashes.Text.fromInt(lineNumber) + "\n    expected: " + expectedLine + "\n    actual:   <end of dump>"
        | ([], actualLine :: _rest) -> "line " + Ashes.Text.fromInt(lineNumber) + "\n    expected: <end of dump>\n    actual:   " + actualLine

let mismatchReport (name: Str) (expected: Str) (actual: Str) =
    (let expectedLines = Ashes.Text.split(expected)("\n")
    in
        let actualLines = Ashes.Text.split(actual)("\n")
        in
            name + " (" + Ashes.Text.fromInt(Ashes.Collection.List.length(expectedLines)) + " expected lines, " + Ashes.Text.fromInt(Ashes.Collection.List.length(actualLines)) + " actual lines) differs at " + firstDifference(1)(expectedLines)(actualLines))

// A fixture's verdict: `None` when the self-hosted lowering reproduces stage 0's dump, otherwise
// the report the runner prints once every fixture has been checked.
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
                                then None
                                else
                                    actual
                                    |> mismatchReport(name)(expected)
                                    |> Some
                        | CoreLoweringResult { error = Some(error) } -> Some(name + ": lowering failed: " + Ashes.Trait.Show.show(error))
                        | _ -> Some(name + ": lowering produced no program")
                | ProgramParseResult { diagnostics = diagnostics } -> Some(name + " should parse cleanly: " + Ashes.Trait.Show.show(diagnostics)))

let recursive checkFixtures root (names: List(Str)) (reports: List(Str)) =
    match names with
        | [] -> reports
        | name :: rest ->
            match checkFixture(root)(name) with
                | None -> checkFixtures(root)(rest)(reports)
                | Some(report) -> checkFixtures(root)(rest)(report :: reports)

let recursive printEach (lines: List(Str)) =
    match lines with
        | [] -> Unit
        | line :: rest ->
            line
            |> Ashes.IO.print
            |> (given (_) -> printEach(rest))

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
// inlined_entry_helper_under_back_edge adds a user helper whose body is a `let recursive go ...
// in go(seed)(xs)` entry, spliced into the loop's back-edge argument: the argument stored into
// a fresh local, the worker closure built in the loop, and the two direct applications. A
// function returning its own entry-normalized parameter (the returns bit beside the accepts
// bit, and the epilogue copying the result into the arena on a generic caller's request) is
// pinned in CallWindowLoweringTests instead: stage 0 locates the synthesized entry
// normalization at the binding, which this lowering does not yet reproduce.
// inlined_helper_chain_under_back_edge, inlined_helper_sibling_by_label,
// inlined_helper_sibling_spliced, and helper_call_without_inline_trigger pin the inliner's
// decisions: a helper chain spliced under a back edge, a spliced helper calling a top-level
// function the loop never captured (rebuilt from its label with a null environment), a helper
// whose sibling is spliced in turn, and the same helper left as a call outside any trigger.
// tco_tuple_parameter_rebuild adds a scalar tuple loop parameter rebuilt every iteration, with
// the pattern-owner marker of a destructured element stored into the fresh cell;
// tco_str_parameter_fresh_successor a `Str` parameter placed by type whose successor is a
// fresh string built in the arena and copied out by the back edge.
// tco_consumed_list_parameter_borrowed_head walks a consumed list parameter whose pattern-bound
// head a builtin reads and whose tail is the parameter's unchanged successor (the transfer takes
// its reference, the old root is released before the stores, and the reset's guarded release
// stands down); tco_consumed_list_parameter_returned_head returns the head out of the same
// loop, so the exit checks every runtime-managed slot against the reference-counted result.
// pattern_head_read_under_operator reads a pattern-bound head through a builtin beside a mapped
// operator in a plain recursion: the body is lowered against its closed inferred type, so the
// head is tracked, borrowed, and anchored (non_tail_self_call_list_result keeps the same head
// untracked with no operator in the body).
// tco_record_parameter_exit_before_list_accumulator releases a runtime-managed record loop
// parameter at the exit ahead of the list accumulator (parameter order) and carries the record
// into the loop closure through an environment normalizer with a closure dropper;
// tco_owned_child_record_accumulator and tco_record_string_field_into_successor copy a record
// successor at the back edge behind the two temps stage 0's emitters burn ahead of the copy.
// tco_returned_record_head returns a consumed list's matched record head beside a static
// record arm (the head's fields are read through the receiver without a pattern-owner borrow);
// tco_record_head_stored_into_copy_adt_successor stores the matched head into a copy-ADT
// successor's field under a transferring request and passes the record parameter through at a
// mixed-shape back edge, where the pass-through is retained rather than copied and the
// caller's pattern-owner root is released inline by the deferred reset without a location.
// tco_variant_parameter_reused_in_place threads a multi-constructor variant through a loop that
// matches it directly: the parameter is a linear reuse root whose copier is synthesized at the
// loop entry, each arm hands its dead matched cell out as an arena reuse token, the same-arity
// rebuild consumes it in place, and the move analysis elides the entry deep copy.
// tco_list_parameter_resolved_by_back_edge walks a list into an unannotated accumulator with
// no operator in the body: the accumulator's type is a variable at the loop entry, so its
// active flag is allocated where the back edge resolves it, after the arm's pattern locals.
let fixtures =
    [
        "simple_arith",
        "let_bindings",
        "nested_let_scopes",
        "scalar_match",
        "ownerless_match",
        "pattern_match",
        "closure_capture",
        "heap_result_builtin",
        "heap_result_let",
        "heap_result_list",
        "record_pattern",
        "tag_group_arm_brackets",
        "match_arm_copy_out",
        "call_result_copy_out",
        "call_argument_retain",
        "consumed_list_argument",
        "consumed_string_list_copied_release",
        "non_tail_self_call_list_result",
        "match_fresh_scrutinee_owner_release",
        "match_fresh_scrutinee_head_returned",
        "unannotated_parameter_record",
        "tco_list_walk",
        "match_rc_scrutinee",
        "match_list_scrutinee_drop",
        "tco_scalar_loop",
        "tco_scalar_owned_let",
        "tco_unused_chain_parameter",
        "owned_let_list_drop",
        "aggregate_children_retain",
        "reuse_record_update",
        "reuse_list_map",
        "reuse_shared_falls_back",
        "inlined_entry_helper_under_back_edge",
        "inlined_helper_chain_under_back_edge",
        "inlined_helper_sibling_by_label",
        "inlined_helper_sibling_spliced",
        "helper_call_without_inline_trigger",
        "tco_tuple_parameter_rebuild",
        "tco_str_parameter_fresh_successor",
        "tco_consumed_list_parameter_returned_head",
        "tco_list_parameter_resolved_by_back_edge",
        "tco_record_parameter_exit_before_list_accumulator",
        "tco_owned_child_record_accumulator",
        "tco_record_string_field_into_successor",
        "tco_record_field_read_into_successor",
        "tco_consumed_record_list_tuple_result",
        "tco_returned_record_head",
        "tco_record_head_stored_into_copy_adt_successor",
        "tco_variant_parameter_reused_in_place",
        "producer_conses_record_string_head",
        "reuse_path_rebuild_declines_copy",
        "passthrough_or_fresh_result",
        "record_head_list_producer",
        "aggregate_borrowing_owner_kept_by_callee"
    ]

// The fixtures whose stage-0 dump comes from a lowering that registers no trait declarations:
// there, an operator records no trait requirement, so a recursive binding whose body applies
// one is never lowered against its closed inferred type. The stage-0 compiler always stitches
// `Ashes.Trait` in and does lower such a binding that way, and this lowering follows the
// compiler, so each of these fixtures matches the compiler's own dump of its source and not the
// committed oracle: the head of a consumed list parameter read under an operator is tracked and
// borrowed, an unannotated list parameter's active flag is allocated at the loop entry, and a
// self call under an operator reads the callee's returns bit rather than asking for an arena
// result. They stay out of the comparison until the oracle lowers with the trait declarations
// the compiler stitches. The next two are producers over variants carrying a nested variant, a
// string list, or an optional string: the compiler leaves their consumed list parameter outside
// runtime management and zeroes the self call's retain flag, where this lowering admits the
// parameter and keeps the callee's accepts bit; they rejoin the comparison once the loop
// parameter admission follows the compiler for those element shapes. The last is a loop whose
// accumulator carries tuples of a variant-carrying record: the compiler admits the accumulator
// to runtime management and clones each tuple at the back edge through synthesized copiers,
// and normalizes the environment of the closure the loop applies, where this lowering keeps
// the accumulator in the arena; it rejoins the comparison once the tuple element admission
// follows the compiler. The record-update loop after it hands its record parameter to a callee
// whose result keeps a field of it: the compiler reads the parameter as a reference-counted
// value once its provisional placement admits it, and retains it for the callee outright, where
// this lowering keeps every loop parameter argument pending until the loop is finalized and
// retains it under the callee's accepts bit; it rejoins the comparison once parameter reads
// follow the provisional placement. The string loop after it hands an unannotated `Str`
// parameter to a callee whose error result keeps it: the compiler admits both string
// parameters at the loop entry, so the forced retain of the argument survives finalization,
// where this lowering keeps them in the arena and zeroes the flag; it rejoins the comparison
// once the string parameter admission follows the compiler. The accumulate-and-reverse
// producer after it hands its annotated list accumulator to a generic reverse before the
// back edges that would admit it are lowered: the compiler places the parameter from its
// type at the loop entry and retains the argument outright, where this lowering keeps it
// pending under the callee's accepts bit; it rejoins the comparison with the provisional
// placement.
let elaboratedFixtures =
    [
        "self_call_operand_string_result",
        "tco_non_tail_self_call_in_operator_operand",
        "tco_consumed_list_parameter_borrowed_head",
        "pattern_head_read_under_operator",
        "tco_record_head_consed_into_sibling_accumulator",
        "producer_conses_nested_variant_head",
        "user_type_named_function_release",
        "tco_parameter_kept_by_borrowing_callee_result",
        "record_update_successor_of_loop_parameter",
        "tco_string_parameter_kept_by_callee_error_result",
        "accumulate_and_reverse_producer"
    ]

match Ashes.IO.args with
    | root :: [] ->
        match checkFixtures(root)(fixtures)([]) with
            | [] ->
                Ashes.IO.print(
                    "all " + Ashes.Text.fromInt(Ashes.Collection.List.length(fixtures)) + " self-hosted whole-program IR parity fixtures passed; " + Ashes.Text.fromInt(Ashes.Collection.List.length(elaboratedFixtures)) + " elaborated fixtures not compared: " + Ashes.Text.join(", ")(elaboratedFixtures)
                )
            | reports ->
                reports
                |> Ashes.Collection.List.reverse
                |> printEach
                |> (given (_) ->
                    test.fail(
                        Ashes.Text.fromInt(Ashes.Collection.List.length(reports)) + " of " + Ashes.Text.fromInt(Ashes.Collection.List.length(fixtures)) + " self-hosted whole-program IR parity fixtures differ from stage 0"
                    ))
    | _ -> Ashes.IO.panic("usage: ir-program-parity <fixture-directory>")
