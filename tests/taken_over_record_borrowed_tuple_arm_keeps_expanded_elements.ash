// expect: 9
type Pat =
    | PAt(Int, Pat)
    | PEmpty
    | PVar(Str)

let recursive expand pattern environment =
    match pattern with
        | PAt(_span, inner) -> expand(inner)(environment)
        | other -> [other]

let recursive expandedArms patterns body guard acc =
    match patterns with
        | [] -> acc
        | pattern :: rest -> expandedArms(rest)(body)(guard)((pattern, body, guard) :: acc)

let recursive expandCases cases environment acc =
    match cases with
        | [] -> Ashes.Collection.List.reverse(acc)
        | (PVar(_) as pattern, body, guard) :: rest -> expandCases(rest)(environment)((pattern, body, guard) :: acc)
        | (pattern, body, guard) :: rest ->
            acc
            |> expandedArms(expand(pattern)(environment))(body)(guard)
            |> expandCases(rest)(environment)

let recursive count xs =
    match xs with
        | [] -> 0
        | _ :: rest -> 1 + count(rest)

let makeCases (n: Int) = [(PAt(n)(PVar("head" + Ashes.Text.fromInt(n))), 1, Some(n)), (PVar("v" + Ashes.Text.fromInt(n)), 2, None), (PEmpty, 3, Some(0))]

let probe (n: Int) =
    (let cases = makeCases(n)
    in
        []
        |> expandCases(cases)(["env"])
        |> count)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(3)
|> Ashes.IO.print
