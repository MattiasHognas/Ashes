import Ashes.Collection.Map
import Ashes.Text
type Ownership =
    | Borrowed
    | Consumed

let lookup (name: Str) (index: MapTree(Str, Maybe(List((Str, Ownership))))) =
    match Ashes.Collection.Map.getStr(name)(index) with
        | Some(value) -> value
        | None -> None

let handoff (index: MapTree(Str, Maybe(List((Str, Ownership))))) (shadowed: List(Str)) (name: Str) (count: Int) =
    if Ashes.Collection.List.length(shadowed) != 0
    then None
    else
        match lookup(name)(index) with
            | Some(values) ->
                if Ashes.Collection.List.length(values) == count
                then Some(values)
                else None
            | None -> None

let sharedCase (seed: Int) =
    (let ownership =
        if seed % 2 == 0
        then Borrowed
        else Consumed
    in
        let index = Ashes.Collection.Map.setStr("key")(Some([("payload", ownership)]))(Ashes.Collection.Map.empty)
        in
            match handoff(index)([])("key")(1) with
                | Some((value, _) :: []) ->
                    match lookup("key")(index) with
                        | Some((original, _) :: []) ->
                            if value == "payload" && original == "payload"
                            then 1
                            else Ashes.IO.panic("shared payload changed")
                        | _ -> Ashes.IO.panic("original payload lost")
                | _ -> Ashes.IO.panic("shared payload lost"))

let recursive build (entries: List((Str, List((Str, Ownership))))) (index: MapTree(Str, Maybe(List((Str, Ownership))))) =
    match entries with
        | [] -> index
        | (name, values) :: rest ->
            let entry =
                match Ashes.Collection.Map.getStr(name)(index) with
                    | None -> Some(values)
                    | Some(_previous) -> None
            in
                index
                |> Ashes.Collection.Map.setStr(name)(entry)
                |> build(rest)

let binderCase (seed: Int) =
    if Ashes.Collection.Map.size(build([("first", [("h", if seed % 2 == 0
    then Borrowed
    else Consumed)]), ("second", [("h", Consumed)]), ("third", [("h", Borrowed)])])(Ashes.Collection.Map.empty)) == 3
    then 1
    else Ashes.IO.panic("wrong index size")

let curryCase (seed: Int) =
    (let source =
        "prefix" + (if seed % 2 == 0
        then "one"
        else "two")
    in
        let index = Ashes.Collection.Map.setStr(source)(seed)(Ashes.Collection.Map.empty)
        in
            if seed % 2 == 0
            then
                if Ashes.Text.length(source) + Ashes.Collection.Map.size(index) == 10
                then 1
                else Ashes.IO.panic("capture changed")
            else
                if Ashes.Collection.Map.size(index) + Ashes.Text.length(source) == 10
                then 1
                else Ashes.IO.panic("capture changed"))

let recursive repeat (remaining: Int) (kind: Int) =
    if remaining == 0
    then Ashes.IO.print("passed")
    else
        let result =
            if kind == 0
            then sharedCase(remaining)
            else
                if kind == 1
                then binderCase(remaining)
                else curryCase(remaining)
        in
            if result == 1
            then repeat(remaining - 1)(kind)
            else Ashes.IO.panic("wrong result")

match Ashes.IO.args with
    | "shared100" :: [] -> repeat(100)(0)
    | "shared1000" :: [] -> repeat(1000)(0)
    | "binder100" :: [] -> repeat(100)(1)
    | "binder1000" :: [] -> repeat(1000)(1)
    | "curry100" :: [] -> repeat(100)(2)
    | "curry1000" :: [] -> repeat(1000)(2)
    | _ -> Ashes.IO.panic("select a case and round count")
