// expect: 42
type Fn =
    | label: Str
    | body: List(Int)

let recursive pairsOf (fns: List(Fn)) acc =
    match fns with
        | [] -> acc
        | fn :: rest -> pairsOf(rest)((fn.label, fn.label + "_next") :: acc)

let recursive lookupLabel (key: Str) (entries: List((Str, Str))) =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupLabel(key)(tail)

let devirt (known: List((Str, Str))) (fn: Fn) =
    match lookupLabel(fn.label)(known) with
        | Some(next) -> fn with label = next
        | None -> fn

let stage (entry: Fn) (fns: List(Fn)) = (entry, fns)

let captured (entry: Fn) (fns: List(Fn)) = (entry, fns)

let recursive weight (fns: List(Fn)) =
    match fns with
        | [] -> 0
        | fn :: rest -> Ashes.Text.byteLength(fn.label) + weight(rest)

let recursive mapFns (f: Fn -> Fn) (fns: List(Fn)) =
    match fns with
        | [] -> []
        | fn :: rest -> f(fn) :: mapFns(f)(rest)

let optimize (entry: Fn) (fns: List(Fn)) =
    (let firstKnown = pairsOf(fns)([])
    in
        weight(mapFns(devirt(firstKnown))(fns)) + Ashes.Text.byteLength(entry.label))

let probe (n: Int) = optimize(Fn(label = "e", body = []))([Fn(label = "f" + Ashes.Text.fromInt(n), body = [1]), Fn(label = "g", body = [])])

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(3)
|> Ashes.IO.print
