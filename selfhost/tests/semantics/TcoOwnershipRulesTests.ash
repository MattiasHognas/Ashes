// The ownership rules of a self-recursive function lowered as a TCO loop, checked on the lowered
// IR text of the loop body's function: an operator operand never sits in tail position, a `let`
// owns a runtime-RC reference only when its value temp is a fresh producer or a transferred
// value, and a tail self-call argument retains the owned bindings it carries out of their scope.
import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
export (
    value runTcoOwnershipRulesTests,
)

let parsedProgram source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let loweredLines source =
    match source
    |> parsedProgram
    |> lowerCoreProgram with
        | CoreLoweringResult { program = Some(program), error = None } -> formatIr(program)(LoweredIr)(None)
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> Ashes.Trait.Show.show
            |> (given (text) -> test.fail("program lowering failed: " + text))
        | _ -> test.fail("program lowering produced no program")

let isFunctionHeader (line: Str) = Ashes.Text.startsWith(line)("function ")

// The lines of the function whose header carries `originText`, up to the next header.
let recursive functionBody (lines: List(Str)) (collected: List(Str)) =
    match lines with
        | [] -> Ashes.Collection.List.reverse(collected)
        | line :: rest ->
            if isFunctionHeader(line)
            then Ashes.Collection.List.reverse(collected)
            else functionBody(rest)(line :: collected)

let recursive functionLines (originText: Str) (lines: List(Str)) =
    match lines with
        | [] -> test.fail("no function with origin " + originText)
        | line :: rest ->
            if isFunctionHeader(line) && Ashes.Text.contains(line)(originText)
            then functionBody(rest)([])
            else functionLines(originText)(rest)

let loopFunctionLines (originText: Str) (source: Str) =
    source
    |> loweredLines
    |> functionLines(originText)

let recursive countContaining (fragment: Str) (lines: List(Str)) =
    match lines with
        | [] -> 0
        | line :: rest ->
            if Ashes.Text.contains(line)(fragment)
            then 1 + countContaining(fragment)(rest)
            else countContaining(fragment)(rest)

// The lines after the last `else_N:` label: the else branch of the innermost `if`.
let recursive afterLastElseLabel (lines: List(Str)) (tail: List(Str)) =
    match lines with
        | [] -> tail
        | line :: rest ->
            if Ashes.Text.startsWith(line)("  else_")
            then afterLastElseLabel(rest)(rest)
            else afterLastElseLabel(rest)(tail)

// The lines before the last `else_N:` label, in reverse order.
let recursive beforeLastElseLabel (lines: List(Str)) (collected: List(Str)) (head: List(Str)) =
    match lines with
        | [] -> head
        | line :: rest ->
            if Ashes.Text.startsWith(line)("  else_")
            then beforeLastElseLabel(rest)(line :: collected)(collected)
            else beforeLastElseLabel(rest)(line :: collected)(head)

// The value of the `Marker=N` field of an instruction line, as text.
let fieldAfter (marker: Str) (line: Str) =
    (let index = Ashes.Text.indexOf(line)(marker)
    in
        if index < 0
        then test.fail("no " + marker + " in " + line)
        else
            match Ashes.Text.split(Ashes.Text.substring(line)(index + Ashes.Text.length(marker))(Ashes.Text.length(line) - index - Ashes.Text.length(marker)))(" ") with
                | first :: _ -> first
                | [] -> "")

let recursive lineContaining (fragment: Str) (lines: List(Str)) =
    match lines with
        | [] -> test.fail("no line containing " + fragment)
        | line :: rest ->
            if Ashes.Text.contains(line)(fragment)
            then line
            else lineContaining(fragment)(rest)

let recursive lineBefore (fragment: Str) (previous: Str) (lines: List(Str)) =
    match lines with
        | [] -> test.fail("no line containing " + fragment)
        | line :: rest ->
            if Ashes.Text.contains(line)(fragment)
            then previous
            else lineBefore(fragment)(line)(rest)

let check (label: Str) (ok: Bool) =
    if ok
    then Unit
    else test.fail("expected " + label)

