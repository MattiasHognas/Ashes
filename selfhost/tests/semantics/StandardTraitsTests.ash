import Ashes.Test as test
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TraitResolution
import AshesCompiler.Semantics.StandardTraits
let expectStandardTraitDeclarations unit =
    (let environment = standardTraitEnvironment(Unit)
    in
        let eqChecked =
            match resolveTraitBinding("Eq")(environment) with
                | Some(TraitInferenceDefinition { parameterCount = 1, methods = TraitMethodInferenceDefinition { name = "equal", defaultImplementation = None } :: TraitMethodInferenceDefinition { name = "notEqual", defaultImplementation = Some(_default) } :: [], supertraits = [] }) -> Unit
                | _ -> test.fail("the standard Eq ABI should include equal and defaulted notEqual")
        in
            ((given (_) ->
                match resolveTraitBinding("Ord")(environment) with
                    | Some(TraitInferenceDefinition { parameterCount = 1, methods = TraitMethodInferenceDefinition { name = "compare" } :: TraitMethodInferenceDefinition { name = "less" } :: TraitMethodInferenceDefinition { name = "lessOrEqual" } :: TraitMethodInferenceDefinition { name = "greater" } :: TraitMethodInferenceDefinition { name = "greaterOrEqual" } :: [], supertraits = TraitConstraint { traitName = "Eq" } :: [] }) -> Unit
                    | _ -> test.fail("the standard Ord ABI should retain Eq and all shipped methods")))(eqChecked))

let expectStandardResolved constraint environment =
    match resolveTraitEvidence(constraint)(environment) with
        | TraitEvidenceResolution { plan = Some(plan), error = None } -> plan
        | _ -> test.fail("standard trait evidence should resolve")

let expectPrimitiveImplementationMatrix unit =
    (let environment = standardTraitEnvironment(Unit)
    in
        let eqBool = expectStandardResolved(TraitConstraint(traitName = "Eq", typeArguments = [SemBool]))(environment)
        in
            let ordRune =
                expectStandardResolved(
                    TraitConstraint(traitName = "Ord", typeArguments = [SemRune]),
                    environment
                )
            in
                let addText =
                    expectStandardResolved(
                        TraitConstraint(traitName = "Add", typeArguments = [SemString]),
                        environment
                    )
                in
                    let defaultU64 =
                        expectStandardResolved(
                            TraitConstraint(traitName = "Default", typeArguments = [SemUInt(64)]),
                            environment
                        )
                    in
                        match (eqBool, ordRune, addText, defaultU64) with
                            | (TraitEvidenceInstance(_eq, _eqImplementation, [], []), TraitEvidenceInstance(_ord, _ordImplementation, [], TraitEvidenceInstance(TraitConstraint { traitName = "Eq", typeArguments = SemRune :: [] }, _eqRuneImplementation, [], []) :: []), TraitEvidenceInstance(_add, _addImplementation, [], []), TraitEvidenceInstance(_default, _defaultImplementation, [], [])) -> Unit
                            | _ ->
                                test.fail(
                                    "the shipped primitive implementation matrix should resolve with Ord supertrait evidence"
                                ))

let expectNestedStructuralEvidence unit =
    (let environment = standardTraitEnvironment(Unit)
    in
        let maybeInt = SemNamed(1)("Maybe")([SemInt])
        in
            match expectStandardResolved(
                TraitConstraint(traitName = "Show", typeArguments = [SemList(maybeInt)]),
                environment
            ) with
                | TraitEvidenceInstance(TraitConstraint { traitName = "Show", typeArguments = SemList(SemNamed(_maybeId, "Maybe", SemInt :: [])) :: [] }, _listImplementation, TraitEvidenceInstance(TraitConstraint { traitName = "Show", typeArguments = SemNamed(_innerMaybeId, "Maybe", SemInt :: []) :: [] }, _maybeImplementation, TraitEvidenceInstance(TraitConstraint { traitName = "Show", typeArguments = SemInt :: [] }, _intImplementation, [], []) :: [], []) :: [], []) -> Unit
                | _ ->
                    test.fail(
                        "structural evidence should recurse through List, Maybe, and primitive implementations"
                    ))

let expectResultAndTupleEvidence unit =
    (let environment = standardTraitEnvironment(Unit)
    in
        let resultType = SemNamed(2)("Result")([SemString, SemInt])
        in
            let resultChecked =
                match expectStandardResolved(
                    TraitConstraint(traitName = "Ord", typeArguments = [resultType]),
                    environment
                ) with
                    | TraitEvidenceInstance(_resultConstraint, _resultImplementation, _requirements, TraitEvidenceInstance(TraitConstraint { traitName = "Eq" }, _eqResultImplementation, _eqRequirements, []) :: []) -> Unit
                    | _ -> test.fail("Result Ord evidence should retain recursively resolved Eq supertrait evidence")
            in
                ((given (_) ->
                    match expectStandardResolved(
                        TraitConstraint(traitName = "Default", typeArguments = [SemTuple([SemInt, SemString])]),
                        environment
                    ) with
                        | TraitEvidenceInstance(_tupleConstraint, _tupleImplementation, TraitEvidenceInstance(TraitConstraint { traitName = "Default", typeArguments = SemInt :: [] }, _intImplementation, [], []) :: TraitEvidenceInstance(TraitConstraint { traitName = "Default", typeArguments = SemString :: [] }, _stringImplementation, [], []) :: [], []) -> Unit
                        | _ ->
                            test.fail(
                                "tuple Default evidence should retain both field requirements"
                            )))(resultChecked))

let rejectUnshippedPrimitiveImplementations unit =
    (let environment = standardTraitEnvironment(Unit)
    in
        let ordBoolRejected =
            match resolveTraitEvidence(TraitConstraint(traitName = "Ord", typeArguments = [SemBool]))(environment) with
                | TraitEvidenceResolution { plan = None, error = Some(MissingTraitImplementation(TraitConstraint { traitName = "Ord", typeArguments = SemBool :: [] }, _trace)) } -> Unit
                | _ -> test.fail("Ord(Bool) is not part of the shipped implementation matrix")
        in
            ((given (_) ->
                match resolveTraitEvidence(
                    TraitConstraint(traitName = "Remainder", typeArguments = [SemFloat]),
                    environment
                ) with
                    | TraitEvidenceResolution { plan = None, error = Some(MissingTraitImplementation(TraitConstraint { traitName = "Remainder", typeArguments = SemFloat :: [] }, _trace)) } -> Unit
                    | _ ->
                        test.fail(
                            "Remainder(Float) is not part of the shipped implementation matrix"
                        )))(ordBoolRejected))

let expectStableImplementationBindingNames unit =
    SemList(SemParameter(2000)("a"))
    |> standardTraitImplementationBindingName("Show")("show")
    |> test.assertEqual("__ashes_standard_trait_Show_show_list_parameter2000")

let runStandardTraitsTests unit =
    unit
    |> expectStandardTraitDeclarations
    |> expectPrimitiveImplementationMatrix
    |> expectNestedStructuralEvidence
    |> expectResultAndTupleEvidence
    |> rejectUnshippedPrimitiveImplementations
    |> expectStableImplementationBindingNames
    |> (given (_) -> Ashes.IO.print("all self-hosted standard trait environment tests passed"))
