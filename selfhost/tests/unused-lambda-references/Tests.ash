import Ashes.Collection.Map.MapTree
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Parser
// PRODUCTION_HELPERS
let assert (label: Str) condition =
    if condition
    then Unit
    else Ashes.IO.panic(label)

let emptyUses includeCallees = NonCalleeUses(names = Ashes.Collection.Map.empty, everyName = false, includeCallees = includeCallees)

let has name uses = uses.everyName || Ashes.Collection.Map.getStr(name)(uses.names) != None

let collect expression =
    true
    |> emptyUses
    |> collectNonCalleeUses(expression)(false)([])

let checkReference label expression =
    expression
    |> collect
    |> has("before")
    |> assert(label)

let parse source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | _ -> Ashes.IO.panic("reference regression source did not parse")

let recursive retainedName name items =
    match items with
        | [] -> false
        | TopLevelAt(_span, inner) :: rest -> retainedName(name)(inner :: rest)
        | TopLevelLet(LetBindingSyntax { name = candidate }, _recursive) :: rest -> candidate == name || retainedName(name)(rest)
        | _ :: rest -> retainedName(name)(rest)

let checkProgram label source expected =
    match parse(source) with
        | ProgramSyntax { items = items, body = body } ->
            let trailing =
                match body with
                    | Some(expression) -> expression
                    | None -> ExprBool(true)
            in
                assert(label)(retainedName("before")(validatedReplayItems(items)(trailing)) == expected)

let checkExtraMode label source =
    match parse(source) with
        | ProgramSyntax { items = items } ->
            let oldUses =
                false
                |> emptyUses
                |> programNonCalleeUses(items)(ExprBool(true))
            in
                let newUses =
                    true
                    |> emptyUses
                    |> programNonCalleeUses(items)(ExprBool(true))
                in assert(label)(!has("before")(oldUses) && has("before")(newUses))

let moduleExports uses = uses.everyName

let declaration = "let before x y = x <= y\n"

let directCall = ExprCall(ExprVar("before"))(ExprInt(1))(false)(callArgumentsInline)

let ordinaryUses =
    false
    |> emptyUses
    |> collectNonCalleeUses(directCall)(false)([])

let allUses = collect(directCall)

let recursive repeatReferenceMap remaining items trailing =
    if remaining <= 0
    then Unit
    else
        let kept = validatedReplayItems(items)(trailing)
        in
            if Ashes.Collection.List.length(kept) == 2
            then repeatReferenceMap(remaining - 1)(items)(trailing)
            else Ashes.IO.panic("repeated reference map changed retained declarations")

let plateauFromArguments unit =
    match Ashes.IO.args with
        | [] -> Unit
        | count :: [] ->
            match Ashes.Text.parseInt(count) with
                | Ok(rounds) ->
                    match parse(declaration + "let unused left right = left <= right\nlet alias = before\ntrue") with
                        | ProgramSyntax { items = items } -> repeatReferenceMap(rounds)(items)(ExprBool(true))
                | Error(_message) -> Ashes.IO.panic("expected integer round count")
        | _ -> Ashes.IO.panic("expected zero or one round count")

Unit
|> (given (_) -> assert("old ownership mode changed")(Ashes.Collection.Map.getStr("before")(ordinaryUses.names) == None && !ordinaryUses.everyName))
|> (given (_) -> assert("all-use mode lost direct callee")(has("before")(allUses) && !allUses.everyName))
|> (given (_) ->
    callArgumentsInline
    |> ExprCall(ExprVar("consume"))(ExprVar("before"))(false)
    |> checkReference("call argument"))
|> (given (_) -> checkReference("tuple escape")(ExprTuple([ExprVar("before"), ExprInt(1)])))
|> (given (_) ->
    false
    |> ExprList([ExprVar("before")])
    |> checkReference("list escape"))
|> (given (_) ->
    None
    |> ExprLambda("x")(ExprVar("before"))
    |> checkReference("closure capture"))
|> (given (_) ->
    "field"
    |> ExprQualifiedVar("before")
    |> checkReference("qualified receiver"))
|> (given (_) ->
    false
    |> ExprRecord("Box")([("value", ExprVar("before"))])
    |> checkReference("record field"))
|> (given (_) ->
    [("value", ExprVar("before"))]
    |> ExprRecordUpdate(ExprVar("record"))
    |> checkReference("record update"))
|> (given (_) ->
    None
    |> ExprMatch(ExprInt(1))([(PatternWildcard, ExprBool(true), Some(directCall))])
    |> checkReference("match guard"))
|> (given (_) ->
    [(None, "operation", [], ExprVar("before"))]
    |> ExprHandle(ExprInt(1))
    |> checkReference("handler conservative fallback"))
|> (given (_) -> checkReference("await conservative fallback")(ExprAwait(ExprVar("before"))))
|> (given (_) ->
    assert("shadowed lambda parameter became outer reference")(!has("before")(None
    |> ExprLambda("before")(ExprVar("before"))
    |> collect)))
|> (given (_) -> checkProgram("unused lambda removed")(declaration + "true")(false))
|> (given (_) -> checkProgram("callee-only reference kept")(declaration + "before(1)(2)")(true))
|> (given (_) -> checkProgram("alias reference kept")(declaration + "let alias = before\ntrue")(true))
|> (given (_) -> checkProgram("result reference kept")(declaration + "before")(true))
|> (given (_) -> checkProgram("aggregate reference kept")(declaration + "(before, true)")(true))
|> (given (_) -> checkProgram("recursive body reference kept")(declaration + "let recursive keep x = before(x)(x)\ntrue")(true))
|> (given (_) -> checkProgram("implementation reference kept")(declaration + "type Box = | Box(Int)\nimplement Eq(Box) = | equal = given x -> given y -> before(x)(y)\ntrue")(true))
|> (given (_) -> checkProgram("trait default reference kept")(declaration + "trait Check(a) = | check : a -> Bool = given x -> before(x)(x)\ntrue")(true))
|> (given (_) -> checkProgram("provider reference kept")(declaration + "capability Check = | run : Int -> Bool\nprovide Check = | run = given x -> before(x)(x)\ntrue")(true))
|> (given (_) -> checkProgram("export reference kept")("export (value before,)\n" + declaration + "true")(true))
|> (given (_) -> checkProgram("eager initializer kept")("let before = Ashes.IO.print(1)\ntrue")(true))
|> (given (_) ->
    true
    |> emptyUses
    |> exportReferenceUses([ExportModule("Nested")])
    |> moduleExports
    |> assert("module export must block pruning"))
|> (given (_) ->
    assert("qualified ownership mode changed")(!has("before")(false
    |> emptyUses
    |> collectNonCalleeUses(ExprQualifiedVar("before")("field"))(false)([]))))
|> (given (_) -> checkExtraMode("implementation old mode unchanged")("implement Eq(Int) = | equal = given x -> given y -> before(x)(y)"))
|> (given (_) -> checkExtraMode("default old mode unchanged")("trait Check(a) = | check : a -> Bool = given x -> before(x)(x)"))
|> (given (_) -> checkExtraMode("provider old mode unchanged")("capability Check = | run : Int -> Bool\nprovide Check = | run = given x -> before(x)(x)"))
|> (given (_) -> checkExtraMode("export old mode unchanged")("export (value before,)"))
|> plateauFromArguments
|> (given (_) -> Ashes.IO.print("unused lambda reference coverage passed"))
