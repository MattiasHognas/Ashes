// expect: 2
type Ex =
    | ExAt(Int, Ex)
    | ExVar(Str)
    | ExLet(Str, Ex, Ex)
    | ExOther

let recursive tailForwarded (body: Ex) =
    match body with
        | ExAt(_span, inner) -> tailForwarded(inner)
        | ExVar(name) -> Some(name)
        | ExLet(nested, _value, nestedBody) ->
            match tailForwarded(nestedBody) with
                | Some(name) ->
                    if name == nested
                    then None
                    else Some(name)
                | None -> None
        | _ -> None

let probe (n: Int) =
    (let name = Ashes.Text.fromInt(n)
    in
        match ExVar(name)
        |> ExAt(2)
        |> ExLet("y")(ExOther)
        |> ExAt(1)
        |> tailForwarded with
            | Some(found) ->
                if found == name
                then 1
                else 0
            | None -> 0)

let shadowed =
    match ExVar("y")
    |> ExLet("y")(ExOther)
    |> tailForwarded with
        | Some(_found) -> 0
        | None -> 1

Ashes.IO.print(probe(7) + shadowed)
