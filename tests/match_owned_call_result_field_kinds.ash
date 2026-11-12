// expect: passed
import Ashes.Collection.Map as Map
import Ashes.Collection.Map.MapTree
import Ashes.Collection.List.length
type Ownership =
    | Borrowed
    | Consumed

type Payload =
    | Payload(List((Str, Ownership)), Int, List((Str, Ownership)))

let fresh (value: Str) = Payload([(value, Borrowed)])(7)([("wildcard", Consumed)])

let existing (key: Str) (index: MapTree(Str, Payload)) =
    match Map.getStr(key)(index) with
        | Some(value) -> value
        | None -> fresh("missing")

let inspect key index =
    match existing(key)(index) with
        | Payload(values, number, _) -> length(values) + number

let inspectFresh value =
    match fresh(value) with
        | Payload(values, number, _) -> length(values) + number

let choose (freshResult: Bool) (index: MapTree(Str, Payload)) =
    if freshResult
    then fresh("fresh")
    else existing("key")(index)

let escape freshResult index =
    match choose(freshResult)(index) with
        | Payload(values, _, _) -> (values, values)

let inspectEscaped freshResult index =
    match escape(freshResult)(index) with
        | (first, second) -> length(first) + length(second)

let recursive run remaining index =
    if remaining == 0
    then Ashes.IO.print("passed")
    else
        match choose(remaining % 2 == 0)(index) with
            | Payload(values, number, _) ->
                if length(values) + number == 8 && inspect("key")(index) == 8 && inspectFresh("fresh") == 8 && inspectEscaped(remaining % 2 == 0)(index) == 2
                then run(remaining - 1)(index)
                else Ashes.IO.panic("wrong fields")

Map.empty
|> Map.setStr("key")(fresh("shared"))
|> run(20)
