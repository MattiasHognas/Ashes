// expect: 4
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

let bucketLength id name index =
    index
    |> heapResourceBucket(id)(name)
    |> Ashes.Collection.List.length

let recursive viaUnknown updater roots index =
    match roots with
        | [] -> index
        | root :: rest ->
            index
            |> updater(root)
            |> viaUnknown(updater)(rest)

let addOne root index = heapIndexResourceFree([root])(index)

let recursive withInvariant roots index preserved =
    match roots with
        | [] -> (index, preserved)
        | root :: rest ->
            withInvariant(rest)(addOne(root)(index))(preserved)

let recursive snapshots roots index =
    match roots with
        | [] -> [index]
        | root :: rest ->
            index :: snapshots(rest)(addOne(root)(index))

let assertInt label expected actual =
    if expected == actual
    then 0
    else Ashes.IO.panic(label)

let query n =
    (let first = Named(40)("A")([Variable(100000 + n)])
    in
        let original = heapIndexResourceFree([first])(Ashes.Collection.Map.empty)
        in
            let replacements = [Named(40)("A")([Scalar]), Named(40)("B")([Scalar]), Named(41)("B")([Scalar]), Named(42)("C")([Scalar]), Named(43)("D")([Scalar]), Named(39)("Z")([Scalar]), Named(38)("Y")([Scalar]), Named(37)("X")([Scalar])]
            in
                let updated = heapIndexResourceFree(replacements)(original)
                in
                    let unknown = viaUnknown(addOne)(replacements)(original)
                    in
                        let (invariantUpdated, preserved) = withInvariant(replacements)(original)(original)
                        in
                            let versions = snapshots(replacements)(original)
                            in
                                assertInt("old version")(1)(bucketLength(40)("A")(original)) + assertInt("replacement")(2)(bucketLength(40)("A")(updated)) + assertInt("nested insert")(1)(bucketLength(40)("B")(updated)) + assertInt("right rotation")(1)(bucketLength(37)("X")(updated)) + assertInt("left rotation")(1)(bucketLength(43)("D")(updated)) + assertInt("unknown callee")(2)(bucketLength(40)("A")(unknown)) + assertInt("invariant old")(1)(bucketLength(40)("A")(preserved)) + assertInt("invariant new")(2)(bucketLength(40)("A")(invariantUpdated)) + assertInt("TMC snapshots")(9)(Ashes.Collection.List.length(versions)) + (match versions with
                                    | head :: _ ->
                                        head
                                        |> bucketLength(40)("A")
                                        |> assertInt("escaped old snapshot")(1)
                                    | [] -> Ashes.IO.panic("missing snapshots")) + bucketLength(40)("A")(updated))

let recursive repeat remaining sum =
    if remaining == 0
    then
        sum
        |> Ashes.Text.fromInt
        |> Ashes.IO.print
    else repeat(remaining - 1)(sum + query(remaining))

repeat(2)(0)
