// expect: 2
// A loop conses each iteration's expanded patterns, an owned call result, onto its accumulator
// through a generic helper; the earlier iterations' lists stay alive while the result holds their
// parts.
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

[(PAt(1)(PVar("head")), 1, Some(1)), (PEmpty, 2, None)]
|> expandCases
|> (given (expanded) -> expanded(["env"])([]))
|> count
|> Ashes.Text.fromInt
|> Ashes.IO.print
