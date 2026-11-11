// expect: passed
import Ashes.Collection.Map as Map
import Ashes.Collection.Map.MapTree
import Ashes.Collection.List.length
type Ownership =
    | Borrowed
    | Consumed
    deriving {Eq}

let lookup (name: Str) (index: MapTree(Str, Maybe(List((Str, Ownership))))) =
    match Map.getStr(name)(index) with
        | Some(value) -> value
        | None -> None

let recursive handoff (index: MapTree(Str, Maybe(List((Str, Ownership))))) (shadowed: List(Str)) (name: Str) (count: Int) =
    if length(shadowed) != 0
    then None
    else
        match if name == "fresh"
        then Some([("payload", Borrowed)])
        else lookup(name)(index) with
            | Some(values) ->
                if length(values) != 1
                then None
                else
                    if count == 1
                    then Some(values)
                    else
                        handoff(index)(shadowed)(if name == "fresh"
                        then "key"
                        else "fresh")(count - 1)
            | None -> None

let recursive run remaining index =
    if remaining == 0
    then Ashes.IO.print("passed")
    else
        match if remaining % 2 == 0
        then Some([("payload", Borrowed)])
        else handoff(index)([])("key")(3) with
            | Some((value, _) :: []) ->
                if value == "payload" && Map.getStr("key")(index) == Some(Some([("payload", Borrowed)]))
                then run(remaining - 1)(index)
                else Ashes.IO.panic("changed payload")
            | _ -> Ashes.IO.panic("missing payload")

Map.empty
|> Map.setStr("key")(Some([("payload", Borrowed)]))
|> run(20)
