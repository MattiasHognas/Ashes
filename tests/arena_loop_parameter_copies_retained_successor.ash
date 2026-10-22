// expect: 4250

type Sem =
    | SemVariable(Int)
    | SemName(Str)

type Supply =
    | nextId: Int

let fresh (supply: Supply) =
    match supply with
        | Supply { nextId = nextId } -> (SemVariable(nextId), Supply(nextId = nextId + 1))

let recursive instantiateQuantifiers quantified supply substitution =
    match quantified with
        | [] -> (substitution, supply)
        | variableId :: tail ->
            match fresh(supply) with
                | (freshVariable, nextSupply) -> instantiateQuantifiers(tail)(nextSupply)((variableId, freshVariable) :: substitution)

let recursive count (items: List((Int, Sem))) (total: Int) =
    match items with
        | [] -> total
        | _ :: rest -> count(rest)(total + 1)

let quantified = [1, 2, 3, 4, 5, 6, 7, 8]

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match instantiateQuantifiers(quantified)(Supply(nextId = n))([]) with
            | (substitution, Supply { nextId = next }) -> rounds(n - 1)(total + count(substitution)(0) + next % 2)

0
|> rounds(500)
|> Ashes.IO.print
