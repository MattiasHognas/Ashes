// expect: 40
// A loop's accumulator is replaced in every arm by a constrained call's result (`setAssociation`
// compares keys with `==`, so it takes a hidden `Eq` dictionary), the key a name a pattern binds
// out of the loop's list parameter. The reference the key takes for the call's arena result is
// released once the loop's successor holds its own references, and the table still answers with
// every key it was given.
let recursive setAssociation key value entries =
    match entries with
        | [] -> [(key, value)]
        | (k, v) :: tail ->
            if k == key
            then (key, value) :: tail
            else (k, v) :: setAssociation(key)(value)(tail)

let recursive collect (groups: List(Maybe(Str))) acc =
    match groups with
        | [] -> acc
        | Some(label) :: tail ->
            acc
            |> setAssociation(label)(1)
            |> collect(tail)
        | None :: tail ->
            acc
            |> setAssociation("e")(2)
            |> collect(tail)

let recursive total (refs: List((Str, Int))) (sum: Int) =
    match refs with
        | [] -> sum
        | (_, c) :: tail -> total(tail)(sum + c)

let recursive keyLengths (refs: List((Str, Int))) (sum: Int) =
    match refs with
        | [] -> sum
        | (k, _) :: tail -> keyLengths(tail)(sum + Ashes.Text.byteLength(k))

let recursive rounds (n: Int) (sum: Int) =
    if n == 0
    then sum
    else
        let refs = collect([Some("a"), Some("b" + Ashes.Text.fromInt(n)), None, Some("a"), Some("c")])([])
        in rounds(n - 1)(sum + total(refs)(0) + keyLengths(refs)(0))

0
|> rounds(4)
|> Ashes.IO.print
