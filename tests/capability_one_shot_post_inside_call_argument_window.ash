// expect: 131
capability Cap =
    | get : Unit -> Str

let one (a: Str) = a + "k"

let fetch (u: Unit) = perform Cap.get(Unit)

let probe (n: Int) =
    handle Unit
    |> fetch
    |> one with
        | Cap.get(u) ->
            let x = resume("lit")
            in x + "s"
        | return(r) -> r + Ashes.Text.fromInt(n)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        loop(n - 1)(acc + Ashes.Text.byteLength(probe(n)))
in
    0
    |> loop(20)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
