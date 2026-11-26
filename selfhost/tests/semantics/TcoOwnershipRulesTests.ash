// The ownership rules of a self-recursive function lowered as a TCO loop, checked on the lowered
// IR text of the loop body's function: an operator operand never sits in tail position, a `let`
// owns a runtime-RC reference only when its value temp is a fresh producer or a transferred
// value, and a tail self-call argument retains the owned bindings it carries out of their scope.
import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.CoreLowering.Raw
import AshesCompiler.Semantics.CoreLowering.Model
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

// The lines outside the pending argument-retain skeletons (stage 0's
// `EmitConditionallyRetainedRuntimeArgument` under a flag finalize zeroes for a parameter the
// frame keeps in the arena): from the `JumpIfFalse` into `rc_call_argument_not_retained_N` up to
// that label, whose `RcDup` is dead in an arena frame.
let recursive withoutPendingRetainSkeletons (lines: List(Str)) (skipping: Bool) =
    match lines with
        | [] -> []
        | line :: rest ->
            if skipping
            then
                if Ashes.Text.startsWith(line)("  rc_call_argument_not_retained_")
                then withoutPendingRetainSkeletons(rest)(false)
                else withoutPendingRetainSkeletons(rest)(true)
            else
                if Ashes.Text.contains(line)("Target=rc_call_argument_not_retained_")
                then withoutPendingRetainSkeletons(rest)(true)
                else line :: withoutPendingRetainSkeletons(rest)(false)

// The representation test gives every copy site a retain of a value that is already
// reference-counted: the RcDup right after the jump on its IsReferenceCounted result, beside the
// copy the site always had. The retains the ownership rules ask for are counted without it.
let recursive withoutRepresentationRetains (lines: List(Str)) (previous: Str) (beforePrevious: Str) =
    match lines with
        | [] -> []
        | line :: rest ->
            if Ashes.Text.contains(line)("RcDup") && Ashes.Text.contains(previous)("JumpIfFalse") && Ashes.Text.contains(beforePrevious)("IsReferenceCounted")
            then withoutRepresentationRetains(rest)(line)(previous)
            else line :: withoutRepresentationRetains(rest)(line)(previous)

let withoutGuards (lines: List(Str)) = withoutRepresentationRetains(lines)("")("")

let loopLines (originText: Str) (source: Str) =
    source
    |> loopFunctionLines(originText)
    |> withoutGuards

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

// `label` owns the fresh reference-counted call result; the tail self-call's argument
// stores it in a constructor field, so the read is retained (`Borrow`, then `RcDup`) and the
// duplicate is what the field stores, while the owner's own release still fires. The loop
// parameter `n` passed to `mk` takes the pending retain skeleton. The accumulator holds records of
// the contract's types, so it lives on the reference-counted heap and its back edge and exit
// release list cells too: the owner's two releases are the ones naming its slot.
let expectTailSelfCallArgumentRetainsOwnedBinding unit =
    ownedLetInTailArgumentRecordSource
    |> loopLines("[ClosureHelper from loop]")
    |> (given (allLines) ->
        false
        |> withoutPendingRetainSkeletons(allLines)
        |> withoutGuards)
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
        |> (given (_) -> check("the back-edge owner release and the scope-exit release past the jump")(countContaining("TypeName=String OwnerSlot=")(lines) == 2))
        |> (given (_) ->
            "TypeName=String OwnerSlot="
            |> Ashes.Text.contains(lineContaining("OwnerSlot=")(lines))
            |> check("the owner release naming the let slot")))

let ownedLetInOperandSelfCallSource = "let mk n = Ashes.Text.fromInt(n)\n\nlet recursive count n acc =\n    if n == 0\n    then 0\n    else\n        let s = mk(n)\n        in\n            if n % 2 == 0\n            then 1 + count(n - 1)(s :: acc)\n            else count(n - 1)(s :: acc)\n\nAshes.IO.print(count(4)([]))"

// The self-call under `1 + ...` is an operator operand, never a tail call, so its cons
// argument stores the plain borrowed read; only the else branch's genuine tail self-call retains
// `s` for the next iteration.
let expectOperandSelfCallIsNotATailCall unit =
    ownedLetInOperandSelfCallSource
    |> loopLines("[ClosureHelper from count]")
    |> (given (allLines) ->
        false
        |> withoutPendingRetainSkeletons(allLines)
        |> withoutGuards)
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

// `r` is bound to a plain read of the loop parameter, a borrowed read that never
// retained, so `r` itself is not a runtime owner and adds neither a release at its own scope
// exit nor a retain of its reads. The parameter `acc` it aliases is a different question — the
// runtime-managed loop parameter placement this port now carries (`runtimeManagedStrOrdinals`)
// sees straight through the alias to recognize `acc` as rebuilt only through `+` on every tail
// self-call, so `acc` itself IS placed on the reference-counted heap: one predecessor release at
// the back edge (`acc`'s value before the fresh `r + "x"` replaces it) and one release at the
// loop's own exit, skipped exactly when the returned value is that same still-live reference (the
// `n == 0` arm, which returns `r` unchanged). Two releases from the loop itself, and no
// duplicate — `r`'s own reads never add one; the third is the arena-result boundary's release
// of the returned string, taken only when a caller asks for an arena result; the other two are
// `r`'s own anchors (`OwnerSlot`), never promoted to a runtime-managed release since the let
// holds the parameter's borrowed reference: one at the scope exit, and one on the back edge,
// which redefines `r` in the loop's first block. Stage 0 emits both once the body's `+` has it
// lowered against its closed type, and only the first with GRC_NO_REDEFINEDOWNERBACKEDGE set.
let expectLetAliasOfParameterIsNotAnOwner unit =
    letAliasOfParameterSource
    |> loopLines("[ClosureHelper from loop]")
    |> withoutGuards
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("the back-edge predecessor release, the exit-guarded release, the arena-result boundary's release, and the alias's two unpromoted anchors, no more")(countContaining("RcDrop")(lines) == 5 && countContaining("OwnerSlot")(lines) == 2 && countContainingBoth("CopyOutArena")("ArenaResultBoundary")(lines) == 1))
        |> (given (_) -> check("no retain of the alias or the parameter")(countContaining("RcDup")(lines) == 0)))

// The `acc` of `count` grows by one cons cell at every tail self-call, and the head it stores
// is a live owner's reference, so `acc` is a runtime-managed list: the direct argument is
// normalized at entry against the caller's ownership flag (a borrowed list copies its spine with
// its string heads), the tail branch's cons cell is allocated on the reference-counted heap, the
// back edge resets the arena to the fixed loop-entry watermark once the successor is stored, and
// the loop exit releases the accumulator under its active flag through the shared-cell walk.
let expectGrownConsAccumulatorIsRuntimeManaged unit =
    ownedLetInOperandSelfCallSource
    |> loopLines("[ClosureHelper from count]")
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
// tail null-tolerantly (the transfer of the pattern-bound tail), walks the old root
// unconditionally and clears its active flag ahead of the parameter stores, keeps the reset's
// release under the active flag (which then stands down), and the loop exit releases whatever
// the slot still holds: three shared-cell walks in all, stage 0's shape.
let expectConsumedTailListIsRuntimeManaged unit =
    consumedTailListSource
    |> loopLines("[ClosureHelper from total]")
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
        |> (given (_) -> check("the transfer's unconditional walk, the guarded walk, and the exit walk")(countContaining("rcdrop_list_shared")(lines) == 6)))

let forwardedStrHeadSource = "let recursive last (n: Int) (xs: List(Str)) (keep: Str) =\n    match xs with\n        | [] -> keep\n        | head :: rest -> last(n - 1)(rest)(head)\n\nAshes.IO.print(last(2)([\"a\", \"b\"])(\"z\"))"

