import PairQueries
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.ResultReachSummaries
import ReferenceResultReachSummaries
import Ashes.Collection.List.append
let entry (name: Str) (count: Int) = ParameterReachEntry(parameterName = name, reachCount = count)

let sumAgrees (left: List(ParameterReachEntry)) (right: List(ParameterReachEntry)) =
    (let fresh = reachSum(ResultReachState(counts = left, causes = [], isPoisoned = false))(ResultReachState(counts = right, causes = [UnmodelledReach], isPoisoned = true))
    in
        let old = ref_reachSum(RefResultReachState(counts = left, causes = [], isPoisoned = false))(RefResultReachState(counts = right, causes = [UnmodelledReach], isPoisoned = true))
        in fresh.counts == old.counts && fresh.causes == old.causes && fresh.isPoisoned == old.isPoisoned)

let pairAgrees (left: Str) (right: Str) =
    (let a = [entry(left)(1)]
    in
        let b = [entry(right)(1)]
        in
            let both = [entry(left)(0), entry(right)(1), entry(left)(2)]
            in oldPairs(a)(b) == indexedPairs(a)(b) && oldPairs(b)(a) == indexedPairs(b)(a) && oldPairs(both)(both) == indexedPairs(both)(both) && sumAgrees(a)(b) && sumAgrees(both)(a))

let recursive prependNames (prefix: Str) (names: List(Str)) =
    match names with
        | [] -> []
        | head :: rest -> prefix + head :: prependNames(prefix)(rest)

let recursive shortNames (depth: Int) =
    if depth == 0
    then [""]
    else
        let previous = shortNames(depth - 1)
        in
            "" :: append(prependNames("a")(previous))(previous
            |> prependNames("*")
            |> append(prependNames("/")(previous)))

let recursive compareRow (name: Str) (names: List(Str)) =
    match names with
        | [] -> true
        | other :: rest -> pairAgrees(name)(other) && compareRow(name)(rest)

let recursive compareAll (remaining: List(Str)) (all: List(Str)) =
    match remaining with
        | [] -> true
        | name :: rest -> compareRow(name)(all) && compareAll(rest)(all)

let summariesAgree (body: Expr) =
    reachFactsOf(singleFunctionReach(["x", "y"])(body)) == ref_reachFactsOf(ref_singleFunctionReach(["x", "y"])(body))

let realSummaries unit =
    summariesAgree(ExprVar("x")) && summariesAgree(ExprTuple([ExprVar("x"), ExprVar("x")])) && summariesAgree(ExprTuple([ExprVar("x"), ExprVar("y")])) && summariesAgree(ExprMatch(ExprVar("x"))([(PatternCons(PatternVar("head"))(PatternVar("tail")), ExprTuple([ExprVar("x"), ExprVar("head")]), None), (PatternEmptyList, ExprVar("y"), None)])(None))

let check unit =
    (let sliced =
        Ashes.Byte.subText(Ashes.Byte.fromText("é"))(1)(1)
    in
        let names =
            ["å", "å/1", "å/", "树", "树/*", "a/", "a//", "/x", "p/10", "p/1", "p/1/2"]
            |> append(["a/" + sliced, sliced, sliced + "/x"])
            |> append(shortNames(3))
        in compareAll(names)(names) && !indexedPairs([entry("a")(1)])([entry("a/")(1)]) && indexedPairs([entry("a")(1)])([entry("a//")(1)]) && realSummaries(Unit))

let recursive makeNames (remaining: Int) (prefix: Str) =
    if remaining == 0
    then []
    else entry(prefix + Ashes.Text.fromInt(remaining) + "/leaf")(1) :: makeNames(remaining - 1)(prefix)

let freshQuery (seed: Int) (old: Bool) =
    (let names = makeNames(16)("fresh" + Ashes.Text.fromInt(seed) + "/")
    in
        if old
        then oldPairs(names)(names)
        else indexedPairs(names)(names))

let recursive plateau (remaining: Int) (old: Bool) =
    if remaining == 0
    then Ashes.IO.print("done")
    else
        if freshQuery(remaining)(old)
        then Ashes.IO.panic("unexpected ancestor")
        else plateau(remaining - 1)(old)

let recursive timed (remaining: Int) (old: Bool) (entries: List(ParameterReachEntry)) (expected: Bool) =
    if remaining == 0
    then Ashes.IO.print("benchmarked")
    else
        let answer =
            if old
            then oldPairs(entries)(entries)
            else indexedPairs(entries)(entries)
        in
            if answer == expected
            then timed(remaining - 1)(old)(entries)(expected)
            else Ashes.IO.panic("benchmark mismatch")

let benchmark (old: Bool) (early: Bool) =
    (let base = makeNames(512)("p")
    in
        let entries =
            if early
            then entry("root")(1) :: entry("root/child")(1) :: base
            else base
        in timed(100)(old)(entries)(early))

match Ashes.IO.args with
    | "check" :: [] ->
        if check(Unit)
        then Ashes.IO.print("equivalent")
        else Ashes.IO.panic("reach mismatch")
    | "old100" :: [] -> plateau(100)(true)
    | "old1000" :: [] -> plateau(1000)(true)
    | "new100" :: [] -> plateau(100)(false)
    | "new1000" :: [] -> plateau(1000)(false)
    | "old-sparse" :: [] -> benchmark(true)(false)
    | "new-sparse" :: [] -> benchmark(false)(false)
    | "old-early" :: [] -> benchmark(true)(true)
    | "new-early" :: [] -> benchmark(false)(true)
    | _ -> Ashes.IO.panic("mode")
