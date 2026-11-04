// expect: 75
import Ashes.Collection.List.length
type Expr =
    | ExprAt(Int, Expr)
    | ExprVar(Str)
    | ExprLet(Str, Expr, Expr)
    | ExprCall(Expr, Expr)

type Analysis =
    | nonCalleeUses: List(Str)
    | remainingBody: Expr
    | escapeOfChain: Maybe(Expr)
    | escapeOfTrailing: Maybe(Expr)
    | trailingFinalVar: Maybe(Str)

let recursive peelRemainingBody (count: Int) (body: Expr) =
    if count <= 0
    then body
    else
        match body with
            | ExprLet(_name, _value, next) -> peelRemainingBody(count - 1)(next)
            | _ -> body

let peelAnalysis (count: Int) (analysis: Analysis) = analysis with remainingBody = peelRemainingBody(count)(analysis.remainingBody)

let recursive size (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> 1 + size(inner)
        | ExprLet(_name, value, body) -> size(value) + size(body)
        | ExprCall(callee, argument) -> size(callee) + size(argument)
        | _ -> 1

let escapeSize (escape: Maybe(Expr)) =
    match escape with
        | Some(found) -> size(found)
        | None -> 0

type Item =
    | ItemAt(Int, Item)
    | ItemExpr(Expr)
    | ItemFn(Int -> Int)

type LState =
    | emitted: List(Str)
    | counter: Int
    | request: Maybe(Expr)

type Opened =
    | chainState: LState
    | chainPending: List(Str)
    | chainError: Maybe(Str)

let escapeRequest (escape: Maybe(Expr)) (state: LState) =
    match escape with
        | Some(body) -> state with counter = state.counter + size(body)
        | None -> state

let openChain name value remaining (escape: Maybe(Expr)) (forwarded: Bool) environment outer closure (pending: List(Str)) (state: LState) =
    if forwarded
    then Opened(chainState = state, chainPending = pending, chainError = Some(name))
    else
        let requested = escapeRequest(escape)(state)
        in Opened(chainState = (requested with emitted = name :: requested.emitted), chainPending = name :: pending, chainError = None)

let topLevelEscapeBody (analysis: Analysis) =
    match analysis.remainingBody with
        | ExprLet(_name, _value, _nested) -> analysis.escapeOfChain
        | _ -> analysis.escapeOfTrailing

let itemSize (item: Item) =
    match item with
        | ItemExpr(expression) -> size(expression)
        | ItemFn(f) -> f(1)
        | ItemAt(_span, _inner) -> 1

let recursive lowerItems (items: List(Item)) trailing seen environment (analysis: Analysis) (pending: List(Str)) (state: LState) =
    match items with
        | [] -> state.counter + length(state.emitted) + size(analysis.remainingBody) + escapeSize(analysis.escapeOfChain) + seen
        | ItemAt(_span, inner) :: rest -> lowerItems(inner :: rest)(trailing)(seen)(environment)(analysis)(pending)(state)
        | item :: rest ->
            let nextAnalysis = peelAnalysis(1)(analysis)
            in
                match openChain("x")(trailing)(nextAnalysis.remainingBody)(topLevelEscapeBody(nextAnalysis))(itemSize(item) > 5)(environment)(1)(2)(pending)(state) with
                    | Opened { chainState = failed, chainError = Some(_error) } -> failed.counter
                    | Opened { chainState = opened, chainPending = nextPending, chainError = None } -> lowerItems(rest)(trailing)(seen + 1)(environment)(nextAnalysis)(nextPending)(opened)

let recursive build (k: Int) (n: Int) =
    if k == 0
    then
        ExprCall(ExprAt(n)(ExprVar("print")))(ExprVar("x"))
    else
        n
        |> build(k - 1)
        |> ExprLet("v")(ExprVar("y"))

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        let trailing =
            ExprVar("z")
            |> ExprAt(1)
            |> ExprCall(ExprVar("f"))
            |> ExprAt(n)
        in
            let analysis = Analysis(nonCalleeUses = ["a"], remainingBody = build(3)(n), escapeOfChain = Some(trailing), escapeOfTrailing = Some(trailing), trailingFinalVar = None)
            in
                loop(n - 1)(acc + lowerItems([ItemExpr(ExprVar("a")), ItemAt(1)(ItemFn(given (x) -> x + 1)), ItemExpr(ExprVar("c"))])(trailing)(0)(4)(analysis)([])(LState(emitted = [], counter = 0, request = None)))
in
    0
    |> loop(3)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
