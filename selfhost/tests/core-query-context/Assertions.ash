let check condition =
    if condition
    then Unit
    else test.fail("core query context mismatch")

let box argument = SemNamed(0)("Box")([argument])

let closure = SemFunction(SemInt)(SemInt)(None)

let definition name arguments body = ConstructorInferenceDefinition(name = name, scheme = TypeScheme(quantified = arguments, body = body, constraints = []), fieldNames = [])

let definitions =
    [None
    |> SemFunction(SemVariable(7))(box(SemVariable(7)))
    |> definition("Box")([(7, "a")])]

let layout (item: ConstructorInferenceDefinition) = CoreConstructorLayout(name = item.name, tag = 0, scheme = item.scheme, fieldNames = item.fieldNames, isZeroCost = false, tagless = true)

let stateFor definitions =
    (let environment = emptyTypeEnvironment(Unit) with constructors = definitions
    in CoreLoweringState(programState = CoreProgramState(coverageTypes = environment, coverageEnvironmentVersion = 0, heapQueryContext = emptyCoreHeapQueryContext(environment), dropperTypes = prepareDropperTypes(definitions), heapLayoutFactsByType = Ashes.Collection.Map.empty, generalRcTypeFacts = Ashes.Collection.Map.empty, contractRecordFacts = Ashes.Collection.Map.empty, constructors = map(layout)(definitions)), functionState = CoreFunctionState(admissibilityAnswers = []), liveHandler = false, specialize = None))

let factCount state =
    (let context = stateHeapQueryContext(state)
    in Ashes.Collection.Map.size(context.facts))

let nodeCount state =
    (let context = stateHeapQueryContext(state)
    in
        let graph = context.graphIndex
        in graph.nodeCount)

let checkIdentity unit =
    (let state = stateFor(definitions)
    in
        match heapShallowCopyOfIndexed(box(SemInt))(state) with
            | (scalar, first) ->
                match heapShallowCopyOfIndexed(box(closure))(first) with
                    | (owned, second) ->
                        scalar && !owned && factCount(second) == 2
                        |> check
                        |> (given (_) ->
                            match heapShallowCopyOfIndexed(box(SemInt))(second) with
                                | (again, reused) -> check(again && nodeCount(reused) == nodeCount(second) && factCount(reused) == 2)))

let checkSettledPriority unit =
    (let named = SemNamed(1)("Plain")([])
    in
        let defs =
            [None
            |> SemFunction(SemInt)(named)
            |> definition("Plain")([])]
        in
            match defs
            |> stateFor
            |> heapShallowCopyOfIndexed(named) with
                | (true, warmed) ->
                    let group = warmed.programState
                    in
                        let full = classifyHeapLayout(named)(group.coverageTypes) with structuralCopy = NoStructuralCopy, containsResource = true
                        in
                            let settled = warmed with programState = (group with heapLayoutFactsByType = Ashes.Collection.Map.setStr("Plain")(full)(group.heapLayoutFactsByType))
                            in
                                match heapShallowCopyOfIndexed(named)(settled) with
                                    | (shallow, queried) ->
                                        match heapAdmissibilityOfIndexed(named)(queried) with
                                            | (facts, completed) -> check(!shallow && facts.containsResource && factCount(completed) == factCount(warmed))
                | _ -> test.fail("scalar root should be shallow"))

let checkRestore unit =
    (let outer = stateFor(definitions)
    in
        match heapCopyPlanFactsOfIndexed(box(SemString))(outer) with
            | (_facts, body) ->
                let types = stateDropperTypes(body)
                in
                    match synthesizeOwnedAggregateReleaseIndexed(0)(box(SemInt))(OwnedReleasePlan(deepUnique = false, constructorName = None))(types)(emptyDropperLabelCache)(1)(0)(0)(0) with
                        | (_synthesis, context) ->
                            let warmed = withStateDropperContext(context)(body)
                            in
                                let restored = restoreHeapQueryContexts(warmed)(outer)
                                in
                                    let restoredTypes = stateDropperTypes(restored)
                                    in
                                        factCount(restored) == factCount(warmed) && factCount(restored) > 0 && Ashes.Collection.Map.size(restoredTypes.sharedFacts) == Ashes.Collection.Map.size(context.facts)
                                        |> check
                                        |> (given (_) ->
                                            let changed =
                                                withStateCoverageTypes(emptyTypeEnvironment(Unit))(warmed)
                                            in
                                                let rejected = restoreHeapQueryContexts(changed)(outer)
                                                in check(factCount(changed) == 0 && factCount(rejected) == 0)))

