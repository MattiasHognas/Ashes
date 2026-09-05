// expect: 110288|174288|2000200020002000200020002000200020002000200020002000200020002000
let recursive last (n: Int) (xs: List(Str)) (keep: Str) =
    match xs with
        | [] -> keep
        | head :: rest -> last(n - 1)(rest)(head)

let recursive widen (n: Int) (text: Str) =
    if n == 0
    then text
    else widen(n - 1)(text + text)

let recursive build (n: Int) (acc: List(Str)) =
    if n == 0
    then acc
    else build(n - 1)(widen(4)(Ashes.Text.fromInt(n)) :: acc)

let recursive totalLength (xs: List(Str)) (n: Int) =
    match xs with
        | [] -> n
        | text :: rest -> totalLength(rest)(n + Ashes.Text.byteLength(text))

let items = build(2000)([])

let kept = last(2000)(items)("z")

let churn = build(3000)([])

Ashes.IO.print(Ashes.Text.fromInt(totalLength(items)(0)) + "|" + Ashes.Text.fromInt(totalLength(churn)(0)) + "|" + kept)
