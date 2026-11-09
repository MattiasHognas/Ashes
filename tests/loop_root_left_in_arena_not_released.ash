// expect: 102
type Fn =
    | label: Str
    | body: List(Int)

let recursive collectGroups (fns: List(Fn)) acc =
    match fns with
        | [] -> acc
        | fn :: rest -> collectGroups(rest)((fn.label, 7) :: acc)

let recursive walk groups (n: Int) =
    match groups with
        | [] -> n
        | (label, index) :: tail -> walk(tail)(n + index + Ashes.Text.byteLength(label))

let recursive fixpoint groups (acc: Int) =
    match walk(groups)(acc) with
        | 0 -> 0
        | next ->
            if next > 100
            then next
            else fixpoint(groups)(next)

let probe (n: Int) (fns: List(Fn)) =
    (let all = Fn(label = "e", body = []) :: fns
    in
        walk(collectGroups(all)([]))(n))

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n)([Fn(label = "f", body = [n]), Fn(label = "g", body = []), Fn(label = "h", body = [2])]))

0
|> loop(3)
|> Ashes.IO.print