let recursive samePlan left right =
    match (left, right) with
        | (ScalarArgumentCopy, ScalarArgumentCopy) -> true
        | (LeafArgumentCopy(a), LeafArgumentCopy(b)) -> a == b
        | (ListHeadArgumentCopy(a), ListHeadArgumentCopy(b)) -> a == b
        | (ListDeepArgumentCopy(a), ListDeepArgumentCopy(b)) -> samePlan(a)(b)
        | (TupleArgumentCopy(a), TupleArgumentCopy(b)) -> samePlans(a)(b)
        | (ShallowAdtArgumentCopy(a), ShallowAdtArgumentCopy(b)) -> a == b
        | (ConstructorArgumentCopy(a), ConstructorArgumentCopy(b)) -> sameConstructorPlan(a)(b)
        | (SwitchArgumentCopy(a), SwitchArgumentCopy(b)) -> sameConstructorPlans(a)(b)
        | (NormalizerArgumentCopy(a), NormalizerArgumentCopy(b)) -> a == b
        | _ -> false
and samePlans left right =
    match (left, right) with
        | ([], []) -> true
        | (a :: restA, b :: restB) -> samePlan(a)(b) && samePlans(restA)(restB)
        | _ -> false
and sameConstructorPlan left right =
    match (left, right) with
        | ((tagA, sizeA, taglessA, fieldsA), (tagB, sizeB, taglessB, fieldsB)) -> tagA == tagB && sizeA == sizeB && taglessA == taglessB && sameFieldPlans(fieldsA)(fieldsB)
and sameConstructorPlans left right =
    match (left, right) with
        | ([], []) -> true
        | (a :: restA, b :: restB) -> sameConstructorPlan(a)(b) && sameConstructorPlans(restA)(restB)
        | _ -> false
and sameFieldPlans left right =
    match (left, right) with
        | ([], []) -> true
        | ((indexA, a) :: restA, (indexB, b) :: restB) -> indexA == indexB && samePlan(a)(b) && sameFieldPlans(restA)(restB)
        | _ -> false

let sameMaybePlan left right =
    match (left, right) with
        | (None, None) -> true
        | (Some(a), Some(b)) -> samePlan(a)(b)
        | _ -> false

let checkStatefulPlans unit =
    (let named = SemNamed(2)("Contract")([])
    in
        let initial =
            stateFor([None
            |> SemFunction(SemString)(named)
            |> definition("Contract")([])])
        in
            let initialGroup = initial.programState
            in
                let state = initial with programState = (initialGroup with contractRecordFacts = Ashes.Collection.Map.setStr("Contract")(false)(initialGroup.contractRecordFacts))
                in
                    match copyPlanOfIndexed(false)(named)(state) with
                        | (before, warmed) ->
                            let group = warmed.programState
                            in
                                let changed = warmed with programState = (group with contractRecordFacts = Ashes.Collection.Map.setStr("Contract")(true)(group.contractRecordFacts))
                                in
                                    match copyPlanOfIndexed(false)(named)(changed) with
                                        | (after, completed) ->
                                            check(!sameMaybePlan(before)(after) && sameMaybePlan(before)(oracleCopyPlanOf(false)(named)(state)) && sameMaybePlan(after)(oracleCopyPlanOf(false)(named)(changed)) && factCount(completed) == factCount(warmed)))

let checkEnvironmentReplacement unit =
    (let named = SemNamed(4)("Changed")([])
    in
        match [None
        |> SemFunction(SemInt)(named)
        |> definition("Changed")([])]
        |> stateFor
        |> heapShallowCopyOfIndexed(named) with
            | (before, warmed) ->
                let environment =
                    emptyTypeEnvironment(Unit) with constructors = [None
                    |> SemFunction(closure)(named)
                    |> definition("Changed")([])]
                in
                    match warmed
                    |> withStateCoverageTypes(environment)
                    |> heapShallowCopyOfIndexed(named) with
                        | (after, queried) -> check(before && !after && factCount(queried) == 1))

