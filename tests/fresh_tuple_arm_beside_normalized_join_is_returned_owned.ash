// expect: 720 540
// A parser's top-level `let`: the `in` arm builds its result tuple under a `let`, the other arm is a
// join already normalized (a tuple built under a `let` beside a call of the mutually recursive
// item loop). The outer join owns both arms' references, so the function's return takes its value
// over instead of retaining it again, which leaked the whole parse result of every call.
type Token =
    | kind: Int
    | text: Str
    | position: Int

type Expr =
    | EInt(Int)
    | ELet(Str, Expr, Expr)

type Item =
    | TopLet(Str, Expr)

type alias State = (List(Token), Str)

let eofToken = Token(kind = 0, text = "", position = 0)

let advance (state: State) =
    match state with
        | (token :: tail, source) -> (token, (tail, source))
        | ([], _source) -> (eofToken, state)

let isKind (kind: Int) (state: State) =
    match state with
        | (token :: _, _source) -> token.kind == kind
        | ([], _source) -> kind == 0

let parseExpression (state: State) =
    match advance(state) with
        | (token, after) -> (EInt(token.position), after)

let recursive programItems (reversedItems: List(Item)) (state: State) =
    if isKind(1)(state)
    then topLevelLet(reversedItems)(state)
    else (Ashes.Collection.List.reverse(reversedItems), None, state)
and topLevelLet reversedItems state =
    match advance(state) with
        | (letToken, afterLet) ->
            match topLevelBinding(afterLet) with
                | (name, value, afterBinding) ->
                    if isKind(3)(afterBinding)
                    then
                        match parseExpression(afterBinding) with
                            | (body, afterBody) ->
                                let expression = ELet(name)(value)(body)
                                in (Ashes.Collection.List.reverse(reversedItems), Some(expression), afterBody)
                    else
                        if letToken.position < 0
                        then
                            let missing = ELet(name)(value)(EInt(0))
                            in (Ashes.Collection.List.reverse(reversedItems), Some(missing), afterBinding)
                        else programItems(TopLet(name)(value) :: reversedItems)(afterBinding)
and topLevelBinding state =
    match advance(state) with
        | (name, afterName) ->
            match parseExpression(afterName) with
                | (value, afterValue) -> (name.text, value, afterValue)

let recursive positions (reversed: List(Int)) (state: State) =
    if isKind(1)(state)
    then positionLet(reversed)(state)
    else (Ashes.Collection.List.reverse(reversed), None, state)
and positionLet reversed state =
    match advance(state) with
        | (letToken, afterLet) ->
            match positionBinding(afterLet) with
                | (name, _value, afterBinding) ->
                    if isKind(3)(afterBinding)
                    then
                        match advance(afterBinding) with
                            | (_inToken, afterIn) ->
                                let found = name + " in"
                                in (Ashes.Collection.List.reverse(reversed), Some(found), afterIn)
                    else
                        if letToken.position < 0
                        then
                            let missing = name + " missing"
                            in (Ashes.Collection.List.reverse(reversed), Some(missing), afterBinding)
                        else positions(letToken.position :: reversed)(afterBinding)
and positionBinding state =
    match advance(state) with
        | (name, afterName) ->
            match parseExpression(afterName) with
                | (value, afterValue) -> (name.text, value, afterValue)

let token kind text position = Token(kind = kind, text = text, position = position)

let tokens (n: Int) =
    [token(1)("let")(1), token(2)("f" + Ashes.Text.fromInt(n))(2), token(4)("1")(3), token(1)("let")(4), token(2)("g")(5), token(4)("2")(6), token(if n % 5 == 0
    then 3
    else 4)("4")(7), token(4)("5")(8)]

let recursive loop rounds total =
    if rounds == 0
    then total
    else
        match programItems([])((tokens(rounds), "source")) with
            | (items, body, _) ->
                loop(rounds - 1)(total + Ashes.Collection.List.length(items) + (match body with
                    | Some(_) -> 3
                    | None -> 0))

let recursive loopPositions rounds total =
    if rounds == 0
    then total
    else
        match positions([])((tokens(rounds), "source")) with
            | (found, _, _) -> loopPositions(rounds - 1)(total + Ashes.Collection.List.length(found))

let rounds = 300

Ashes.IO.print(Ashes.Text.fromInt(loop(rounds)(0)) + " " + Ashes.Text.fromInt(loopPositions(rounds)(0)))
