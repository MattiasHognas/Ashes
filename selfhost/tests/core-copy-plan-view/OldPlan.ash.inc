let recursive ownedConstructorChildren (constructorName: Str) (children: List(HeapLayoutChild)) =
    match children with
        | [] -> []
        | HeapLayoutChild { dropKind = NoChildDrop } :: rest -> ownedConstructorChildren(constructorName)(rest)
        | HeapLayoutChild { constructorName = Some(owner), fieldIndex = index, childType = childType } :: rest ->
            if owner == constructorName
            then (index, childType) :: ownedConstructorChildren(constructorName)(rest)
            else ownedConstructorChildren(constructorName)(rest)
        | _ :: rest -> ownedConstructorChildren(constructorName)(rest)

// The list-head copy kind of a list argument: scalar heads copy inline, string heads and heads
// that are lists of scalars copy with the spine; any other element type has no spine copy.
let runtimeManagedAdtLayout (facts: HeapLayoutFacts) =
    match facts with
        | HeapLayoutFacts { runtimeRecordAdtSupported = record, runtimeOwnedChildAdtSupported = ownedChild, runtimeTcoOwnedChildAdtSupported = tcoOwnedChild, runtimePositionalAdtSupported = positional } -> record || ownedChild || tcoOwnedChild || positional

let recursive anyChildSurvivesNoReset (children: List(HeapLayoutChild)) (state: CoreLoweringState) =
    match children with
        | [] -> false
        | HeapLayoutChild { childType = childType } :: rest -> !resultSurvivesReset(childType)(state) || anyChildSurvivesNoReset(rest)(state)

let recursive old_copyPlanOf (general: Bool) (semanticType: SemanticType) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString -> Some(LeafArgumentCopy(-1))
        | SemBytes -> Some(LeafArgumentCopy(-1))
        | SemBigInt -> Some(LeafArgumentCopy(bigIntCopySizeBytes))
        | SemList(element) ->
            match listHeadCopyKindOf(element)(state) with
                | Some(headCopy) -> Some(ListHeadArgumentCopy(headCopy))
                | None ->
                    match old_copyPlanOf(general)(element)(state) with
                        | Some(elementPlan) -> Some(ListDeepArgumentCopy(elementPlan))
                        | None -> None
        | SemTuple(elements) ->
            match old_copyPlansOf(general)(elements)(state) with
                | Some(plans) -> Some(TupleArgumentCopy(plans))
                | None -> None
        | SemNamed(_symbolId, name, _arguments) as named ->
            match heapFactsOf(named)(state) with
                | HeapLayoutFacts { structuralCopy = ShallowCopy } ->
                    state
                    |> shallowAdtCopySizeBytes(name)
                    |> ShallowAdtArgumentCopy
                    |> Some
                | HeapLayoutFacts { children = children } as facts ->
                    if runtimeManagedAdtLayout(facts)
                    then
                        let layouts = state |> stateConstructorsOfType(name)
                        in
                            if old_isInlineCopiedContractRecord(named)(layouts)(facts)(state)
                            then Some(NormalizerArgumentCopy(named))
                            else old_adtCopyPlanOfWith(general)(layouts)(children)(state)
                    else
                        // Stage 0's `NeedsRuntimeManagedAdtNormalizer`: an admissible named type no
                        // inline copy expresses is copied by its synthesized helper.
                        if general && isGeneralRcAdmissible(named)([])(state)
                        then Some(NormalizerArgumentCopy(named))
                        else None
        | resolved ->
            if resultSurvivesReset(resolved)(state)
            then Some(ScalarArgumentCopy)
            else None
and old_copyPlansOf (general: Bool) (types: List(SemanticType)) (state: CoreLoweringState) =
    match types with
        | [] -> Some([])
        | semanticType :: rest ->
            match (old_copyPlanOf(general)(semanticType)(state), old_copyPlansOf(general)(rest)(state)) with
                | (Some(plan), Some(plans)) -> Some(plan :: plans)
                | _ -> None
and old_childCopyPlansOfWith (general: Bool) (children: List((Int, SemanticType))) (state: CoreLoweringState) =
    match children with
        | [] -> Some([])
        | (index, childType) :: rest ->
            match (old_copyPlanOf(general)(childType)(state), old_childCopyPlansOfWith(general)(rest)(state)) with
                | (Some(plan), Some(plans)) -> Some((index, plan) :: plans)
                | _ -> None
and old_constructorCopyPlansOfWith (general: Bool) (layouts: List(CoreConstructorLayout)) (children: List(HeapLayoutChild)) (state: CoreLoweringState) =
    match layouts with
        | [] -> Some([])
        | (CoreConstructorLayout { name = constructorName, tag = tag, tagless = tagless } as layout) :: rest ->
            match (old_childCopyPlansOfWith(general)(ownedConstructorChildren(constructorName)(children))(state), old_constructorCopyPlansOfWith(general)(rest)(children)(state)) with
                | (Some(plans), Some(restPlans)) -> Some((tag, layoutAllocationSizeBytes(layout), tagless, plans) :: restPlans)
                | _ -> None
and old_adtCopyPlanOfWith (general: Bool) (layouts: List(CoreConstructorLayout)) (children: List(HeapLayoutChild)) (state: CoreLoweringState) =
    match old_constructorCopyPlansOfWith(general)(layouts)(children)(state) with
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
and old_isInlineCopiedContractRecord (named: SemanticType) (layouts: List(CoreConstructorLayout)) (facts: HeapLayoutFacts) (state: CoreLoweringState) =
    match recordedContractRecord(named)(state) with
        | Some(answer) -> answer
        | None -> old_classifyInlineCopiedContractRecord(named)(layouts)(facts)(state)
and old_classifyInlineCopiedContractRecord (named: SemanticType) (layouts: List(CoreConstructorLayout)) (facts: HeapLayoutFacts) (state: CoreLoweringState) =
    match (layouts, facts) with
        | (_layout :: [], HeapLayoutFacts { runtimeOwnedChildAdtSupported = false, arenaDeepCopySupported = true, children = children }) ->
            anyChildSurvivesNoReset(children)(state) && !canRuntimeManageRecursiveCopyAdt(named)(state) && (match old_adtCopyPlanOfWith(false)(layouts)(children)(state) with
                | Some(_plan) -> true
                | None -> false)
        | _ -> false

// The copy of a value into the reference-counted heap by the inline copies alone, which is also
// how the placement rules ask whether a type has one.
