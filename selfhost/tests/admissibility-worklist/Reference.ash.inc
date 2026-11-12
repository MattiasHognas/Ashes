let recursive referenceOriginalGeneralRcAdmissible (semanticType: SemanticType) (path: List(SemanticType)) (state: CoreLoweringState) =
    match resolveType(state)(semanticType) with
        | SemString -> true
        | SemBytes -> true
        | SemBigInt -> true
        | SemList(element) -> referenceOriginalGeneralRcAdmissible(element)(path)(state)
        | SemTuple(elements) -> referenceOriginalAllGeneralRcAdmissible(elements)(path)(state)
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
                            match (state |> stateConstructorsOfType(name), heapAdmissibilityOf(named)(state)) with
                                | ([], _facts) -> false
                                | (_layouts, HeapAdmissibilityFacts { containsResource = true }) -> false
                                | (_layouts, HeapAdmissibilityFacts { containsUnresolvedType = true }) -> false
                                | (_layouts, HeapAdmissibilityFacts { children = children }) -> referenceOriginalAllChildrenGeneralRcAdmissible(children)(named :: path)(state)
        | resolved -> resultSurvivesReset(resolved)(state)
and referenceOriginalAllGeneralRcAdmissible (types: List(SemanticType)) (path: List(SemanticType)) (state: CoreLoweringState) =
    match types with
        | [] -> true
        | semanticType :: rest -> referenceOriginalGeneralRcAdmissible(semanticType)(path)(state) && referenceOriginalAllGeneralRcAdmissible(rest)(path)(state)
and referenceOriginalAllChildrenGeneralRcAdmissible (children: List((SemanticType, HeapChildDropKind))) (path: List(SemanticType)) (state: CoreLoweringState) =
    match children with
        | [] -> true
        | (_childType, DropClosure) :: _rest -> false
        | (_childType, UnsupportedChildDrop) :: _rest -> false
        | (childType, _dropKind) :: rest -> referenceOriginalGeneralRcAdmissible(childType)(path)(state) && referenceOriginalAllChildrenGeneralRcAdmissible(rest)(path)(state)

