// expect: w,22 2!,22|x0| s0|t!,t2| w,11 1!,11|x0| s0|t!,t1|
// A function running inside a handle builds a constructor around a perform's list and around a list
// a helper builds from a perform's string: returned on its own, consed onto a list, and with a
// one-shot post.
capability Words =
    | get : Unit -> List(Str)

capability Name =
    | get : Unit -> Str

type Item =
    | Item(List(Str), Int)

let twice (s: Str) = [s + "!", s]

let performed (i: Int) = Item(perform Words.get(Unit))(i)

let helped (i: Int) =
    [Item(twice(perform Name.get(Unit)))(i), Item(["x"])(0)]

let posted (i: Int) =
    Item(twice(perform Name.get(Unit)))(i) :: []

let show (it: Item) =
    match it with
        | Item(ws, k) -> Ashes.Text.join(",")(ws) + Ashes.Text.fromInt(k)

let recursive render (xs: List(Item)) =
    match xs with
        | [] -> ""
        | x :: rest -> show(x) + "|" + render(rest)

let probe (n: Int) =
    (let a =
        show(handle performed(n) with
            | Words.get(u) -> resume(["w", Ashes.Text.fromInt(n)]))
    in
        let b =
            render(handle helped(n) with
                | Name.get(u) ->
                    n
                    |> Ashes.Text.fromInt
                    |> resume)
        in
            let c =
                render(handle posted(n) with
                    | Name.get(u) ->
                        let x = resume("t")
                        in Item(["s"])(0) :: x
                    | return(r) -> r)
            in a + " " + b + " " + c + " ")

let recursive loop (n: Int) (acc: Str) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

""
|> loop(2)
|> Ashes.IO.print