// A string head forwarded by name to a different parameter of the tail self-call is a pattern
// owner whose type is reference-counted whatever holds it: the arm retains it right after the
// pattern binds it (stage 0's protective duplicate), the self-call argument retains it again for
// the successor, and the owner's own reference is released at the back edge under the resolved
// type name. That protection is what admits the head's list to the reference-counted heap even
// though its heads outlive their arm: the list is normalized at entry, the consumed tail is
// retained for the successor, and the old root's cell walk frees the cells the retains no longer
// keep.
let expectForwardedStrHeadIsProtected unit =
    forwardedStrHeadSource
    |> loopLines("[ClosureHelper from last]")
    |> (given (lines) ->
        Unit
        |> (given (_) ->
            "RcDup"
            |> countContaining
            |> (given (count) -> check("the protective duplicate, the argument retain and the successor retain")(count(lines) == 3)))
        |> (given (_) -> check("all three retains are real reference-count operations")(countContainingBoth("RcDup")("RuntimeManaged=true")(lines) == 3))
        |> (given (_) ->
            "RuntimeManaged=true"
            |> Ashes.Text.contains(lineContaining("TypeName=String OwnerSlot=")(lines))
            |> check("the owner's release under the resolved type name"))
        |> (given (_) ->
            "LoadLocal"
            |> Ashes.Text.contains(lineBefore("RcDup")("")(lines))
            |> check("the pattern binds the head before it is retained"))
        |> (given (_) -> check("the head's list is normalized to the reference-counted heap at entry")(countContainingBoth("CopyOutList")("RuntimeManaged=true")(lines) == 1))
        |> (given (_) -> check("the consumed tail is retained null-tolerantly for the successor")(countContainingBoth("RcDup")("MayBeEmpty=true")(lines) == 1))
        |> (given (_) -> check("the old root is released through the cell walk")(countContaining("rcdrop_list")(lines) > 0)))

let forwardedInnerListHeadSource = "let recursive last (n: Int) (xs: List(List(Int))) (keep: List(Int)) =\n    match xs with\n        | [] -> keep\n        | head :: rest -> last(n - 1)(rest)(head)\n\nmatch last(2)([[1], [2, 3]])([]) with\n    | [] -> Ashes.IO.print(0)\n    | first :: _ -> Ashes.IO.print(first)"

// The same shape over inner-list heads keeps the list in the arena: only a string-like head is
// protected by its pattern owner's retain, an aggregate head would need a structural release the
// placement does not name yet, so the cell walk that would free the forwarded head never runs.
let expectForwardedInnerListHeadKeepsListInArena unit =
    forwardedInnerListHeadSource
    |> loopLines("[ClosureHelper from last]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("no entry normalization of the list")(countContainingBoth("CopyOutList")("HeadCopy=InnerList")(lines) == 0))
        |> (given (_) -> check("no cell walk over the list")(countContaining("rcdrop_list")(lines) == 0))
        |> (given (_) -> check("no runtime-managed retain of the head")(countContainingBoth("RcDup")("RuntimeManaged=true")(lines) == 0)))

let copyAdtLoopSource = "type Counter =\n    | count: Int\n    | total: Int\n\nlet recursive bump (n: Int) (c: Counter) =\n    if n == 0\n    then c\n    else bump(n - 1)(Counter(count = c.count + 1, total = c.total + n))\n\nlet bumped = bump(10)(Counter(count = 0, total = 0))\n\nAshes.IO.print(bumped.count)"

// A single-constructor record loop parameter lives on the reference-counted heap: the
// borrowed argument is copied out under the caller's ownership flag at entry, each back edge
// copies the fresh arena cell out before the iteration's allocations are reclaimed and releases
// the predecessor, and the exit transfers the parameter's own value to the caller or releases it.
let expectCopyAdtLoopParameterIsRuntimeManaged unit =
    copyAdtLoopSource
    |> loopLines("[ClosureHelper from bump]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("the entry normalizes the borrowed cell under the ownership flag, and the epilogue reads the word for the arena-result request")(countContaining("LoadArgumentOwnership")(lines) == 2))
        |> (given (_) -> check("the entry copy and the back-edge copy of the cell")(countContainingBoth("CopyOutArena")("StaticSizeBytes=16 RuntimeManaged=true")(lines) == 2))
        |> (given (_) -> check("the predecessor, exit, and arena-result boundary releases under the type name")(countContainingBoth("RcDrop")("TypeName=Counter RuntimeManaged=true")(lines) == 3))
        |> (given (_) -> check("the back edge restores the loop-entry watermark")(countContaining("RestoreArenaState")(lines) == 1))
        |> (given (_) -> check("the exit transfers the parameter's own value to the caller")(countContaining("rc_tco_exit_transfer")(lines) > 0)))

let scalarTupleLoopSource = "let recursive step (n: Int) (s: (Int, Int)) =\n    match s with\n        | (a, b) ->\n            if n == 0\n            then a + b\n            else step(n - 1)((b, a + n))\n\nAshes.IO.print(step(10)((1, 2)))"

// A tuple of scalars as a loop parameter takes the record's placement under the type name
// `Tuple`: the entry copy under the ownership flag, the back-edge copy of the fresh cell, and
// the two releases; the body's scalar result never transfers the cell, so the exit release is
// unconditional under the active flag.
let expectScalarTupleLoopParameterIsRuntimeManaged unit =
    scalarTupleLoopSource
    |> loopLines("[ClosureHelper from step]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("the entry normalizes the borrowed cell under the ownership flag")(countContaining("LoadArgumentOwnership")(lines) == 1))
        |> (given (_) -> check("the entry copy and the back-edge copy of the cell")(countContainingBoth("CopyOutArena")("StaticSizeBytes=16 RuntimeManaged=true")(lines) == 2))
        |> (given (_) -> check("the predecessor and exit releases under the tuple's name")(countContainingBoth("RcDrop")("TypeName=Tuple RuntimeManaged=true")(lines) == 2))
        |> (given (_) -> check("a scalar result never transfers the cell")(countContaining("rc_tco_exit_transfer")(lines) == 0)))

let ownedChildRecordLoopSource = "type State =\n    | label: Str\n    | count: Int\n\nlet recursive step (n: Int) (s: State) =\n    if n == 0\n    then s\n    else step(n - 1)(State(label = Ashes.Text.fromInt(n), count = s.count + Ashes.Text.byteLength(s.label)))\n\nlet final = step(10)(State(label = \"seed\", count = 0))\n\nAshes.IO.print(final.label)"

// A record with a string field as a loop parameter is a contract record. The entry copy and the
// result's copy go through its normalization helper; the back edge copies the successor built
// at the self call inline, the cell and then the string out of it, and releases the dying
// successor's own string; the predecessor, exit, and arena-result boundary releases go through
// the record's dropper, and the result is the function's own, so no exit transfer is reserved.
let expectOwnedChildRecordLoopParameterIsRuntimeManaged unit =
    ownedChildRecordLoopSource
    |> loopLines("[ClosureHelper from step]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("the entry normalizes the borrowed cell under the ownership flag, and the epilogue reads the word for the arena-result request and twice more when asked for the result's independence")(countContaining("LoadArgumentOwnership")(lines) == 4))
        |> (given (_) -> check("the entry copy and the result copy through the normalization helper")(countContaining("__rcnorm_")(lines) == 2))
        |> (given (_) -> check("the back-edge copy of the cell")(countContainingBoth("CopyOutArena")("StaticSizeBytes=16 RuntimeManaged=true")(lines) == 1))
        |> (given (_) -> check("the string child copies out with the cell copy")(countContainingBoth("CopyOutArena")("SrcTemp")(lines) == 2))
        |> (given (_) -> check("the copied child is stored into the copy")(countContainingBoth("SetAdtField")("FieldIndex=0")(lines) >= 1))
        |> (given (_) -> check("the successor's own string releases as a string")(countContainingBoth("RcDrop")("TypeName=String RuntimeManaged=true")(lines) == 1))
        |> (given (_) -> check("the predecessor, exit, and arena-result boundary releases through the record's dropper")(countContaining("__rcdrop_record_")(lines) == 3))
        |> (given (_) -> check("no exit transfer for a result the function owns")(countContaining("rc_tco_exit_transfer")(lines) == 0)))

