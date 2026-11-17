// A call to a later member of the same recursive group asks for its result in the arena; the
// callee answers with an arena copy, so the caller copies the result out before the call's window
// is reclaimed even though the callee's closure reports results on the reference-counted heap.
// expect: v,k,a

type TypeExpr =
    | TypeNamed(Str)
    | TypeApplied(Str, List(TypeExpr))

let recursive headNames (typeExpr: TypeExpr) (names: List(Str)) =
    match typeExpr with
        | TypeNamed(name) ->
            if Ashes.Text.length(name) == 1
            then name :: names
            else names
        | TypeApplied(_name, arguments) -> headNamesOf(arguments)(names)
and headNamesOf (typeExprs: List(TypeExpr)) (names: List(Str)) =
    match typeExprs with
        | [] -> names
        | head :: rest ->
            names
            |> headNames(head)
            |> headNamesOf(rest)

[]
|> headNamesOf([TypeNamed("a"), TypeApplied("Map")([TypeNamed("k"), TypeNamed("Int"), TypeNamed("v")])])
|> Ashes.Text.join(",")
|> Ashes.IO.print
