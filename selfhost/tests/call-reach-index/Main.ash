import AshesCompiler.Semantics.CallOwnership
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.ResultReachSummaries.ResultReachState
let entry (name: Str) (count: Int) = ParameterReachEntry(parameterName = name, reachCount = count)

let recursive wholeCount (name: Str) (entries: List(ParameterReachEntry)) =
    match entries with
        | [] -> 0
        | ParameterReachEntry { parameterName = candidate, reachCount = count } :: rest ->
            (if candidate == name
            then count
            else 0) + wholeCount(name)(rest)

let recursive hasUnenumerated (causes: List(ResultReachCause)) =
    match causes with
        | [] -> false
        | cause :: rest -> cause == UnenumeratedInputs || hasUnenumerated(rest)

let recursive referenceFlags (parameters: List(Str)) (entries: List(ParameterReachEntry)) =
    match parameters with
        | [] -> []
        | parameter :: rest -> (reachCountOf(parameter)(entries) > 0) :: referenceFlags(rest)(entries)

let recursive compareArguments (index: Int) (parameters: List(Str)) (entries: List(ParameterReachEntry)) (causes: List(ResultReachCause)) (facts: Maybe(CoreCalleeFacts)) =
    match parameters with
        | [] -> !calleeResultReachesArgument(facts)(index) && !calleeResultReachesArgumentWhole(facts)(index) && !calleeResultCannotKeepArgumentWhole(facts)(index)
        | parameter :: rest ->
            let expectedAny = reachCountOf(parameter)(entries) > 0
            in
                let expectedWhole = wholeCount(parameter)(entries) > 0
                in
                    let expectedCannotKeep = !hasUnenumerated(causes) && wholeCount(parameter)(entries) == 0 && wholeCount("~" + parameter)(entries) == 0
                    in calleeResultReachesArgument(facts)(index) == expectedAny && calleeResultReachesArgumentWhole(facts)(index) == expectedWhole && calleeResultCannotKeepArgumentWhole(facts)(index) == expectedCannotKeep && compareArguments(index + 1)(rest)(entries)(causes)(facts)

let compare (parameters: List(Str)) (entries: List(ParameterReachEntry)) (causes: List(ResultReachCause)) (poisoned: Bool) =
    (let reach = ResultReachState(counts = entries, causes = causes, isPoisoned = poisoned)
    in
        let prepared = prepareCallReach(parameters)(reach)
        in
            let facts = Some(CoreCalleeFacts(label = None, parameters = parameters, ownership = [], reach = reach, preparedReach = prepared, rcEligible = false, argumentCount = 0))
            in preparedCallReachFlags(prepared) == referenceFlags(parameters)(entries) && !calleeResultReachesArgument(facts)(-1) && !calleeResultReachesArgumentWhole(facts)(-1) && !calleeResultCannotKeepArgumentWhole(facts)(-1) && compareArguments(0)(parameters)(entries)(causes)(facts))

let parameters = ["p", "p/", "pa", "", "p", "~p"]

let entries = [entry("p")(1), entry("p")(-1), entry("p/0")(2), entry("p/0")(-2), entry("pa")(2), entry("~p")(1), entry("p")(0), entry("")(1), entry("/")(1), entry("p/")(1), entry("p//")(1), entry("~p/0")(1)]

let checkEntries selected = compare(parameters)(selected)([])(false) && compare(parameters)(selected)([UnenumeratedInputs])(true) && compare(parameters)(selected)([InternalSharing])(true) && compare(parameters)(selected)([UnenumeratedInputs, UnenumeratedInputs])(false)

let recursive selections depth selected =
    if depth == 0
    then checkEntries(selected)
    else choices(depth)(selected)(entries)
and choices depth selected remaining =
    match remaining with
        | [] -> true
        | head :: rest -> selections(depth - 1)(head :: selected) && choices(depth)(selected)(rest)

let fresh seed =
    (let name = "räv" + Ashes.Text.fromInt(seed)
    in compare([name, name + "/", "other"])([entry(name)(1), entry(name + "/0")(2), entry("~" + name)(1)])([])(false))

let recursive run count =
    if count == 0
    then Ashes.IO.print("call reach index ok")
    else
        if fresh(count)
        then run(count - 1)
        else Ashes.IO.panic("fresh call reach mismatch")

match Ashes.IO.args with
    | "check" :: [] ->
        if selections(3)([]) && compare([])(entries)([])(false) && !calleeResultReachesArgument(None)(0) && !calleeResultReachesArgumentWhole(None)(0) && !calleeResultCannotKeepArgumentWhole(None)(0)
        then Ashes.IO.print("6912 call reach comparisons ok")
        else Ashes.IO.panic("call reach mismatch")
    | "1000" :: [] -> run(1000)
    | "10000" :: [] -> run(10000)
    | _ -> Ashes.IO.panic("mode required")
