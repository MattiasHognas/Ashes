let normalizerPlansAgree (named: SemanticType) (state: CoreLoweringState) =
    match (old_emitNormalizerAdtCopy(73)(named)(state), emitNormalizerAdtCopy(73)(named)(state)) with
        | ((oldState, oldTemp), (newState, newTemp)) ->
            oldTemp == newTemp && sameMaybePlan(oldState.emittedPlan)(newState.emittedPlan)
