// expect: 130
type Shape =
    | PassShape
    | GrownShape

let shapeOf (argument: Int) =
    if argument > 1
    then GrownShape
    else PassShape

let recursive observe (arguments: List(Int)) (observed: List(Maybe(Shape))) =
    match (arguments, observed) with
        | (argument :: restArguments, current :: restObserved) ->
            Some(shapeOf(argument)) :: observe(restArguments)(restObserved)
        | _ -> observed

let recursive observeNested (arguments: List(Int)) (observed: List(Maybe(Shape))) =
    match arguments with
        | argument :: restArguments ->
            match observed with
                | current :: restObserved ->
                    Some(shapeOf(argument)) :: observeNested(restArguments)(restObserved)
                | [] -> observed
        | [] -> observed

let recursive initial (count: Int) =
    if count == 0
    then []
    else None :: initial(count - 1)

let recursive countSome (observed: List(Maybe(Shape))) =
    match observed with
        | [] -> 0
        | Some(GrownShape) :: rest -> 1 + countSome(rest)
        | _ :: rest -> countSome(rest)

let run (seed: Int) =
    countSome(3
    |> initial
    |> observe([seed, 2, 3])) + (match 3
    |> initial
    |> observeNested([seed, 2, 3]) with
        | [] -> 0
        | _ :: rest -> countSome(rest))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(n % 3))

0
|> rounds(30)
|> Ashes.IO.print
