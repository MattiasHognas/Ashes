// expect: 4/5 3/4
// A function called inside a handle returns a tuple holding a perform's result, and the handle's
// value goes straight to another function, with and without a return arm.
capability Cap =
    | get : Unit -> Str

let make (i: Int) = (perform Cap.get(Unit), i)

let size (p: (Str, Int)) =
    match p with
        | (s, a) -> Ashes.Text.byteLength(s) + a

let probe (n: Int) =
    (let a =
        size(handle make(n) with
            | Cap.get(u) ->
                n * 10
                |> Ashes.Text.fromInt
                |> resume)
    in
        let b =
            size(handle make(n) with
                | Cap.get(u) -> resume("lit")
                | return(r) -> r)
        in Ashes.Text.fromInt(a) + "/" + Ashes.Text.fromInt(b) + " ")

let recursive loop (n: Int) (acc: Str) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

""
|> loop(2)
|> Ashes.IO.print
