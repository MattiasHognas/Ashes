// expect: 9|9|9
type Box =
    | Box(Str, Int)

capability Source(a) =
    | get : Unit -> a

let field (n: Int) =
    handle (match perform Source.get(Unit) with
        | Box(t, _) -> t) with
        | Source.get(_) ->
            n
            |> Box(Ashes.Text.fromInt(n))
            |> resume
        | return(r) -> r

let fresh (n: Int) =
    handle (match perform Source.get(Unit) with
        | Box(t, _) ->
            t
            |> Ashes.Text.byteLength
            |> Ashes.Text.fromInt) with
        | Source.get(_) ->
            n
            |> Box(Ashes.Text.fromInt(n))
            |> resume
        | return(r) -> r

let either (n: Int) =
    handle (match perform Source.get(Unit) with
        | 0 -> ""
        | k -> Ashes.Text.fromInt(k)) with
        | Source.get(_) -> resume(n)
        | return(r) -> r

let recursive total (probe: Int -> Str) (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        total(probe)(n - 1)(acc + Ashes.Text.byteLength(probe(n)))
in
    Ashes.IO.print(Ashes.Text.fromInt(total(field)(9)(0)) + "|" + Ashes.Text.fromInt(total(fresh)(9)(0)) + "|" + Ashes.Text.fromInt(total(either)(9)(0)))
