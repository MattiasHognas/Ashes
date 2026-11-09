// expect: 12
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
        | (pattern, body, guard) :: rest ->
            acc
            |> expandedArms(expand(pattern)(environment))(body)(guard)
            |> expandCases(rest)(environment)

let recursive count xs =
    match xs with
        | [] -> 0
        | _ :: rest -> 1 + count(rest)

let makeCases (n: Int) = [(PAt(n)(PVar("head" + Ashes.Text.fromInt(n))), 1, Some(n)), (PVar("v" + Ashes.Text.fromInt(n)), 2, None), (PEmpty, 3, Some(0))]

let counter cases environment =
    (let expanded = expandCases(cases)(environment)([])
    in
        given (extra) -> count(expanded) + extra)

let probe (n: Int) =
    counter(makeCases(n))(["env"])(1)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(3)
|> Ashes.IO.print
