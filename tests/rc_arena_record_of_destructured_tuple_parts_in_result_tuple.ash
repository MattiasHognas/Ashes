// expect: 18000
// An arena record built from the parts of a destructured reference-counted tuple, returned inside
// a reference-counted tuple: the result tuple stores its own copy of the record, which holds a
// reference to each part it shares, so the destructured tuple is released at its scope exit like
// any other. Every round of this loop used to leave that tuple behind.
type Token =
    | text: Str
    | kind: Int

type alias State = (List(Token), List(Str))

type TypeExpr =
    | TypeName(Str)
    | TypeApp(Str, List(TypeExpr))

type Binding =
    | name: Str
    | annotation: Maybe(TypeExpr)
    | requirements: List(TypeExpr)

let advance (state: State) =
    match state with
        | (token :: rest, notes) -> (token, (rest, notes))
        | ([], _notes) -> (Token(text = "", kind = 0), state)

let kindOf (state: State) =
    match state with
        | (token :: _, _notes) -> token.kind
        | ([], _notes) -> 0

let parseBinding (state: State) =
    match advance(state) with
        | (name, afterName) ->
            let header =
                if kindOf(afterName) == 2
                then
                    match advance(afterName) with
                        | (typeToken, afterType) -> (Some(TypeName(typeToken.text)), [TypeApp(typeToken.text)([])], afterType)
                else (None, [], afterName)
            in
                match header with
                    | (annotation, requirements, afterHeader) ->
                        let binding = Binding(name = name.text, annotation = annotation, requirements = requirements)
                        in (binding, afterHeader)

let recursive count (items: List(TypeExpr)) (total: Int) =
    match items with
        | [] -> total
        | _ :: rest -> count(rest)(total + 1)

let recursive textLength (tokens: List(Token)) (total: Int) =
    match tokens with
        | [] -> total
        | token :: rest -> textLength(rest)(total + Ashes.Text.byteLength(token.text))

let measure (binding: Binding) (rest: List(Token)) =
    match binding.annotation with
        | Some(TypeName(text)) -> Ashes.Text.byteLength(binding.name + text) + count(binding.requirements)(0) + textLength(rest)(0)
        | _ -> 0

let recursive rounds (tokens: List(Token)) (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match parseBinding((tokens, [])) with
            | (binding, (rest, _notes)) -> rounds(tokens)(n - 1)(total + measure(binding)(rest))

0
|> rounds([Token(text = "f", kind = 0), Token(text = "Int", kind = 2), Token(text = "more", kind = 0)])(2000)
|> Ashes.IO.print
