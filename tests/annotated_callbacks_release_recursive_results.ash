// expect: 6000

type Value =
    | Variable(Int)
    | Number(Int)
    | Nested(Maybe(Value))

let recursive lookupValue : Int -> List((Int, Value)) -> Maybe(Value) =
    given (key) ->
        given (values) ->
            match values with
                | [] -> None
                | (candidate, value) :: rest ->
                    if key == candidate
                    then Some(value)
                    else lookupValue(key)(rest)

let ordinary : (Int -> Maybe(Value)) -> Int -> Bool =
    given (lookup) ->
        given (key) ->
            match lookup(key) with
                | None -> false
                | Some(_) -> true

let recursive recursiveCall : (Int -> Maybe(Value)) -> Int -> Bool =
    given (lookup) ->
        given (key) ->
            if key < 0
            then recursiveCall(lookup)(-key)
            else
                match lookup(key) with
                    | None -> false
                    | Some(_) -> true

let recursive first : (Int -> Maybe(Value)) -> Int -> Bool =
    given (lookup) ->
        given (key) ->
            if key < 0
            then second(lookup)(-key)
            else
                match lookup(key) with
                    | None -> false
                    | Some(_) -> true
and second : (Int -> Maybe(Value)) -> Int -> Bool =
    given (lookup) ->
        given (key) ->
            if key < 0
            then first(lookup)(-key)
            else
                match lookup(key) with
                    | None -> false
                    | Some(_) -> true

let fresh round =
    (let values = [(7, Nested(Some(Number(round))))]
    in
        let lookup =
            given (key) -> lookupValue(key)(values)
        in
            let nested : (Int -> Maybe(Value)) -> Int -> Bool =
                given (callback) ->
                    given (key) ->
                        match callback(key) with
                            | None -> false
                            | Some(_) -> true
            in
                let recursive nestedRecursive : (Int -> Maybe(Value)) -> Int -> Bool =
                    given (callback) ->
                        given (key) ->
                            if key < 0
                            then nestedRecursive(callback)(-key)
                            else
                                match callback(key) with
                                    | None -> false
                                    | Some(_) -> true
                in
                    if ordinary(lookup)(7) && !ordinary(lookup)(8) && recursiveCall(lookup)(-7) && !recursiveCall(lookup)(8) && first(lookup)(-7) && !first(lookup)(8) && second(lookup)(-7) && !second(lookup)(8) && nested(lookup)(7) && !nested(lookup)(8) && nestedRecursive(lookup)(-7) && !nestedRecursive(lookup)(8)
                    then 6
                    else Ashes.IO.panic("callback result changed"))

let recursive repeat remaining total =
    if remaining == 0
    then
        total
        |> Ashes.Text.fromInt
        |> Ashes.IO.print
    else repeat(remaining - 1)(total + fresh(remaining))

match Ashes.IO.args with
    | "10000" :: [] -> repeat(10000)(0)
    | _ -> repeat(1000)(0)
