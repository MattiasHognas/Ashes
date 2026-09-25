// expect: 175

let fresh (supply: Int) = (supply, supply + 1)

let pairFresh variableId supply =
    match fresh(supply) with
        | (freshVariable, nextSupply) -> ((variableId, freshVariable), nextSupply)

let measure (n: Int) =
    match pairFresh(n)(n) with
        | ((variable, id), next) -> variable + id + next

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(measure(n) + total)

0
|> rounds(10)
|> Ashes.IO.print
