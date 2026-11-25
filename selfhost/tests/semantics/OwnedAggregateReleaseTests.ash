// The runtime-managed aggregate rules of the lowering, checked on the lowered IR text: a `let`
// bound to a fresh list of runtime strings places the list on the reference-counted heap and
// walks its spine inline at the scope exit, a lambda whose body is a fresh record tree allocates
// the record runtime-managed and advertises the placement on its closure, an owned record is
// released through its constructor's field walk, and an escaping tuple, list literal, or cons
// cell retains the owned bindings it stores. The syntactic predicates behind the requests are
// checked on hand-built syntax.
import Ashes.Collection.List.length
import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.AggregateOwnership
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.CoreLowering.Raw
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
export (
    value runOwnedAggregateReleaseTests,
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

let recursive functionBody (lines: List(Str)) (collected: List(Str)) =
    match lines with
        | [] -> Ashes.Collection.List.reverse(collected)
        | line :: rest ->
            if isFunctionHeader(line)
            then Ashes.Collection.List.reverse(collected)
            else functionBody(rest)(line :: collected)

// The lines of the function whose header carries `originText`, up to the next header.
let recursive functionLines (originText: Str) (lines: List(Str)) =
    match lines with
        | [] -> test.fail("no function with origin " + originText)
        | line :: rest ->
            if isFunctionHeader(line) && Ashes.Text.contains(line)(originText)
            then functionBody(rest)([])
            else functionLines(originText)(rest)

let recursive countContaining (fragment: Str) (lines: List(Str)) =
    match lines with
        | [] -> 0
        | line :: rest ->
            if Ashes.Text.contains(line)(fragment)
            then 1 + countContaining(fragment)(rest)
            else countContaining(fragment)(rest)

let recursive countContainingBoth (fragment: Str) (other: Str) (lines: List(Str)) =
    match lines with
        | [] -> 0
        | line :: rest ->
            if Ashes.Text.contains(line)(fragment) && Ashes.Text.contains(line)(other)
            then 1 + countContainingBoth(fragment)(other)(rest)
            else countContainingBoth(fragment)(other)(rest)

// The line following the first line containing `fragment`.
let recursive lineAfter (fragment: Str) (lines: List(Str)) =
    match lines with
        | [] -> ""
        | line :: next :: rest ->
            if Ashes.Text.contains(line)(fragment)
            then next
            else lineAfter(fragment)(next :: rest)
        | _line :: [] -> ""

let ownedListSource = "let describe n =\n    let labels = [Ashes.Text.fromInt(n), Ashes.Text.fromInt(7)]\n    in\n        match labels with\n            | first :: _ -> Ashes.Text.byteLength(first)\n            | [] -> 0\n\nAshes.IO.print(Ashes.Text.fromInt(describe(1)))"

// A `let` list of fresh strings matched immediately: the strings and both cells are allocated
// runtime-managed, the scope exit walks the spine releasing each string head, testing each cell
// for another owner first since a copy under the representation test may share it, and no
// placeable list drop names the owner slot.
let testOwnedListLetWalksSpineInline unit =
    ownedListSource
    |> loweredLines
    |> functionLines("SourceFunction from describe")
    |> (given (lines: List(Str)) ->
        Unit
        |> (given (_) ->
            lines
            |> countContainingBoth("TextFromInt")("RuntimeManaged=true")
            |> test.assertEqual(2))
        |> (given (_) ->
            lines
            |> countContaining("SizeBytes=16 RuntimeManaged=true")
            |> test.assertEqual(2))
        |> (given (_) ->
            lines
            |> countContaining("Target=rcdrop_list_end_")
            |> test.assertEqual(2))
        |> (given (_) ->
            lines
            |> countContaining("TypeName=String RuntimeManaged=true")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> countContaining("TypeName=List RuntimeManaged=true")
            |> test.assertEqual(2))
        |> (given (_) ->
            lines
            |> countContaining("TypeName=List OwnerSlot=")
            |> test.assertEqual(0)))

let recordSource = "type Label =\n    | text: Str\n    | width: Int\n\nlet label n = Label(text = Ashes.Text.fromInt(n), width = n)\n\nlet made = label(7)\n\nmatch made with\n    | Label { text = text } -> Ashes.IO.print(text)"

// A lambda whose body is a fresh record tree with a fresh string field allocates the record
// runtime-managed (its string field asked for a runtime string) and its closure advertises the
// placement.
let testLambdaReturningRecordAllocatesRuntimeManaged unit =
    recordSource
    |> loweredLines
    |> (given (lines: List(Str)) ->
        Unit
        |> (given (_) ->
            lines
            |> functionLines("SourceFunction from label")
            |> countContaining("AllocAdt              Target=3 Tag=0 FieldCount=2 RuntimeManaged=true Tagless=true")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> functionLines("SourceFunction from label")
            |> countContaining("TextFromInt           Target=1 ValueTemp=0 RuntimeManaged=true")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> functionLines("ProgramEntry")
            |> countContaining("FuncLabel=lambda_0 EnvPtrTemp=0 EnvSizeBytes=0 ReturnsRuntimeManaged=true")
            |> test.assertEqual(1)))

// The record is a contract record, so the top-level `let` owning it is released by its owner on
// each path out of the match that reads it, after the arm has taken the string field it needs,
// and no field walk is emitted inline at the scope exit.
let testOwnedRecordLetReleasedByItsOwner unit =
    recordSource
    |> loweredLines
    |> functionLines("ProgramEntry")
    |> (given (lines: List(Str)) ->
        Unit
        |> (given (_) ->
            lines
            |> countContaining("Target=rc_drop_shared_")
            |> test.assertEqual(0))
        |> (given (_) ->
            lines
            |> countContaining("RcDrop                SourceTemp=6 TypeName=Label OwnerSlot=9")
            |> test.assertEqual(2))
        |> (given (_) ->
            lines
            |> lineAfter("GetAdtField           Target=11 Ptr=8 FieldIndex=0 Tagless=true")
            |> test.assertEqual("    RcDrop                SourceTemp=6 TypeName=Label OwnerSlot=9")))

let aggregateSource = "let label n = Ashes.Text.fromInt(n)\n\nlet pair n =\n    let first = label(n)\n    in\n        let second = label(7)\n        in (first, second)\n\nlet listed n =\n    let first = label(n)\n    in\n        let second = label(7)\n        in [first, second]\n\nlet prefixed n =\n    let first = label(n)\n    in\n        let rest = listed(7)\n        in first :: rest\n\nmatch prefixed(1) with\n    | first :: _ -> Ashes.IO.print(first)\n    | [] -> Ashes.IO.print(\"empty\")"

// An escaping tuple of two owned strings is allocated runtime-managed with both children
// retained before the cell, each owner released after its retain.
let testEscapingTupleRetainsOwnedChildren unit =
    aggregateSource
    |> loweredLines
    |> functionLines("SourceFunction from pair")
    |> (given (lines: List(Str)) ->
        Unit
        |> (given (_) ->
            lines
            |> Ashes.Collection.List.filter(given (line: Str) -> Ashes.Text.contains(line)("RuntimeManaged=true"))
            |> countContaining("RcDup                 Target=")
            |> test.assertEqual(2))
        |> (given (_) ->
            lines
            |> countContaining("Alloc                 Target=12 SizeBytes=16 RuntimeManaged=true")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> lineAfter("RcDup                 Target=13")
            |> test.assertEqual("    RcDrop                SourceTemp=3 TypeName=String OwnerSlot=7 RuntimeManaged=true")))

// The end-to-end programs the cases in this file lower are copied in with their library calls replaced:
// this harness lowers without the shipped library, so `reverse`, `length` and `map` are defined in
// the program and `Ashes.Text.length` reads `Ashes.Text.byteLength`. The shapes the cases count
// are the same (checked against the compiler's dump of both versions).
let callerAliasSource = "let recursive reverseOnto acc xs =\n    match xs with\n        | [] -> acc\n        | x :: rest -> reverseOnto(x :: acc)(rest)\n\nlet reverse xs = reverseOnto([])(xs)\n\nlet recursive length xs =\n    match xs with\n        | [] -> 0\n        | _ :: rest -> 1 + length(rest)\n\nlet recursive map f xs =\n    match xs with\n        | [] -> []\n        | x :: rest -> f(x) :: map(f)(rest)\n\nlet recursive parseParams reversed (tokens: List(Str)) =\n    match tokens with\n        | [] -> (reverse(reversed), 0)\n        | parameter :: rest -> parseParams((parameter, Some(parameter + \"!\")) :: reversed)(rest)\n\nlet recursive parameterNames parameters =\n    match parameters with\n        | [] -> []\n        | (name, _annotation) :: tail -> name :: parameterNames(tail)\n\nlet probe (n: Int) =\n    (let header =\n        if n < 0\n        then ([], [n])\n        else\n            match parseParams([])([\"seen\" + Ashes.Text.fromInt(n), \"h\"]) with\n                | (parameters, after) -> (parameters, [after])\n    in\n        match header with\n            | (parameters, requirements) ->\n                length(parameterNames(parameters)) + length(requirements))\n\nlet recursive loop (n: Int) (acc: Int) =\n    if n == 0\n    then acc\n    else loop(n - 1)(acc + probe(n))\n\n0\n|> loop(10)\n|> Ashes.IO.print"

// `parseParams((parameter, Some(parameter + "!")) :: reversed)(rest)`: the tuple at the head of the
// accumulator's successor cell holds a pattern owner's read inside the back-edge argument, so it
// stays on the reference-counted heap with its cell, and the accumulator keeps its placement: the
// loop entry normalizes it (more than the one list walk the arena tuple left).
let testBackEdgeHeadTupleKeepsAccumulatorPlaced unit =
    callerAliasSource
    |> loweredLines
    |> functionLines("ClosureHelper from parseParams")
    |> Ashes.Collection.List.filter(given (line: Str) -> Ashes.Text.startsWith(line)("  rc_normalize_list_") && !Ashes.Text.contains(line)("_end_") && !Ashes.Text.contains(line)("_first_") && !Ashes.Text.contains(line)("_linked_") && !Ashes.Text.contains(line)("_cell_"))
    |> length
    |> (given (count: Int) -> count > 1)
    |> test.assertEqual(true)

let nestedCallTupleSourcePath = "tests/loop_head_binders_in_argument_tuple.ash"

// `lowerItems(rest)(prepareGroup([(name, value)])(total))`: the tuple of pattern parts is in the
// argument of a call nested in the back-edge argument, whose frame is that call's own. The tuple
// stays in the arena (the one reference-counted allocation left is the parameter's entry
// normalization) and the two owner duplicates it holds are released right after the call's
// window, as stage 0 does.
let testNestedCallTupleInBackEdgeArgumentReleasesOwners unit =
    match Ashes.IO.File.readText(nestedCallTupleSourcePath) with
        | Error(message) -> test.fail("could not read " + nestedCallTupleSourcePath + ": " + message)
        | Ok(source) ->
            source
            |> loweredLines
            |> functionLines("ClosureHelper from lowerItems")
            |> (given (lines: List(Str)) ->
                Unit
                |> (given (_) ->
                    lines
                    |> countContainingBoth("Alloc ")("RuntimeManaged=true")
                    |> test.assertEqual(1))
                |> (given (_) ->
                    lines
                    |> Ashes.Collection.List.filter(given (line: Str) -> Ashes.Text.contains(line)("TypeName=String RuntimeManaged=true") && Ashes.Text.contains(line)("RcDrop ") && !Ashes.Text.contains(line)("OwnerSlot="))
                    |> length
                    |> test.assertEqual(2)))

// An escaping list literal of two owned strings is a runtime list whose cells retain their heads.
let testEscapingListLiteralRetainsOwnedChildren unit =
    aggregateSource
    |> loweredLines
    |> functionLines("SourceFunction from listed")
    |> (given (lines: List(Str)) ->
        Unit
        |> (given (_) ->
            lines
            |> Ashes.Collection.List.filter(given (line: Str) -> Ashes.Text.contains(line)("RuntimeManaged=true"))
            |> countContaining("RcDup                 Target=")
            |> test.assertEqual(2))
        |> (given (_) ->
            lines
            |> countContaining("SizeBytes=16 RuntimeManaged=true")
            |> test.assertEqual(2)))

// An escaping cons onto an owned list is an arena cell that stores an arena copy of its owned
// head, releasing the head's retain at once, and retains its owned tail (null-tolerantly). The
// list owner's scope exit walks the spine under a uniqueness test, and so does the function's
// exit for the owned slot holding the cell's tail retain, as in stage 0's
// `aggregate_children_retain` fixture.
let testEscapingConsRetainsHeadAndTail unit =
    aggregateSource
    |> loweredLines
    |> functionLines("SourceFunction from prefixed")
    |> (given (lines: List(Str)) ->
        Unit
        |> (given (_) ->
            lines
            |> countContaining("RcDup                 Target=10 SourceTemp=9 RuntimeManaged=true")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> countContaining("RcDup                 Target=13 SourceTemp=12 RuntimeManaged=true MayBeEmpty=true")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> countContaining("Alloc                 Target=15 SizeBytes=16")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> countContaining("CopyOutArena          DestTemp=14 SrcTemp=10 Purpose=IndependentClone")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> countContainingBoth("Alloc                 Target=15")("RuntimeManaged")
            |> test.assertEqual(0))
        |> (given (_) ->
            lines
            |> countContaining("Target=rcdrop_list_shared_")
            |> test.assertEqual(2))
        |> (given (_) ->
            lines
            |> countContaining("RcIsUnique")
            |> test.assertEqual(2)))

let matchOn (name: Str) (cases: List((Pattern, Expr, Maybe(Expr)))) = ExprMatch(ExprVar(name))(cases)(None)

let printOf (expression: Expr) =
    ExprCall(ExprQualifiedVar("Ashes.IO")("print"))(expression)(false)(callArgumentsInline)

// `mentionsName` sees through spans and respects shadowing by a `let`, a lambda, and a pattern.
let testMentionsNameRespectsShadowing unit =
    Unit
    |> (given (_) ->
        ExprVar("x")
        |> ExprAdd(ExprInt(1))
        |> ExprAt(TextSpan(start = 0, end = 1))
        |> mentionsName("x")
        |> test.assertEqual(true))
    |> (given (_) ->
        []
        |> ExprLet("x")(ExprInt(1))(ExprVar("x"))([])(None)
        |> mentionsName("x")
        |> test.assertEqual(false))
    |> (given (_) ->
        []
        |> ExprLet("y")(ExprVar("x"))(ExprVar("y"))([])(None)
        |> mentionsName("x")
        |> test.assertEqual(true))
    |> (given (_) ->
        None
        |> ExprLambda("x")(ExprVar("x"))
        |> mentionsName("x")
        |> test.assertEqual(false))
    |> (given (_) ->
        [(PatternVar("x"), ExprVar("x"), None)]
        |> matchOn("xs")
        |> mentionsName("x")
        |> test.assertEqual(false))
    |> (given (_) ->
        [(PatternWildcard, ExprVar("x"), None)]
        |> matchOn("xs")
        |> mentionsName("x")
        |> test.assertEqual(true))

// A list is fresh when it is a literal or a cons chain ending in one, never a cons onto a binding.
let testFreshListConstruction unit =
    Unit
    |> (given (_) ->
        false
        |> ExprList([ExprInt(1)])
        |> isFreshListConstruction
        |> test.assertEqual(true))
    |> (given (_) ->
        false
        |> ExprList([])
        |> ExprAt(TextSpan(start = 0, end = 1))
        |> ExprCons(ExprInt(1))
        |> isFreshListConstruction
        |> test.assertEqual(true))
    |> (given (_) ->
        ExprVar("rest")
        |> ExprCons(ExprInt(1))
        |> isFreshListConstruction
        |> test.assertEqual(false))
    |> (given (_) ->
        ExprVar("xs")
        |> isFreshListConstruction
        |> test.assertEqual(false))

let consArm (tailPattern: Pattern) (body: Expr) = (PatternCons(PatternVar("first"))(tailPattern), body, None)

// An immediate list match needs two arms, none reading the binding again and none keeping a
// spine binding alive; a nested cons onto the binding matched immediately counts too.
let testImmediateListMatchUse unit =
    Unit
    |> (given (_) ->
        [ExprVar("first")
        |> printOf
        |> consArm(PatternWildcard), (PatternEmptyList, ExprInt(0), None)]
        |> matchOn("xs")
        |> isImmediateListMatchUse("xs")
        |> test.assertEqual(true))
    |> (given (_) ->
        [consArm(PatternVar("rest"))(ExprVar("rest")), (PatternEmptyList, ExprInt(0), None)]
        |> matchOn("xs")
        |> isImmediateListMatchUse("xs")
        |> test.assertEqual(false))
    |> (given (_) ->
        [consArm(PatternWildcard)(ExprVar("xs")), (PatternEmptyList, ExprInt(0), None)]
        |> matchOn("xs")
        |> isImmediateListMatchUse("xs")
        |> test.assertEqual(false))
    |> (given (_) ->
        [consArm(PatternWildcard)(ExprInt(1))]
        |> matchOn("xs")
        |> isImmediateListMatchUse("xs")
        |> test.assertEqual(false))
    |> (given (_) ->
        []
        |> ExprLet("ys")(ExprCons(ExprInt(0))(ExprVar("xs")))(matchOn("ys")([consArm(PatternWildcard)(ExprInt(1)), (PatternEmptyList, ExprInt(0), None)]))([])(None)
        |> isTailConsumedByImmediateListMatch("xs")
        |> test.assertEqual(true))

// A record is consumed immediately by one constructor arm that never reads it again, or as a
// field receiver inside a scalar expression.
let testImmediateRecordUses unit =
    Unit
    |> (given (_) ->
        [(PatternConstructor("Label")([PatternVar("text")]), ExprVar("text"), None)]
        |> matchOn("r")
        |> isImmediateRecordMatchUse("r")
        |> test.assertEqual(true))
    |> (given (_) ->
        [(PatternConstructor("Label")([PatternVar("text")]), ExprVar("r"), None)]
        |> matchOn("r")
        |> isImmediateRecordMatchUse("r")
        |> test.assertEqual(false))
    |> (given (_) ->
        ExprInt(2)
        |> ExprAdd(ExprQualifiedVar("r")("width"))
        |> isImmediateCopyUseOfRecord("r")
        |> test.assertEqual(true))
    |> (given (_) ->
        ExprTuple([ExprVar("r")])
        |> isImmediateCopyUseOfRecord("r")
        |> test.assertEqual(false))

// The escape terminals look through `let` bodies and every `if` and `match` arm, and the
// reconciliation accepts a fresh arm only when its keyless siblings are fresh too.
let testFreshEscapeTerminals unit =
    (let body =
        ExprLet("x")(ExprInt(1))([(PatternWildcard, ExprVar("z"), None)]
        |> matchOn("y")
        |> ExprIf(ExprBool(true))(ExprTuple([ExprInt(1)])))([])(None)([])
    in
        Unit
        |> (given (_) ->
            body
            |> freshEscapeTerminals
            |> length
            |> test.assertEqual(2))
        |> (given (_) ->
            body
            |> freshEscapeTerminals
            |> allTerminals(given (terminal: Expr) ->
                match terminal with
                    | ExprTuple(_) -> true
                    | _ -> false)
            |> test.assertEqual(false))
        |> (given (_) ->
            ExprTuple([ExprInt(1)])
            |> ExprIf(ExprBool(true))(ExprTuple([]))
            |> freshEscapeTerminals
            |> allTerminals(given (terminal: Expr) ->
                match terminal with
                    | ExprTuple(_) -> true
                    | _ -> false)
            |> test.assertEqual(true))
        |> (given (_) ->
            (given (_terminal: Expr) -> false)
            |> anyArmConsistentlyFresh(freshEscapeTerminals(body))(given (terminal: Expr) ->
                match terminal with
                    | ExprTuple(_) -> true
                    | _ -> false)(given (terminal: Expr) ->
                match terminal with
                    | ExprTuple(_) -> Some("Tuple")
                    | _ -> None)
            |> test.assertEqual(false))
        |> (given (_) ->
            (given (terminal: Expr) ->
                match terminal with
                    | ExprVar(_) -> true
                    | _ -> false)
            |> anyArmConsistentlyFresh(freshEscapeTerminals(body))(given (terminal: Expr) ->
                match terminal with
                    | ExprTuple(_) -> true
                    | _ -> false)(given (terminal: Expr) ->
                match terminal with
                    | ExprTuple(_) -> Some("Tuple")
                    | _ -> None)
            |> test.assertEqual(true)))

// The trailing pipe `0 |> pass(build(2)([]))` hands `pass` the list `build` returns owned; the
// program's entry keeps it in an owned slot and releases it at its end, as a function would at
// its return.
let trailingPipeSource = "type Where =\n    | line: Int\n    | column: Int\n\ntype Instr =\n    | kind: Int\n    | location: Where\n\nlet step (kind: Int) (location: Where) = (kind, Instr(kind = kind + 1, location = location))\n\nlet recursive pass (instructions: List(Instr)) (total: Int) =\n    match instructions with\n        | [] -> total\n        | Instr { kind = kind, location = location } :: tail ->\n            match step(kind)(location) with\n                | (_n, rewritten) -> pass(tail)(total + rewritten.kind)\n\nlet recursive build (n: Int) (acc: List(Instr)) =\n    if n == 0\n    then acc\n    else build(n - 1)(Instr(kind = n, location = Where(line = n, column = 1)) :: acc)\n\n0\n|> pass(build(2)([]))\n|> Ashes.IO.print"

let testEntryReleasesTrailingPipeFreshArgument unit =
    trailingPipeSource
    |> loweredLines
    |> functionLines("ProgramEntry")
    |> countContaining("general_rc_owned_absent_")
    |> test.assertEqual(3)

let nestedConstructorFieldSource = "type State =\n    | S(List(Int))\n\ntype Step =\n    | Done\n    | Continue(State, Int)\n\nlet recursive setAt i value values =\n    match values with\n        | [] -> []\n        | head :: tail ->\n            if i == 0\n            then value :: tail\n            else head :: setAt(i - 1)(value)(tail)\n\nlet advance r state =\n    match state with\n        | S(perm) ->\n            let perm2 = setAt(r)(r)(perm)\n            in Continue(S(perm2))(r)\n\nmatch advance(1)(S([1, 2, 3])) with\n    | Done -> Ashes.IO.print(\"done\")\n    | Continue(_next, r) -> Ashes.IO.print(Ashes.Text.fromInt(r))"

// A reference-counted constructor nested in another stores a `let`-bound list that owns its
// reference-counted value: the cell takes one retain of the binding's read and no reference or
// copy under a representation test besides, so the only list copy-out left in the function is
// the call's own conditional one.
let testNestedConstructorRetainsLiveOwnerFieldOnce unit =
    nestedConstructorFieldSource
    |> loweredLines
    |> functionLines("ClosureHelper from advance")
    |> (given (lines: List(Str)) ->
        Unit
        |> (given (_) ->
            lines
            |> countContainingBoth("CopyOutList")("HeadCopy=Inline")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> countContainingBoth("RcDup")("MayBeEmpty=true")
            |> test.assertEqual(1)))

let letCellExitSource = "type State =\n    | S(List(Int))\n\nlet recursive advance r state =\n    match state with\n        | S(perm) ->\n            let perm2 = 0 :: perm\n            in\n                if r == 0\n                then S(perm2)\n                else advance(r - 1)(S(perm2))\n\nmatch advance(0)(S([1, 2])) with\n    | S(xs) -> Ashes.IO.print(\"s\")"

// A loop's exit wraps a `let`-bound arena cell over a matched list in a constructor the return
// copies onto the reference-counted heap: the copy retains the list itself, so the duplicate the
// cell took of its tail is withdrawn and only the pattern owner's own duplicate stays a retain.
let testLetCellCopiedAtExitWithdrawsTailDuplicate unit =
    letCellExitSource
    |> loweredLines
    |> functionLines("ClosureHelper from advance")
    |> countContainingBoth("RcDup")("MayBeEmpty=true")
    |> test.assertEqual(1)

let agreementCellSource = "type Definition =\n    | MakeClosure(Int, Str)\n    | Other(Int)\n    | Nested(Definition)\n\ntype CaptureSite =\n    | creator: List((Int, Definition))\n    | sourceTemp: Int\n\ntype CaptureResolution =\n    | CaptureKnown(Str)\n    | CapturePending\n    | CaptureUnknown\n\nlet recursive lookupAssociation key entries =\n    match entries with\n        | [] -> None\n        | (k, v) :: tail ->\n            if k == key\n            then Some(v)\n            else lookupAssociation(key)(tail)\n\nlet resolveLabel (definitions: List((Int, Definition))) (temp: Int) known =\n    match lookupAssociation(temp)(definitions) with\n        | Some(MakeClosure(_, label)) -> CaptureKnown(label)\n        | Some(Other(_)) -> CapturePending\n        | Some(Nested(_)) -> CapturePending\n        | None -> CaptureUnknown\n\nlet recursive resolveGroup (sites: List(CaptureSite)) known (agreed: Maybe(Str)) (seen: List(Str)) (pending: Bool) =\n    match sites with\n        | [] ->\n            if pending\n            then CapturePending\n            else\n                match agreed with\n                    | Some(label) -> CaptureKnown(label)\n                    | None -> CapturePending\n        | site :: tail ->\n            match resolveLabel(site.creator)(site.sourceTemp)(known) with\n                | CaptureUnknown -> CaptureUnknown\n                | CapturePending -> resolveGroup(tail)(known)(agreed)(seen)(true)\n                | CaptureKnown(label) ->\n                    match agreed with\n                        | None -> resolveGroup(tail)(known)(Some(label))(label :: seen)(pending)\n                        | Some(previous) ->\n                            if previous == label\n                            then resolveGroup(tail)(known)(agreed)(label :: seen)(pending)\n                            else CaptureUnknown\n\nlet probe (n: Int) =\n    (let definitions = [(1, MakeClosure(1)(\"lambda_\" + Ashes.Text.fromInt(n % 10))), (2, Other(2))]\n    in\n        match resolveGroup([CaptureSite(creator = definitions, sourceTemp = 1)])([])(None)([])(false) with\n            | CaptureKnown(l) -> Ashes.Text.byteLength(l)\n            | CapturePending -> 7\n            | CaptureUnknown -> 9)\n\nlet recursive loop (n: Int) (acc: Int) =\n    if n == 0\n    then acc\n    else loop(n - 1)(acc + probe(n))\nin\n    0\n    |> loop(10)\n    |> Ashes.Text.fromInt\n    |> Ashes.IO.print"

// The agreement loop's back edges hand the matched label on in a `Some` cell that retains it for
// itself, so the iteration's owners of the label and of the matched resolution are released at
// every back edge as at the exit: a retained field names no owner's value unretained.
let testRetainedSuccessorFieldLeavesIterationOwnersReleased unit =
    agreementCellSource
    |> loweredLines
    |> functionLines("ClosureHelper from resolveGroup")
    |> (given (lines: List(Str)) ->
        Unit
        |> (given (_) ->
            lines
            |> countContaining("TypeName=String OwnerSlot=")
            |> test.assertEqual(6))
        |> (given (_) ->
            lines
            |> countContaining("TypeName=CaptureResolution RuntimeManaged=true")
            |> test.assertEqual(3)))

let sharedSpanSuccessorSourcePath = "tests/loop_literal_successor_borrows_binding_matched_from_shared_list.ash"

// The record successor's `Some(span)` is an arena cell of a two-constructor type that retained
// `span`: once the back edge has copied the successor, that retain is released through the `Some`
// constructor's own field, as stage 0's literal children release does for any constructor.
let testVariantLiteralChildReleasedAtBackEdge unit =
    match Ashes.IO.File.readText(sharedSpanSuccessorSourcePath) with
        | Error(message) -> test.fail("could not read " + sharedSpanSuccessorSourcePath + ": " + message)
        | Ok(source) ->
            source
            |> loweredLines
            |> functionLines("ClosureHelper from lowerItems")
            |> Ashes.Collection.List.filter(given (line: Str) -> Ashes.Text.contains(line)("TypeName=Span RuntimeManaged=true") && !Ashes.Text.contains(line)("OwnerSlot="))
            |> length
            |> test.assertEqual(1)

let tableLookupSource = "let recursive reverseOnto acc xs =\n    match xs with\n        | [] -> acc\n        | x :: rest -> reverseOnto(x :: acc)(rest)\n\nlet reverse xs = reverseOnto([])(xs)\n\nlet recursive length xs =\n    match xs with\n        | [] -> 0\n        | _ :: rest -> 1 + length(rest)\n\nlet recursive map f xs =\n    match xs with\n        | [] -> []\n        | x :: rest -> f(x) :: map(f)(rest)\n\ntype Inst =\n    | ConstInt(Int, Int)\n    | Jump(Str)\n    | JumpIfFalse(Int, Str)\n    | Label(Str)\n    | Other(Int)\n\nlet recursive lookupAssociation (key: Str) (entries: List((Str, List((Int, Int))))) =\n    match entries with\n        | [] -> None\n        | (k, v) :: tail ->\n            if k == key\n            then Some(v)\n            else lookupAssociation(key)(tail)\n\nlet recursive setInt (key: Int) (value: Int) entries =\n    match entries with\n        | [] -> [(key, value)]\n        | (k, v) :: tail ->\n            if k == key\n            then (key, value) :: tail\n            else (k, v) :: setInt(key)(value)(tail)\n\nlet recursive setSaved (key: Str) (value: List((Int, Int))) entries =\n    match entries with\n        | [] -> [(key, value)]\n        | (k, v) :: tail ->\n            if k == key\n            then (key, value) :: tail\n            else (k, v) :: setSaved(key)(value)(tail)\n\nlet recursive removeAssociation (key: Str) (entries: List((Str, List((Int, Int))))) =\n    match entries with\n        | [] -> []\n        | (k, v) :: tail ->\n            if k == key\n            then tail\n            else (k, v) :: removeAssociation(key)(tail)\n\nlet reduce instructions =\n    (let recursive reduceLoop insts knownInts savedInts prevTerm acc =\n        match insts with\n            | [] -> reverse(acc)\n            | inst :: tail ->\n                match inst with\n                    | ConstInt(dest, v) ->\n                        reduceLoop(tail)(setInt(dest)(v)(knownInts))(savedInts)(false)(inst :: acc)\n                    | Label(name) ->\n                        let fallback = setInt(0)(0)([])\n                        in\n                            let nextInts =\n                                if prevTerm\n                                then\n                                    match lookupAssociation(name)(savedInts) with\n                                        | Some(_) -> fallback\n                                        | None -> []\n                                else knownInts\n                            in\n                                reduceLoop(tail)(nextInts)(removeAssociation(name)(savedInts))(false)(inst :: acc)\n                    | Jump(target) ->\n                        let nextSaved = setSaved(target)(knownInts)(savedInts)\n                        in reduceLoop(tail)(knownInts)(nextSaved)(true)(inst :: acc)\n                    | JumpIfFalse(_, target) ->\n                        let nextSaved = setSaved(target)(knownInts)(savedInts)\n                        in reduceLoop(tail)(knownInts)(nextSaved)(false)(inst :: acc)\n                    | _ -> reduceLoop(tail)(knownInts)(savedInts)(false)(inst :: acc)\n    in reduceLoop(instructions)([])([])(false)([]))\n\nlet recursive build i acc =\n    if i == 0\n    then acc\n    else\n        let l = \"L\" + Ashes.Text.fromInt(i % 7)\n        in build(i - 1)(ConstInt(i)(i * 2) :: JumpIfFalse(i)(l) :: Other(i) :: Jump(l) :: Label(l) :: acc)\n\nlet recursive rounds n total =\n    if n == 0\n    then total\n    else\n        rounds(n - 1)(total + length([]\n        |> build(40)\n        |> reduce))\n\n0\n|> rounds(3)\n|> Ashes.Text.fromInt\n|> Ashes.IO.print"

// The label arm's `nextInts` join becomes the known-table parameter, which stays in the arena: its
// lookup arm returns `fallback`, a `let` owner bound before the match, so the arm takes a reference
// of its own and the join copies it into the arena (an `rc_arena_demote_done` block), the
// reference released after the copy, rather than handing the parameter the owner's own cells.
let testSuccessorJoinArmOfLetOwnerIsDemoted unit =
    tableLookupSource
    |> loweredLines
    |> functionLines("ClosureHelper from reduceLoop")
    |> countContaining("Target=rc_arena_demote_done_")
    |> test.assertEqual(1)

let letCellBackEdgeSource = "type State =\n    | S(List(Int))\n\nlet recursive advance r state =\n    match state with\n        | S(perm) ->\n            let perm2 = 0 :: perm\n            in\n                if r == 0\n                then 0\n                else advance(r - 1)(S(perm2))\n\nadvance(2)(S([1, 2]))\n|> Ashes.Text.fromInt\n|> Ashes.IO.print"

// The back edge copies the arena successor `S(perm2)` onto the reference-counted heap with
// references of its own to its fields, as stage 0 builds the successor there directly, so the
// duplicate the let-bound cell took of its matched tail is withdrawn and only the pattern owner's
// own duplicate stays a retain.
let testBackEdgeCopiedSuccessorWithdrawsLetCellTailDuplicate unit =
    letCellBackEdgeSource
    |> loweredLines
    |> functionLines("ClosureHelper from advance")
    |> countContainingBoth("RcDup")("MayBeEmpty=true")
    |> test.assertEqual(1)

let letAliasBackEdgeSource = "let recursive length xs =\n    match xs with\n        | [] -> 0\n        | _ :: rest -> 1 + length(rest)\n\nlet recursive build (n: Int) (acc: List(Int)) =\n    if n == 0\n    then acc\n    else build(n - 1)(n :: acc)\n\nlet recursive walk (n: Int) (known: List(Int)) (total: Int) =\n    if n == 0\n    then total + length(known)\n    else\n        let fresh = build(3)([])\n        in\n            let next = fresh\n            in walk(n - 1)(next)(total + length(known))\n\nwalk(5)([])(0)\n|> Ashes.Text.fromInt\n|> Ashes.IO.print"

// The temp after `key=` in one whitespace-separated token of an IR line, when it carries it.
let recursive tokenOperand (key: Str) (tokens: List(Str)) =
    match tokens with
        | [] -> ""
        | token :: rest ->
            if Ashes.Text.startsWith(token)(key + "=")
            then Ashes.Text.drop(token)(Ashes.Text.length(key) + 1)
            else tokenOperand(key)(rest)

let lineOperand (key: Str) (line: Str) =
    " "
    |> Ashes.Text.split(line)
    |> tokenOperand(key)

// How many retains take a reference to the borrow on the line before them.
let recursive borrowsRetainedNext (lines: List(Str)) (count: Int) =
    match lines with
        | first :: second :: rest ->
            if Ashes.Text.contains(first)("Borrow ") && Ashes.Text.contains(second)("RcDup ") && lineOperand("SourceTemp")(second) == lineOperand("Target")(first)
            then borrowsRetainedNext(second :: rest)(count + 1)
            else borrowsRetainedNext(second :: rest)(count)
        | _ -> count

// A back edge handing the arena parameter `known` a `let` alias of an iteration owner
// (`let next = fresh in walk(n - 1)(next)`) retains the alias's read, as stage 0's
// DuplicateRuntimeManagedOwnedValueForTransfer does, so the successor keeps its own reference
// when the back edge releases `fresh`.
let testBackEdgeRetainsLetAliasOfIterationOwner unit =
    letAliasBackEdgeSource
    |> loweredLines
    |> functionLines("ClosureHelper from walk")
    |> (given (lines: List(Str)) -> borrowsRetainedNext(lines)(0))
    |> test.assertEqual(1)

let unchangedFieldUpdateSource = "let recursive reverseOnto acc xs =\n    match xs with\n        | [] -> acc\n        | x :: rest -> reverseOnto(x :: acc)(rest)\n\nlet reverse xs = reverseOnto([])(xs)\n\nlet recursive length xs =\n    match xs with\n        | [] -> 0\n        | _ :: rest -> 1 + length(rest)\n\nlet recursive map f xs =\n    match xs with\n        | [] -> []\n        | x :: rest -> f(x) :: map(f)(rest)\n\ntype Tree =\n    | Leaf(Str)\n    | Node(Tree, Tree)\n\ntype Fn =\n    | label: Str\n    | calls: List(Str)\n    | tree: Tree\n\nlet recursive retarget (known: List((Str, Str))) (fn: Fn) =\n    match known with\n        | [] -> fn\n        | (label, target) :: rest ->\n            if label == fn.label\n            then fn with calls = target :: fn.calls\n            else retarget(rest)(fn)\n\nlet recursive collect (functions: List(Fn)) (known: List((Str, Str))) =\n    match functions with\n        | [] -> known\n        | fn :: rest -> collect(rest)((fn.label, fn.label + \"_r\") :: known)\n\nlet run (functions: List(Fn)) =\n    match collect(functions)([]) with\n        | [] -> functions\n        | known ->\n            map(retarget(known))(functions)\n\nlet recursive sumCalls (functions: List(Fn)) (acc: Int) =\n    match functions with\n        | [] -> acc\n        | fn :: rest -> sumCalls(rest)(acc + length(fn.calls))\n\nlet recursive build (n: Int) (acc: List(Fn)) =\n    if n == 0\n    then acc\n    else build(n - 1)(Fn(label = \"lambda_\" + Ashes.Text.fromInt(n), calls = [\"f\"], tree = Leaf(\"t\")) :: acc)\n\n0\n|> sumCalls([]\n|> build(40)\n|> run)\n|> Ashes.IO.print"

// How many owned slots are stored straight from an identity duplicate on the line before them.
let recursive identityDuplicatesStoredNext (lines: List(Str)) (count: Int) =
    match lines with
        | first :: second :: rest ->
            if Ashes.Text.contains(first)("RcDup ") && !Ashes.Text.contains(first)("RuntimeManaged=true") && Ashes.Text.contains(second)("StoreLocal ") && lineOperand("Source")(second) == lineOperand("Target")(first)
            then identityDuplicatesStoredNext(second :: rest)(count + 1)
            else identityDuplicatesStoredNext(second :: rest)(count)
        | _ -> count

// `fn with calls = target :: fn.calls` in a loop that hands `fn` on unchanged: `fn` is not placed,
// so the cell's duplicate of `fn.calls` stays an identity, and its owned slot holds zero rather
// than the list, which a release by its representation would free under the caller.
let testUnplacedParameterTailSlotStaysZero unit =
    unchangedFieldUpdateSource
    |> loweredLines
    |> functionLines("ClosureHelper from retarget")
    |> (given (lines: List(Str)) -> identityDuplicatesStoredNext(lines)(0))
    |> test.assertEqual(0)

let loopExitFieldSource = "type State =\n    | refs: List(Str)\n\nlet recursive scanMatch (tokens: List(Str)) (state: State) =\n    match tokens with\n        | [] -> state.refs\n        | text :: rest -> scanMatch(rest)(State(refs = text :: state.refs))\n\nlet recursive count (xs: List(Str)) (n: Int) =\n    match xs with\n        | [] -> n\n        | _ :: rest -> count(rest)(n + 1)\n\ncount(scanMatch([\"a\"])(State(refs = [])))(0)\n|> Ashes.Text.fromInt\n|> Ashes.IO.print"

// A loop's exit returning a heap field of its reference-counted record parameter returns the
// retain the arm took, promoted once the parameter is placed, so the loop's result is
// reference-counted and owned and its closure carries `ReturnsRuntimeManaged`, as stage 0's.
let testLoopExitFieldResultIsReferenceCounted unit =
    loopExitFieldSource
    |> loweredLines
    |> functionLines("SourceFunction from scanMatch")
    |> countContainingBoth("MakeClosure")("ReturnsRuntimeManaged=true")
    |> test.assertEqual(1)

let loopExitFieldIfSource = "let recursive length xs =\n    match xs with\n        | [] -> 0\n        | _ :: rest -> 1 + length(rest)\n\ntype State =\n    | refs: List(Str)\n\nlet recursive scanIf (tokens: List(Str)) (state: State) =\n    if length(tokens) == 0\n    then state.refs\n    else\n        match tokens with\n            | [] -> []\n            | text :: rest -> scanIf(rest)(State(refs = text :: state.refs))\n\nlet recursive count (xs: List(Str)) (n: Int) =\n    match xs with\n        | [] -> n\n        | _ :: rest -> count(rest)(n + 1)\n\ncount(scanIf([\"a\"])(State(refs = [])))(0)\n|> Ashes.Text.fromInt\n|> Ashes.IO.print"

// The same exit from an `if` branch beside a nil arm: the branch's value is normalized with a
// reference of its own while its retain still looks unowned, so the retain stays an identity and
// the join of the normalized value and nil is the loop's owned reference-counted result.
let testLoopExitFieldResultThroughIfIsReferenceCounted unit =
    loopExitFieldIfSource
    |> loweredLines
    |> functionLines("SourceFunction from scanIf")
    |> countContainingBoth("MakeClosure")("ReturnsRuntimeManaged=true")
    |> test.assertEqual(1)

let lastOfSource = "let recursive lastOf list =\n    match list with\n        | last :: [] -> last\n        | _ :: rest -> lastOf(rest)\n        | [] -> \"\"\n\nAshes.IO.print(lastOf([\"a\", \"b\" + Ashes.Text.fromInt(1)]))"

// A loop exit arm returning a head bound under a nested pattern (`| last :: [] -> last`) beside a
// literal arm: the arm's duplicate of the pattern owner is only normalized by the join (a reference
// or a reference-counted copy), so it stays an identity and the normalization's reference is the
// result's. Promoted as well, it made two references for one result. Four real retains remain: the
// parameter's entry normalization, the owner's protective retain, the join's and the back edge's.
let testExitArmPatternOwnerNormalizedOnce unit =
    lastOfSource
    |> loweredLines
    |> functionLines("SourceFunction from lastOf")
    |> countContainingBoth("RcDup")("RuntimeManaged=true")
    |> test.assertEqual(4)

let runOwnedAggregateReleaseTests unit =
    Unit
    |> testExitArmPatternOwnerNormalizedOnce
    |> testLoopExitFieldResultThroughIfIsReferenceCounted
    |> testLoopExitFieldResultIsReferenceCounted
    |> testBackEdgeRetainsLetAliasOfIterationOwner
    |> testUnplacedParameterTailSlotStaysZero
    |> testBackEdgeCopiedSuccessorWithdrawsLetCellTailDuplicate
    |> testSuccessorJoinArmOfLetOwnerIsDemoted
    |> testVariantLiteralChildReleasedAtBackEdge
    |> testRetainedSuccessorFieldLeavesIterationOwnersReleased
    |> testLetCellCopiedAtExitWithdrawsTailDuplicate
    |> testNestedConstructorRetainsLiveOwnerFieldOnce
    |> testOwnedListLetWalksSpineInline
    |> testLambdaReturningRecordAllocatesRuntimeManaged
    |> testOwnedRecordLetReleasedByItsOwner
    |> testEscapingTupleRetainsOwnedChildren
    |> testBackEdgeHeadTupleKeepsAccumulatorPlaced
    |> testNestedCallTupleInBackEdgeArgumentReleasesOwners
    |> testEscapingListLiteralRetainsOwnedChildren
    |> testEscapingConsRetainsHeadAndTail
    |> testMentionsNameRespectsShadowing
    |> testFreshListConstruction
    |> testImmediateListMatchUse
    |> testImmediateRecordUses
    |> testEntryReleasesTrailingPipeFreshArgument
    |> testFreshEscapeTerminals
    |> (given (_) -> Ashes.IO.print("owned aggregate release tests passed"))
