// expect: 6
import Ashes.Collection.Map.MapTree
type Payload =
    | Named(Int, Str, List(Payload))
    | Variable(Int)
    | Scalar

let heapResourceBucket (symbolId: Int) (name: Str) (index: MapTree(Int, MapTree(Str, List(Payload)))) =
    match Ashes.Collection.Map.getInt(symbolId)(index) with
        | None -> []
        | Some(byName) ->
            match Ashes.Collection.Map.getStr(name)(byName) with
                | None -> []
                | Some(types) -> types

let recursive heapIndexResourceFree (roots: List(Payload)) (index: MapTree(Int, MapTree(Str, List(Payload)))) =
    match roots with
        | [] -> index
        | root :: rest ->
            match root with
                | Named(symbolId, name, _arguments) ->
                    let byName =
                        match Ashes.Collection.Map.getInt(symbolId)(index) with
                            | None -> Ashes.Collection.Map.empty
                            | Some(existing) -> existing
                    in
                        let bucket = heapResourceBucket(symbolId)(name)(index)
                        in
                            index
                            |> Ashes.Collection.Map.setInt(symbolId)(Ashes.Collection.Map.setStr(name)(root :: bucket)(byName))
                            |> heapIndexResourceFree(rest)
                | _ -> heapIndexResourceFree(rest)(index)

let identityMap index = index

let recursive mixedExit remaining choose index =
    if remaining == 0
    then
        if choose
        then index
        else identityMap(index)
    else
        index
        |> heapIndexResourceFree([Named(remaining)("A")([Scalar])])
        |> mixedExit(remaining - 1)(choose)

let recursive shadowAccumulator remaining index =
    if remaining == 0
    then
        let index = heapIndexResourceFree([Named(99)("A")([Scalar])])(Ashes.Collection.Map.empty)
        in index
    else
        index
        |> heapIndexResourceFree([Named(remaining)("A")([Scalar])])
        |> shadowAccumulator(remaining - 1)

let recursive shadowSelf remaining index =
    if remaining == 0
    then
        let shadowSelf = identityMap
        in shadowSelf(index)
    else
        index
        |> heapIndexResourceFree([Named(remaining)("A")([Scalar])])
        |> shadowSelf(remaining - 1)

let recursive constrained remaining witness index =
    if remaining == 0
    then index
    else
        if witness == witness
        then
            index
            |> heapIndexResourceFree([Named(remaining)("A")([Scalar])])
            |> constrained(remaining - 1)(witness)
        else
            index
            |> heapIndexResourceFree([Named(remaining)("A")([Variable(0)])])
            |> constrained(remaining - 1)(witness)

let recursive ordinaryRecursive remaining index =
    if remaining == 0
    then index
    else
        index
        |> heapIndexResourceFree([Named(remaining)("A")([Scalar])])
        |> ordinaryRecursive(remaining - 1)
        |> identityMap

let measure id index =
    index
    |> heapResourceBucket(id)("A")
    |> Ashes.Collection.List.length

measure(1)(mixedExit(3)(true)(Ashes.Collection.Map.empty)) + measure(1)(mixedExit(3)(false)(Ashes.Collection.Map.empty)) + measure(99)(shadowAccumulator(3)(Ashes.Collection.Map.empty)) + measure(1)(shadowSelf(3)(Ashes.Collection.Map.empty)) + measure(1)(ordinaryRecursive(3)(Ashes.Collection.Map.empty)) + measure(1)(constrained(3)("evidence")(Ashes.Collection.Map.empty))
|> Ashes.Text.fromInt
|> Ashes.IO.print
