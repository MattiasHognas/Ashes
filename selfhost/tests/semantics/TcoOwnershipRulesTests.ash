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

let recursive countContainingBoth (first: Str) (second: Str) (lines: List(Str)) =
    match lines with
        | [] -> 0
        | line :: rest ->
            if Ashes.Text.contains(line)(first) && Ashes.Text.contains(line)(second)
            then 1 + countContainingBoth(first)(second)(rest)
            else countContainingBoth(first)(second)(rest)

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
        |> (given (_) -> check("the back-edge owner release and the scope-exit release past the jump")(countContaining("RcDrop")(lines) == 2))
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
            check("the operand branch storing the plain borrow beside the callee's own borrowed read")(countContaining("Borrow")(beforeLastElseLabel(lines)([])([])) == 2)))

let letAliasOfParameterSource = "let recursive loop n acc =\n    (let r = acc\n    in\n        if n == 0\n        then r\n        else loop(n - 1)(r + \"x\"))\n\nAshes.IO.print(loop(5)(\"\"))"

// OPT-27: `r` is bound to a plain read of the loop parameter, a borrowed read that never
// retained, so `r` itself is not a runtime owner and adds neither a release at its own scope
// exit nor a retain of its reads. The parameter `acc` it aliases is a different question — the
// runtime-managed loop parameter placement this port now carries (`runtimeManagedStrOrdinals`)
// sees straight through the alias to recognize `acc` as rebuilt only through `+` on every tail
// self-call, so `acc` itself IS placed on the reference-counted heap: one predecessor release at
// the back edge (`acc`'s value before the fresh `r + "x"` replaces it) and one release at the
// loop's own exit, skipped exactly when the returned value is that same still-live reference (the
// `n == 0` arm, which returns `r` unchanged). Two releases total, and no duplicate — `r`'s own
// reads never add a third.
let expectLetAliasOfParameterIsNotAnOwner unit =
    letAliasOfParameterSource
    |> loopFunctionLines("[ClosureHelper from loop]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("the back-edge predecessor release and the exit-guarded release, no more")(countContaining("RcDrop")(lines) == 2))
        |> (given (_) -> check("no retain of the alias or the parameter")(countContaining("RcDup")(lines) == 0)))

// The `acc` of `count` grows by one cons cell at every tail self-call, and the head it stores
// is a live owner's reference, so `acc` is a runtime-managed list: the direct argument is
// normalized at entry against the caller's ownership flag (a borrowed list copies its spine with
// its string heads), the tail branch's cons cell is allocated on the reference-counted heap, the
// back edge resets the arena to the fixed loop-entry watermark once the successor is stored, and
// the loop exit releases the accumulator under its active flag through the shared-cell walk.
let expectGrownConsAccumulatorIsRuntimeManaged unit =
    ownedLetInOperandSelfCallSource
    |> loopFunctionLines("[ClosureHelper from count]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("the entry normalization reads the ownership flag")(countContaining("LoadArgumentOwnership")(lines) == 1))
        |> (given (_) ->
            "HeadCopy=String RuntimeManaged=true"
            |> Ashes.Text.contains(lineContaining("CopyOutList")(lines))
            |> check("the borrowed list copies its spine with its string heads"))
        |> (given (_) ->
            check("the tail branch's cons cell on the reference-counted heap")(countContaining("Alloc ")(afterLastElseLabel(lines)([])) == 1))
        |> (given (_) ->
            "RuntimeManaged=true"
            |> Ashes.Text.contains([]
            |> afterLastElseLabel(lines)
            |> lineContaining("Alloc "))
            |> check("a runtime-managed cons cell"))
        |> (given (_) ->
            "RestoreArenaState     CursorLocalSlot=" + fieldAfter("CursorLocalSlot=")(lineContaining("SaveArenaState")(lines))
            |> countContaining
            |> (given (count) ->
                []
                |> afterLastElseLabel(lines)
                |> count)
            |> (given (resets) -> check("the back edge resets to the fixed loop-entry watermark")(resets == 1)))
        |> (given (_) ->
            "ReclaimArenaChunks    SavedEndSlot=" + fieldAfter("EndLocalSlot=")(lineContaining("SaveArenaState")(lines))
            |> countContaining
            |> (given (count) ->
                []
                |> afterLastElseLabel(lines)
                |> count)
            |> (given (reclaims) -> check("the back edge reclaims the chunks above the fixed watermark")(reclaims == 1)))
        |> (given (_) -> check("the exit release under the active flag")(countContaining("rc_tco_exit_drop_inactive")(lines) == 2))
        |> (given (_) -> check("the exit release walks the shared-cell list")(countContaining("rcdrop_list_shared")(lines) == 2)))

