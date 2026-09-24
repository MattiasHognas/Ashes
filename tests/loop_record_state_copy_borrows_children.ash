// A loop threading a record state through record updates and a helper call: the back edge's copy of
// the record borrows its children, which the state it replaces still owns.
// expect: 129
import Ashes.IO as io
type Walk =
    | candidates: List(Int)
    | sawSelfCall: Bool

let recursive removeOrdinal (ordinal: Int) (items: List(Int)) =
    match items with
        | [] -> []
        | item :: rest ->
            if item == ordinal
            then removeOrdinal(ordinal)(rest)
            else item :: removeOrdinal(ordinal)(rest)

let recursive disqualifyNames (only: Maybe(Int)) (walk: Walk) (names: List(Int)) =
    match names with
        | [] -> walk
        | name :: rest ->
            match only with
                | Some(ordinal) ->
                    if ordinal == name
                    then disqualifyNames(only)((walk with candidates = removeOrdinal(name)(walk.candidates)))(rest)
                    else disqualifyNames(only)(walk)(rest)
                | None -> disqualifyNames(only)((walk with candidates = removeOrdinal(name)(walk.candidates)))(rest)

let disqualify (argument: Int) (only: Maybe(Int)) (walk: Walk) = [argument % 5, (argument + 1) % 5] |> disqualifyNames(only)(walk)

let recursive walkArgument (argument: Int) (index: Int) (candidates: List(Int)) (walk: Walk) =
    match candidates with
        | [] -> walk
        | candidate :: rest ->
            if index == candidate && argument % 2 == 0
            then walkArgument(argument)(index)(rest)(walk)
            else
                walk
                |> disqualify(argument)(Some(candidate))
                |> walkArgument(argument)(index)(rest)

let recursive walkArguments (arguments: List(Int)) (index: Int) (walk: Walk) =
    match arguments with
        | [] -> walk
        | argument :: rest ->
            walk
            |> walkArgument(argument)(index)(walk.candidates)
            |> walkArguments(rest)(index + 1)

let recursive range (index: Int) (count: Int) (acc: List(Int)) =
    if index >= count
    then acc
    else range(index + 1)(count)(index :: acc)

let recursive rounds (round: Int) (total: Int) =
    if round >= 300
    then total
    else
        match walkArguments(range(0)(round % 7 + 2)([]))(0)(Walk(candidates = range(0)(5)([]), sawSelfCall = false)) with
            | Walk { candidates = candidates } -> rounds(round + 1)(total + Ashes.Collection.List.length(candidates))

0
|> rounds(0)
|> io.print
