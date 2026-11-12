// Frozen decision oracle from cc50f0f5. State/layout test seams are shared by both engines.
let oracleHeapShallowCopyOf (semanticType: SemanticType) (state: CoreLoweringState) =
    (let resolved = resolveType(state)(semanticType)
    in
        match resolved with
            | SemNamed(_symbolId, name, []) ->
                match state
                |> stateHeapLayoutFactsByType
                |> Ashes.Collection.Map.getStr(name) with
                    | Some(facts) -> facts.structuralCopy == ShallowCopy
                    | None ->
                        state
                        |> coverageEnvironment
                        |> classifyHeapShallowCopy(resolved)
            | _ ->
                state
                |> coverageEnvironment
                |> classifyHeapShallowCopy(resolved))

// Preserve the same settled monomorphic cache answer as heapFactsOf. An uncached resource
// query needs only the resource walk, not unrelated copy/drop layout classification.
let oracleScopeCopyOutOf (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString -> Some(ShallowScopeCopyOut(-1))
        | SemBytes -> Some(ShallowScopeCopyOut(-1))
        | SemList(element) ->
            if element
            |> resolveType(state)
            |> canArenaResetLayout
            then Some(ListScopeCopyOut)
            else None
        | SemNamed(_symbolId, name, _arguments) as named ->
            if oracleHeapShallowCopyOf(named)(state)
            then
                state
                |> shallowAdtCopySizeBytes(name)
                |> ShallowScopeCopyOut
                |> Some
            else None
        | _ -> None

// The copy is reference-counted, or an arena copy where the function may run under a live handler
// post (stage 0's `normalizeToRuntimeOwnership`).
let oracleHeapAdmissibilityOf (semanticType: SemanticType) (state: CoreLoweringState) =
    (let resolved = resolveType(state)(semanticType)
    in
        match resolved with
            | SemNamed(_symbolId, name, []) ->
                match state
                |> stateHeapLayoutFactsByType
                |> Ashes.Collection.Map.getStr(name) with
                    | Some(facts) ->
                        HeapAdmissibilityFacts(
                            containsResource = facts.containsResource,
                            containsUnresolvedType = facts.containsUnresolvedType,
                            children = map(admissibilityChildOf)(facts.children)
                        )
                    | None ->
                        state
                        |> coverageEnvironment
                        |> classifyHeapAdmissibility(resolved)
            | _ ->
                state
                |> coverageEnvironment
                |> classifyHeapAdmissibility(resolved))

let recursive oracleOriginalGeneralRcAdmissible (semanticType: SemanticType) (path: List(SemanticType)) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString -> true
        | SemBytes -> true
        | SemBigInt -> true
        | SemList(element) -> oracleOriginalGeneralRcAdmissible(element)(path)(state)
        | SemTuple(elements) -> oracleOriginalAllGeneralRcAdmissible(elements)(path)(state)
        | SemNamed(_symbolId, name, arguments) as named ->
            match (path, arguments, recordedGeneralRcTypeFacts(name)(state)) with
                | ([], [], Some((admissible, _reaches))) -> admissible
                | _ ->
                    if resultSurvivesReset(named)(state)
                    then true
                    else
                        if name == "Task" || containsType(named)(path)
                        then name != "Task"
                        else
                            match (state |> stateConstructorsOfType(name), oracleHeapAdmissibilityOf(named)(state)) with
                                | ([], _facts) -> false
                                | (_layouts, HeapAdmissibilityFacts { containsResource = true }) -> false
                                | (_layouts, HeapAdmissibilityFacts { containsUnresolvedType = true }) -> false
                                | (_layouts, HeapAdmissibilityFacts { children = children }) -> oracleOriginalAllChildrenGeneralRcAdmissible(children)(named :: path)(state)
        | resolved -> resultSurvivesReset(resolved)(state)
and oracleOriginalAllGeneralRcAdmissible (types: List(SemanticType)) (path: List(SemanticType)) (state: CoreLoweringState) =
    match types with
        | [] -> true
        | semanticType :: rest -> oracleOriginalGeneralRcAdmissible(semanticType)(path)(state) && oracleOriginalAllGeneralRcAdmissible(rest)(path)(state)
and oracleOriginalAllChildrenGeneralRcAdmissible (children: List((SemanticType, HeapChildDropKind))) (path: List(SemanticType)) (state: CoreLoweringState) =
    match children with
        | [] -> true
        | (_childType, DropClosure) :: _rest -> false
        | (_childType, UnsupportedChildDrop) :: _rest -> false
        | (childType, _dropKind) :: rest -> oracleOriginalGeneralRcAdmissible(childType)(path)(state) && oracleOriginalAllChildrenGeneralRcAdmissible(rest)(path)(state)

let recursive oracleSearchGeneralRcAdmissibility pending visited state =
    match pending with
        | [] -> Some(true)
        | CheckAdmissibilityChild(_childType, DropClosure) :: _rest -> Some(false)
        | CheckAdmissibilityChild(_childType, UnsupportedChildDrop) :: _rest -> Some(false)
        | CheckAdmissibilityChild(childType, _dropKind) :: rest -> oracleSearchGeneralRcAdmissibility(VisitAdmissibilityType(childType)(false) :: rest)(visited)(state)
        | VisitAdmissibilityType(semanticType, allowRootCache) :: rest ->
            match resolveType(state)(semanticType) with
                | SemString -> oracleSearchGeneralRcAdmissibility(rest)(visited)(state)
                | SemBytes -> oracleSearchGeneralRcAdmissibility(rest)(visited)(state)
                | SemBigInt -> oracleSearchGeneralRcAdmissibility(rest)(visited)(state)
                | SemList(element) -> oracleSearchGeneralRcAdmissibility(VisitAdmissibilityType(element)(allowRootCache) :: rest)(visited)(state)
                | SemTuple(elements) ->
                    oracleSearchGeneralRcAdmissibility(prependAdmissibilityTypes(elements)(allowRootCache)(rest))(visited)(state)
                | SemNamed(_symbolId, name, arguments) as named ->
                    match (allowRootCache, arguments, recordedGeneralRcTypeFacts(name)(state)) with
                        | (true, [], Some((admissible, _reaches))) ->
                            if admissible
                            then oracleSearchGeneralRcAdmissibility(rest)(visited)(state)
                            else Some(false)
                        | _ ->
                            if resultSurvivesReset(named)(state)
                            then oracleSearchGeneralRcAdmissibility(rest)(visited)(state)
                            else
                                if name == "Task"
                                then Some(false)
                                else
                                    match admissibilityVisitStatus(named)(visited) with
                                        | None -> None
                                        | Some(true) -> oracleSearchGeneralRcAdmissibility(rest)(visited)(state)
                                        | Some(false) ->
                                            match (state |> stateConstructorsOfType(name), oracleHeapAdmissibilityOf(named)(state)) with
                                                | ([], _facts) -> Some(false)
                                                | (_layouts, HeapAdmissibilityFacts { containsResource = true }) -> Some(false)
                                                | (_layouts, HeapAdmissibilityFacts { containsUnresolvedType = true }) -> Some(false)
                                                | (_layouts, HeapAdmissibilityFacts { children = children }) ->
                                                    oracleSearchGeneralRcAdmissibility(prependAdmissibilityChildren(children)(rest))(named :: visited)(state)
                | resolved ->
                    if resultSurvivesReset(resolved)(state)
                    then oracleSearchGeneralRcAdmissibility(rest)(visited)(state)
                    else Some(false)

let oracleQueryGeneralRcAdmissibility (semanticType: SemanticType) (path: List(SemanticType)) (state: CoreLoweringState) =
    match oracleSearchGeneralRcAdmissibility([VisitAdmissibilityType(semanticType)(path == [])])(path)(state) with
        | Some(answer) -> answer
        | None -> oracleOriginalGeneralRcAdmissible(semanticType)(path)(state)

let oracleIsGeneralRcAdmissible (semanticType: SemanticType) (path: List(SemanticType)) (state: CoreLoweringState) =
    match path with
        | [] ->
            let group = state.functionState
            in
                match lookupAdmissibilityAnswer(resolveType(state)(semanticType))(group.admissibilityAnswers) with
                    | Some(answer) -> answer
                    | None -> oracleQueryGeneralRcAdmissibility(semanticType)(path)(state)
        | _ -> oracleQueryGeneralRcAdmissibility(semanticType)(path)(state)

let oracleHeapCopyPlanFactsOf (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemNamed(_symbolId, name, []) as resolved ->
            match state
            |> stateHeapLayoutFactsByType
            |> Ashes.Collection.Map.getStr(name) with
                | Some(facts) -> projectHeapCopyPlanFacts(facts)
                | None ->
                    state
                    |> coverageEnvironment
                    |> classifyHeapCopyPlan(resolved)
        | resolved ->
            state
            |> coverageEnvironment
            |> classifyHeapCopyPlan(resolved)

let recursive oracleCopyPlanOf (general: Bool) (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString -> Some(LeafArgumentCopy(-1))
        | SemBytes -> Some(LeafArgumentCopy(-1))
        | SemBigInt -> Some(LeafArgumentCopy(bigIntCopySizeBytes))
        | SemList(element) ->
            match listHeadCopyKindOf(element)(state) with
                | Some(headCopy) -> Some(ListHeadArgumentCopy(headCopy))
                | None ->
                    match oracleCopyPlanOf(general)(element)(state) with
                        | Some(elementPlan) -> Some(ListDeepArgumentCopy(elementPlan))
                        | None -> None
        | SemTuple(elements) ->
            match oracleCopyPlansOf(general)(elements)(state) with
                | Some(plans) -> Some(TupleArgumentCopy(plans))
                | None -> None
        | SemNamed(_symbolId, name, _arguments) as named ->
            match oracleHeapCopyPlanFactsOf(named)(state) with
                | HeapCopyPlanFacts { shallowCopy = true } ->
                    state
                    |> shallowAdtCopySizeBytes(name)
                    |> ShallowAdtArgumentCopy
                    |> Some
                | HeapCopyPlanFacts { children = children } as facts ->
                    if runtimeManagedCopyPlanLayout(facts)
                    then
                        let layouts = state |> stateConstructorsOfType(name)
                        in
                            if oracleIsInlineCopiedContractRecord(named)(layouts)(facts)(state)
                            then Some(NormalizerArgumentCopy(named))
                            else oracleAdtCopyPlanOfWith(general)(layouts)(children)(state)
                    else
                        // Stage 0's `NeedsRuntimeManagedAdtNormalizer`: an admissible named type no
                        // inline copy expresses is copied by its synthesized helper.
                        if general && oracleIsGeneralRcAdmissible(named)([])(state)
                        then Some(NormalizerArgumentCopy(named))
                        else None
        | resolved ->
            if resultSurvivesReset(resolved)(state)
            then Some(ScalarArgumentCopy)
            else None
and oracleCopyPlansOf (general: Bool) (types: List(SemanticType)) (state: CoreLoweringState) =
    match types with
        | [] -> Some([])
        | semanticType :: rest ->
            match (oracleCopyPlanOf(general)(semanticType)(state), oracleCopyPlansOf(general)(rest)(state)) with
                | (Some(plan), Some(plans)) -> Some(plan :: plans)
                | _ -> None
and oracleChildCopyPlansOfWith (general: Bool) (children: List((Int, SemanticType))) (state: CoreLoweringState) =
    match children with
        | [] -> Some([])
        | (index, childType) :: rest ->
            match (oracleCopyPlanOf(general)(childType)(state), oracleChildCopyPlansOfWith(general)(rest)(state)) with
                | (Some(plan), Some(plans)) -> Some((index, plan) :: plans)
                | _ -> None
and oracleConstructorCopyPlansOfWith (general: Bool) (layouts: List(CoreConstructorLayout)) (children: List(HeapCopyPlanChild)) (state: CoreLoweringState) =
    match layouts with
        | [] -> Some([])
        | (CoreConstructorLayout { name = constructorName, tag = tag, tagless = tagless } as layout) :: rest ->
            match (oracleChildCopyPlansOfWith(general)(ownedCopyPlanChildren(constructorName)(children))(state), oracleConstructorCopyPlansOfWith(general)(rest)(children)(state)) with
                | (Some(plans), Some(restPlans)) -> Some((tag, layoutAllocationSizeBytes(layout), tagless, plans) :: restPlans)
                | _ -> None
and oracleAdtCopyPlanOfWith (general: Bool) (layouts: List(CoreConstructorLayout)) (children: List(HeapCopyPlanChild)) (state: CoreLoweringState) =
    match oracleConstructorCopyPlansOfWith(general)(layouts)(children)(state) with
        | Some(single :: []) -> Some(ConstructorArgumentCopy(single))
        | Some([]) -> None
        | Some(plans) -> Some(SwitchArgumentCopy(plans))
        | None -> None
// Stage 0's `IsInlineCopiedContractRecord`: a record the entry normalization re-establishes (one
// constructor, an inline copy, an arena deep copy) with a heap child, that the owned-child and
// recursive-copy paths do not manage. It joins the ownership contract and is copied by its
// synthesized normalization helper, one call per site instead of its whole graph. An
// argument-free type is answered from the table the program's start recorded: classifying one
// walks its children's copy plans, which ask the same of every record nested in it.
and oracleIsInlineCopiedContractRecord (named: SemanticType) (layouts: List(CoreConstructorLayout)) (facts: HeapCopyPlanFacts) (state: CoreLoweringState) =
    match recordedContractRecord(named)(state) with
        | Some(answer) -> answer
        | None -> oracleClassifyCopyPlanContractRecord(named)(layouts)(facts)(state)
and oracleClassifyCopyPlanContractRecord (named: SemanticType) (layouts: List(CoreConstructorLayout)) (facts: HeapCopyPlanFacts) (state: CoreLoweringState) =
    match (layouts, facts) with
        | (_layout :: [], HeapCopyPlanFacts { runtimeOwnedChildAdtSupported = false, arenaDeepCopySupported = true, children = children }) ->
            anyCopyPlanChildSurvivesNoReset(children)(state) && !canRuntimeManageRecursiveCopyAdt(named)(state) && (match oracleAdtCopyPlanOfWith(false)(layouts)(children)(state) with
                | Some(_plan) -> true
                | None -> false)
        | _ -> false

let oracleArgumentCopyPlanOf (semanticType: SemanticType) (state: CoreLoweringState) = oracleCopyPlanOf(false)(semanticType)(state)

// The same copy with the synthesized normalization helper standing in for every named type the
// inline copies do not express, stage 0's `EmitRuntimeManagedTcoDeepCopy` in full.
let oracleGeneralCopyPlanOf (semanticType: SemanticType) (state: CoreLoweringState) = oracleCopyPlanOf(true)(semanticType)(state)

let oracleHeapChildrenOfNamed (named: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(named) with
        | SemNamed(_symbolId, name, []) as resolved ->
            match state
            |> stateHeapLayoutFactsByType
            |> Ashes.Collection.Map.getStr(name) with
                | Some(HeapLayoutFacts { children = children }) -> children
                | None ->
                    state
                    |> coverageEnvironment
                    |> classifyHeapChildren(resolved)
        | resolved ->
            state
            |> coverageEnvironment
            |> classifyHeapChildren(resolved)

let oracleHeapChildTypesOfNamed (named: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(named) with
        | SemNamed(_symbolId, name, []) as resolved ->
            match state
            |> stateHeapLayoutFactsByType
            |> Ashes.Collection.Map.getStr(name) with
                | Some(HeapLayoutFacts { children = children }) ->
                    map(given (child: HeapLayoutChild) -> child.childType)(children)
                | None ->
                    state
                    |> coverageEnvironment
                    |> classifyHeapChildTypes(resolved)
        | resolved ->
            state
            |> coverageEnvironment
            |> classifyHeapChildTypes(resolved)

// The cell-by-cell copy of a named type written inline, a record of the contract's types included:
// stage 0's constructor deep copy, which the normalization helper's body and a successor built at
// the self-call use.
let oracleIsGeneralRcNamedType (named: SemanticType) (state: CoreLoweringState) =
    match oracleGeneralCopyPlanOf(named)(state) with
        | Some(NormalizerArgumentCopy(_type)) ->
            programMayRunUnderLiveHandlerPost(state) == false && !canRuntimeManageRecursiveCopyAdt(named)(state) && anyChildSurvivesNoReset(oracleHeapChildrenOfNamed(named)(state))(state)
        | _ -> false

// Stage 0's `ReachesGeneralRcNamedType`.
let recursive oracleReachesGeneralRcNamedType (semanticType: SemanticType) (path: List(SemanticType)) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemList(element) -> oracleReachesGeneralRcNamedType(element)(path)(state)
        | SemTuple(elements) -> oracleAnyReachesGeneralRcNamedType(elements)(path)(state)
        | SemNamed(_symbolId, name, arguments) as named ->
            match (path, arguments, recordedGeneralRcTypeFacts(name)(state)) with
                | ([], [], Some((_admissible, reaches))) -> reaches
                | _ ->
                    oracleIsGeneralRcNamedType(named)(state) || !containsType(named)(path) && oracleAnyReachesGeneralRcNamedType(oracleHeapChildTypesOfNamed(named)(state))(named :: path)(state)
        | _ -> false
and oracleAnyReachesGeneralRcNamedType (types: List(SemanticType)) (path: List(SemanticType)) (state: CoreLoweringState) =
    match types with
        | [] -> false
        | semanticType :: rest -> oracleReachesGeneralRcNamedType(semanticType)(path)(state) || oracleAnyReachesGeneralRcNamedType(rest)(path)(state)

// Both walks' answers for every argument-free type the program declares whose layout facts are
// settled (no unresolved type left for inference to close), so a question asked at a call site reads
// them instead of walking the type's graph again.
let oracleCallCopyOutOf (semanticType: SemanticType) (state: CoreLoweringState) =
    match oracleScopeCopyOutOf(semanticType)(state) with
        | Some(ShallowScopeCopyOut(staticSizeBytes)) -> Some(ShallowCallCopyOut(staticSizeBytes))
        | Some(ListScopeCopyOut) -> Some(ListCallCopyOut(InlineListHead))
        | None ->
            match resolveType(state)(semanticType) with
                | SemList(element) ->
                    match element
                    |> resolveType(state)
                    |> listHeadCopyOf with
                        | Some(headCopy) -> Some(ListCallCopyOut(headCopy))
                        | None -> None
                | _ -> None

// Stage 0's `IsGeneralRcValueType`: a resolved type that reaches a named type only the
// normalization helper expresses and has no fixed copy-out. A value of it follows the contract:
// passed borrowed, returned owned. A reuse specialization keeps its values in the persistent
// region and takes no part in it, nor does a program where a function may run under a
// live handler post.
let oracleIsGeneralRcValueType (semanticType: SemanticType) (state: CoreLoweringState) =
    match (stateSpecializationFreshInputs(state), oracleCallCopyOutOf(semanticType)(state)) with
        | (None, None) -> programMayRunUnderLiveHandlerPost(state) == false && !resultSurvivesReset(semanticType)(state) && oracleIsGeneralRcAdmissible(semanticType)([])(state) && oracleReachesGeneralRcNamedType(semanticType)([])(state)
        | _ -> false
// Stage 0's `IsCallResultAccumulatorElement`: an accumulator a self-call replaces with a call's
// result holds the contract's values (resolved, as stage 0's `IsGeneralRcValueType` asks) or any
// other heap element the loop can place, a list inside it included: its arena cells would otherwise
// keep values the iteration owns with no owner left to release them. An arena sibling that may
// receive such an inner list blocks the accumulator (`siblingReceivesAccumulatorPart`).
