import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.NodeTypeTable
import AshesCompiler.Semantics.StrictPartialStages
export (
    value runStrictPartialStagesTests,
)

let call function argument = ExprCall(function)(argument)(false)(callArgumentsInline)

let binding name value = TopLevelLet(LetBindingSyntax(name = name, value = value, sugarParameters = [], typeAnnotation = None, requirements = [], declarationStart = -1))(false)

// `let make n = (let s = tag(n) in given (x) -> x + s)` and `let use n = (let f = make(n) in f("arg"))`:
// a curried function with a strict prefix, called saturated at a `let`.
let unconstrainedPartialItems unit =
    [
        None
        |> ExprLambda("n")(ExprLet("s")(call(ExprVar("tag"))(ExprVar("n")))(ExprLambda("x")(ExprVar("s"))(None))([])(None)([]))
        |> binding("make"),
        None
        |> ExprLambda("n")(ExprLet("f")(call(ExprVar("make"))(ExprVar("n")))(call(ExprVar("f"))(ExprString("arg")))([])(None)([]))
        |> binding("use")
    ]

let recursive countItems (items: List(TopLevelItem)) =
    match items with
        | [] -> 0
        | _ :: rest -> 1 + countItems(rest)

let scheduledItemCount nodeTypes =
    match ExprInt(1)
    |> call(ExprVar("use"))
    |> scheduleStrictProgram(nodeTypes)(unconstrainedPartialItems(Unit)) with
        | (items, _trailing) -> countItems(items)

// With the inference's node table, a partial call with no recorded evidence stages carries no trait
// requirement, so it is not split, as stage 0 splits none; without the table it still is.
let expectRecordedCallWithoutEvidenceIsNotSplit unit =
    Unit
    |> (given (_) ->
        if scheduledItemCount(Some(emptyNodeTypeTable)) == 2
        then Unit
        else test.fail("a call the node table records no evidence stages for should not be split"))
    |> (given (_) ->
        if scheduledItemCount(None) == 4
        then Unit
        else test.fail("without a node table the partial call should still be split"))

let runStrictPartialStagesTests unit = expectRecordedCallWithoutEvidenceIsNotSplit(Unit)
