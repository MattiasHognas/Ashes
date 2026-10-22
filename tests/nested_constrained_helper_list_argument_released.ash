// expect: 12
// A nested constrained helper (`addCases` compares through `Eq`) is handed a list a pattern binds
// out of the loop's list parameter. Its result keeps the list's strings, never the list itself; the
// reference the list takes for that result is released once the loop's successor holds references
// of its own, and every label is still counted.
type Kind =
    | SwitchTag(List(Str), Str)
    | Other(Int)

let recursive setAssociation key value entries =
    match entries with
        | [] -> [(key, value)]
        | (k, v) :: tail ->
            if k == key
            then (key, value) :: tail
            else (k, v) :: setAssociation(key)(value)(tail)

let recursive countRefs instructions acc =
    match instructions with
        | [] -> acc
        | inst :: tail ->
            match inst with
                | SwitchTag(cases, defaultLabel) ->
                    let recursive addCases cs entries =
                        match cs with
                            | [] -> entries
                            | l :: cTail ->
                                entries
                                |> setAssociation(l)(1)
                                |> addCases(cTail)
                    in
                        let mapWithCases = addCases(cases)(acc)
                        in
                            mapWithCases
                            |> setAssociation(defaultLabel)(1)
                            |> countRefs(tail)
                | _ -> countRefs(tail)(acc)

let recursive total (refs: List((Str, Int))) (sum: Int) =
    match refs with
        | [] -> sum
        | (_, c) :: tail -> total(tail)(sum + c)

let run n =
    total(countRefs([SwitchTag(["a", "b" + Ashes.Text.fromInt(n)])("c"), Other(1), SwitchTag([])("a")])([]))(0)

let recursive rounds n sum =
    if n == 0
    then sum
    else rounds(n - 1)(sum + run(n))

0
|> rounds(4)
|> Ashes.IO.print
