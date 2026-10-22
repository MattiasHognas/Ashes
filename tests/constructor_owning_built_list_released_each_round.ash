// expect: 18
// A list literal's element is a constructor whose list field holds a string built at run time. The
// constructor's cell is built on the reference-counted heap, owning that fresh list, so the
// consumer's release of the literal frees it every round.
type Kind =
    | SwitchTag(Int, List(Str), Str)
    | Other(Int)

let recursive countRefs (instructions: List(Kind)) (acc: List((Str, Int))) =
    match instructions with
        | [] -> acc
        | inst :: tail ->
            match inst with
                | SwitchTag(_, labels, defaultLabel) -> countRefs(tail)((defaultLabel, Ashes.Collection.List.length(labels)) :: acc)
                | _ -> countRefs(tail)(acc)

let recursive total (refs: List((Str, Int))) (sum: Int) =
    match refs with
        | [] -> sum
        | (label, c) :: tail -> total(tail)(sum + c + Ashes.Text.byteLength(label))

let program n = [SwitchTag(1)(["b" + Ashes.Text.fromInt(n)])("c"), Other(1), SwitchTag(2)(["a", "b" + Ashes.Text.fromInt(n)])("ab")]

let run n =
    total(countRefs(program(n))([]))(0)

let recursive rounds n sum =
    if n == 0
    then sum
    else rounds(n - 1)(sum + run(n))

0
|> rounds(3)
|> Ashes.IO.print