let stringFieldIntoSuccessorSource = "type State =\n    | label: Str\n    | count: Int\n\nlet recursive step (n: Int) (s: State) =\n    if n == 0\n    then s\n    else step(n - 1)(State(label = s.label, count = s.count + n))\n\nlet final = step(10)(State(label = Ashes.Text.fromInt(12345), count = 0))\n\nAshes.IO.print(final.label)"

// A string field read out of the runtime-managed record parameter and stored into its own
// successor is retained (the marker promoted once the parameter's placement is known): the back
// edge copies the successor's string and releases the dying successor's reference, and the old
// parameter's dropper releases its own, so the stored borrow needs a reference of its own.
let expectStringFieldReadIntoSuccessorIsRetained unit =
    stringFieldIntoSuccessorSource
    |> loopLines("[ClosureHelper from step]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("the field read is retained before the successor stores it")(countContainingBoth("RcDup")("RuntimeManaged=true")(lines) == 1))
        |> (given (_) -> check("no identity marker is left behind")(countContaining("RcDup")(lines) == 1))
        |> (given (_) -> check("the successor's own string releases as a string")(countContainingBoth("RcDrop")("TypeName=String RuntimeManaged=true")(lines) == 1))
        |> (given (_) -> check("the predecessor, exit, and arena-result boundary releases through the record's dropper")(countContaining("__rcdrop_record_")(lines) == 3)))

let nestedRecordLoopSource = "type State =\n    | label: Str\n    | count: Int\n\ntype Pair =\n    | previous: State\n    | current: State\n\nlet recursive walk (n: Int) (pair: Pair) =\n    if n == 0\n    then pair\n    else walk(n - 1)(Pair(previous = State(label = Ashes.Text.fromInt(n), count = n), current = pair.current))\n\nlet final = walk(3)(Pair(previous = State(label = \"a\", count = 0), current = State(label = \"b\", count = 0)))\n\nlet current = final.current\n\nAshes.IO.print(current.label)"

// A record of records as a loop parameter is placed on the reference-counted heap (the
// classification's cycle guard keys on the type's id and name together, so the nested `State`
// is not taken for a cycle back into `Pair`). Both records are contract records: the entry and
// the result copy `Pair` through its normalization helper, the back edge copies the successor
// built at the self call inline and its `State` children through theirs, and releases only the
// references the dying successor holds: the retained `current` read, through `State`'s dropper,
// and the string inside the fresh `previous` literal, never that literal's arena cell. The
// predecessor, exit, and arena-result boundary releases go through `Pair`'s dropper, which tests
// uniqueness itself. The scalar `n` stored into the literal's `count` takes stage 0's pending
// constructor-field skeleton, whose guarded duplicate stays behind a flag finalize zeroes for the
// unadmitted parameter.
let expectNestedRecordLoopParameterIsRuntimeManaged unit =
    nestedRecordLoopSource
    |> loopLines("[ClosureHelper from walk]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("the entry normalizes the borrowed cell under the ownership flag, and the epilogue reads the word for the arena-result request and twice more when asked for the result's independence")(countContaining("LoadArgumentOwnership")(lines) == 4))
        |> (given (_) -> check("the field read is retained before the successor stores it, beside the skeleton's guarded duplicate of the scalar field")(countContainingBoth("RcDup")("RuntimeManaged=true")(lines) == 2 && countContaining("rc_constructor_field_not_retained")(lines) == 2))
        |> (given (_) -> check("the entry, the two children at the back edge, and the result copy through a normalization helper")(countContaining("__rcnorm_")(lines) == 4))
        |> (given (_) -> check("only the fresh literal's string releases inline")(countContainingBoth("RcDrop")("TypeName=String RuntimeManaged=true")(lines) == 1))
        |> (given (_) -> check("the retained child, the predecessor, the exit, and the arena-result boundary release through droppers")(countContaining("__rcdrop_record_")(lines) == 4))
        |> (given (_) -> check("a fresh literal child is never tested for uniqueness")(countContaining("RcIsUnique")(lines) == 0)))

let recordListAccumulatorSource = "type State =\n    | label: Str\n    | count: Int\n\nlet recursive collect (n: Int) (s: State) (acc: List(State)) =\n    if n == 0\n    then s :: acc\n    else collect(n - 1)(State(label = Ashes.Text.fromInt(n), count = s.count + 1))(s :: acc)\n\nlet items = collect(10)(State(label = \"seed\", count = 0))([])\n\nAshes.IO.print(1)"

// A list accumulator over record heads is admitted with the record parameter consed into it:
// the entry normalizes the borrowed list through the `rc_normalize_list` deep-copy loop (each
// head copied into a fresh reference-counted cell), the record read consed at the back edge is
// retained for the cell (the marker promoted once the parameter is placed), the back edge's cons
// cell lives on the reference-counted heap, and the list's exit release and the arena-result
// boundary each walk its cells. The exit arm's cell is reference-counted as well: it retains the
// record and takes a reference to the accumulator, or a copy of it, before the loop releases its
// parameters. The record is a contract record, so every copy of it goes through its normalization
// helper.
let expectRecordListAccumulatorIsRuntimeManaged unit =
    recordListAccumulatorSource
    |> loopLines("[ClosureHelper from collect]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("the entry normalizes the borrowed list under the ownership flag, and the epilogue reads the word for the arena-result request and twice more when asked for the result's independence")(countContaining("LoadArgumentOwnership")(lines) == 4))
        |> (given (_) -> check("the entry deep-copies the list cell by cell")(countContaining("rc_normalize_list")(lines) > 0))
        |> (given (_) -> check("the record read is retained for the exit arm's cell and for the back edge's")(countContainingBoth("RcDup")("RuntimeManaged=true")(lines) == 2))
        |> (given (_) -> check("the exit arm's tail takes no retain")(countContainingBoth("RcDup")("MayBeEmpty=true")(lines) == 0))
        |> (given (_) -> check("the back edge's cons cell lives on the reference-counted heap")(countContainingBoth("Alloc ")("SizeBytes=16 RuntimeManaged=true")(lines) >= 2))
        |> (given (_) -> check("the accumulator releases as a list at the exit and at the arena-result boundary")(countContainingBoth("RcDrop")("TypeName=List RuntimeManaged=true")(lines) == 4)))

let nonAffineStrParameterSource = "let recursive collect (n: Int) (text: Str) (acc: List(Str)) =\n    if n == 0\n    then acc\n    else collect(n - 1)(text + Ashes.Text.fromInt(n))(text :: acc)\n\nlet items = collect(10)(\"seed\")([])\n\nAshes.IO.print(1)"

// A `Str` parameter read a second time outside its own successor is placed on the
// reference-counted heap by its type, not through the affine in-place append: the captured
// string is copied out at entry, the successor concatenation is placed on the reference-counted
// heap (the back edge stores it as the parameter's own value), the read consed into the sibling
// accumulator is retained for the cell, and the back edge releases the parameter's old value.
let expectNonAffineStrParameterIsRuntimeManaged unit =
    nonAffineStrParameterSource
    |> loopLines("[ClosureHelper from collect]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("the successor concatenation lives on the reference-counted heap")(countContainingBoth("ConcatStr")("RuntimeManaged=true")(lines) == 1))
        |> (given (_) -> check("the consed read is retained for the accumulator's cell")(countContainingBoth("RcDup")("RuntimeManaged=true")(lines) == 1))
        |> (given (_) -> check("the accumulator's cons cell lives on the reference-counted heap")(countContainingBoth("Alloc ")("SizeBytes=16 RuntimeManaged=true")(lines) >= 1))
        |> (given (_) -> check("the back edge releases the parameter's old value and the exits release the rest")(countContainingBoth("RcDrop")("TypeName=String RuntimeManaged=true")(lines) >= 3))
        |> (given (_) -> check("the entry copies the captured string out")(countContainingBoth("CopyOutArena")("RuntimeManaged=true")(lines) >= 1)))

