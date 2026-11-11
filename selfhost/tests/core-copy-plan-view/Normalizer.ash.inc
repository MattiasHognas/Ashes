// Emission stubs preserve the exact plans delivered by the actual production caller.
let emitConstructorDeepCopy sourceTemp plan (state: CoreLoweringState) =
    ((state with emittedPlan = Some(ConstructorArgumentCopy(plan))), sourceTemp)
let emitAdtSwitchDeepCopyWith (ctorLabel: Str) (endLabel: Str) sourceTemp plans (state: CoreLoweringState) =
    if ctorLabel == "rcnorm_ctor" && endLabel == "rcnorm_end"
    then ((state with emittedPlan = Some(SwitchArgumentCopy(plans))), sourceTemp)
    else Ashes.IO.panic("normalizer labels changed")

let old_emitNormalizerAdtCopy (sourceTemp: Int) (named: SemanticType) (state: CoreLoweringState) =
    match named with
        | SemNamed(_symbolId, name, _arguments) ->
            match old_constructorCopyPlansOfWith(true)(state |> stateConstructorsOfType(name))(state |> heapChildrenOfNamed(named))(state) with
                | Some(single :: []) -> emitConstructorDeepCopy(sourceTemp)(single)(state)
                | Some(_first :: _rest as plans) -> emitAdtSwitchDeepCopyWith("rcnorm_ctor")("rcnorm_end")(sourceTemp)(plans)(state)
                | _ -> (state, sourceTemp)
        | _ -> (state, sourceTemp)
