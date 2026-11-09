// expect: 40
// A loop calls a helper whose environment parameter is generic with a fresh list each round.
// The list goes to the call as an owned reference-counted reference the caller settles after
// the call, instead of a deep copy into the persistent region that nothing reclaims.
type Pat =
    | PAt(Int, Pat)
    | PEmpty
    | PVar(Str)

let recursive expand pattern environment =
    match pattern with
        | PAt(_span, inner) -> expand(inner)(environment)
        | other -> [other]

let recursive count xs =
    match xs with
        | [] -> 0
        | _ :: rest -> 1 + count(rest)

let makeCases (n: Int) = [(PEmpty, 1, Some(n)), (PEmpty, 2, None), (PEmpty, 3, Some(0))]

let recursive expandCases cases environment acc =
    match cases with
        | [] -> Ashes.Collection.List.reverse(acc)
        | (pattern, body, guard) :: rest ->
            acc
            |> expandedArms(expand(pattern)(environment))(body)(guard)
            |> expandCases(rest)(environment)
and expandedArms patterns body guard acc =
    match patterns with
        | [] -> acc
        | pattern :: rest -> expandedArms(rest)(body)(guard)((pattern, body, guard) :: acc)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        loop(n - 1)(acc + count(expand(PAt(n)(PEmpty))(["env"])))

0
|> loop(40)
|> Ashes.IO.print
