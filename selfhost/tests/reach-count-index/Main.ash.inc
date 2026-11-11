import CountQueries
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.ResultReachSummaries
import ReferenceResultReachSummaries
import Ashes.Collection.List.append
let entry name count = ParameterReachEntry(parameterName = name, reachCount = count)

let equalEntries (left: List(ParameterReachEntry)) (right: List(ParameterReachEntry)) = left == right

let state counts = ResultReachState(counts = counts, causes = [UnmodelledReach], isPoisoned = true)

let oldState counts = RefResultReachState(counts = counts, causes = [UnmodelledReach], isPoisoned = true)

let statesAgree (current: ResultReachState) (old: RefResultReachState) = equalEntries(current.counts)(old.counts) && current.causes == old.causes && current.isPoisoned == old.isPoisoned

let pairAgrees left right =
    equalEntries(indexedSum(left)(right))(oldSum(left)(right)) && equalEntries(indexedMax(left)(right))(oldMax(left)(right)) && indexedRepeat(right) == oldRepeat(right) && statesAgree(right
    |> state
    |> reachSum(state(left)))(right
    |> oldState
    |> ref_reachSum(oldState(left))) && statesAgree(right
    |> state
    |> reachJoin(state(left)))(right
    |> oldState
    |> ref_reachJoin(oldState(left)))

let recursive prependEach atom lists =
    match lists with
        | [] -> []
        | head :: rest -> (atom :: head) :: prependEach(atom)(rest)

let recursive extendAll atoms lists =
    match atoms with
        | [] -> []
        | atom :: rest ->
            lists
            |> extendAll(rest)
            |> append(prependEach(atom)(lists))

let recursive smallLists depth atoms =
    if depth == 0
    then [[]]
    else
        [] :: extendAll(atoms)(smallLists(depth - 1)(atoms))

let recursive row left rightLists =
    match rightLists with
        | [] -> true
        | right :: rest -> pairAgrees(left)(right) && row(left)(rest)

let recursive allPairs remaining all =
    match remaining with
        | [] -> true
        | left :: rest -> row(left)(all) && allPairs(rest)(all)

let recursive padding remaining =
    if remaining == 0 then [] else entry("padding/" + Ashes.Text.fromInt(remaining))(1) :: padding(remaining - 1)
let recursive largeCases entries =
    match entries with | [] -> [] | head :: rest -> append(head)(padding(256)) :: largeCases(rest)

let checks unit =
    (let atoms = [entry("")(-2), entry("")(-1), entry("")(0), entry("")(1), entry("")(2), entry("")(3), entry("a")(-2), entry("a")(-1), entry("a")(0), entry("a")(1), entry("a")(2), entry("a")(3)]
    in
        let lists = smallLists(2)(atoms)
        in
            let special = [[entry("a")(1), entry("a")(-1), entry("a")(2)], [entry("a")(3), entry("a")(-2), entry("a")(-1)], [entry("a")(-1), entry("a")(0), entry("a")(3)], [entry("a")(1), entry("a/leaf")(1), entry("树")(0)], [entry("树")(2), entry("a/leaf")(-1), entry("a")(3)]]
            in let large = largeCases(special) in allPairs(lists)(lists) && allPairs(special)(special) && row([])(special) && allPairs(large)(large))

let recursive makeEntries count prefix value =
    if count == 0
    then []
    else entry(prefix + Ashes.Text.fromInt(count))(value) :: makeEntries(count - 1)(prefix)(value)

let consume entries = Ashes.Collection.List.length(entries)

let fresh seed old size =
    (let prefix = "fresh" + Ashes.Text.fromInt(seed)
    in
        let left = makeEntries(size)(prefix)(1)
        in
            let right = makeEntries(size)(prefix + "r")(1)
            in
                if old
                then
                    consume(oldSum(left)(right)) + consume(oldMax(left)(right))
                else
                    consume(indexedSum(left)(right)) + consume(indexedMax(left)(right)))

let recursive plateau remaining old size =
    if remaining == 0
    then Ashes.IO.print("done")
    else
        if fresh(remaining)(old)(size) == size * 4
        then plateau(remaining - 1)(old)(size)
        else Ashes.IO.panic("count")

let recursive timed remaining old left right expected =
    if remaining == 0
    then Ashes.IO.print("benchmarked")
    else
        let result =
            if old
            then
                consume(oldSum(left)(right)) + consume(oldMax(left)(right))
            else
                consume(indexedSum(left)(right)) + consume(indexedMax(left)(right))
        in
            if result == expected
            then timed(remaining - 1)(old)(left)(right)(expected)
            else Ashes.IO.panic("benchmark count")

let benchmark old size overlap =
    (let left = makeEntries(size)("L")(1)
    in
        let right =
            makeEntries(size)(if overlap
            then "L"
            else "R")(1)
        in
            timed(if size < 8
            then 100000
            else 100)(old)(left)(right)(if overlap
            then size * 2
            else size * 4))

let skewBenchmark old reversed =
    (let big = makeEntries(512)("big")(1)
    in
        let small = makeEntries(2)("small")(1)
        in
            if reversed
            then timed(100)(old)(small)(big)(1028)
            else timed(100)(old)(big)(small)(1028))

match Ashes.IO.args with
    | "crossover" :: old :: size :: [] ->
        match Ashes.Text.parseInt(size) with
            | Ok(count) -> timed(5000)(old == "old")(makeEntries(count)("L")(1))(makeEntries(count)("R")(1))(count * 4)
            | Error(message) -> Ashes.IO.panic(message)
    | "old-index10" :: [] -> plateau(10)(true)(256)
    | "old-index100" :: [] -> plateau(100)(true)(256)
    | "new-index10" :: [] -> plateau(10)(false)(256)
    | "new-index100" :: [] -> plateau(100)(false)(256)
    | "old-one" :: [] -> benchmark(true)(1)(false)
    | "new-one" :: [] -> benchmark(false)(1)(false)
    | "old-skew" :: [] -> skewBenchmark(true)(false)
    | "new-skew" :: [] -> skewBenchmark(false)(false)
    | "old-reverse-skew" :: [] -> skewBenchmark(true)(true)
    | "new-reverse-skew" :: [] -> skewBenchmark(false)(true)
    | "check" :: [] ->
        if checks(Unit)
        then Ashes.IO.print("equivalent")
        else Ashes.IO.panic("count mismatch")
    | "old100" :: [] -> plateau(100)(true)(16)
    | "old1000" :: [] -> plateau(1000)(true)(16)
    | "new100" :: [] -> plateau(100)(false)(16)
    | "new1000" :: [] -> plateau(1000)(false)(16)
    | "old-small" :: [] -> benchmark(true)(2)(false)
    | "new-small" :: [] -> benchmark(false)(2)(false)
    | "old-large" :: [] -> benchmark(true)(512)(false)
    | "new-large" :: [] -> benchmark(false)(512)(false)
    | "old-overlap" :: [] -> benchmark(true)(512)(true)
    | "new-overlap" :: [] -> benchmark(false)(512)(true)
    | _ -> Ashes.IO.panic("mode")
