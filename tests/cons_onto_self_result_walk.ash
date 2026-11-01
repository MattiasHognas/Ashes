// expect: 11
type Shape =
    | PassShape
    | GrownShape
    | OtherShape
    deriving {Eq}

type Walk =
    | observed: List(Maybe(Shape))
    | count: Int

let merge (observed: Maybe(Shape)) (local: Shape) =
    match observed with
        | None -> Some(local)
        | Some(previous) ->
            if previous == local
            then Some(previous)
            else Some(OtherShape)

let shapeOf (argument: Int) =
    if argument > 1
    then GrownShape
    else PassShape

let recursive observe (arguments: List(Int)) (observed: List(Maybe(Shape))) =
    match (arguments, observed) with
        | (argument :: restArguments, current :: restObserved) ->
            merge(current)(shapeOf(argument)) :: observe(restArguments)(restObserved)
        | _ -> observed

let recursive initial (count: Int) =
    if count == 0
    then []
    else None :: initial(count - 1)

let recursive walkCalls (calls: List(List(Int))) (walk: Walk) =
    match calls with
        | [] -> walk
        | arguments :: rest -> walkCalls(rest)(Walk(observed = observe(arguments)(walk.observed), count = walk.count + 1))

let recursive countSome (observed: List(Maybe(Shape))) =
    match observed with
        | [] -> 0
        | Some(OtherShape) :: rest -> 1 + countSome(rest)
        | _ :: rest -> countSome(rest)

let run (seed: Int) =
    match walkCalls([[seed, 2, 3], [1, seed, 0], [5, 5, seed]])(Walk(observed = initial(3), count = 0)) with
        | Walk { observed = observed, count = count } -> countSome(observed) + count

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(n))

0
|> rounds(2)
|> Ashes.IO.print
