// expect: 21
import Names.collect as collected

module Names =
    let recursive collect (items: List(Str)) (acc: List((Str, Int))) =
        match items with
            | [] -> Ashes.Collection.List.reverse(acc)
            | item :: rest -> collect(rest)((item, 1) :: acc)

let recursive overlay (proven: List(Str)) (pairs: List((Str, Int))) =
    match (proven, pairs) with
        | (_p :: provenRest, (name, _n) :: rest) -> (name, 2) :: overlay(provenRest)(rest)
        | (_, remaining) -> remaining

let pick (flag: Bool) (pairs: List((Str, Int))) (proven: List(Str)) =
    if flag
    then overlay(proven)(pairs)
    else pairs

let recursive count (xs: List((Str, Int))) =
    match xs with
        | [] -> 0
        | (_name, n) :: rest -> n + count(rest)

let probe (n: Int) =
    (let s = Ashes.Text.fromInt(n)
    in count(pick(n > 0)(collected(["a", "b" + s, "c"])([]))(["a"])) + count(pick(n > 0)(Names.collect(["d" + s, "e"])([]))(["d"])))

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

Ashes.IO.print(loop(3)(0))