let freshListRebuildSource = "let recursive step (n: Int) (items: List(Str)) (total: Int) =\n    match items with\n        | head :: _rest ->\n            if n == 0\n            then total\n            else step(n - 1)([Ashes.Text.fromInt(n), head])(total + Ashes.Text.byteLength(head))\n        | [] -> total\n\nAshes.IO.print(Ashes.Text.fromInt(step(10)([\"seed\"])(0)))"

// A list parameter rebuilt as a fresh literal at every back edge takes the ADT-slot placement
// under the type name `List`: the borrowed list is normalized at entry under the ownership flag,
// the back edge copies the fresh arena literal out with its string heads and releases the
// predecessor through the list walk, and the matched head consed into the literal is retained
// by its pattern owner once the root parameter is placed.
let expectFreshListRebuildParameterIsRuntimeManaged unit =
    freshListRebuildSource
    |> loopLines("[ClosureHelper from step]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("the list is a captured parameter, copied out unconditionally at entry")(countContaining("LoadArgumentOwnership")(lines) == 0))
        |> (given (_) -> check("the entry copy and the back-edge copy of the list")(countContainingBoth("CopyOutList")("HeadCopy=String RuntimeManaged=true")(lines) == 2))
        |> (given (_) -> check("the matched head is retained by its pattern owner")(countContainingBoth("RcDup")("RuntimeManaged=true")(lines) == 1))
        |> (given (_) -> check("the predecessor and the exit release walk the list")(countContainingBoth("RcDrop")("TypeName=List RuntimeManaged=true")(lines) >= 2)))

let consumedRecordHeadsEscapeSource = "type Item =\n    | name: Str\n    | weight: Int\n\ntype Best =\n    | item: Item\n    | score: Int\n\nlet recursive heaviest (items: List(Item)) (best: Best) =\n    match items with\n        | [] -> best\n        | head :: rest ->\n            if head.weight > best.score\n            then heaviest(rest)(Best(item = head, score = head.weight))\n            else heaviest(rest)(best)\n\nlet found = heaviest([Item(name = \"a\", weight = 3)])(Best(item = Item(name = \"none\", weight = 0), score = 0))\n\nAshes.IO.print(found.score)"

// A consumed list of records whose matched head escapes into a sibling record accumulator is
// admitted like a list of strings: the list is normalized at entry through the cell walk, the
// head's pattern owner is retained once more for the successor, the accumulator passed through
// unchanged is retained at its back edge rather than copied, and the head's promoted release
// names the record's structural dropper in both branches of the arm, since the release reaches
// the record's string child. Both records are contract records, so their copies go through
// normalization helpers, and the accumulator the loop returns is its own result rather than a
// transfer.
let expectConsumedRecordHeadsEscapeIntoAccumulator unit =
    consumedRecordHeadsEscapeSource
    |> loopLines("[ClosureHelper from heaviest]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("the list is normalized at entry through the cell walk")(countContaining("rc_normalize_list")(lines) > 0))
        |> (given (_) -> check("the accumulator is normalized under the ownership flag, and the epilogue reads the word for the arena-result request and twice more when asked for the result's independence")(countContaining("LoadArgumentOwnership")(lines) == 4))
        |> (given (_) -> check("the head's owner release names the structural dropper in both branches")(countContainingBoth("TypeName=Item OwnerSlot=")("StructuralDropperLabel=__rcdrop_structural")(lines) == 2))
        |> (given (_) -> check("the head is retained by its owner and once more for the successor, and the passed-through accumulator at its back edge")(countContainingBoth("RcDup")("RuntimeManaged=true")(lines) == 5))
        |> (given (_) -> check("no exit transfer for a result the function owns")(countContaining("rc_tco_exit_transfer")(lines) == 0)))

let findStringHeadSource = "let recursive findLong (items: List(Str)) (limit: Int) =\n    match items with\n        | [] -> \"none\"\n        | head :: rest ->\n            if Ashes.Text.byteLength(head) > limit\n            then head\n            else findLong(rest)(limit)\n\nAshes.IO.print(findLong([\"a\", \"bb\"])(1))"

// A search loop returning the matched head of a consumed string list beside a literal arm: the
// branch retains the head for the result (stage 0's `TransferDirectRuntimeManagedBranchResult`),
// the literal arm is copied to the reference-counted heap beside it, the arm's reset copies
// nothing, and the loop's closure advertises a runtime-managed result.
let expectFindLoopReturningStringHeadNormalizesTheLiteralArm unit =
    findStringHeadSource
    |> loopLines("[ClosureHelper from findLong]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("the head's owner retains once, the branch retains once more, the tail is retained for the successor")(countContainingBoth("RcDup")("RuntimeManaged=true")(lines) == 3))
        |> (given (_) -> check("the literal arm is normalized to the reference-counted heap")(countContainingBoth("CopyOutArena")("RuntimeManaged=true Purpose=RcNormalization")(lines) >= 1))
        |> (given (_) -> check("the head's owner releases under the string name")(countContainingBoth("TypeName=String OwnerSlot=")("RuntimeManaged=true")(lines) == 2)))
    |> (given (_) ->
        findStringHeadSource
        |> loopLines("[SourceFunction from findLong]")
        |> (given (lines) -> check("the loop's closure returns a runtime-managed result")(countContaining("ReturnsRuntimeManaged=true")(lines) == 1)))

let findRecordHeadSource = "type Item =\n    | name: Str\n    | weight: Int\n\nlet recursive findHeavy (items: List(Item)) (limit: Int) =\n    match items with\n        | [] -> Item(name = \"none\", weight = 0)\n        | head :: rest ->\n            if head.weight > limit\n            then head\n            else findHeavy(rest)(limit)\n\nlet found = findHeavy([Item(name = \"a\", weight = 3)])(1)\n\nAshes.IO.print(found.weight)"

// The record-head sibling of the search above: the static record arm (every field a literal)
// cannot be allocated runtime-managed by placement alone, so it is built in the arena and
// copied to the reference-counted heap by the contract record's normalization helper, its string
// child included, beside the retained head, and the loop's closure advertises a runtime-managed
// result. The entry list's heads and the result copy through the same helper.
let expectFindLoopReturningRecordHeadCopiesTheStaticArm unit =
    findRecordHeadSource
    |> loopLines("[ClosureHelper from findHeavy]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("the static record arm, the entry list's heads, and the result copy through the normalization helper")(countContaining("__rcnorm_")(lines) == 3))
        |> (given (_) -> check("the head's owner retains once, the branch retains once more, the tail is retained for the successor")(countContainingBoth("RcDup")("RuntimeManaged=true")(lines) == 3))
        |> (given (_) -> check("the head's owner releases through the structural dropper")(countContainingBoth("TypeName=Item OwnerSlot=")("StructuralDropperLabel=__rcdrop_structural")(lines) == 2)))
    |> (given (_) ->
        findRecordHeadSource
        |> loopLines("[SourceFunction from findHeavy]")
        |> (given (lines) -> check("the loop's closure returns a runtime-managed result")(countContaining("ReturnsRuntimeManaged=true")(lines) == 1)))

let forwardedGenericHeadSource = "let recursive last n xs keep =\n    match xs with\n        | [] -> keep\n        | head :: rest -> last(n - 1)(rest)(head)\n\nAshes.IO.print(last(2)([\"a\", \"b\"])(\"z\"))"

// The same shape over an unresolved element type keeps its markers as identities: the argument
// duplicate and the owner's release name the binding, not a runtime-managed type.
let expectForwardedGenericHeadKeepsIdentityMarkers unit =
    forwardedGenericHeadSource
    |> loopLines("[ClosureHelper from last]")
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
    |> loopLines("[ClosureHelper from loop]")
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

