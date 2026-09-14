// expect: X.Y X.Y A.B.C A.B
type State =
    | path: Str
    | afterDot: Bool
    | seen: List(Str)

let recursive containsText (value: Str) (values: List(Str)) =
    match values with
        | [] -> false
        | candidate :: rest ->
            if candidate == value
            then true
            else containsText(value)(rest)

let extend (known: List(Str)) (text: Str) (state: State) =
    match state with
        | State { path = path, seen = seen } ->
            let candidate = path + "." + text
            in
                State(
                    path = candidate,
                    afterDot = false,
                    seen = if containsText(candidate)(known)
                    then candidate :: seen
                    else seen
                )

let recursive scan (known: List(Str)) (tokens: List(Str)) (state: State) =
    match tokens with
        | [] -> state.seen
        | token :: rest ->
            if token == "."
            then scan(known)(rest)((state with afterDot = true))
            else
                if state.afterDot
                then
                    state
                    |> extend(known)(token)
                    |> scan(known)(rest)
                else scan(known)(rest)(State(path = token, afterDot = false, seen = state.seen))

let recursive joinAll (items: List(Str)) (acc: Str) =
    match items with
        | [] -> acc
        | item :: rest -> joinAll(rest)(acc + item + " ")

let known = ["A.B", "A.B.C", "X.Y"]

let tokens = ["A", ".", "B", ".", "C", "q", "X", ".", "Y", "A", ".", "Z", "X", ".", "Y", ".", "Z"]

""
|> joinAll(scan(known)(tokens)(State(path = "", afterDot = false, seen = [])))
|> Ashes.IO.print
