// expect: lit|lit|2| s|s|1j|2j|2| s0|s0|i1|i2|22|,lit|lit|1| s|s|1j|2j|1| s0|s0|i1|i2|11|,
// A tail-recursive loop running inside a handle conses values built from a perform's result onto
// its list accumulator: the string itself, a string joined with it, and a constructor holding it,
// with and without a one-shot post in the arm.
capability Cap =
    | get : Unit -> Str

type Item =
    | Item(Str, Int)

let recursive plain (i: Int) (acc: List(Str)) =
    if i == 0
    then acc
    else plain(i - 1)(perform Cap.get(Unit) :: acc)

let recursive joined (i: Int) (acc: List(Str)) =
    if i == 0
    then acc
    else joined(i - 1)(Ashes.Text.fromInt(i) + perform Cap.get(Unit) :: acc)

let recursive items (i: Int) (acc: List(Item)) =
    if i == 0
    then acc
    else items(i - 1)(Item(perform Cap.get(Unit))(i) :: acc)

let recursive render (xs: List(Str)) =
    match xs with
        | [] -> ""
        | x :: rest -> x + "|" + render(rest)

let recursive renderItems (xs: List(Item)) =
    match xs with
        | [] -> ""
        | Item(s, k) :: rest -> s + Ashes.Text.fromInt(k) + "|" + renderItems(rest)

let probe (n: Int) =
    (let a =
        render(handle plain(2)([Ashes.Text.fromInt(n)]) with
            | Cap.get(u) -> resume("lit"))
    in
        let b =
            render(handle joined(2)([Ashes.Text.fromInt(n)]) with
                | Cap.get(u) ->
                    let x = resume("j")
                    in "s" :: x
                | return(r) -> r)
        in
            let c =
                renderItems(handle items(2)([Item(Ashes.Text.fromInt(n))(n)]) with
                    | Cap.get(u) ->
                        let x = resume("i")
                        in Item("s")(0) :: x
                    | return(r) -> r)
            in a + " " + b + " " + c + ",")

let recursive loop (n: Int) (acc: Str) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

""
|> loop(2)
|> Ashes.IO.print
