let makeState table names indexed = CoreLoweringState(oldTable = table, ownershipIndex = if indexed then indexProgramParameterOwnership(table) else Ashes.Collection.Map.empty, names = names)
let agrees (name: Str) (parameters: List(Str)) (ownership: List((Str, ParameterOwnership))) (state: CoreLoweringState) =
    lookupProgramParameterOwnership(name)(state.oldTable) == lookupIndexedProgramParameterOwnership(name)(state.ownershipIndex) && provenParameterOwnership(name)(parameters)(ownership)(state) == oldProvenParameterOwnership(name)(parameters)(ownership)(state)

let check (_unit: Int) =
    let table = [("module.helper", [("x", Borrowed), ("y", Consumed)]), ("module.helper", [("x", Consumed)]), ("helper", [("x", Borrowed)]), ("empty", []), ("empty", [("x", Borrowed)]), ("树/å", [("资源", Borrowed)])]
    in
        let names = Ashes.Collection.Map.empty |> Ashes.Collection.Map.setStr("module.helper")(true) |> Ashes.Collection.Map.setStr("empty")(true) |> Ashes.Collection.Map.setStr("树/å")(true)
        in
            let state = makeState(table)(names)(true)
            in agrees("module.helper")(["x", "y"])([("x", Consumed), ("y", Consumed)])(state) && agrees("module.helper")(["other", "y"])([("other", Consumed), ("y", Consumed)])(state) && agrees("module.helper")(["x"])([("x", Consumed)])(state) && agrees("helper")(["x"])([("x", Consumed)])(state) && agrees("missing")([])([])(state) && agrees("empty")([])([])(state) && agrees("树/å")(["资源"])([("资源", Consumed)])(state) && provenParameterOwnership("module.helper")(["x", "y"])([("x", Consumed), ("y", Consumed)])(state) == [("x", Borrowed), ("y", Consumed)] && lookupIndexedProgramParameterOwnership("empty")(state.ownershipIndex) == Some([])

let recursive makeTable count prefix =
    if count <= 0 then []
    else (prefix + Ashes.Text.fromInt(count), [("p" + Ashes.Text.fromInt(count), Borrowed)]) :: makeTable(count - 1)(prefix)

let freshQuery (seed: Int) (indexed: Bool) =
    let prefix = "module" + Ashes.Text.fromInt(seed) + "."
    in
        let table = makeTable(16)(prefix)
        in
            let name = prefix + "1"
            in
                let names = Ashes.Collection.Map.setStr(name)(true)(Ashes.Collection.Map.empty)
                in
                    let state = makeState(table)(names)(indexed)
                    in (if indexed then provenParameterOwnership(name)(["p1"])([("p1", Consumed)])(state) else oldProvenParameterOwnership(name)(["p1"])([("p1", Consumed)])(state)) == [("p1", Borrowed)]

let recursive fresh count indexed =
    if count <= 0 then Ashes.IO.print("done")
    else if freshQuery(count)(indexed) then fresh(count - 1)(indexed) else Ashes.IO.panic("fresh query mismatch")

let recursive timed count indexed state =
    if count <= 0 then Ashes.IO.print("benchmarked")
    else
        let found = if indexed then provenParameterOwnership("module.1")(["p1"])([("p1", Consumed)])(state) else oldProvenParameterOwnership("module.1")(["p1"])([("p1", Consumed)])(state)
        in if found == [("p1", Borrowed)] then timed(count - 1)(indexed)(state) else Ashes.IO.panic("benchmark mismatch")

let benchmark indexed =
    let state = makeState(makeTable(2048)("module."))(Ashes.Collection.Map.setStr("module.1")(true)(Ashes.Collection.Map.empty))(indexed)
    in timed(10000)(indexed)(state)

match Ashes.IO.args with
    | "check" :: [] -> if check(0) then Ashes.IO.print("equivalent") else Ashes.IO.panic("ownership mismatch")
    | "old100" :: [] -> fresh(100)(false)
    | "old1000" :: [] -> fresh(1000)(false)
    | "new100" :: [] -> fresh(100)(true)
    | "new1000" :: [] -> fresh(1000)(true)
    | "old-bench" :: [] -> benchmark(false)
    | "new-bench" :: [] -> benchmark(true)
    | _ -> Ashes.IO.panic("mode")