let recursive checkTypes types state =
    match types with
        | [] -> Unit
        | semanticType :: rest ->
            match copyPlanOfIndexed(false)(semanticType)(state) with
                | (inlinePlan, first) ->
                    match copyPlanOfIndexed(true)(semanticType)(first) with
                        | (generalPlan, second) ->
                            match isGeneralRcValueTypeIndexed(semanticType)(second) with
                                | (valueType, third) ->
                                    sameMaybePlan(inlinePlan)(oracleCopyPlanOf(false)(semanticType)(state)) && sameMaybePlan(generalPlan)(oracleCopyPlanOf(true)(semanticType)(state)) && valueType == oracleIsGeneralRcValueType(semanticType)(state)
                                    |> check
                                    |> (given (_) ->
                                        match isGeneralRcValueTypeIndexed(semanticType)((third with liveHandler = true)) with
                                            | (handlerAnswer, queried) ->
                                                handlerAnswer == oracleIsGeneralRcValueType(semanticType)((state with liveHandler = true))
                                                |> check
                                                |> (given (_) -> checkTypes(rest)((queried with liveHandler = false))))

let differential unit =
    definitions
    |> stateFor
    |> checkTypes([SemInt, SemString, SemBytes, SemBigInt, closure, SemVariable(99), SemList(SemInt), SemString
    |> box
    |> SemList, SemTuple([box(SemInt), box(SemString)]), box(SemInt), box(SemString), box(closure), SemString
    |> box
    |> box, SemString
    |> box
    |> SemList
    |> box])

let freshOnce n =
    (let name = "Fresh" + Ashes.Text.fromInt(n)
    in
        let named = SemNamed(0)(name)([])
        in
            let state =
                stateFor([None
                |> SemFunction(SemString)(named)
                |> definition(name)([])])
            in
                match heapCopyPlanFactsOfIndexed(named)(state) with
                    | (facts, first) ->
                        match heapAdmissibilityOfIndexed(named)(first) with
                            | (admissibility, second) ->
                                match heapShallowCopyOfIndexed(named)(second) with
                                    | (shallow, completed) -> factCount(completed) == 1 && !shallow && !admissibility.containsResource && !facts.shallowCopy)

let recursive fresh rounds =
    if rounds == 0
    then Ashes.IO.print("fresh passed")
    else
        if freshOnce(rounds)
        then fresh(rounds - 1)
        else test.fail("fresh context mismatch")

let recursive warm rounds state checksum =
    if rounds == 0
    then
        checksum
        |> Ashes.Text.fromInt
        |> Ashes.IO.print
    else
        match isGeneralRcValueTypeIndexed(SemString
        |> box
        |> box)(state) with
            | (answer, queried) ->
                warm(rounds - 1)(restoreHeapQueryContexts(queried)(state))(checksum + (if answer
                then 1
                else 0))

let recursive baselineWarm rounds state checksum =
    if rounds == 0
    then
        checksum
        |> Ashes.Text.fromInt
        |> Ashes.IO.print
    else
        baselineWarm(rounds - 1)(state)(checksum + (if oracleIsGeneralRcValueType(SemString
        |> box
        |> box)(state)
        then 1
        else 0))

match Ashes.IO.args with
    | "check" :: [] ->
        Unit
        |> checkIdentity
        |> checkSettledPriority
        |> checkRestore
        |> checkStatefulPlans
        |> checkEnvironmentReplacement
        |> differential
        |> (given (_) -> Ashes.IO.print("core context checks passed"))
    | "fresh1000" :: [] -> fresh(1000)
    | "fresh10000" :: [] -> fresh(10000)
    | "warm1000" :: [] ->
        warm(1000)(stateFor(definitions))(0)
    | "warm10000" :: [] ->
        warm(10000)(stateFor(definitions))(0)
    | "warm100000" :: [] ->
        warm(100000)(stateFor(definitions))(0)
    | "baseline100000" :: [] ->
        baselineWarm(100000)(stateFor(definitions))(0)
    | _ -> test.fail("expected check/fresh1000/fresh10000/warm1000/warm10000/warm100000")
