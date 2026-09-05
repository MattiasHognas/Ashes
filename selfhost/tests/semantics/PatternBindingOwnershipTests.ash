// The pre-lowering classification of the names a loop function's `match` patterns extract out of
// its own parameters, checked on the facts `patternBindingFacts` computes for small loop bodies.
import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.PatternBindingOwnership
export (
    value runPatternBindingOwnershipTests,
)

let parsedItems source =
    match parseProgram(source) with
        | ProgramParseResult { program = ProgramSyntax { items = items }, diagnostics = [] } -> items
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let recursive unspanExpression (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> unspanExpression(inner)
        | other -> other

let recursive lambdaParameters (expression: Expr) (names: List(Str)) =
    match unspanExpression(expression) with
        | ExprLambda(parameter, body, _annotation) -> lambdaParameters(body)(parameter :: names)
        | _ -> Ashes.Collection.List.reverse(names)

let recursive innermostBody (expression: Expr) =
    match unspanExpression(expression) with
        | ExprLambda(_parameter, body, _annotation) -> innermostBody(body)
        | other -> other

// The facts of the top-level `let recursive` binding `name` in `source`: its parameters are the
// whole curried chain and its body the innermost lambda body, the way the lowering enters a loop.
let recursive factsOfItems (name: Str) (items: List(TopLevelItem)) =
    match items with
        | [] -> test.fail("no top-level binding " + name)
        | TopLevelAt(_span, inner) :: rest -> factsOfItems(name)(inner :: rest)
        | TopLevelLet(LetBindingSyntax { name = candidate, value = value }, _recursive) :: rest ->
            if candidate == name
            then
                value
                |> innermostBody
                |> patternBindingFacts(name)(lambdaParameters(value)([]))([])([])
            else factsOfItems(name)(rest)
        | _ :: rest -> factsOfItems(name)(rest)

let factsOf (name: Str) (source: Str) =
    source
    |> parsedItems
    |> factsOfItems(name)

let recursive ownershipOf (name: Str) (facts: List(PatternBindingFact)) =
    match facts with
        | [] -> test.fail("no fact for binder " + name)
        | PatternBindingFact { name = candidate, ownership = ownership } :: rest ->
            if candidate == name
            then ownership
            else ownershipOf(name)(rest)

let recursive depthOf (name: Str) (facts: List(PatternBindingFact)) =
    match facts with
        | [] -> test.fail("no fact for binder " + name)
        | PatternBindingFact { name = candidate, extractionDepth = depth } :: rest ->
            if candidate == name
            then depth
            else depthOf(name)(rest)

let check (label: Str) (ok: Bool) =
    if ok
    then Unit
    else test.fail("expected " + label)

let expectOwnership (label: Str) (name: Str) (expected: PatternBindingOwnershipKind) (facts: List(PatternBindingFact)) =
    check(label + ": " + name + " is " + Ashes.Trait.Show.show(expected) + ", got " + Ashes.Trait.Show.show(ownershipOf(name)(facts)))(ownershipOf(name)(facts) == expected)

let forwardedHeadSource = "let recursive last n xs keep =\n    match xs with\n        | [] -> keep\n        | head :: rest -> last(n - 1)(rest)(head)\n\nAshes.IO.print(last(1)([\"a\"])(\"z\"))"

// A head forwarded to a different parameter of the exact tail self-call escapes on its own; the
// tail forwarded to its own parameter transfers.
let expectForwardedHeadEscapesAndTailTransfers unit =
    forwardedHeadSource
    |> factsOf("last")
    |> (given (facts) ->
        Unit
        |> (given (_) -> expectOwnership("forwarded head")("head")(PatternEscapesIndependently)(facts))
        |> (given (_) -> expectOwnership("consumed tail")("rest")(PatternTransferredToSameParameter)(facts))
        |> (given (_) -> check("the head is extracted one level below the parameter")(depthOf("head")(facts) == 1)))

let embeddedHeadSource = "let recursive walk items acc =\n    match items with\n        | [] -> acc\n        | line :: rest -> walk(rest)(line :: acc)\n\nAshes.IO.print(Ashes.Text.fromInt(Ashes.Collection.List.length(walk([\"a\"])([]))))"

// A head consed onto another parameter is embedded in the cell that owns it.
let expectConsedHeadIsEmbedded unit =
    embeddedHeadSource
    |> factsOf("walk")
    |> (given (facts) ->
        Unit
        |> (given (_) -> expectOwnership("consed head")("line")(PatternEmbeddedInOwner)(facts))
        |> (given (_) -> expectOwnership("consumed tail")("rest")(PatternTransferredToSameParameter)(facts)))

let borrowedHeadSource = "let recursive total xs acc =\n    match xs with\n        | [] -> acc\n        | s :: rest -> total(rest)(acc + Ashes.Text.byteLength(s))\n\nAshes.IO.print(total([\"a\"])(0))"

// A head read only by a plain call stays a borrow.
let expectHeadPassedToPlainCallIsBorrowed unit =
    borrowedHeadSource
    |> factsOf("total")
    |> (given (facts) ->
        Unit
        |> (given (_) -> expectOwnership("borrowed head")("s")(PatternBorrowedOnly)(facts))
        |> (given (_) -> expectOwnership("consumed tail")("rest")(PatternTransferredToSameParameter)(facts)))

let capturedHeadSource = "let recursive pick xs f =\n    match xs with\n        | [] -> f\n        | head :: rest -> pick(rest)(given (unit) -> head)\n\nAshes.IO.print(pick([\"a\"])(given (unit) -> \"z\")(Unit))"

// A head a closure captures escapes with the closure.
let expectCapturedHeadEscapes unit =
    capturedHeadSource
    |> factsOf("pick")
    |> (given (facts) ->
        Unit
        |> (given (_) -> expectOwnership("captured head")("head")(PatternEscapesIndependently)(facts))
        |> (given (_) -> expectOwnership("consumed tail")("rest")(PatternTransferredToSameParameter)(facts)))

let nestedFieldSource = "let recursive lengths xs acc =\n    match xs with\n        | [] -> acc\n        | (text, count) :: rest -> lengths(rest)(acc + Ashes.Text.byteLength(text) + count)\n\nAshes.IO.print(lengths([(\"a\", 1)])(0))"

// A field pulled out of a list element sits two levels below the parameter, and a plain call
// lets such a deeper extraction escape.
let expectNestedFieldPassedToPlainCallEscapes unit =
    nestedFieldSource
    |> factsOf("lengths")
    |> (given (facts) ->
        Unit
        |> (given (_) -> check("the tuple field is extracted two levels below the parameter")(depthOf("text")(facts) == 2))
        |> (given (_) -> expectOwnership("nested field")("text")(PatternEscapesIndependently)(facts))
        |> (given (_) -> expectOwnership("consumed tail")("rest")(PatternTransferredToSameParameter)(facts)))

let runPatternBindingOwnershipTests unit =
    unit
    |> expectForwardedHeadEscapesAndTailTransfers
    |> (given (_) -> expectConsedHeadIsEmbedded(Unit))
    |> (given (_) -> expectHeadPassedToPlainCallIsBorrowed(Unit))
    |> (given (_) -> expectCapturedHeadEscapes(Unit))
    |> (given (_) -> expectNestedFieldPassedToPlainCallEscapes(Unit))
    |> (given (_) -> Ashes.IO.print("all self-hosted pattern binding ownership tests passed"))
