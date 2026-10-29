// expect: 15|6|6
capability Source(a) =
    | get : Unit -> a

let first (n: Int) =
    handle (match perform Source.get(Unit) with
        | x :: _ -> x
        | [] -> 0) with
        | Source.get(_) -> resume([n, n])
        | return(r) -> r

let head (n: Int) =
    handle (match perform Source.get(Unit) with
        | x :: _ -> x
        | [] -> "") with
        | Source.get(_) -> resume([Ashes.Text.fromInt(n), "a"])

let headLength (n: Int) =
    handle (match perform Source.get(Unit) with
        | x :: _ -> Ashes.Text.byteLength(x)
        | [] -> 0) with
        | Source.get(_) -> resume([Ashes.Text.fromInt(n), "a"])
        | return(r) -> r

let recursive firsts (n: Int) (acc: Int) =
    if n == 0
    then acc
    else firsts(n - 1)(acc + first(n))

let recursive heads (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        heads(n - 1)(acc + Ashes.Text.byteLength(head(n)))

let recursive lengths (n: Int) (acc: Int) =
    if n == 0
    then acc
    else lengths(n - 1)(acc + headLength(n))
in
    Ashes.IO.print(Ashes.Text.fromInt(firsts(5)(0)) + "|" + Ashes.Text.fromInt(heads(6)(0)) + "|" + Ashes.Text.fromInt(lengths(6)(0)))
