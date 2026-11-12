let seed n = CoreLoweringState(substitution = Ashes.Collection.Map.setInt(n)(SemVariable(n + 1))(Ashes.Collection.Map.empty), functionState = CoreFunctionState(admissibilityAnswers = [(SemVariable(n), false)], pendingTraitGoals = [], traitGoalsPinned = false, active = false), programState = CoreProgramState(provenTraitGoals = Ashes.Collection.Map.empty), currentSpan = Some(TextSpan(start = n, end = n + 1)), nextTemp = 8)

let input semanticType state = LoweredCoreValue(state = state, temp = 7, semanticType = semanticType, error = None)

let freshBinding n =
    let variable = SemVariable(n)
    in
        let entered = seed(n)
        in
            match finishOrDeferCoreLogicalNot(noLower)(input(variable)(entered)) with
                | LoweredCoreValue { state = deferred, semanticType = resultType, error = None } ->
                    if resultType != SemVariable(n + 1) || resolveType(deferred)(variable) != SemVariable(n + 1)
                    then false
                    else
                        match bindType(SemBool)(variable)(deferred) with
                            | (bound, None) ->
                                let proofs = traitGoalMap(resolvedTraitGoals(statePendingTraitGoals(bound))(bound)([]))(Ashes.Collection.Map.empty)
                                in
                                    let pinned = entered |> withStateProvenTraitGoals(proofs) |> withStateTraitGoalsPinned(true)
                                    in
                                        match finishOrDeferCoreLogicalNot(noLower)(input(variable)(pinned)) with
                                            | LoweredCoreValue { state = finished, semanticType = SemBool, error = None } ->
                                                let group = finished.functionState
                                                in resolveType(finished)(variable) == SemBool && group.admissibilityAnswers == [] && group.pendingTraitGoals == [] && resolveType(entered)(variable) == SemVariable(n + 1)
                                            | _ -> false
                            | _ -> false
                | _ -> false

let checkSpecial unit =
    let entered = seed(10) |> withStateTraitGoalsPinned(true)
    in
        match finishOrDeferCoreLogicalNot(noLower)(input(SemVariable(10))(entered)) with
            | LoweredCoreValue { state = unchanged, error = Some(CoreOperatorTypeMismatch("not", SemVariable(11), SemVariable(11))) } ->
                let group = unchanged.functionState
                in
                    if resolveType(unchanged)(SemVariable(10)) != SemVariable(11)
                    then Ashes.IO.panic("abstract Not became Bool")
                    else
                        match finishOrDeferCoreLogicalNot(noLower)(input(SemVariable(10))((unchanged with functionState = (group with active = true)))) with
                            | LoweredCoreValue { semanticType = SemVariable(11), error = None } ->
                                match finishOrDeferCoreLogicalNot(noLower)(input(SemInt)(entered)) with
                                    | LoweredCoreValue { error = Some(CoreOperatorTypeMismatch("not", SemInt, SemInt)) } -> Unit
                                    | _ -> Ashes.IO.panic("invalid Int accepted")
                            | _ -> Ashes.IO.panic("active generic evidence lost")
            | _ -> Ashes.IO.panic("pinned abstract Not did not fail closed")

let recursive rounds n =
    if n == 0
    then Ashes.IO.print("bindings passed")
    else
        if freshBinding(n * 2)
        then rounds(n - 1)
        else Ashes.IO.panic("logical-not goal changed state incorrectly")

Unit |> checkSpecial |> (given (_) ->
    match Ashes.IO.args with
        | "1000" :: [] -> rounds(1000)
        | "10000" :: [] -> rounds(10000)
        | _ -> Ashes.IO.panic("expected 1000/10000"))
