// expect: 4250

type Sem =
    | SemVariable(Int)
    | SemName(Str)

type Supply =
    | nextId: Int

let fresh (supply: Supply) =
    match supply with
        | Supply { nextId = nextId } -> (SemVariable(nextId), Supply(nextId = nextId + 1))

let step variableId supply substitution =
    match fresh(supply) with
        | (freshVariable, nextSupply) -> ((variableId, freshVariable) :: substitution, nextSupply)

let recursive instantiateQuantifiers (quantified: List(Int)) (supply: Supply) (substitution: List((Int, Sem))) =
    match quantified with
        | [] -> (substitution, supply)
        | variableId :: tail ->
            match step(variableId)(supply)(substitution) with
                | (nextSubstitution, nextSupply) -> instantiateQuantifiers(tail)(nextSupply)(nextSubstitution)

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