// A read-only builtin consuming the fresh result of a call releases it right after the
// read, straight from the call and through an if join whose every branch produced a fresh value.
let expectReadBuiltinReleasesFreshCallResult unit =
    Unit
    |> (given (_) -> expectReadReleasesConsumedFreshResult("direct")(readBuiltinDirectSource))
    |> (given (_) -> expectReadReleasesConsumedFreshResult("join")(readBuiltinJoinSource))

// A join with one borrowed branch (the loop's own parameter) beside a fresh one takes a reference of
// its own in the borrowed branch, so the read releases the join like a fresh result; a let-bound
// result is owned by its scope, whose exit release covers it.
let expectReadBuiltinKeepsOwnedResults unit =
    Unit
    |> (given (_) -> expectReadReleasesConsumedFreshResult("borrowed join")(readBuiltinBorrowedJoinSource))
    |> (given (_) ->
        readBuiltinLetScopeSource
        |> loopLines("[ClosureHelper from loop]")
        |> (given (lines) ->
            Unit
            |> (given (_) -> check("a let-bound result is not released by its reads")(countContaining("RcDrop")(lines) == countContainingBoth("RcDrop")("OwnerSlot=")(lines)))
            |> (given (_) -> check("the let's scope releases the result it owns")(countContainingBoth("TypeName=String OwnerSlot=")("RuntimeManaged=true")(lines) > 0))))

let affineStrAccumulatorSource = "let recursive accumulate (n: Int) (acc: Str) =\n    if n == 0\n    then acc\n    else accumulate(n - 1)(acc + \"x\")\n\nAshes.IO.print(accumulate(5)(\"\"))"

// An affine `Str` accumulator (rebuilt only through `+` rooted at itself) grows its reservation in
// place: the tail self-call's argument is the reservation-growing `ConcatStrTip`, flagged
// runtime-managed once the parameter's placement is final, so the back edge stores it as the
// successor with no predecessor release (the append consumed the old reference), restores the
// fixed loop-entry watermark and sets the active flag; the entry copies the borrowed argument out
// under the ownership flag, the exit transfers the value to the caller when it is the result or
// releases it under the flag, and the loop's closure advertises a runtime-managed result.
let expectAffineStrAccumulatorGrowsInPlace unit =
    Unit
    |> (given (_) ->
        affineStrAccumulatorSource
        |> loopLines("[ClosureHelper from accumulate]")
        |> (given (lines) ->
            Unit
            |> (given (_) -> check("the append grows the reservation in place on the reference-counted heap")(countContainingBoth("ConcatStrTip")("RuntimeManaged=true")(lines) == 1))
            |> (given (_) -> check("no copying concatenation")(countContaining("ConcatStr ")(lines) == 0))
            |> (given (_) -> check("the entry copies the borrowed argument out under the ownership flag, and the epilogue reads the word again for the arena-result request")(countContaining("LoadArgumentOwnership")(lines) == 2 && countContainingBoth("CopyOutArena")("RuntimeManaged=true")(lines) == 1))
            |> (given (_) -> check("the back edge restores the fixed loop-entry watermark")(countContaining("RestoreArenaState")(lines) == 1 && countContaining("ReclaimArenaChunks")(lines) == 1))
            |> (given (_) -> check("the exit's guarded release and the arena-result boundary's release are the only releases")(countContainingBoth("RcDrop")("TypeName=String RuntimeManaged=true")(lines) == 2 && countContaining("rc_tco_exit_drop_inactive")(lines) > 0 && countContainingBoth("CopyOutArena")("ArenaResultBoundary")(lines) == 1))
            |> (given (_) -> check("the exit transfers the value to the caller when it is the result")(countContaining("rc_tco_exit_transfer_not_selected")(lines) > 0))))
    |> (given (_) ->
        affineStrAccumulatorSource
        |> loopLines("[SourceFunction from accumulate]")
        |> (given (lines) -> check("the loop's closure advertises a runtime-managed result")(countContainingBoth("MakeClosure")("ReturnsRuntimeManaged=true")(lines) == 1)))

let arenaVariantAccumulatorSource = "type Slot =\n    | Empty\n    | Filled(Int, Int)\n\nlet recursive step (n: Int) (s: Slot) =\n    if n == 0\n    then\n        match s with\n            | Empty -> 0\n            | Filled(a, b) -> a + b\n    else\n        step(n - 1)(match s with\n            | Empty -> Filled(n, 1)\n            | Filled(a, b) -> Filled(a + 1, b + n))\n\nAshes.IO.print(Ashes.Text.fromInt(step(10)(Empty)))"

// A multi-constructor variant parameter stays in the arena and takes stage 0's fixed-watermark
// compaction at the back edge: under the growth threshold check (twice the recorded live size
// plus slack, or a crossed chunk) the successor is cloned twice above the cursor through the
// type's synthesized copier, the arena is reset to the loop-entry watermark, the clone is copied
// down onto it and stored, the chunks above are reclaimed, and the live size is recorded or the
// watermark rebased into a new chunk. The copier switches on the constructor tag and rebuilds
// each cell field by field.
let expectVariantParameterCompactsTheArena unit =
    Unit
    |> (given (_) ->
        arenaVariantAccumulatorSource
        |> loopLines("[ClosureHelper from step]")
        |> (given (lines) ->
            Unit
            |> (given (_) -> check("the back edge tests the compaction threshold")(countContaining("tco_compact_skip")(lines) == 2 && countContaining("Value=4096")(lines) == 1))
            |> (given (_) -> check("the successor is cloned twice up and once down")(countContainingBoth("MakeClosure")("FuncLabel=__deepcopy_")(lines) == 3))
            |> (given (_) -> check("the arena is reset to the fixed loop-entry watermark")(countContainingBoth("RestoreArenaState")("CursorLocalSlot=3 EndLocalSlot=4")(lines) == 1))
            |> (given (_) -> check("the live size is recorded or the watermark rebased")(countContaining("tco_compact_rebase")(lines) == 2 && countContaining("tco_compact_recorded")(lines) == 2))))
    |> (given (_) ->
        arenaVariantAccumulatorSource
        |> loopLines("[AdtDeepCopier]")
        |> (given (lines) ->
            Unit
            |> (given (_) -> check("the copier switches on the constructor tag")(countContaining("SwitchTag")(lines) == 1 && countContaining("LoadEnv")(lines) == 1))
            |> (given (_) -> check("the copier rebuilds each constructor's cell")(countContaining("AllocAdt")(lines) == 2 && countContaining("StoreMemOffset")(lines) == 2))))

let matchedPairTwoArmLoopSource = "type Tok =\n    | kind: Int\n    | text: Str\n\ntype Diag =\n    | message: Str\n    | at: Int\n\nlet readNext (position: Int) =\n    if position - position / 7 * 7 == 0\n    then (Tok(kind = 1, text = Ashes.Text.fromInt(position)), Some(Diag(message = \"odd \" + Ashes.Text.fromInt(position), at = position)))\n    else (Tok(kind = 2, text = Ashes.Text.fromInt(position)), None)\n\nlet recursive rounds (n: Int) (total: Int) =\n    if n == 0\n    then total\n    else\n        match readNext(n) with\n            | (token, None) -> rounds(n - 1)(total + Ashes.Text.byteLength(token.text))\n            | (token, Some(diagnostic)) -> rounds(n - 1)(total + Ashes.Text.byteLength(token.text) + diagnostic.at)\n\nAshes.IO.print(rounds(30)(0))"

// A loop that matches a pair a callee returned in two arms, each ending in a tail self-call. The
// pair holds contract records, so the call result is kept in an owned slot, beside the slot for
// what an earlier iteration left there: every arm's back edge releases both, and so does the
// loop's exit, once. The owner has no binding to go out of scope with its arm, so an arm that did
// not retire it left the next arm's back edge releasing the first arm's slot as well.
let expectEachArmReleasesItsMatchedPairOnce unit =
    matchedPairTwoArmLoopSource
    |> loopLines("[ClosureHelper from rounds]")
    |> (given (lines) -> check("two tail-calling arms release the owned and predecessor slots once each, and the exit releases both")(countContaining("TypeName=Tuple RuntimeManaged=true")(lines) == 6))

