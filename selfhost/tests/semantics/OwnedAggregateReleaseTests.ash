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
// runtime-managed, the scope exit walks the spine as unique cells releasing each string head,
// and no placeable list drop names the owner slot.
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
            |> countContaining("Target=rcdrop_unique_list_end_")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> countContaining("TypeName=String RuntimeManaged=true")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> countContaining("TypeName=List RuntimeManaged=true")
            |> test.assertEqual(1))
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

// The top-level `let` owning the record releases it at its scope exit through the record's
// field walk: the string field is released under a uniqueness test, then the cell.
let testOwnedRecordLetReleasesFieldsInline unit =
    recordSource
    |> loweredLines
    |> functionLines("ProgramEntry")
    |> (given (lines: List(Str)) ->
        Unit
        |> (given (_) ->
            lines
            |> countContaining("Target=rc_drop_shared_")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> countContaining("GetAdtField           Target=20 Ptr=18 FieldIndex=0 Tagless=true")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> lineAfter("GetAdtField           Target=20")
            |> test.assertEqual("    RcDrop                SourceTemp=20 TypeName=String RuntimeManaged=true"))
        |> (given (_) ->
            lines
            |> countContaining("RcDrop                SourceTemp=18 TypeName=Label RuntimeManaged=true")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> countContaining("TypeName=Label OwnerSlot=")
            |> test.assertEqual(0)))

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

// An escaping cons onto an owned list is an arena cell that retains its owned head and its
// owned tail (null-tolerantly), and the list owner's scope exit walks the spine under a
// uniqueness test.
let testEscapingConsRetainsHeadAndTail unit =
    aggregateSource
    |> loweredLines
    |> functionLines("SourceFunction from prefixed")
    |> (given (lines: List(Str)) ->
        Unit
        |> (given (_) ->
            lines
            |> countContaining("RcDup                 Target=15 SourceTemp=14 RuntimeManaged=true")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> countContaining("RcDup                 Target=18 SourceTemp=17 RuntimeManaged=true MayBeEmpty=true")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> countContaining("Alloc                 Target=19 SizeBytes=16")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> countContainingBoth("Alloc                 Target=19")("RuntimeManaged")
            |> test.assertEqual(0))
        |> (given (_) ->
            lines
            |> countContaining("Target=rcdrop_list_shared_")
            |> test.assertEqual(1))
        |> (given (_) ->
            lines
            |> countContaining("RcIsUnique")
            |> test.assertEqual(1)))

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

let runOwnedAggregateReleaseTests unit =
    Unit
    |> testOwnedListLetWalksSpineInline
    |> testLambdaReturningRecordAllocatesRuntimeManaged
    |> testOwnedRecordLetReleasesFieldsInline
    |> testEscapingTupleRetainsOwnedChildren
    |> testEscapingListLiteralRetainsOwnedChildren
    |> testEscapingConsRetainsHeadAndTail
    |> testMentionsNameRespectsShadowing
    |> testFreshListConstruction
    |> testImmediateListMatchUse
    |> testImmediateRecordUses
    |> testFreshEscapeTerminals
    |> (given (_) -> Ashes.IO.print("owned aggregate release tests passed"))
