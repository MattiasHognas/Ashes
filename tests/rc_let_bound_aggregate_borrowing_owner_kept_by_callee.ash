// expect: tok0 tok1 tok2 tok3 tok4 tok5 2000 2
type Token =
    | kind: Int
    | text: Str

type LexerResult =
    | tokens: List(Token)
    | diagnostics: List(Str)

type Node =
    | Leaf(Str)
    | Pair(Node, Node)

type Program =
    | items: List(Node)
    | diagnostics: List(Str)

let recursive rev remaining result =
    match remaining with
        | [] -> result
        | head :: tail -> rev(tail)(head :: result)

let recursive lexerScan (n: Int) (tokens: List(Token)) (diagnostics: List(Str)) =
    (let next = n - 1
    in
        if next < 0
        then LexerResult(tokens = rev(tokens)([]), diagnostics = rev(diagnostics)([]))
        else
            match next % 3 with
                | 0 -> lexerScan(next)(Token(kind = next, text = "tok" + Ashes.Text.fromInt(next)) :: tokens)("warn" + Ashes.Text.fromInt(next) :: diagnostics)
                | _ -> lexerScan(next)(Token(kind = next, text = "tok" + Ashes.Text.fromInt(next)) :: tokens)(diagnostics))

let tokenize (n: Int) = lexerScan(n)([])([])

let current (state: (List(Token), Int)) =
    match state with
        | (token :: _, _) -> token
        | ([], _) -> Token(kind = 0, text = "eof")

let recursive parseItems (state: (List(Token), Int)) (acc: List(Node)) =
    match state with
        | (tokens, count) ->
            match tokens with
                | [] -> (acc, state)
                | _ :: rest ->
                    let cur = current(state)
                    in parseItems((rest, count + 1))(Pair(Leaf(cur.text))(Leaf("x")) :: acc)

let parseProgram (n: Int) =
    (let lexed = tokenize(n)
    in
        let initial = (lexed.tokens, 0)
        in
            match parseItems(initial)([]) with
                | (items, _state) -> Program(items = items, diagnostics = lexed.diagnostics))

let recursive churn (k: Int) (acc: List(Str)) =
    if k == 0
    then acc
    else churn(k - 1)("garbage" + Ashes.Text.fromInt(k) :: acc)

let recursive render (nodes: List(Node)) (acc: Str) =
    match nodes with
        | [] -> acc
        | Pair(Leaf(text), _) :: rest -> render(rest)(acc + text + " ")
        | _ :: rest -> render(rest)(acc + "? ")

let program = parseProgram(6)

let noise = churn(2000)([])

Ashes.IO.print(render(program.items)("") + Ashes.Text.fromInt(Ashes.Collection.List.length(noise)) + " " + Ashes.Text.fromInt(Ashes.Collection.List.length(program.diagnostics)))