let consumedTailListSource = "let recursive total xs acc =\n    match xs with\n        | [] -> acc\n        | s :: rest -> total(rest)(acc + Ashes.Text.byteLength(s))\n\nAshes.IO.print(total([Ashes.Text.fromInt(1)])(0))"

// The `xs` of `total` is consumed through its own pattern-bound tail at every tail self-call
// over string heads, so it is a runtime-managed list: the chain parameter is always copied at
// entry (no ownership flag reaches a captured parameter), the back edge retains the successor
// tail null-tolerantly before releasing the old root under the active flag, and the loop exit
// releases whatever the slot still holds.
let expectConsumedTailListIsRuntimeManaged unit =
    consumedTailListSource
    |> loopFunctionLines("[ClosureHelper from total]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("no ownership flag for a captured chain parameter")(countContaining("LoadArgumentOwnership")(lines) == 0))
        |> (given (_) ->
            "HeadCopy=String RuntimeManaged=true"
            |> Ashes.Text.contains(lineContaining("CopyOutList")(lines))
            |> check("the entry copies the spine with its string heads"))
        |> (given (_) -> check("the successor tail retained null-tolerantly at the back edge")(countContaining("MayBeEmpty=true")(lines) == 1))
        |> (given (_) -> check("the old root released under the active flag at the back edge")(countContaining("rc_tco_drop_inactive")(lines) == 2))
        |> (given (_) -> check("the exit release under the active flag")(countContaining("rc_tco_exit_drop_inactive")(lines) == 2))
        |> (given (_) -> check("one list walk at the back edge and one at the exit")(countContaining("rcdrop_list_shared")(lines) == 4)))

let forwardedStrHeadSource = "let recursive last (n: Int) (xs: List(Str)) (keep: Str) =\n    match xs with\n        | [] -> keep\n        | head :: rest -> last(n - 1)(rest)(head)\n\nAshes.IO.print(last(2)([\"a\", \"b\"])(\"z\"))"

// A string head forwarded by name to a different parameter of the tail self-call is a pattern
// owner whose type is reference-counted whatever holds it: the arm retains it right after the
// pattern binds it (stage 0's protective duplicate), the self-call argument retains it again for
// the successor, and the owner's own reference is released at the back edge under the resolved
// type name. The head's list stays in the arena, so its cell walk never runs.
let expectForwardedStrHeadIsProtected unit =
    forwardedStrHeadSource
    |> loopFunctionLines("[ClosureHelper from last]")
    |> (given (lines) ->
        Unit
        |> (given (_) ->
            "RcDup"
            |> countContaining
            |> (given (count) -> check("the protective duplicate and the argument retain")(count(lines) == 2)))
        |> (given (_) -> check("both retains are real reference-count operations")(countContainingBoth("RcDup")("RuntimeManaged=true")(lines) == 2))
        |> (given (_) ->
            "RuntimeManaged=true"
            |> Ashes.Text.contains(lineContaining("TypeName=String OwnerSlot=")(lines))
            |> check("the owner's release under the resolved type name"))
        |> (given (_) ->
            "LoadLocal"
            |> Ashes.Text.contains(lineBefore("RcDup")("")(lines))
            |> check("the pattern binds the head before it is retained"))
        |> (given (_) -> check("the head's list stays in the arena")(countContaining("rcdrop_list")(lines) == 0)))

let forwardedGenericHeadSource = "let recursive last n xs keep =\n    match xs with\n        | [] -> keep\n        | head :: rest -> last(n - 1)(rest)(head)\n\nAshes.IO.print(last(2)([\"a\", \"b\"])(\"z\"))"

// The same shape over an unresolved element type keeps its markers as identities: the argument
// duplicate and the owner's release name the binding, not a runtime-managed type.
let expectForwardedGenericHeadKeepsIdentityMarkers unit =
    forwardedGenericHeadSource
    |> loopFunctionLines("[ClosureHelper from last]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("one identity duplicate at the self-call argument")(countContaining("RcDup")(lines) == 1))
        |> (given (_) -> check("no runtime-managed retain")(countContainingBoth("RcDup")("RuntimeManaged=true")(lines) == 0))
        |> (given (_) ->
            "RuntimeManaged=true"
            |> Ashes.Text.contains(lineContaining("TypeName=PatternBinding OwnerSlot=")(lines))
            |> (given (runtime) -> check("the owner's release stays an identity marker")(runtime == false))))

let recursive lineAfter (fragment: Str) (lines: List(Str)) =
    match lines with
        | [] -> test.fail("no line containing " + fragment)
        | line :: rest ->
            if Ashes.Text.contains(line)(fragment)
            then
                match rest with
                    | next :: _ -> next
                    | [] -> ""
            else lineAfter(fragment)(rest)

let widenPrelude = "let recursive widen (n: Int) (text: Str) =\n    if n == 0\n    then text\n    else widen(n - 1)(text + text)\n\n"

