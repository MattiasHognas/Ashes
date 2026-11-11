let bit value divisor = value / divisor % 2 == 1

let plansAgree (semanticType: SemanticType) (state: CoreLoweringState) =
    sameMaybePlan(old_copyPlanOf(false)(semanticType)(state))(copyPlanOf(false)(semanticType)(state)) && sameMaybePlan(old_copyPlanOf(true)(semanticType)(state))(copyPlanOf(true)(semanticType)(state))

let check seed =
    let name = "Cached" + Ashes.Text.fromInt(seed)
    in
        let named = SemNamed(seed)(name)([])
        in
            let environment = emptyTypeEnvironment(Unit) with constructorFieldGroups = [ConstructorFieldGroup(groupSymbolId = seed, groupTypeName = name, groupIsGeneric = false, groupFields = [("A", [SemString, SemInt]), ("B", [SemList(SemBytes)])], groupGenericFields = [])]
            in
                let base = classifyHeapLayout(SemTuple([SemString]))(environment)
                in
                    let mask = seed / 3
                    in
                        let full = base with structuralCopy = (if bit(mask)(1) then ShallowCopy else DeepCopy), arenaDeepCopySupported = bit(mask)(2), runtimeRecordAdtSupported = bit(mask)(4), runtimeOwnedChildAdtSupported = bit(mask)(8), runtimeTcoOwnedChildAdtSupported = bit(mask)(16), runtimePositionalAdtSupported = bit(mask)(32), children = [HeapLayoutChild(constructorName = Some("A"), fieldIndex = 0, childType = SemString, dropKind = DropString, copyKind = ShallowCopy), HeapLayoutChild(constructorName = Some("A"), fieldIndex = 1, childType = SemInt, dropKind = NoChildDrop, copyKind = InlineCopy), HeapLayoutChild(constructorName = Some("B"), fieldIndex = 0, childType = SemList(SemBytes), dropKind = DropList, copyKind = DeepCopy)]
                        in
                            let layouts = if seed % 4 == 0 then [] else if seed % 4 == 1 then [CoreConstructorLayout(name = "A", tag = 7, tagless = true, size = 16)] else [CoreConstructorLayout(name = "A", tag = 7, tagless = false, size = 24), CoreConstructorLayout(name = "B", tag = 3, tagless = false, size = 16)]
                            in
                                let plain = CoreLoweringState(environment = environment, facts = Ashes.Collection.Map.empty, resolvedOverride = None, layouts = layouts, recorded = (if seed % 3 == 0 then None else Some(seed % 3 == 1)), recursiveCopy = false, admissible = false, emittedPlan = None)
                                in
                                    let cached = plain with facts = Ashes.Collection.Map.setStr(name)(full)(Ashes.Collection.Map.empty)
                                    in
                                        let generic = SemNamed(seed)(name)([SemInt])
                                        in
                                            normalizerPlansAgree(named)(plain) && normalizerPlansAgree(named)(cached) && normalizerPlansAgree(generic)(cached) && normalizerPlansAgree(SemInt)(plain) && heapCopyPlanFactsOf(named)(cached) == projectHeapCopyPlanFacts(full) && heapCopyPlanFactsOf(generic)(cached) == projectHeapCopyPlanFacts(heapFactsOf(generic)(cached)) && plansAgree(named)(plain) && plansAgree(named)(cached) && plansAgree(named)((cached with recursiveCopy = true, admissible = true)) && plansAgree(SemVariable(seed))((cached with resolvedOverride = Some(named))) && plansAgree(SemTuple([SemString, SemList(SemBytes), SemInt]))(plain)

let recursive repeat remaining =
    if remaining == 0
    then Ashes.IO.print("done")
    else
        if check(remaining)
        then repeat(remaining - 1)
        else Ashes.IO.panic("copy plan decision mismatch")

let benchmarkState cached =
    let named = SemNamed(77)("Measured")([])
    in
        let environment = emptyTypeEnvironment(Unit) with constructorFieldGroups = [ConstructorFieldGroup(groupSymbolId = 77, groupTypeName = "Measured", groupIsGeneric = false, groupFields = [("A", [SemString, SemList(SemInt)])], groupGenericFields = [])]
        in
            let full = classifyHeapLayout(named)(environment)
            in
                CoreLoweringState(environment = environment, facts = (if cached then Ashes.Collection.Map.setStr("Measured")(full)(Ashes.Collection.Map.empty) else Ashes.Collection.Map.empty), resolvedOverride = None, layouts = [CoreConstructorLayout(name = "A", tag = 0, tagless = false, size = 24)], recorded = None, recursiveCopy = false, admissible = true, emittedPlan = None)

let benchmarkToken (plan: Maybe(ArgumentCopyPlan)) =
    match plan with
        | None -> 0
        | Some(ScalarArgumentCopy) -> 1
        | Some(LeafArgumentCopy(_size)) -> 2
        | Some(ListHeadArgumentCopy(_kind)) -> 3
        | Some(ListDeepArgumentCopy(_child)) -> 4
        | Some(TupleArgumentCopy(_children)) -> 5
        | Some(ShallowAdtArgumentCopy(_size)) -> 6
        | Some(ConstructorArgumentCopy(_layout)) -> 7
        | Some(SwitchArgumentCopy(_layouts)) -> 8
        | Some(NormalizerArgumentCopy(_type)) -> 9

let recursive benchmarkOld remaining state checksum =
    if remaining == 0
    then Ashes.IO.print(Ashes.Text.fromInt(checksum))
    else
        benchmarkOld(remaining - 1)(state)(checksum + benchmarkToken(old_copyPlanOf(false)(SemNamed(77)("Measured")([]))(state)))

let recursive benchmarkNew remaining state checksum =
    if remaining == 0
    then Ashes.IO.print(Ashes.Text.fromInt(checksum))
    else
        benchmarkNew(remaining - 1)(state)(checksum + benchmarkToken(copyPlanOf(false)(SemNamed(77)("Measured")([]))(state)))

match Ashes.IO.args with
    | "old-cache" :: [] -> benchmarkOld(100000)(benchmarkState(true))(0)
    | "new-cache" :: [] -> benchmarkNew(100000)(benchmarkState(true))(0)
    | "old-miss" :: [] -> benchmarkOld(10000)(benchmarkState(false))(0)
    | "new-miss" :: [] -> benchmarkNew(10000)(benchmarkState(false))(0)
    | "check" :: [] -> repeat(192)
    | "1000" :: [] -> repeat(1000)
    | "10000" :: [] -> repeat(10000)
    | _ -> Ashes.IO.panic("mode")
