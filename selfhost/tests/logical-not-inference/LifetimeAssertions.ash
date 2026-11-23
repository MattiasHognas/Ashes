let seed n = CoreLoweringState(substitution = Ashes.Collection.Map.setInt(n)(SemVariable(n + 1))(Ashes.Collection.Map.empty), functionState = CoreFunctionState(admissibilityAnswers = [(SemVariable(n), false)], pendingTraitGoals = [], traitGoalsPinned = false, active = false, activeTraitMethods = []), programState = CoreProgramState(provenTraitGoals = Ashes.Collection.Map.empty), currentSpan = Some(TextSpan(start = n, end = n + 1)), nextTemp = 8)

let input semanticType state = LoweredCoreValue(state = state, temp = 7, semanticType = semanticType, error = None)

let freshBinding n =
    (let variable = SemVariable(n)
    in
        let entered = seed(n)
        in
            match entered
            |> input(variable)
            |> finishOrDeferCoreLogicalNot(noLower) with
                | LoweredCoreValue { state = deferred, semanticType = resultType, error = None } ->
                    if resultType != SemVariable(n + 1) || resolveType(deferred)(variable) != SemVariable(n + 1)
                    then false
                    else
                        match bindType(SemBool)(variable)(deferred) with
                            | (bound, None) ->
                                let proofs =
                                    match traitGoalMap(resolvedTraitGoals(statePendingTraitGoals(bound))(bound)(Ashes.Collection.Map.empty))([]) with
                                        | (shared, _ordered) -> shared
                                in
                                    let pinned =
                                        entered
                                        |> withStateProvenTraitGoals(proofs)
                                        |> withStateTraitGoalsPinned(true)
                                    in
                                        match pinned
                                        |> input(variable)
                                        |> finishOrDeferCoreLogicalNot(noLower) with
                                            | LoweredCoreValue { state = finished, semanticType = SemBool, error = None } ->
                                                let group = finished.functionState
                                                in resolveType(finished)(variable) == SemBool && group.admissibilityAnswers == [] && group.pendingTraitGoals == [] && resolveType(entered)(variable) == SemVariable(n + 1)
                                            | _ -> false
                            | _ -> false
                | _ -> false)

// Replaying a site lowered at two types consumes its proofs in lowering order.
// The open occurrence between them must advance without borrowing its neighbor's type.
let checkOrderedProofs unit =
    (let entered = seed(10)
    in
        match traitGoalKey("Eq")("equal")(entered) with
            | None -> Ashes.IO.panic("test site has no span")
            | Some(key) ->
                match traitGoalMap(resolvedTraitGoals([(key, SemString, []), (key, SemVariable(42), []), (key, SemInt, [])])(entered)(Ashes.Collection.Map.empty))([]) with
                    | (proofs, true) ->
                        match entered
                        |> withStateProvenTraitGoals(proofs)
                        |> pinProvenTraitGoal("Eq")("equal")(SemVariable(20)) with
                            | (first, SemInt) ->
                                match pinProvenTraitGoal("Eq")("equal")(SemVariable(21))(first) with
                                    | (second, SemVariable(21)) ->
                                        match pinProvenTraitGoal("Eq")("equal")(SemVariable(22))(second) with
                                            | (_third, SemString) -> Unit
                                            | _ -> Ashes.IO.panic("last occurrence lost its proof")
                                    | _ -> Ashes.IO.panic("unsettled occurrence borrowed another proof")
                            | _ -> Ashes.IO.panic("first occurrence lost its proof")
                    | _ -> Ashes.IO.panic("conflicting proofs were shared"))

// The same source site is visited in two frames. Only the first visit settled
// against a lexical method; replay has fresh type variables for that frame.
let checkAmbientProof unit =
    (let discovery = seed(10)
    in
        match traitGoalKey("Seed")("first")(discovery) with
            | None -> Ashes.IO.panic("missing ambient goal site")
            | Some(key) ->
                let pending = [(key, SemVariable(42), []), (key, SemVariable(10), [("dictionary", SemVariable(11))])]
                in
                    match traitGoalMap(resolvedTraitGoals(pending)(discovery)(Ashes.Collection.Map.empty))([]) with
                        | (proofs, true) ->
                            let frame = discovery.functionState
                            in
                                let replay = discovery with functionState = (frame with activeTraitMethods = [("Seed", SemVariable(80), "first", "dictionary")])
                                in
                                    match replay
                                    |> withStateProvenTraitGoals(proofs)
                                    |> pinProvenTraitGoal("Seed")("first")(SemVariable(81)) with
                                        | (first, SemVariable(_id)) ->
                                            if resolveType(first)(SemVariable(81)) != resolveType(first)(SemVariable(80))
                                            then Ashes.IO.panic("ambient proof lost replay dictionary")
                                            else
                                                match pinProvenTraitGoal("Seed")("first")(SemVariable(82))(first) with
                                                    | (_unchanged, SemVariable(82)) -> Unit
                                                    | _ -> Ashes.IO.panic("an unresolved neighbor borrowed an ambient proof")
                                        | _ -> Ashes.IO.panic("ambient proof became concrete")
                        | _ -> Ashes.IO.panic("ambient proof did not keep visit order"))

let checkSpecial unit =
    (let entered =
        10
        |> seed
        |> withStateTraitGoalsPinned(true)
    in
        match entered
        |> input(SemVariable(10))
        |> finishOrDeferCoreLogicalNot(noLower) with
            | LoweredCoreValue { state = unchanged, error = Some(CoreOperatorTypeMismatch("not", SemVariable(11), SemVariable(11))) } ->
                let group = unchanged.functionState
                in
                    if resolveType(unchanged)(SemVariable(10)) != SemVariable(11)
                    then Ashes.IO.panic("abstract Not became Bool")
                    else
                        match finishOrDeferCoreLogicalNot(noLower)(input(SemVariable(10))((unchanged with functionState = (group with active = true)))) with
                            | LoweredCoreValue { semanticType = SemVariable(11), error = None } ->
                                match entered
                                |> input(SemInt)
                                |> finishOrDeferCoreLogicalNot(noLower) with
                                    | LoweredCoreValue { error = Some(CoreOperatorTypeMismatch("not", SemInt, SemInt)) } -> Unit
                                    | _ -> Ashes.IO.panic("invalid Int accepted")
                            | _ -> Ashes.IO.panic("active generic evidence lost")
            | _ -> Ashes.IO.panic("pinned abstract Not did not fail closed"))

let recursive rounds n =
    if n == 0
    then Ashes.IO.print("bindings passed")
    else
        if freshBinding(n * 2)
        then rounds(n - 1)
        else Ashes.IO.panic("logical-not goal changed state incorrectly")

Unit
|> checkSpecial
|> checkOrderedProofs
|> checkAmbientProof
|> (given (_) ->
    match Ashes.IO.args with
        | "1000" :: [] -> rounds(1000)
        | "10000" :: [] -> rounds(10000)
        | _ -> Ashes.IO.panic("expected 1000/10000"))