let readBuiltinDirectSource = widenPrelude + "let recursive loop (n: Int) (total: Int) =\n    if n == 0\n    then total\n    else loop(n - 1)(total + Ashes.Text.byteLength(widen(6)(Ashes.Text.fromInt(n))))\n\nAshes.IO.print(loop(10)(0))"

let readBuiltinJoinSource = widenPrelude + "let recursive loop (n: Int) (total: Int) =\n    if n == 0\n    then total\n    else\n        loop(n - 1)(total + Ashes.Text.byteLength(if n % 2 == 0\n        then widen(6)(Ashes.Text.fromInt(n))\n        else widen(5)(Ashes.Text.fromInt(n))))\n\nAshes.IO.print(loop(10)(0))"

let readBuiltinBorrowedJoinSource = widenPrelude + "let recursive loop (n: Int) (text: Str) (total: Int) =\n    if n == 0\n    then total\n    else\n        loop(n - 1)(text)(total + Ashes.Text.byteLength(if n % 2 == 0\n        then widen(6)(Ashes.Text.fromInt(n))\n        else text))\n\nAshes.IO.print(loop(10)(\"ab\")(0))"

let readBuiltinLetScopeSource = widenPrelude + "let recursive loop (n: Int) (total: Int) =\n    if n == 0\n    then total\n    else\n        let wide = widen(6)(Ashes.Text.fromInt(n))\n        in loop(n - 1)(total + Ashes.Text.byteLength(wide) + Ashes.Text.byteLength(wide))\n\nAshes.IO.print(loop(10)(0))"

// The release a read-only builtin emits for the fresh reference-counted value it consumed: an
// unowned runtime-managed `RcDrop` of the string right after the read.
let expectReadReleasesConsumedFreshResult (label: Str) (source: Str) =
    source
    |> loopFunctionLines("[ClosureHelper from loop]")
    |> lineAfter("TextByteLength")
    |> (given (line) ->
        Unit
        |> (given (_) ->
            "RcDrop"
            |> Ashes.Text.contains(line)
            |> check(label + ": the read releases the consumed result"))
        |> (given (_) ->
            "TypeName=String RuntimeManaged=true"
            |> Ashes.Text.contains(line)
            |> check(label + ": the release is a runtime-managed string drop"))
        |> (given (_) -> check(label + ": the released value has no owner")(Ashes.Text.contains(line)("OwnerSlot=") == false)))

// OPT-39: a read-only builtin consuming the fresh result of a call releases it right after the
// read, straight from the call and through an if join whose every branch produced a fresh value.
let expectReadBuiltinReleasesFreshCallResult unit =
    Unit
    |> (given (_) -> expectReadReleasesConsumedFreshResult("direct")(readBuiltinDirectSource))
    |> (given (_) -> expectReadReleasesConsumedFreshResult("join")(readBuiltinJoinSource))

// A join with one borrowed branch (the loop's own parameter) is a transferred value the read
// must not release, and a let-bound result is owned by its scope, whose exit release covers it.
let expectReadBuiltinKeepsOwnedResults unit =
    Unit
    |> (given (_) ->
        readBuiltinBorrowedJoinSource
        |> loopFunctionLines("[ClosureHelper from loop]")
        |> lineAfter("TextByteLength")
        |> (given (line) -> check("a borrowed branch keeps the join unreleased")(Ashes.Text.contains(line)("RcDrop") == false)))
    |> (given (_) ->
        readBuiltinLetScopeSource
        |> loopFunctionLines("[ClosureHelper from loop]")
        |> (given (lines) ->
            Unit
            |> (given (_) -> check("a let-bound result is not released by its reads")(countContaining("RcDrop")(lines) == countContainingBoth("RcDrop")("OwnerSlot=")(lines)))
            |> (given (_) -> check("the let's scope releases the result it owns")(countContainingBoth("TypeName=String OwnerSlot=")("RuntimeManaged=true")(lines) > 0))))

let runTcoOwnershipRulesTests unit =
    unit
    |> expectTailSelfCallArgumentRetainsOwnedBinding
    |> (given (_) -> expectOperandSelfCallIsNotATailCall(Unit))
    |> (given (_) -> expectLetAliasOfParameterIsNotAnOwner(Unit))
    |> (given (_) -> expectGrownConsAccumulatorIsRuntimeManaged(Unit))
    |> (given (_) -> expectConsumedTailListIsRuntimeManaged(Unit))
    |> (given (_) -> expectForwardedStrHeadIsProtected(Unit))
    |> (given (_) -> expectForwardedGenericHeadKeepsIdentityMarkers(Unit))
    |> (given (_) -> expectReadBuiltinReleasesFreshCallResult(Unit))
    |> (given (_) -> expectReadBuiltinKeepsOwnedResults(Unit))
    |> (given (_) -> Ashes.IO.print("all self-hosted tco ownership rule tests passed"))
