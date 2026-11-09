// expect: 100
type Item =
    | name: Str
    | size: Int

let recursive walk (items: List(Item)) (current: Item) (agreed: Maybe(Str)) =
    match items with
        | [] ->
            match agreed with
                | Some(found) -> Ashes.Text.length(found) + current.size
                | None -> 0
        | next :: tail -> walk(tail)(next)(Some(current.name))

let probe (n: Int) =
    (let label = "lambda_" + Ashes.Text.fromInt(n % 10)
    in walk([Item(name = label, size = 1), Item(name = label + "x", size = 2)])(Item(name = label + "y", size = 3))(None))

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(10)
|> Ashes.IO.print
