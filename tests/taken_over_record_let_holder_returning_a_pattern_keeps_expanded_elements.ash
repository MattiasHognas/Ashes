// expect: 3
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

let firstPattern cases environment =
    (let expanded = expandCases(cases)(environment)([])
    in
        match expanded with
            | (pattern, _, _) :: _ -> Some(pattern)
            | [] -> None)

let probe (n: Int) =
    match firstPattern(makeCases(n))(["env"]) with
        | Some(PAt(_, PVar(name))) -> Ashes.Text.length(name)
        | Some(_) -> 1
        | None -> 0

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(3)
|> Ashes.IO.print
