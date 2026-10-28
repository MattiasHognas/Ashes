// expect: 306
type Token =
    | kind: Int
    | text: Str
    | position: Int

type Expr =
    | Num(Int)
    | Bound(Str, Expr, Expr)

let recursive rev (xs: List(Int)) (out: List(Int)) =
    match xs with
        | [] -> out
        | h :: t -> rev(t)(h :: out)

let advance (state: (List(Token), Str)) =
    match state with
        | (head :: tail, source) -> (head, (tail, source))
        | ([], _source) -> (Token(kind = 0, text = "", position = 0), state)

let isKind (kind: Int) (state: (List(Token), Str)) =
    match state with
        | (head :: _, _source) -> head.kind == kind
        | ([], _source) -> kind == 0

let parseValue (state: (List(Token), Str)) =
    match advance(state) with
        | (head, after) -> (Num(head.position), after)

let recursive items (reversed: List(Int)) (state: (List(Token), Str)) =
    if isKind(1)(state)
    then topLet(reversed)(state)
    else (rev(reversed)([]), None, state)
and topLet reversed state =
    match advance(state) with
        | (letToken, afterLet) ->
            match binding(afterLet) with
                | (name, value, afterBinding) ->
                    if isKind(3)(afterBinding)
                    then
                        match parseValue(afterBinding) with
                            | (body, afterBody) ->
                                let found = Bound(name)(value)(body)
                                in (rev(reversed)([]), Some(found), afterBody)
                    else items(letToken.position :: reversed)(afterBinding)
and binding state =
    match advance(state) with
        | (name, afterName) ->
            match parseValue(afterName) with
                | (value, afterValue) -> (name.text, value, afterValue)

let recursive size (xs: List(Int)) (total: Int) =
    match xs with
        | [] -> total
        | _ :: rest -> size(rest)(total + 1)

let run (seed: Int) =
    (let tokens = [Token(kind = 1, text = "let", position = 1), Token(kind = 2, text = "x", position = 2), Token(kind = 4, text = "1", position = seed), Token(kind = 1, text = "let", position = 4), Token(kind = 2, text = "g", position = 5), Token(kind = seed % 2 + 3, text = "2", position = 6), Token(kind = 4, text = "4", position = 7)]
    in
        match items([])((tokens, "source")) with
            | (found, body, _) ->
                size(found)(0) + (match body with
                    | Some(Bound(_name, Num(value), _rest)) -> value
                    | Some(_) -> 0
                    | None -> 100))

let recursive rounds (n: Int) (total: Int) =
    match n with
        | 0 -> total
        | _ -> rounds(n - 1)(total + run(n))

0
|> rounds(3)
|> Ashes.IO.print