let selfCallOnMatchedTailSource = "type Pattern =\n    | PatternWildcard\n    | PatternConstructor(Str, List(Pattern))\n\ntype Expr =\n    | ExprBool(Bool)\n    | ExprMatch(Expr, List((Pattern, Expr, Maybe(Expr))), Maybe(Int))\n\nlet orderingCase name = (PatternConstructor(name)([]), ExprBool(true), None)\n\nlet cases accepted =\n    (let recursive acceptedCases names =\n        match names with\n            | [] -> [(PatternWildcard, ExprBool(false), None)]\n            | name :: tail -> orderingCase(name) :: acceptedCases(tail)\n    in ExprMatch(ExprBool(true), acceptedCases(accepted), None))\n\nAshes.IO.print(1)"

// A lone recursive binding's call to itself on a matched tail its result reaches: the call reads
// no ownership summary, so the tail goes under the closure's adoption bit and nothing waits in an
// owned slot for it. A forced retain there was adopted by the callee and released again from the
// caller's owned slot.
let expectSelfCallOnMatchedTailIsNotHandedOver unit =
    selfCallOnMatchedTailSource
    |> loopFunctionLines("from acceptedCases]")
    |> (given (lines) -> check("the self call's tail argument is not kept in an owned slot")(countContaining("rc_handed_over_kept")(lines) == 0))

let tupleResultLoopSource = "type Ty =\n    | TyVar(Int)\n    | TyList(Ty)\n\ntype C =\n    | name: Str\n    | args: List(Ty)\n\ntype Summary =\n    | constraints: List(C)\n    | stages: List(Int)\n\ntype Key =\n    | Same\n    | Missing\n\nlet recursive appendC left right =\n    match left with\n        | [] -> right\n        | head :: tail -> head :: appendC(tail)(right)\n\nlet recursive lookup key entries =\n    match (key, entries) with\n        | (_, []) -> None\n        | (Same, (Same, summary) :: _rest) -> Some(summary)\n        | (Missing, (Missing, summary) :: _rest) -> Some(summary)\n        | (_, _entry :: rest) -> lookup(key)(rest)\n\nlet recursive sites flows summaries requirements demands =\n    match flows with\n        | [] -> (requirements, demands)\n        | (node, key) :: rest ->\n            match lookup(key)(summaries) with\n                | Some(Summary { constraints = required }) ->\n                    sites(rest)(summaries)(appendC(required)(requirements))(node :: demands)\n                | None -> sites(rest)(summaries)(requirements)(demands)\n\nlet eq = C(name = \"Eq\", args = [TyVar(7)])\n\nlet summaries = [(Same, Summary(constraints = [eq], stages = [1]))]\n\nmatch sites([(1025, Same), (1026, Missing), (1027, Same)])(summaries)([eq])([1000]) with\n    | (_first :: _second :: _third :: [], _demands) -> Ashes.IO.print(\"three\")\n    | _ -> Ashes.IO.print(\"other\")"

let recursive firstIndexContaining (fragment: Str) (index: Int) (lines: List(Str)) =
    match lines with
        | [] -> -1
        | line :: rest ->
            if Ashes.Text.contains(line)(fragment)
            then index
            else firstIndexContaining(fragment)(index + 1)(rest)

// A loop returning a tuple of its accumulators hands the tuple's parts to a caller asking for them
// before its exit releases the accumulator parameter a part may be: the parts' retains come first.
let expectTupleResultPartsPrecedeExitRelease unit =
    tupleResultLoopSource
    |> loopFunctionLines("[ClosureHelper from sites]")
    |> (given (lines) ->
        (firstIndexContaining("tuple_parts_not_requested")(0)(lines), firstIndexContaining("rc_tco_exit_drop_inactive")(0)(lines)) |> (given (indices) ->
            match indices with
                | (partsIndex, exitIndex) -> check("the tuple result's parts are handed over before the exit releases the loop parameters")(partsIndex >= 0 && exitIndex >= 0 && partsIndex < exitIndex)))

let routedTreePushTwoValueTypesSource = "type Tree(V) =\n    | Leaf\n    | Node(Tree(V), V, Tree(V))\n\nlet push value =\n    (let recursive go tree =\n        match tree with\n            | Leaf -> Node(Leaf)(value)(Leaf)\n            | Node(left, existing, right) ->\n                right\n                |> go\n                |> Node(left)(existing)\n    in go)\n\nlet leftOf tree =\n    match tree with\n        | Leaf -> Leaf\n        | Node(left, _value, _right) -> left\n\nlet recursive assign (labels: List(Str)) (flag: Bool) (m: Tree(Int)) =\n    match labels with\n        | [] -> leftOf(m)\n        | _label :: rest ->\n            if flag\n            then\n                m\n                |> push(1)\n                |> assign(rest)(false)\n            else\n                m\n                |> push(2)\n                |> assign(rest)(true)\n\nlet recursive mark (names: List(Str)) (flag: Bool) (m: Tree(Bool)) =\n    match names with\n        | [] -> leftOf(m)\n        | _name :: rest ->\n            if flag\n            then\n                m\n                |> push(true)\n                |> mark(rest)(false)\n            else\n                m\n                |> push(false)\n                |> mark(rest)(true)\n\nlet a = assign([\"a\", \"b\", \"c\"])(true)(Leaf)\n\nlet b = mark([\"x\", \"y\"])(true)(Leaf)\n\nmatch (a, b) with\n    | (Node(_left, first, _right), Node(_bLeft, flag, _bRight)) ->\n        if flag\n        then\n            first\n            |> Ashes.Text.fromInt\n            |> Ashes.IO.print\n        else Ashes.IO.print(\"no\")\n    | _ -> Ashes.IO.print(\"empty\")"

// Two loops routed to the in-place specialization of the same tree insertion (defined in the
// source, since this harness lowers without the shipped library), each from two call sites so the
// second takes the cached specialization, at `Int` and then `Bool` values: the cached calls bind
// the callee at a fresh instantiation, so the second loop lowers and both reach their
// specializations. Each loop returns a subtree of its tree, not the tree itself: a loop returning
// its own tree holds a changing generic variant parameter, which stage 0 places on the
// reference-counted heap ahead of the specialization scan (`IsGeneralRcGenericVariantLoopParameter`),
// so no call would be routed.
let expectRoutedCallsAtTwoValueTypesLower unit =
    routedTreePushTwoValueTypesSource
    |> loopFunctionLines("[ClosureHelper from mark]")
    |> (given (lines) -> check("the second loop's calls reach the specialization")(countContaining("__reuse")(lines) > 0))

let choiceVariantAccumulatorSource = "type Tree =\n    | Leaf\n    | Node(Tree, Str, Tree)\n\nlet recursive insert (k: Str) t =\n    match t with\n        | Leaf -> Node(Leaf, k, Leaf)\n        | Node(l, x, r) ->\n            if Ashes.Text.byteLength(k) < Ashes.Text.byteLength(x)\n            then Node(insert(k)(l), x, r)\n            else Node(l, x, insert(k)(r))\n\nlet put (k: Str) t = insert(k)(t)\n\nlet recursive build i n m =\n    if i >= n\n    then m\n    else\n        let m2 =\n            if i - i / 2 * 2 == 0\n            then put(\"a\")(m)\n            else put(\"b\")(m)\n        in build(i + 1)(n)(m2)\n\nlet recursive size t =\n    match t with\n        | Leaf -> 0\n        | Node(l, _x, r) -> size(l) + 1 + size(r)\n\nAshes.IO.print(Ashes.Text.fromInt(size(build(0)(4)(Leaf))))"