let ownedLetInTailArgumentRecordSource = "type Inst =\n    | Jump(Str)\n    | Other\n\ntype Wrapped =\n    | instruction: Inst\n    | location: Maybe(Int)\n\nlet mk n = Ashes.Text.fromInt(n)\n\nlet recursive loop n acc =\n    if n == 0\n    then acc\n    else\n        let label = mk(n)\n        in loop(n - 1)(Wrapped(instruction = Jump(label), location = None) :: acc)\n\nAshes.IO.print(1)"

// OPT-26: `label` owns the fresh reference-counted call result; the tail self-call's argument
// stores it in a constructor field, so the read is retained (`Borrow`, then `RcDup`) and the
// duplicate is what the field stores, while the owner's own release still fires.
let expectTailSelfCallArgumentRetainsOwnedBinding unit =
    ownedLetInTailArgumentRecordSource
    |> loopFunctionLines("[ClosureHelper from loop]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("one RcDup in the loop body")(countContaining("RcDup")(lines) == 1))
        |> (given (_) ->
            "Borrow"
            |> Ashes.Text.contains(lineBefore("RcDup")("")(lines))
            |> check("the RcDup right after the owner's Borrow"))
        |> (given (_) ->
            "RuntimeManaged=true"
            |> Ashes.Text.contains(lineContaining("RcDup")(lines))
            |> check("a runtime-managed RcDup"))
        |> (given (_) ->
            "Source=" + fieldAfter("Target=")(lineContaining("RcDup")(lines))
            |> Ashes.Text.contains(lineContaining("SetAdtField")(lines))
            |> check("the constructor field storing the duplicate"))
        |> (given (_) -> check("one owner release in the loop body")(countContaining("RcDrop")(lines) == 1))
        |> (given (_) ->
            "TypeName=String OwnerSlot="
            |> Ashes.Text.contains(lineContaining("RcDrop")(lines))
            |> check("the owner release naming the let slot")))

let ownedLetInOperandSelfCallSource = "let mk n = Ashes.Text.fromInt(n)\n\nlet recursive count n acc =\n    if n == 0\n    then 0\n    else\n        let s = mk(n)\n        in\n            if n % 2 == 0\n            then 1 + count(n - 1)(s :: acc)\n            else count(n - 1)(s :: acc)\n\nAshes.IO.print(count(4)([]))"

// OPT-29: the self-call under `1 + ...` is an operator operand, never a tail call, so its cons
// argument stores the plain borrowed read; only the else branch's genuine tail self-call retains
// `s` for the next iteration.
let expectOperandSelfCallIsNotATailCall unit =
    ownedLetInOperandSelfCallSource
    |> loopFunctionLines("[ClosureHelper from count]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("one RcDup in the loop body")(countContaining("RcDup")(lines) == 1))
        |> (given (_) ->
            check("no RcDup in the operand branch")(countContaining("RcDup")(beforeLastElseLabel(lines)([])([])) == 0))
        |> (given (_) ->
            check("the RcDup in the tail branch")(countContaining("RcDup")(afterLastElseLabel(lines)([])) == 1))
        |> (given (_) ->
            check("the operand branch storing the plain borrow")(countContaining("Borrow")(beforeLastElseLabel(lines)([])([])) == 1)))

let letAliasOfParameterSource = "let recursive loop n acc =\n    (let r = acc\n    in\n        if n == 0\n        then r\n        else loop(n - 1)(r + \"x\"))\n\nAshes.IO.print(loop(5)(\"\"))"

// OPT-27: `r` is bound to a plain read of the loop parameter, a borrowed read that never
// retained, so it is not a runtime owner: no release at its scope exit and no retain of its
// reads, unlike the fresh call result above.
let expectLetAliasOfParameterIsNotAnOwner unit =
    letAliasOfParameterSource
    |> loopFunctionLines("[ClosureHelper from loop]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("no owner release for the alias")(countContaining("RcDrop")(lines) == 0))
        |> (given (_) -> check("no retain of the alias")(countContaining("RcDup")(lines) == 0)))

let runTcoOwnershipRulesTests unit =
    unit
    |> expectTailSelfCallArgumentRetainsOwnedBinding
    |> (given (_) -> expectOperandSelfCallIsNotATailCall(Unit))
    |> (given (_) -> expectLetAliasOfParameterIsNotAnOwner(Unit))
    |> (given (_) -> Ashes.IO.print("all self-hosted tco ownership rule tests passed"))
