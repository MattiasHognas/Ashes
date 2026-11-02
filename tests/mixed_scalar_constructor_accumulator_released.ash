// expect: 9
type Ty =
    | TyInt
    | TyVar(Int)

let recursive freshTypes (count: Int) (acc: List(Ty)) =
    if count == 0
    then acc
    else freshTypes(count - 1)(TyVar(count) :: acc)

let run (seed: Int) =
    match freshTypes(2)([]) with
        | [] -> seed
        | _ :: _ -> seed + 1

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(n))

0
|> rounds(3)
|> Ashes.IO.print
