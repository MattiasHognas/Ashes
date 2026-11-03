// expect: lit2 lit2|x0| s0|s0|a1|a2| lit1 lit1|x0| s0|s0|a1|a2|
// A function running inside a handle builds a constructor around a perform's result: returned on
// its own, consed onto a list, and consed by a local loop, with and without a one-shot post.
capability Cap =
    | get : Unit -> Str

type Item =
    | Item(Str, Int)

let single (i: Int) = Item(perform Cap.get(Unit))(i)

let pair (i: Int) = [Item(perform Cap.get(Unit))(i), Item("x")(0)]

let gathered (i: Int) =
    (let recursive go (j: Int) (acc: List(Item)) =
        if j == 0
        then acc
        else go(j - 1)(Item(perform Cap.get(Unit))(j) :: acc)
    in go(i)([]))

let show (it: Item) =
    match it with
        | Item(s, k) -> s + Ashes.Text.fromInt(k)

let recursive render (xs: List(Item)) =
    match xs with
        | [] -> ""
        | x :: rest -> show(x) + "|" + render(rest)

let probe (n: Int) =
    (let a =
        show(handle single(n) with
            | Cap.get(u) -> resume("lit"))
    in
        let b =
            render(handle pair(n) with
                | Cap.get(u) -> resume("lit"))
        in
            let c =
                render(handle gathered(2) with
                    | Cap.get(u) ->
                        let x = resume("a")
                        in Item("s")(0) :: x
                    | return(r) -> r)
            in a + " " + b + " " + c + " ")

let recursive loop (n: Int) (acc: Str) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

""
|> loop(2)
|> Ashes.IO.print
