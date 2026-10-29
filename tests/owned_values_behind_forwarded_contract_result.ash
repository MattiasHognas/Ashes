// A function returns, as it is, a generic call's result built from owned values it passed in: the
// innermost function's contract result holds references of its own and says so through the
// caller's mask, so every forwarding function releases the owned values behind it.
// expect: 20
type St =
    | items: List(Str)
    | n: Int

type Fresh =
    | state: St
    | temp: Int

type Lowered =
    | state: St
    | ok: Bool

let freshTemp (state: St) =
    match state with
        | St { items = items, n = n } -> Fresh(state = St(items = items, n = n + 1), temp = n)

let loaded pattern lower fresh =
    match fresh with
        | Fresh { state = state, temp = temp } -> lower(pattern)(temp)(state)

let loadField pattern lower (state: St) =
    state
    |> freshTemp
    |> (given (fresh) ->
        match fresh with
            | Fresh { state = St { items = items, n = n }, temp = temp } -> Fresh(state = St(items = "x" :: items, n = n), temp = temp))
    |> loaded(pattern)(lower)

let lowerPattern (pattern: Str) (temp: Int) (state: St) = Lowered(state = state, ok = temp > 0)

let recursive loop (remaining: Int) (total: Int) =
    if remaining == 0
    then total
    else
        match loadField("p")(lowerPattern)(St(items = ["a" + Ashes.Text.fromInt(remaining)], n = remaining)) with
            | Lowered { state = St { n = n } } -> loop(remaining - 1)(total + n)

0
|> loop(5)
|> Ashes.IO.print
