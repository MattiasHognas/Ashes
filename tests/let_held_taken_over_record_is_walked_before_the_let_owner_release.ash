// expect: 20
// A let holding a call's taken-over record list is settled at the let's end; the walk over the
// arena cells reads the list through the let's own slot, so the owner's release follows the walk.
type LeakPat =
    | LeakPatAt(Int, LeakPat)
    | LeakPatEmpty
    | LeakPatVar(Str)

let recursive expand pattern environment =
    match pattern with
        | LeakPatAt(_span, inner) -> expand(inner)(environment)
        | other -> [other]

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

let recursive count xs =
    match xs with
        | [] -> 0
        | _ :: rest -> 1 + count(rest)

let makeCases (n: Int) = [(LeakPatAt(n)(LeakPatVar("v" + Ashes.Text.fromInt(n))), 1, Some(n)), (LeakPatVar("w" + Ashes.Text.fromInt(n)), 2, None)]

let check cases environment =
    (let expanded = expandCases(cases)(environment)([])
    in count(expanded) + count(expanded))

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        loop(n - 1)(acc + check(makeCases(n))(["env"]))

let takenOverRecordRun (seed: Int) = loop(3)(seed)

let generatedResult : Int = takenOverRecordRun(8)
in
    generatedResult
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
