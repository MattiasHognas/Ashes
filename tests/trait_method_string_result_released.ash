// expect: 42
type Fn =
    | label: Str
    | body: List(Int)

let variantOf (callee: Fn) (counter: Int) = Some((callee with label = callee.label + "__scalarenv" + Ashes.Trait.Show.show(counter)))

let probe (n: Int) =
    match variantOf(Fn(label = "f" + Ashes.Text.fromInt(n), body = [1]))(n % 10) with
        | Some(v) -> Ashes.Text.byteLength(v.label)
        | None -> 0

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(3)
|> Ashes.IO.print
