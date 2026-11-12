let recursive sameTestExpr (left: Expr) (right: Expr) =
    match (left, right) with
        | (ExprInt(a), ExprInt(b)) -> a == b
        | (ExprString(a), ExprString(b)) -> a == b
        | (ExprLambda(a, first, None), ExprLambda(b, second, None)) -> a == b && sameTestExpr(first)(second)
        | _ -> false

let sameCandidate (left: Maybe((List(Str), Expr))) (right: Maybe((List(Str), Expr))) =
    match (left, right) with
        | (None, None) -> true
        | (Some((firstParameters, first)), Some((secondParameters, second))) -> firstParameters == secondParameters && sameTestExpr(first)(second)
        | _ -> false

let sameValue (left: Maybe(Expr)) (right: Maybe(Expr)) =
    match (left, right) with
        | (None, None) -> true
        | (Some(first), Some(second)) -> sameTestExpr(first)(second)
        | _ -> false

let recursive sameCandidates (left: List((Str, List(Str), Expr))) (right: List((Str, List(Str), Expr))) =
    match (left, right) with
        | ([], []) -> true
        | ((firstName, firstParameters, first) :: firstRest, (secondName, secondParameters, second) :: secondRest) -> firstName == secondName && firstParameters == secondParameters && sameTestExpr(first)(second) && sameCandidates(firstRest)(secondRest)
        | _ -> false

let recursive legacyCandidate name candidates =
    match candidates with
        | [] -> None
        | (candidate, parameters, value) :: rest ->
            if name == candidate
            then Some((parameters, value))
            else legacyCandidate(name)(rest)

let recursive legacyReference name refs =
    match refs with
        | [] -> None
        | (candidate, label, scheme) :: rest ->
            if name == candidate
            then Some((label, scheme))
            else legacyReference(name)(rest)

let matchesCandidate name candidates index =
    match legacyCandidate(name)(candidates) with
        | None ->
            sameCandidate(lookupSpecializationCandidate(name)(index))(None) && specializationCandidateArity(name)(index) == None && sameValue(specializationCandidateValueOf(name)(index))(None)
        | Some((parameters, value)) ->
            sameCandidate(lookupSpecializationCandidate(name)(index))(Some((parameters, value))) && specializationCandidateArity(name)(index) == Some(specializationArity(parameters)(value)) && sameValue(specializationCandidateValueOf(name)(index))(Some(value))

let scheme semanticType = TypeScheme(quantified = [], body = semanticType, constraints = [])

let emptyState unit = CoreLoweringState(programState = CoreProgramState(specializationCandidates = [], specializationCandidateByName = Ashes.Collection.Map.empty, topLevelFunctionRefs = [], topLevelFunctionRefByName = Ashes.Collection.Map.empty))

let stateChecks name =
    (let candidates = [(name, [name], ExprInt(1)), (name, [], ExprInt(2))]
    in
        let state =
            Unit
            |> emptyState
            |> withStateSpecializationCandidates(candidates)
        in
            let first =
                withRegisteredTopLevelFunctionRef(name)("first")(scheme(SemInt))(state)
            in
                let second =
                    withRegisteredTopLevelFunctionRef(name)("second")(scheme(SemString))(first)
                in
                    let replaced = withStateSpecializationCandidates([])(second)
                    in
                        sameCandidates(stateSpecializationCandidates(state))(candidates) && sameCandidate(state
                        |> stateSpecializationCandidateIndex
                        |> lookupSpecializationCandidate(name))(Some(([name], ExprInt(1)))) && sameCandidate(replaced
                        |> stateSpecializationCandidateIndex
                        |> lookupSpecializationCandidate(name))(None) && lookupTopLevelFunctionRef(name)(stateTopLevelFunctionRefIndex(first)) == Some(("first", scheme(SemInt))) && lookupTopLevelFunctionRef(name)(stateTopLevelFunctionRefIndex(second)) == Some(("second", scheme(SemString))) && lookupTopLevelFunctionRef(name)(stateTopLevelFunctionRefIndex(state)) == None)

let checks unit =
    (let candidates = [("same", ["x"], ExprInt(1)), ("Module.same", ["x", "y"], ExprInt(2)), ("same", ["z", "w"], ExprInt(3))]
    in
        let index = specializationCandidateIndex(candidates)
        in
            let first =
                registerFunctionReference("same")("first")(scheme(SemInt))(Ashes.Collection.Map.empty)
            in
                let second =
                    registerFunctionReference("same")("second")(scheme(SemString))(first)
                in
                    let refs = [("same", "second", scheme(SemString)), ("same", "first", scheme(SemInt))]
                    in
                        matchesCandidate("same")(candidates)(index) && matchesCandidate("Module.same")(candidates)(index) && matchesCandidate("missing")(candidates)(index) && sameCandidate([]
                        |> specializationCandidateIndex
                        |> lookupSpecializationCandidate("same"))(None) && lookupTopLevelFunctionRef("same")(second) == legacyReference("same")(refs) && lookupTopLevelFunctionRef("same")(first) == Some(("first", scheme(SemInt))) && lookupTopLevelFunctionRef("missing")(second) == None)

let freshCheck seed =
    (let name = "Function" + Ashes.Text.fromInt(seed)
    in
        let value = ExprString(name)
        in
            let index = specializationCandidateIndex([(name, [name], value), (name, [], ExprInt(seed))])
            in
                let refs =
                    registerFunctionReference(name)(name)([]
                    |> SemNamed(seed)(name)
                    |> scheme)(Ashes.Collection.Map.empty)
                in
                    stateChecks(name) && sameCandidate(lookupSpecializationCandidate(name)(index))(Some(([name], value))) && lookupTopLevelFunctionRef(name)(refs) == Some((name, []
                    |> SemNamed(seed)(name)
                    |> scheme)))

let recursive run remaining =
    if remaining == 0
    then Ashes.IO.print("function indexes ok")
    else
        if freshCheck(remaining)
        then run(remaining - 1)
        else Ashes.IO.panic("fresh function index mismatch")

if !(checks(Unit) && stateChecks("state"))
then Ashes.IO.panic("function index mismatch")
else
    match Ashes.IO.args with
        | "1000" :: [] -> run(1000)
        | "10000" :: [] -> run(10000)
        | _ -> Ashes.IO.panic("usage: 1000|10000")
