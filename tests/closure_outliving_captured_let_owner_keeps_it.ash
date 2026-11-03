// expect: lit3qarg3q3,lit2qarg2q2,lit1qarg1q1,
capability Cap =
    | get : Unit -> Str

let tag (n: Int) = Ashes.Text.fromInt(n) + "q"

let probe (n: Int) =
    handle perform Cap.get(Unit) with
        | Cap.get(u) ->
            let s = tag(n)
            in
                let x = resume("lit")
                in x + s

let make (n: Int) =
    (let s = tag(n)
    in
        let t = Ashes.Text.fromInt(n)
        in
            given (x) -> x + s + t)

let use (n: Int) =
    (let f = make(n)
    in f("arg"))

let recursive loop (n: Int) (acc: Str) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n) + use(n) + ",")

""
|> loop(3)
|> Ashes.IO.print