// A variant of the contract's types whose successor is a choice between two calls consuming it is
// placed on the reference-counted heap whatever its self-call shape: the list-only choice shape
// does not keep a named parameter in the arena, so the back edge releases the old root under its
// active flag.
let expectChoiceShapedVariantParameterIsRuntimeManaged unit =
    choiceVariantAccumulatorSource
    |> loopLines("[ClosureHelper from build]")
    |> (given (lines) -> check("the back edge releases the variant's old root under the active flag")(countContaining("rc_tco_drop_inactive")(lines) > 0))

let letBoundMatchMapAccumulatorSource = "type Table(V) =\n    | TLeaf\n    | TNode(Table(V), Str, V, Table(V))\n\nlet recursive setStr (k: Str) value t =\n    match t with\n        | TLeaf -> TNode(TLeaf, k, value, TLeaf)\n        | TNode(l, x, w, r) ->\n            if k == x\n            then TNode(l, k, value, r)\n            else\n                if Ashes.Text.byteLength(k) < Ashes.Text.byteLength(x)\n                then TNode(setStr(k)(value)(l), x, w, r)\n                else TNode(l, x, w, setStr(k)(value)(r))\n\nlet recursive size t =\n    match t with\n        | TLeaf -> 0\n        | TNode(l, _x, _w, r) -> size(l) + 1 + size(r)\n\ntype Choice =\n    | Even\n    | Odd(Int)\n\nlet recursive build i n m =\n    if i >= n\n    then m\n    else\n        let tag =\n            if i - i / 2 * 2 == 0\n            then Even\n            else Odd(i)\n        in\n            let m2 =\n                match tag with\n                    | Even -> setStr(Ashes.Text.fromInt(i))(i)(m)\n                    | Odd(k) -> setStr(Ashes.Text.fromInt(k))(i + 7)(m)\n            in build(i + 1)(n)(m2)\n\nAshes.IO.print(Ashes.Text.fromInt(size(build(0)(4)(TLeaf))))"

// A map's tree (a local generic table, since this harness lowers without the shipped library)
// threaded through a loop that returns it is a changing generic variant of the
// contract's types: it is placed on the reference-counted heap ahead of the specialization scan,
// so neither call is routed to the in-place specialization and the back edge releases the old
// root under its active flag.
let expectGenericVariantLoopParameterIsRuntimeManaged unit =
    letBoundMatchMapAccumulatorSource
    |> loopLines("[ClosureHelper from build]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("the back edge releases the map's old root under the active flag")(countContaining("rc_tco_drop_inactive")(lines) > 0))
        |> (given (_) -> check("no call is routed to the in-place specialization")(countContaining("__reuse")(lines) == 0)))

let tupleResultMapArmsSource = "type Table(V) =\n    | TLeaf\n    | TNode(Table(V), Str, V, Table(V))\n\nlet recursive setStr (k: Str) value t =\n    match t with\n        | TLeaf -> TNode(TLeaf, k, value, TLeaf)\n        | TNode(l, x, w, r) ->\n            if k == x\n            then TNode(l, k, value, r)\n            else\n                if Ashes.Text.byteLength(k) < Ashes.Text.byteLength(x)\n                then TNode(setStr(k)(value)(l), x, w, r)\n                else TNode(l, x, w, setStr(k)(value)(r))\n\nlet recursive size t =\n    match t with\n        | TLeaf -> 0\n        | TNode(l, _x, _w, r) -> size(l) + 1 + size(r)\n\nlet recursive build i n m =\n    if i >= n\n    then (m, i)\n    else\n        let m2 =\n            if i - i / 2 * 2 == 0\n            then setStr(Ashes.Text.fromInt(i - i / 10 * 10))(i)(m)\n            else setStr(Ashes.Text.fromInt(i - i / 10 * 10))(i + 7)(m)\n        in build(i + 1)(n)(m2)\n\nmatch build(0)(4)(TLeaf) with\n    | (built, count) -> Ashes.IO.print(Ashes.Text.fromInt(size(built) * 1000 + count))"

// A call routed to the map's in-place specialization from an arm of the loop body hands its result
// on as the rewritten accumulator: when either arm's call is routed there, no call result in the
// loop is normalized into an owned call-result slot.
let expectRoutedSpecializationResultIsNotNormalized unit =
    tupleResultMapArmsSource
    |> loopLines("[ClosureHelper from build]")
    |> (given (lines) -> check("a routed call's result is not normalized into an owned slot")(countContaining("__reuse")(lines) == 0 || countContaining("general_rc_result_normalize")(lines) == 0))

let owningClosureRecordSource = "type Box =\n    | reader: Int -> Str\n\nlet box (s: Str) =\n    Box(reader = given (u: Int) -> s)\n\nlet k =\n    match box(Ashes.Text.fromInt(5) + \"x\") with\n        | Box { reader = reader } -> Ashes.Text.byteLength(reader(0))\n\nAshes.IO.print(Ashes.Text.fromInt(k))"

// A record returned with a closure over the entry-normalized string parameter owns that closure,
// and the closure owns the parameter: the environment and the closure are allocated on the
// reference-counted heap, the closure's fourth word holds the dropper releasing the parameter,
// and the record cell is reference-counted too.
let expectOwningClosureRecordIsRuntimeManaged unit =
    owningClosureRecordSource
    |> loopFunctionLines("[SourceFunction from box]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("the closure is allocated on the reference-counted heap")(countContainingBoth("MakeClosure")("RuntimeManaged=true")(lines) == 1))
        |> (given (_) -> check("its environment is allocated on the reference-counted heap")(countContainingBoth("Alloc ")("RuntimeManaged=true")(lines) > 0))
        |> (given (_) -> check("its fourth word holds the capture dropper")(countContainingBoth("LoadFuncAddr")("__rc_cdrop_")(lines) == 1 && countContainingBoth("StoreMemOffset")("OffsetBytes=24")(lines) == 1))
        |> (given (_) -> check("the record cell is reference-counted")(countContainingBoth("AllocAdt")("RuntimeManaged=true")(lines) == 1)))

let letBoundStringArmTupleSource = "type Tok =\n    | kind: Int\n    | text: Str\n\nlet readNext (position: Int) =\n    (let text = Ashes.Text.fromInt(position)\n    in\n        if position < 1000\n        then (Tok(kind = 1, text = text), None)\n        else (Tok(kind = 2, text = text), Some(position)))\n\nlet recursive scan (count: Int) (position: Int) (kinds: Int) (bytes: Int) =\n    if position >= count\n    then Ashes.Text.fromInt(kinds) + \"|\" + Ashes.Text.fromInt(bytes)\n    else\n        match readNext(position) with\n            | (Tok { kind = kind, text = text }, _note) -> scan(count)(position + 1)(kinds + kind)(bytes + Ashes.Text.byteLength(text))\n\n0\n|> scan(100)(0)(0)\n|> Ashes.IO.print"

// A record built in an `if` arm from a `let`-bound string, inside a tuple the function returns,
// stays in the arena: a let-bound name is no borrowed string a reference-counted parent copies
// (stage 0's `Binding.Scheme`), so neither arm copies the string out or allocates the record on
// the reference-counted heap.
let expectLetBoundStringKeepsArmRecordInArena unit =
    letBoundStringArmTupleSource
    |> loopFunctionLines("[SourceFunction from readNext]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("no arm allocates the record on the reference-counted heap")(countContainingBoth("AllocAdt")("RuntimeManaged=true")(lines) == 0))
        |> (given (_) -> check("no arm copies the let-bound string out")(countContainingBoth("CopyOutArena")("RcNormalization")(lines) == 0)))

let coroutineCalleeSource = "let echo (s: Str) = async(s)\n\nlet recursive fill (n: Int) (acc: Str) =\n    if n == 0\n    then acc\n    else fill(n - 1)(acc + \"abcdefgh\")\n\nlet compute =\n    async(let recursive loop (n: Int) (total: Int) =\n        if n == 0\n        then total\n        else\n            match await echo(n\n            |> Ashes.Text.fromInt\n            |> fill(2)) with\n                | Error(_message) -> -1\n                | Ok(kept) -> loop(n - 1)(total + Ashes.Text.byteLength(kept))\n    in loop(10)(0))\n\nmatch Ashes.Task.run(compute) with\n    | Ok(n) -> Ashes.IO.print(Ashes.Text.fromInt(n))\n    | Error(_e) -> Ashes.IO.print(\"err\")"

