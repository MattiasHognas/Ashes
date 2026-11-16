// A tail-recursive scan whose record parameter's Bool field is read into a tuple scrutinee
// beside an ADT and a comparison: the field is a copied word that the tuple cell stores without
// a retain, even when the parameter itself is placed on the reference-counted heap.
// expect: Ashes.IO.print

type Kind =
    | Ident
    | Dot
    | Other

type Tok =
    | kind: Kind
    | text: Str

type State =
    | path: Str
    | longest: Maybe(Str)
    | afterDot: Bool
    | refs: List(Str)

let flush (state: State) =
    match state.longest with
        | Some(name) -> State(path = "", longest = None, afterDot = false, refs = name :: state.refs)
        | None -> State(path = "", longest = None, afterDot = false, refs = state.refs)

let start (text: Str) (state: State) =
    State(
        path = if text == "Ashes"
        then "Ashes"
        else "",
        longest = None,
        afterDot = false,
        refs = state.refs
    )

let extend (text: Str) (state: State) = State(path = state.path + "." + text, longest = Some(state.path + "." + text), afterDot = false, refs = state.refs)

let recursive scan (tokens: List(Tok)) (state: State) =
    match tokens with
        | [] ->
            match flush(state) with
                | State { refs = refs } -> refs
        | Tok { kind = kind, text = text } :: rest ->
            match (kind, state.afterDot, state.path == "") with
                | (Ident, true, _) ->
                    state
                    |> extend(text)
                    |> scan(rest)
                | (Dot, false, false) -> scan(rest)((state with afterDot = true))
                | (Ident, _, _) ->
                    state
                    |> flush
                    |> start(text)
                    |> scan(rest)
                | (_other, _, _) ->
                    state
                    |> flush
                    |> scan(rest)

let tokens = [Tok(kind = Ident, text = "Ashes"), Tok(kind = Dot, text = "."), Tok(kind = Ident, text = "IO"), Tok(kind = Dot, text = "."), Tok(kind = Ident, text = "print"), Tok(kind = Other, text = "("), Tok(kind = Ident, text = "x"), Tok(kind = Other, text = ")")]

State(path = "", longest = None, afterDot = false, refs = [])
|> scan(tokens)
|> Ashes.Text.join(",")
|> Ashes.IO.print
