// expect: 3
type Inst =
    | Jump(Str)
    | Other(Int)

let recursive lookupAssociation key entries =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupAssociation(key)(tail)

let recursive countKnown instructions acc found =
    match instructions with
        | [] -> found
        | inst :: tail ->
            match inst with
                | Jump(target) ->
                    match lookupAssociation(target)(acc) with
                        | Some(_v) -> countKnown(tail)(acc)(found + 1)
                        | None -> countKnown(tail)(acc)(found)
                | _ -> countKnown(tail)(acc)(found)

let label (n: Int) = "label_" + Ashes.Text.fromInt(n)

0
|> countKnown([1
|> label
|> Jump, Other(2), 2
|> label
|> Jump, 1
|> label
|> Jump, 3
|> label
|> Jump])([("label_1", 1), ("label_3", 3)])
|> Ashes.Text.fromInt
|> Ashes.IO.print