// A function an `async` body calls may run inside a coroutine, so it places nothing on the
// reference-counted heap (stage 0's `AllowsAsyncIndependentRcPlacement`): `fill`'s concatenation
// and its result stay in the arena.
let expectCoroutineCalleeKeepsValuesInArena unit =
    coroutineCalleeSource
    |> loopFunctionLines("[ClosureHelper from fill]")
    |> (given (lines) -> check("a function called from an async body places nothing on the reference-counted heap")(countContaining("RuntimeManaged=true")(lines) == 0))

// The async loop reads no callee's returns bit, so the callee's result is copied to the arena at
// its call boundary rather than onto the reference-counted heap.
let expectCoroutineCallResultCopiedToArena unit =
    coroutineCalleeSource
    |> loopFunctionLines("[ClosureHelper from $async_helper")
    |> (given (lines) -> check("the async loop copies a call result to the arena")(countContainingBoth("CopyOutArena")("RcNormalization")(lines) == 0))

let frameRetainedCaptureSource = "let build (n: Int) = Ashes.Text.fromInt(n) + \"-tail\"\n\nlet measure (n: Int) =\n    (let text = build(n)\n    in\n        async(match await Ashes.Task.sleep(0) with\n            | Ok(_u) -> Ashes.Text.byteLength(text)\n            | Error(_e) -> -1))\n\nmatch Ashes.Task.run(measure(7)) with\n    | Ok(n) -> Ashes.IO.print(Ashes.Text.fromInt(n))\n    | Error(_e) -> Ashes.IO.print(\"err\")"

// A task frame takes its own reference to a captured reference-counted string (stage 0's
// `RetainCoroutineCapture`) and its frame dropper releases it: the task is built from the arena
// closure, with no reference-counted copy of it, and names the dropper.
let expectTaskFrameRetainsCapture unit =
    frameRetainedCaptureSource
    |> loopFunctionLines("[SourceFunction from measure]")
    |> (given (lines) ->
        Unit
        |> (given (_) -> check("the captured string is retained for the frame")(countContainingBoth("RcDup")("RuntimeManaged=true")(lines) == 1))
        |> (given (_) -> check("the task is built from the arena closure")(countContaining("CopyOutClosure")(lines) == 0))
        |> (given (_) -> check("the task names its frame dropper")(countContainingBoth("CreateTask")("_frame_drop")(lines) == 1)))

let frameCaptureResultSource (result: Str) = "let build (n: Int) = Ashes.Text.fromInt(n) + \"-tail\"\n\nlet measure (n: Int) =\n    (let text = build(n)\n    in\n        async(match await Ashes.Task.sleep(0) with\n            | Ok(_u) -> " + result + "\n            | Error(_e) -> \"err\"))\n\nmatch Ashes.Task.run(measure(7)) with\n    | Ok(t) -> Ashes.IO.print(t)\n    | Error(_e) -> Ashes.IO.print(\"err\")"

// The frame releases a retained capture at teardown unless the task's result can share it (stage
// 0's `CaptureSharedWithResult`): a string built from the capture holds nothing of it, so the task
// names a frame dropper; the capture returned as it is keeps the frame's reference, and the frame
// releases nothing.
let expectFrameKeepsOnlyACaptureTheResultShares unit =
    Unit
    |> (given (_) ->
        "text + \"!\""
        |> frameCaptureResultSource
        |> loopFunctionLines("[SourceFunction from measure]")
        |> (given (lines) -> check("a capture the result is built from is released by the frame")(countContainingBoth("CreateTask")("_frame_drop")(lines) == 1)))
    |> (given (_) ->
        "text"
        |> frameCaptureResultSource
        |> loopFunctionLines("[SourceFunction from measure]")
        |> (given (lines) ->
            Unit
            |> (given (_) -> check("a capture the result returns is still retained for the frame")(countContainingBoth("RcDup")("RuntimeManaged=true")(lines) == 1))
            |> (given (_) -> check("a capture the result returns is not released by the frame")(countContainingBoth("CreateTask")("_frame_drop")(lines) == 0))))

let runTcoOwnershipRulesTests unit =
    unit
    |> expectTailSelfCallArgumentRetainsOwnedBinding
    |> (given (_) -> expectAffineStrAccumulatorGrowsInPlace(Unit))
    |> (given (_) -> expectVariantParameterCompactsTheArena(Unit))
    |> (given (_) -> expectOperandSelfCallIsNotATailCall(Unit))
    |> (given (_) -> expectLetAliasOfParameterIsNotAnOwner(Unit))
    |> (given (_) -> expectGrownConsAccumulatorIsRuntimeManaged(Unit))
    |> (given (_) -> expectConsumedTailListIsRuntimeManaged(Unit))
    |> (given (_) -> expectForwardedStrHeadIsProtected(Unit))
    |> (given (_) -> expectForwardedInnerListHeadKeepsListInArena(Unit))
    |> (given (_) -> expectCopyAdtLoopParameterIsRuntimeManaged(Unit))
    |> (given (_) -> expectScalarTupleLoopParameterIsRuntimeManaged(Unit))
    |> (given (_) -> expectOwnedChildRecordLoopParameterIsRuntimeManaged(Unit))
    |> (given (_) -> expectStringFieldReadIntoSuccessorIsRetained(Unit))
    |> (given (_) -> expectNestedRecordLoopParameterIsRuntimeManaged(Unit))
    |> (given (_) -> expectRecordListAccumulatorIsRuntimeManaged(Unit))
    |> (given (_) -> expectNonAffineStrParameterIsRuntimeManaged(Unit))
    |> (given (_) -> expectFreshListRebuildParameterIsRuntimeManaged(Unit))
    |> (given (_) -> expectConsumedRecordHeadsEscapeIntoAccumulator(Unit))
    |> (given (_) -> expectFindLoopReturningStringHeadNormalizesTheLiteralArm(Unit))
    |> (given (_) -> expectFindLoopReturningRecordHeadCopiesTheStaticArm(Unit))
    |> (given (_) -> expectForwardedGenericHeadKeepsIdentityMarkers(Unit))
    |> (given (_) -> expectReadBuiltinReleasesFreshCallResult(Unit))
    |> (given (_) -> expectReadBuiltinKeepsOwnedResults(Unit))
    |> (given (_) -> expectEachArmReleasesItsMatchedPairOnce(Unit))
    |> (given (_) -> expectSelfCallOnMatchedTailIsNotHandedOver(Unit))
    |> (given (_) -> expectTupleResultPartsPrecedeExitRelease(Unit))
    |> (given (_) -> expectRoutedCallsAtTwoValueTypesLower(Unit))
    |> (given (_) -> expectCoroutineCalleeKeepsValuesInArena(Unit))
    |> (given (_) -> expectCoroutineCallResultCopiedToArena(Unit))
    |> (given (_) -> expectTaskFrameRetainsCapture(Unit))
    |> (given (_) -> expectFrameKeepsOnlyACaptureTheResultShares(Unit))
    |> (given (_) -> expectLetBoundStringKeepsArmRecordInArena(Unit))
    |> (given (_) -> expectChoiceShapedVariantParameterIsRuntimeManaged(Unit))
    |> (given (_) -> expectGenericVariantLoopParameterIsRuntimeManaged(Unit))
    |> (given (_) -> expectRoutedSpecializationResultIsNotNormalized(Unit))
    |> (given (_) -> expectOwningClosureRecordIsRuntimeManaged(Unit))
    |> (given (_) -> Ashes.IO.print("all self-hosted tco ownership rule tests passed"))
