// expect: 18
let recursive setAssociation key value entries =
    match entries with
        | [] -> [(key, value)]
        | (k, v) :: tail ->
            if k == key
            then (key, value) :: tail
            else (k, v) :: setAssociation(key)(value)(tail)

let recursive addCases (cs: List(Str)) entries =
    match cs with
        | [] -> entries
        | label :: cTail ->
            entries
            |> setAssociation(label)(1)
            |> addCases(cTail)

let recursive loop (groups: List(Maybe(List(Str)))) acc =
    match groups with
        | [] -> acc
        | Some(cases) :: tail ->
            let mapWithCases = addCases(cases)(acc)
            in
                mapWithCases
                |> setAssociation("d")(1)
                |> loop(tail)
        | None :: tail -> loop(tail)(acc)

let recursive total (refs: List((Str, Int))) (sum: Int) =
    match refs with
        | [] -> sum
        | (_, c) :: tail -> total(tail)(sum + c)

let run (n: Int) =
    (let refs = loop([Some(["a", "b" + Ashes.Text.fromInt(n)]), None, Some([])])([])
    in total(refs)(0) + total(refs)(0))

let recursive rounds (n: Int) (sum: Int) =
    if n == 0
    then sum
    else rounds(n - 1)(sum + run(n))

0
|> rounds(3)
|> Ashes.IO.print
