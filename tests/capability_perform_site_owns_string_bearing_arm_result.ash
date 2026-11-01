// expect: 6|9|21|9|6
type Box =
    | Box(Str, Int)

capability Source(a) =
    | get : Unit -> a

let text (n: Int) =
    handle Ashes.Text.byteLength(perform Source.get(Unit)) with
        | Source.get(_) ->
            n * 100
            |> Ashes.Text.fromInt
            |> resume
        | return(r) -> r

let boxed (n: Int) =
    handle (match perform Source.get(Unit) with
        | Box(t, k) -> Ashes.Text.byteLength(t) + k) with
        | Source.get(_) ->
            n
            |> Box(Ashes.Text.fromInt(n))
            |> resume
        | return(r) -> r

let checked (n: Int) =
    handle (match perform Source.get(Unit) with
        | Ok(t) -> Ashes.Text.byteLength(t) + n
        | Error(_) -> 0) with
        | Source.get(_) -> resume(Ok("owned"))
        | return(r) -> r

let failed (n: Int) =
    handle (match perform Source.get(Unit) with
        | Ok(_) -> 0
        | Error(t) -> Ashes.Text.byteLength(t) + n) with
        | Source.get(_) ->
            n
            |> Ashes.Text.fromInt
            |> Error
            |> resume
        | return(r) -> r

let named (n: Int) =
    Ashes.Text.byteLength(handle (match perform Source.get(Unit) with
        | Ok(t) -> t
        | Error(_) -> "none") with
        | Source.get(_) ->
            n * 10
            |> Ashes.Text.fromInt
            |> Ok
            |> resume
        | return(r) -> r)

let recursive sum (probe: Int -> Int) (n: Int) (acc: Int) =
    if n == 0
    then acc
    else sum(probe)(n - 1)(acc + probe(n))

Ashes.IO.print(Ashes.Text.fromInt(sum(text)(2)(0)) + "|" + Ashes.Text.fromInt(sum(boxed)(3)(0)) + "|" + Ashes.Text.fromInt(sum(checked)(3)(0)) + "|" + Ashes.Text.fromInt(sum(failed)(3)(0)) + "|" + Ashes.Text.fromInt(sum(named)(3)(0)))
