import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.TraitResolution
import AshesCompiler.Semantics.TypeInference
let equalMethod =
    TraitMethodDecl(name = "equal", signature = TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None))([])(None), defaultImplementation = None)

let eqDeclaration = TraitDecl(name = "Equal", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [equalMethod])

let ordDeclaration =
    TraitDecl(name = "Ordered", typeParameters = [TypeParameter(name = "a")], supertraits = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeNamed("a")])], methods = [TraitMethodDecl(name = "compare", signature = TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("a"))(TypeNamed("Int"))([])(None))([])(None), defaultImplementation = None)])

let recursive containsConstraint name constraints =
    match constraints with
        | [] -> false
        | TraitConstraint { traitName = candidateName, typeArguments = _typeArguments } :: tail ->
            if name == candidateName
            then true
            else containsConstraint(name)(tail)

let expectTraitRegistration unit =
    match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration)], body = "equal"
    |> ExprQualifiedVar("Equal")
    |> Some)) with
        | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = environment, error = None } ->
            let methodTypeChecked =
                match applySubstitution(substitution)(semanticType) with
                    | SemFunction(SemVariable(leftId), SemFunction(SemVariable(rightId), SemBool, None), None) -> test.assertEqual(leftId)(rightId)
                    | other -> test.fail("trait method should retain its declared type: " + formatSemanticType(other))
            in
                match resolveTraitBinding("Equal")(environment) with
                    | Some(TraitInferenceDefinition { name = "Equal", parameterCount = 1, methods = methods, supertraits = [] }) ->
                        match methods with
                            | TraitMethodInferenceDefinition { name = "equal", scheme = scheme, defaultImplementation = None } :: [] ->
                                match scheme with
                                    | TypeScheme { quantified = _quantified, body = _body, constraints = constraints } ->
                                        if containsConstraint("Equal")(constraints)
                                        then Unit
                                        else test.fail("trait methods should require their declaring trait")
                            | _ -> test.fail("Equal should register its method")
                    | _ -> test.fail("Equal should be registered")
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("trait declaration should infer: " + Ashes.Trait.Show.show(error))

let expectForwardSupertraitRegistration unit =
    match inferProgram(ProgramSyntax(items = [TopLevelTrait(ordDeclaration), TopLevelTrait(eqDeclaration)], body = None)) with
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = environment, error = None } ->
            match resolveTraitBinding("Ordered")(environment) with
                | Some(TraitInferenceDefinition { name = "Ordered", parameterCount = 1, methods = _methods, supertraits = constraints }) ->
                    if containsConstraint("Equal")(constraints)
                    then Unit
                    else test.fail("Ordered should retain its Equal supertrait")
                | _ -> test.fail("Ordered should be registered before its later-declared supertrait")
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("forward supertraits should register: " + Ashes.Trait.Show.show(error))

let expectInvalidTraitDeclarations unit =
    (let duplicateChecked =
        match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelTrait(eqDeclaration)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(DuplicateTraitDeclaration("Equal")) } -> Unit
            | _ -> test.fail("duplicate trait declarations should be rejected")
    in
        let emptyParameters = TraitDecl(name = "Empty", typeParameters = [], supertraits = [], methods = [TraitMethodDecl(name = "value", signature = TypeNamed("Int"), defaultImplementation = None)])
        in
            let emptyParametersChecked =
                match inferProgram(ProgramSyntax(items = [TopLevelTrait(emptyParameters)], body = None)) with
                    | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(TraitRequiresTypeParameter("Empty")) } -> Unit
                    | _ -> test.fail("traits should require a type parameter")
            in
                let duplicateParameters = TraitDecl(name = "Duplicate", typeParameters = [TypeParameter(name = "a"), TypeParameter(name = "a")], supertraits = [], methods = [equalMethod])
                in
                    let duplicateParametersChecked =
                        match inferProgram(ProgramSyntax(items = [TopLevelTrait(duplicateParameters)], body = None)) with
                            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(DuplicateTraitTypeParameter("Duplicate", "a")) } -> Unit
                            | _ -> test.fail("duplicate trait parameters should be rejected")
                    in
                        let duplicateMethods = TraitDecl(name = "Duplicate", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [equalMethod, equalMethod])
                        in
                            let duplicateMethodsChecked =
                                match inferProgram(ProgramSyntax(items = [TopLevelTrait(duplicateMethods)], body = None)) with
                                    | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(DuplicateTraitMethod("Duplicate", "equal")) } -> Unit
                                    | _ -> test.fail("duplicate trait methods should be rejected")
                            in
                                let detachedMethod = TraitDecl(name = "Detached", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [TraitMethodDecl(name = "value", signature = TypeNamed("Int"), defaultImplementation = None)])
                                in
                                    match inferProgram(ProgramSyntax(items = [TopLevelTrait(detachedMethod)], body = None)) with
                                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(TraitMethodMustMentionParameter("Detached", "value")) } -> Unit
                                        | _ -> test.fail("trait methods should mention a trait parameter"))

let expectInvalidSupertraits unit =
    (let unknown = TraitDecl(name = "Ordered", typeParameters = [TypeParameter(name = "a")], supertraits = [TraitConstraintSyntax(traitName = "Missing", typeArguments = [TypeNamed("a")])], methods = [equalMethod])
    in
        let unknownChecked =
            match inferProgram(ProgramSyntax(items = [TopLevelTrait(unknown)], body = None)) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(UnknownSupertrait("Ordered", "Missing")) } -> Unit
                | _ -> test.fail("unknown supertraits should be rejected")
        in
            let badArity = TraitDecl(name = "Ordered", typeParameters = [TypeParameter(name = "a")], supertraits = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [])], methods = [equalMethod])
            in
                let arityChecked =
                    match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelTrait(badArity)], body = None)) with
                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(SupertraitArityMismatch("Equal", 1, 0)) } -> Unit
                        | _ -> test.fail("supertrait arity should be checked")
                in
                    let first = TraitDecl(name = "First", typeParameters = [TypeParameter(name = "a")], supertraits = [TraitConstraintSyntax(traitName = "Second", typeArguments = [TypeNamed("a")])], methods = [equalMethod])
                    in
                        let second = TraitDecl(name = "Second", typeParameters = [TypeParameter(name = "a")], supertraits = [TraitConstraintSyntax(traitName = "First", typeArguments = [TypeNamed("a")])], methods = [equalMethod])
                        in
                            match inferProgram(ProgramSyntax(items = [TopLevelTrait(first), TopLevelTrait(second)], body = None)) with
                                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(CyclicSupertraitRequirements(_trace)) } -> Unit
                                | _ -> test.fail("supertrait cycles should be rejected"))

let expectDefaultMethodValidation unit =
    (let forwardedEqual =
        ExprLambda("left")(ExprLambda("right")(ExprCall(ExprCall(ExprQualifiedVar("Equal")("equal"))(ExprVar("left"))(false))(ExprVar("right"))(false))(None))(None)
    in
        let valid =
            TraitDecl(name = "Equal", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [equalMethod, TraitMethodDecl(name = "same", signature = TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None))([])(None), defaultImplementation = Some(forwardedEqual))])
        in
            let validChecked =
                match inferProgram(ProgramSyntax(items = [TopLevelTrait(valid)], body = None)) with
                    | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = None } -> Unit
                    | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("well-typed defaults should infer: " + Ashes.Trait.Show.show(error))
            in
                let invalid =
                    TraitDecl(name = "Rendered", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [TraitMethodDecl(name = "render", signature = TypeArrow(TypeNamed("a"))(TypeNamed("Str"))([])(None), defaultImplementation = None
                    |> ExprLambda("value")(ExprInt(42))
                    |> Some)])
                in
                    match inferProgram(ProgramSyntax(items = [TopLevelTrait(invalid)], body = None)) with
                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InferenceUnificationError(TypeMismatch(_left, _right)))) } -> Unit
                        | _ -> test.fail("default implementations should match their method signatures"))

let choiceMethod name dependency =
    TraitMethodDecl(name = name, signature = TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None), defaultImplementation = None
    |> ExprLambda("value")(ExprCall(ExprQualifiedVar("Choice")(dependency))(ExprVar("value"))(false))
    |> Some)

let choiceBaseMethod = TraitMethodDecl(name = "base", signature = TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None), defaultImplementation = None)

let choiceImplementation = ExprLambda("value")(ExprBool(true))(None)

let rejectsDefaultMethodDependencyCycle unit =
    (let choice = TraitDecl(name = "Choice", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [choiceMethod("first")("second"), choiceMethod("second")("first"), choiceBaseMethod])
    in
        let implementation = TraitImplementationDecl(traitName = "Choice", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "base", implementation = choiceImplementation)])
        in
            match inferProgram(ProgramSyntax(items = [TopLevelTrait(choice), TopLevelImplementation(implementation)], body = None)) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(DefaultTraitMethodDependencyCycle("Choice", "first")) } -> Unit
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("unexpected default cycle result: " + Ashes.Trait.Show.show(error))
                | _ -> test.fail("active trait defaults should not form a dependency cycle"))

let rejectsSelfDependentDefaultMethod unit =
    (let recursiveChoice = TraitDecl(name = "Choice", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [choiceMethod("first")("first"), choiceBaseMethod])
    in
        let implementation = TraitImplementationDecl(traitName = "Choice", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "base", implementation = choiceImplementation)])
        in
            match inferProgram(ProgramSyntax(items = [TopLevelTrait(recursiveChoice), TopLevelImplementation(implementation)], body = None)) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(DefaultTraitMethodDependencyCycle("Choice", "first")) } -> Unit
                | _ -> test.fail("a selected trait default should not depend on itself"))

let acceptsOverrideBreakingDefaultMethodCycle unit =
    (let choice = TraitDecl(name = "Choice", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [choiceMethod("first")("second"), choiceMethod("second")("first")])
    in
        let implementation = TraitImplementationDecl(traitName = "Choice", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "first", implementation = choiceImplementation)])
        in
            match inferProgram(ProgramSyntax(items = [TopLevelTrait(choice), TopLevelImplementation(implementation)], body = None)) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = None } -> Unit
                | _ -> test.fail("an implementation override should break a chain of trait defaults"))

let expectDefaultMethodDependencyCycles unit =
    unit
    |> rejectsDefaultMethodDependencyCycle
    |> rejectsSelfDependentDefaultMethod
    |> acceptsOverrideBreakingDefaultMethodCycle

let trueEqualImplementation =
    ExprLambda("left")(ExprLambda("right")(ExprBool(true))(None))(None)

let genericEqualImplementation = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeApplied("List")([TypeNamed("a")])], requirements = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeNamed("a")])], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])

let expectTraitImplementationRegistration unit =
    match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(genericEqualImplementation)], body = None)) with
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = environment, error = None } ->
            match resolveTraitImplementations("Equal")(environment) with
                | TraitImplementationInferenceDefinition { traitName = "Equal", typeArguments = typeArguments, requirements = requirements, methods = methods } :: [] ->
                    let headChecked =
                        match typeArguments with
                            | SemList(SemParameter(_parameterId, "a")) :: [] -> Unit
                            | _ -> test.fail("generic implementation head should use a rigid parameter")
                    in
                        let requirementChecked =
                            match requirements with
                                | TraitConstraint { traitName = "Equal", typeArguments = SemParameter(_requirementId, "a") :: [] } :: [] -> Unit
                                | _ -> test.fail("generic implementation should retain its requirement")
                        in
                            match methods with
                                | TraitImplementationMethodInferenceDefinition { name = "equal", implementation = _implementation, semanticType = methodType } :: [] ->
                                    match methodType with
                                        | SemFunction(SemList(SemParameter(_leftId, "a")), SemFunction(SemList(SemParameter(_rightId, "a")), SemBool, None), None) -> Unit
                                        | _ -> test.fail("implementation method should use the specialized trait signature")
                                | _ -> test.fail("generic implementation should retain its supplied method")
                | _implementations -> test.fail("generic implementation should register its rigid head, requirement, and specialized method")
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("generic implementation should infer: " + Ashes.Trait.Show.show(error))

let expectTraitImplementationDefaults unit =
    (let defaultedMethod =
        TraitMethodDecl(name = "same", signature = TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None))([])(None), defaultImplementation = Some(trueEqualImplementation))
    in
        let defaultedTrait =
            TraitDecl(name = "Comparable", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [TraitMethodDecl(name = "equal", signature = TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None))([])(None), defaultImplementation = None), defaultedMethod])
        in
            let inherited = TraitImplementationDecl(traitName = "Comparable", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
            in
                let overridden = TraitImplementationDecl(traitName = "Comparable", typeArguments = [TypeNamed("Str")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation), TraitImplementationMethodBinding(methodName = "same", implementation = trueEqualImplementation)])
                in
                    match inferProgram(ProgramSyntax(items = [TopLevelTrait(defaultedTrait), TopLevelImplementation(inherited), TopLevelImplementation(overridden)], body = None)) with
                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = environment, error = None } ->
                            match resolveTraitImplementations("Comparable")(environment) with
                                | TraitImplementationInferenceDefinition { traitName = "Comparable", typeArguments = SemString :: [], requirements = [], methods = _overriddenMethods } :: tail ->
                                    match tail with
                                        | TraitImplementationInferenceDefinition { traitName = "Comparable", typeArguments = SemInt :: [], requirements = [], methods = _inheritedEqual :: [] } :: [] -> Unit
                                        | _implementations -> test.fail("default methods should be optional and overridable")
                                | _implementations -> test.fail("default methods should be optional and overridable")
                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("defaulted implementations should infer: " + Ashes.Trait.Show.show(error)))

let rejectUnknownTraitImplementation unit =
    (let implementation = TraitImplementationDecl(traitName = "Missing", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelImplementation(implementation)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(UnknownTraitImplementation("Missing")) } -> Unit
            | _ -> test.fail("implementations of unknown traits should be rejected"))

let rejectTraitImplementationArity unit =
    (let implementation = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("Int"), TypeNamed("Str")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(implementation)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(TraitImplementationArityMismatch("Equal", 1, 2)) } -> Unit
            | _ -> test.fail("implementation arity should be checked"))

let rejectUnknownTraitImplementationMethod unit =
    (let implementation = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "other", implementation = trueEqualImplementation)])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(implementation)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(UnknownTraitImplementationMethod("Equal", "other")) } -> Unit
            | _ -> test.fail("unknown implementation methods should be rejected"))

let rejectDuplicateTraitImplementationMethod unit =
    (let implementation = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation), TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(implementation)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(DuplicateTraitImplementationMethod("Equal", "equal")) } -> Unit
            | _ -> test.fail("duplicate implementation methods should be rejected"))

let rejectMissingTraitImplementationMethod unit =
    (let implementation = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(implementation)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(MissingTraitImplementationMethod("Equal", "equal")) } -> Unit
            | _ -> test.fail("required implementation methods should be checked"))

let rejectEscapedTraitImplementationRequirement unit =
    (let implementation = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeApplied("List")([TypeNamed("a")])], requirements = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeNamed("b")])], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(implementation)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(TraitImplementationRequirementVariableEscapes("Equal", "b")) } -> Unit
            | _ -> test.fail("implementation requirement variables should occur in the head"))

let expectInvalidTraitImplementations unit =
    unit
    |> rejectUnknownTraitImplementation
    |> rejectTraitImplementationArity
    |> rejectUnknownTraitImplementationMethod
    |> rejectDuplicateTraitImplementationMethod
    |> rejectMissingTraitImplementationMethod
    |> rejectEscapedTraitImplementationRequirement

let expectInvalidTraitImplementationRequirements unit =
    (let unknownRequirement = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeApplied("List")([TypeNamed("a")])], requirements = [TraitConstraintSyntax(traitName = "Missing", typeArguments = [TypeNamed("b")])], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
    in
        let unknownChecked =
            match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(unknownRequirement)], body = None)) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(UnknownTraitImplementationRequirement("Equal", "Missing")) } -> Unit
                | _ -> test.fail("unknown implementation requirement traits should be rejected before their variables")
        in
            let badArity = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("Int")], requirements = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [])], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
            in
                let arityChecked =
                    match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(badArity)], body = None)) with
                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(TraitImplementationRequirementArityMismatch("Equal", 1, 0)) } -> Unit
                        | _ -> test.fail("implementation requirement arity should be checked")
                in
                    let unorderedMethods = TraitDecl(name = "UnorderedMethods", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [TraitMethodDecl(name = "zeta", signature = TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None), defaultImplementation = None), TraitMethodDecl(name = "alpha", signature = TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None), defaultImplementation = None)])
                    in
                        let missingMethods = TraitImplementationDecl(traitName = "UnorderedMethods", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [])
                        in
                            match inferProgram(ProgramSyntax(items = [TopLevelTrait(unorderedMethods), TopLevelImplementation(missingMethods)], body = None)) with
                                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(MissingTraitImplementationMethod("UnorderedMethods", "alpha")) } -> Unit
                                | _ -> test.fail("missing implementation methods should be diagnosed in ordinal order"))

let rejectsExpandingTraitImplementationRequirement unit =
    (let expanding = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("a")], requirements = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeApplied("List")([TypeNamed("a")])])], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(expanding)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(NonDecreasingTraitImplementationRequirement("Equal", "Equal")) } -> Unit
            | _ -> test.fail("generic implementation requirements should be structurally smaller than their head"))

let rejectsUnchangedTraitImplementationRequirement unit =
    (let unchanged = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeApplied("List")([TypeNamed("a")])], requirements = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeApplied("List")([TypeNamed("a")])])], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(unchanged)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(NonDecreasingTraitImplementationRequirement("Equal", "Equal")) } -> Unit
            | _ -> test.fail("equal-sized generic implementation requirements should be rejected"))

let acceptsDecreasingTraitImplementationRequirement unit =
    (let decreasing = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeApplied("List")([TypeNamed("a")])], requirements = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeNamed("a")])], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(decreasing)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = None } -> Unit
            | _ -> test.fail("smaller generic implementation requirements should be accepted"))

let acceptsConcreteTraitImplementationRequirement unit =
    (let concrete = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("Int")], requirements = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeApplied("List")([TypeNamed("Int")])])], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(concrete)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = None } -> Unit
            | _ -> test.fail("fully concrete implementation heads should not require structural decrease"))

let expectTraitImplementationRequirementTermination unit =
    unit
    |> rejectsExpandingTraitImplementationRequirement
    |> rejectsUnchangedTraitImplementationRequirement
    |> acceptsDecreasingTraitImplementationRequirement
    |> acceptsConcreteTraitImplementationRequirement

let expectTraitConstraintCanonicalization unit =
    (let ranked = TraitDecl(name = "Ranked", typeParameters = [TypeParameter(name = "a")], supertraits = [TraitConstraintSyntax(traitName = "Ordered", typeArguments = [TypeNamed("a")])], methods = [TraitMethodDecl(name = "rank", signature = TypeArrow(TypeNamed("a"))(TypeNamed("Int"))([])(None), defaultImplementation = None)])
    in
        let redundant = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("Int")], requirements = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeNamed("Int")]), TraitConstraintSyntax(traitName = "Ordered", typeArguments = [TypeNamed("Int")]), TraitConstraintSyntax(traitName = "Ranked", typeArguments = [TypeNamed("Int")]), TraitConstraintSyntax(traitName = "Ranked", typeArguments = [TypeNamed("Int")])], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
        in
            match inferProgram(ProgramSyntax(items = [TopLevelTrait(ranked), TopLevelTrait(ordDeclaration), TopLevelTrait(eqDeclaration), TopLevelImplementation(redundant)], body = None)) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = environment, error = None } ->
                    match resolveTraitImplementations("Equal")(environment) with
                        | TraitImplementationInferenceDefinition { traitName = "Equal", typeArguments = _typeArguments, requirements = TraitConstraint { traitName = "Ranked", typeArguments = SemInt :: [] } :: [], methods = _methods } :: [] -> Unit
                        | _ -> test.fail("implementation requirements should be deduplicated and omit transitively implied supertraits")
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("canonical implementation requirements should infer: " + Ashes.Trait.Show.show(error)))

let recursive findBindingScheme name bindings =
    match bindings with
        | [] -> None
        | (candidateName, scheme) :: tail ->
            if name == candidateName
            then Some(scheme)
            else findBindingScheme(name)(tail)

let binaryTraitCall traitName methodName =
    ExprCall(ExprCall(ExprQualifiedVar(traitName)(methodName))(ExprVar("left"))(false))(ExprVar("right"))(false)

let expectInferredConstraintCanonicalization unit =
    (let body =
        ExprLet("ignored")(binaryTraitCall("Equal")("equal"))(binaryTraitCall("Ordered")("compare"))([])(None)([])
    in
        let value =
            ExprLambda("left")(ExprLambda("right")(body)(None))(None)
        in
            let binding = LetBindingSyntax(name = "ordered", value = value, sugarParameters = [], typeAnnotation = None, requirements = [])
            in
                match inferProgram(ProgramSyntax(items = [TopLevelTrait(ordDeclaration), TopLevelTrait(eqDeclaration), TopLevelLet(binding)(false)], body = None)) with
                    | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = TypeEnvironment { bindings = bindings }, error = None } ->
                        match findBindingScheme("ordered")(bindings) with
                            | Some(TypeScheme { quantified = _quantified, body = _body, constraints = TraitConstraint { traitName = "Ordered", typeArguments = _arguments } :: [] }) -> Unit
                            | _ -> test.fail("inferred schemes should omit supertraits implied by stronger constraints")
                    | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("canonical inferred constraints should infer: " + Ashes.Trait.Show.show(error)))

let showDeclaration = TraitDecl(name = "Show", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [TraitMethodDecl(name = "show", signature = TypeArrow(TypeNamed("a"))(TypeNamed("Str"))([])(None), defaultImplementation = None)])

let sameValue =
    ExprLambda("value")(ExprCall(ExprCall(ExprQualifiedVar("Equal")("equal"))(ExprVar("value"))(false))(ExprVar("value"))(false))(None)

let sameAnnotation = TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None)

let expectValidWrittenBindingRequirement unit =
    (let binding = LetBindingSyntax(name = "same", value = sameValue, sugarParameters = [], typeAnnotation = Some(sameAnnotation), requirements = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeNamed("a")])])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelLet(binding)(false)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = TypeEnvironment { bindings = bindings }, error = None } ->
                match findBindingScheme("same")(bindings) with
                    | Some(TypeScheme { quantified = _quantified, body = _body, constraints = TraitConstraint { traitName = "Equal", typeArguments = _arguments } :: [] }) -> Unit
                    | _ -> test.fail("a justified written requires clause should become the binding scheme")
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("a justified written requires clause should infer: " + Ashes.Trait.Show.show(error)))

let rejectsMissingWrittenBindingRequirement unit =
    (let binding = LetBindingSyntax(name = "bad", value = sameValue, sugarParameters = [], typeAnnotation = Some(sameAnnotation), requirements = [TraitConstraintSyntax(traitName = "Show", typeArguments = [TypeNamed("a")])])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelTrait(showDeclaration), TopLevelLet(binding)(false)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(MissingWrittenTraitRequirement("Equal"))) } -> Unit
            | _ -> test.fail("written requires clauses should cover inferred requirements"))

let rejectsUnjustifiedWrittenBindingRequirement unit =
    (let identity = ExprLambda("value")(ExprVar("value"))(None)
    in
        let binding =
            LetBindingSyntax(name = "identity", value = identity, sugarParameters = [], typeAnnotation = None
            |> TypeArrow(TypeNamed("a"))(TypeNamed("a"))([])
            |> Some, requirements = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeNamed("a")])])
        in
            match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelLet(binding)(false)], body = None)) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(UnjustifiedWrittenTraitRequirement("Equal"))) } -> Unit
                | _ -> test.fail("written requires clauses should not introduce unjustified evidence"))

let rejectsAmbiguousWrittenBindingRequirement unit =
    (let binding = LetBindingSyntax(name = "ambiguous", value = ExprInt(1), sugarParameters = [], typeAnnotation = Some(TypeNamed("Int")), requirements = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeNamed("a")])])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelLet(binding)(false)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(AmbiguousTraitRequirement("Equal"))) } -> Unit
            | _ -> test.fail("written requirement variables should occur in the binding type"))

let rejectsInvalidWrittenBindingRequirements unit =
    (let unknown = LetBindingSyntax(name = "unknown", value = ExprInt(1), sugarParameters = [], typeAnnotation = Some(TypeNamed("Int")), requirements = [TraitConstraintSyntax(traitName = "Missing", typeArguments = [TypeNamed("Int")])])
    in
        let unknownChecked =
            match inferProgram(ProgramSyntax(items = [TopLevelLet(unknown)(false)], body = None)) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(UnknownWrittenTraitRequirement("Missing"))) } -> Unit
                | _ -> test.fail("written requires clauses should reject unknown traits")
        in
            let wrongArity = LetBindingSyntax(name = "wrongArity", value = ExprInt(1), sugarParameters = [], typeAnnotation = Some(TypeNamed("Int")), requirements = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [])])
            in
                match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelLet(wrongArity)(false)], body = None)) with
                    | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(WrittenTraitRequirementArityMismatch("Equal", 1, 0))) } -> Unit
                    | _ -> test.fail("written requires clauses should enforce trait arity"))

let rejectsNestedMissingWrittenBindingRequirement unit =
    (let nested = ExprLet("bad")(sameValue)(ExprInt(0))([])(Some(sameAnnotation))([TraitConstraintSyntax(traitName = "Show", typeArguments = [TypeNamed("a")])])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelTrait(showDeclaration)], body = Some(nested))) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(MissingWrittenTraitRequirement("Equal"))) } -> Unit
            | _ -> test.fail("nested written requires clauses should be validated"))

let acceptsRecursiveWrittenBindingRequirement unit =
    (let binding = LetBindingSyntax(name = "same", value = sameValue, sugarParameters = [], typeAnnotation = Some(sameAnnotation), requirements = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeNamed("a")])])
    in
        match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelLet(binding)(true)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = None } -> Unit
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("recursive written requires clauses should infer: " + Ashes.Trait.Show.show(error)))

let rejectsInvalidRequirementInRecursiveGroup unit =
    (let valid = LetBindingSyntax(name = "first", value = sameValue, sugarParameters = [], typeAnnotation = Some(sameAnnotation), requirements = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeNamed("a")])])
    in
        let invalid = LetBindingSyntax(name = "second", value = sameValue, sugarParameters = [], typeAnnotation = Some(sameAnnotation), requirements = [TraitConstraintSyntax(traitName = "Show", typeArguments = [TypeNamed("a")])])
        in
            match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelTrait(showDeclaration), TopLevelRecursiveGroup([valid, invalid])], body = None)) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(MissingWrittenTraitRequirement("Equal"))) } -> Unit
                | _ -> test.fail("every member of a recursive group should validate its written requires clause"))

let expectWrittenBindingRequirements unit =
    unit
    |> expectValidWrittenBindingRequirement
    |> rejectsMissingWrittenBindingRequirement
    |> rejectsUnjustifiedWrittenBindingRequirement
    |> rejectsAmbiguousWrittenBindingRequirement
    |> rejectsInvalidWrittenBindingRequirements
    |> rejectsNestedMissingWrittenBindingRequirement
    |> acceptsRecursiveWrittenBindingRequirement
    |> rejectsInvalidRequirementInRecursiveGroup

let expectTraitImplementationBodyValidation unit =
    (let invalidBody =
        ExprLambda("left")(ExprLambda("right")(ExprInt(42))(None))(None)
    in
        let invalid = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = invalidBody)])
        in
            match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(invalid)], body = None)) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InferenceUnificationError(TypeMismatch(_left, _right)))) } -> Unit
                | _ -> test.fail("implementation bodies should match substituted method signatures"))

let expectTraitImplementationCapabilityRows unit =
    (let clock =
        CapabilityDecl(name = "TraitClock", typeParameters = [], operations = [CapabilityOperation(name = "now", signature = None
        |> TypeArrow(TypeUnit)(TypeNamed("Int"))([])
        |> Some)])
    in
        let log =
            CapabilityDecl(name = "TraitLog", typeParameters = [], operations = [CapabilityOperation(name = "write", signature = None
            |> TypeArrow(TypeUnit)(TypeNamed("Int"))([])
            |> Some)])
        in
            let timed = TraitDecl(name = "Timed", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [TraitMethodDecl(name = "time", signature = TypeArrow(TypeNamed("a"))(TypeNamed("Int"))([("TraitClock", [])])(None), defaultImplementation = None)])
            in
                let clockBody =
                    ExprLambda("value")(ExprCall(ExprQualifiedVar("TraitClock")("now"))(ExprTuple([]))(false))(None)
                in
                    let valid = TraitImplementationDecl(traitName = "Timed", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "time", implementation = clockBody)])
                    in
                        let validChecked =
                            match inferProgram(ProgramSyntax(items = [TopLevelCapability(clock), TopLevelCapability(log), TopLevelTrait(timed), TopLevelImplementation(valid)], body = None)) with
                                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = None } -> Unit
                                | _ -> test.fail("implementation bodies should retain matching capability rows")
                        in
                            let logBody =
                                ExprLambda("value")(ExprCall(ExprQualifiedVar("TraitLog")("write"))(ExprTuple([]))(false))(None)
                            in
                                let invalid = TraitImplementationDecl(traitName = "Timed", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "time", implementation = logBody)])
                                in
                                    match inferProgram(ProgramSyntax(items = [TopLevelCapability(clock), TopLevelCapability(log), TopLevelTrait(timed), TopLevelImplementation(invalid)], body = None)) with
                                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InferenceUnificationError(TypeMismatch(_left, _right)))) } -> Unit
                                        | _ -> test.fail("implementation capability rows should match method signatures"))

let expectTraitImplementationCoherence unit =
    (let exact = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
    in
        let exactChecked =
            match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(exact), TopLevelImplementation(exact)], body = None)) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(OverlappingTraitImplementations("Equal")) } -> Unit
                | _ -> test.fail("exact duplicate implementation heads should overlap")
        in
            let concrete = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeApplied("List")([TypeNamed("Int")])], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
            in
                let genericFirstChecked =
                    match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(genericEqualImplementation), TopLevelImplementation(concrete)], body = None)) with
                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(OverlappingTraitImplementations("Equal")) } -> Unit
                        | _ -> test.fail("generic and concrete heads should overlap")
                in
                    let concreteFirstChecked =
                        match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(concrete), TopLevelImplementation(genericEqualImplementation)], body = None)) with
                            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(OverlappingTraitImplementations("Equal")) } -> Unit
                            | _ -> test.fail("overlap detection should not depend on implementation order")
                    in
                        let pairTrait =
                            TraitDecl(name = "PairEqual", typeParameters = [TypeParameter(name = "a"), TypeParameter(name = "b")], supertraits = [], methods = [TraitMethodDecl(name = "equal", signature = TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("b"))(TypeNamed("Bool"))([])(None))([])(None), defaultImplementation = None)])
                        in
                            let repeated = TraitImplementationDecl(traitName = "PairEqual", typeArguments = [TypeNamed("a"), TypeNamed("a")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
                            in
                                let distinct = TraitImplementationDecl(traitName = "PairEqual", typeArguments = [TypeNamed("Int"), TypeNamed("Str")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
                                in
                                    match inferProgram(ProgramSyntax(items = [TopLevelTrait(pairTrait), TopLevelImplementation(repeated), TopLevelImplementation(distinct)], body = None)) with
                                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = environment, error = None } ->
                                            match resolveTraitImplementations("PairEqual")(environment) with
                                                | _first :: _second :: [] -> Unit
                                                | _ -> test.fail("non-overlapping repeated-variable heads should both register")
                                        | _ -> test.fail("a repeated head variable should reject incompatible concrete arguments"))

let cardType = TypeDecl(name = "Card", typeParameters = [], constructors = [TypeConstructor(name = "Card", parameters = [], fieldNames = [])], isRecord = false, derivingTraits = [])

let pairEqualDeclaration =
    TraitDecl(name = "PairEqual", typeParameters = [TypeParameter(name = "a"), TypeParameter(name = "b")], supertraits = [], methods = [TraitMethodDecl(name = "equal", signature = TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("b"))(TypeNamed("Bool"))([])(None))([])(None), defaultImplementation = None)])

let pairEqualImplementation =
    ExprLambda("left")(ExprLambda("right")(ExprBool(true))(None))(None)

let inferPackage packageId environment items =
    match inferProgramFromPackage(packageId)(environment)(ProgramSyntax(items = items, body = None)) with
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = inferredEnvironment, error = None } -> inferredEnvironment
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("package should infer: " + Ashes.Trait.Show.show(error))

let equalIntImplementation requirements = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("Int")], requirements = requirements, bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])

let recursive nestedListType : Int -> SemanticType -> SemanticType =
    given (depth) ->
        given (semanticType) ->
            if depth <= 0
            then semanticType
            else nestedListType(depth - 1)(SemList(semanticType))

let expectExactTraitEvidence environment unit =
    match resolveTraitEvidence(TraitConstraint(traitName = "Equal", typeArguments = [SemInt]))(environment) with
        | TraitEvidenceResolution { plan = Some(TraitEvidenceInstance(TraitConstraint { traitName = "Equal", typeArguments = SemInt :: [] }, _implementation, [], [])), error = None } -> Unit
        | _ -> test.fail("a unique concrete implementation should resolve to instance evidence")

let expectRecursiveTraitEvidence environment unit =
    match resolveTraitEvidence(TraitConstraint(traitName = "Equal", typeArguments = [SemList(SemInt)]))(environment) with
        | TraitEvidenceResolution { plan = Some(TraitEvidenceInstance(_goal, _implementation, TraitEvidenceInstance(TraitConstraint { traitName = "Equal", typeArguments = SemInt :: [] }, _innerImplementation, [], []) :: [], [])), error = None } -> Unit
        | _ -> test.fail("concrete implementation requirements should resolve recursively")

let expectAbstractTraitEvidence environment unit =
    match resolveTraitEvidence(TraitConstraint(traitName = "Equal", typeArguments = [SemList(SemVariable(99))]))(environment) with
        | TraitEvidenceResolution { plan = Some(TraitEvidenceInstance(_goal, _implementation, TraitEvidenceParameter(TraitConstraint { traitName = "Equal", typeArguments = SemVariable(99) :: [] }) :: [], [])), error = None } -> Unit
        | _ -> test.fail("unresolved abstract requirements should remain evidence parameters")

let expectSupertraitEvidence environment unit =
    match resolveTraitEvidence(TraitConstraint(traitName = "Ordered", typeArguments = [SemInt]))(environment) with
        | TraitEvidenceResolution { plan = Some(TraitEvidenceInstance(_goal, _implementation, [], TraitEvidenceInstance(TraitConstraint { traitName = "Equal", typeArguments = SemInt :: [] }, _equalImplementation, [], []) :: [])), error = None } -> Unit
        | _ -> test.fail("resolved instance evidence should include resolved supertraits")

let expectConcreteTraitEvidenceResolution unit =
    (let orderedImplementation =
        TraitImplementationDecl(traitName = "Ordered", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "compare", implementation = ExprLambda("left")(ExprLambda("right")(ExprInt(0))(None))(None))])
    in
        let environment =
            inferPackage("traits")(emptyTypeEnvironmentForPackage("traits"))([TopLevelTrait(eqDeclaration), TopLevelTrait(ordDeclaration), []
            |> equalIntImplementation
            |> TopLevelImplementation, TopLevelImplementation(genericEqualImplementation), TopLevelImplementation(orderedImplementation)])
        in
            unit
            |> expectExactTraitEvidence(environment)
            |> expectRecursiveTraitEvidence(environment)
            |> expectAbstractTraitEvidence(environment)
            |> expectSupertraitEvidence(environment))

let expectMissingTraitEvidence environment unit =
    match resolveTraitEvidence(TraitConstraint(traitName = "Equal", typeArguments = [SemList(SemString)]))(environment) with
        | TraitEvidenceResolution { plan = None, error = Some(MissingTraitImplementation(TraitConstraint { traitName = "Equal", typeArguments = SemString :: [] }, trace)) } ->
            trace
            |> formatTraitResolutionTrace
            |> test.assertEqual("Equal(List(Str)) -> Equal(Str)")
        | _ -> test.fail("a missing concrete implementation should fail resolution")

let expectAmbiguousTraitEvidence environment unit =
    match resolveTraitEvidence(TraitConstraint(traitName = "Equal", typeArguments = [SemInt]))(environment) with
        | TraitEvidenceResolution { plan = None, error = Some(AmbiguousTraitImplementation(TraitConstraint { traitName = "Equal", typeArguments = SemInt :: [] }, TraitConstraint { traitName = "Equal", typeArguments = SemInt :: [] } :: [])) } -> Unit
        | _ -> test.fail("multiple matching implementations should fail resolution")

let expectCyclicTraitEvidence environment unit =
    match resolveTraitEvidence(TraitConstraint(traitName = "Equal", typeArguments = [SemInt]))(environment) with
        | TraitEvidenceResolution { plan = None, error = Some(CyclicTraitResolution(TraitConstraint { traitName = "Equal", typeArguments = SemInt :: [] } :: TraitConstraint { traitName = "Equal", typeArguments = SemList(SemInt) :: [] } :: TraitConstraint { traitName = "Equal", typeArguments = SemInt :: [] } :: [])) } -> Unit
        | _ -> test.fail("cyclic concrete requirements should fail resolution")

let expectTraitEvidenceDepthLimit environment unit =
    match resolveTraitEvidence(TraitConstraint(traitName = "Equal", typeArguments = [nestedListType(65)(SemInt)]))(environment) with
        | TraitEvidenceResolution { plan = None, error = Some(TraitResolutionDepthExceeded(_goal, 64, _trace)) } -> Unit
        | _ -> test.fail("trait resolution should enforce the C# depth limit")

let expectAmbiguousTraitVariable environment unit =
    match requireTraitEvidence(TraitConstraint(traitName = "Equal", typeArguments = [SemVariable(99)]))(environment) with
        | TraitEvidenceResolution { plan = None, error = Some(AmbiguousTraitEvidence(TraitConstraint { traitName = "Equal", typeArguments = SemVariable(99) :: [] }, TraitConstraint { traitName = "Equal", typeArguments = SemVariable(99) :: [] } :: [])) } -> Unit
        | _ -> test.fail("a required abstract goal should diagnose its ambiguous type variable")

let expectTraitEvidenceResolutionFailures unit =
    (let baseEnvironment =
        inferPackage("traits")(emptyTypeEnvironmentForPackage("traits"))([TopLevelTrait(eqDeclaration), []
        |> equalIntImplementation
        |> TopLevelImplementation, TopLevelImplementation(genericEqualImplementation)])
    in
        let duplicateEnvironment = addTraitImplementation("Equal")([SemInt])([])([])(baseEnvironment)
        in
            let cyclicEnvironment =
                inferPackage("traits")(emptyTypeEnvironmentForPackage("traits"))([TopLevelTrait(eqDeclaration), [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeApplied("List")([TypeNamed("Int")])])]
                |> equalIntImplementation
                |> TopLevelImplementation, TopLevelImplementation(genericEqualImplementation)])
            in
                unit
                |> expectMissingTraitEvidence(baseEnvironment)
                |> expectAmbiguousTraitEvidence(duplicateEnvironment)
                |> expectCyclicTraitEvidence(cyclicEnvironment)
                |> expectTraitEvidenceDepthLimit(baseEnvironment)
                |> expectAmbiguousTraitVariable(baseEnvironment))

let expectTraitEvidenceResolution unit =
    unit
    |> expectConcreteTraitEvidenceResolution
    |> expectTraitEvidenceResolutionFailures

let ownedTypeImplementation unit = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("Card")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])

let expectOwnedTraitImplementation traitPackage unit =
    (let implementation = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
    in
        [TopLevelImplementation(implementation)]
        |> inferPackage("traits")(traitPackage)
        |> (given (_) -> Unit))

let expectOwnedTypeImplementation traitPackage unit =
    [TopLevelType(cardType), Unit
    |> ownedTypeImplementation
    |> TopLevelImplementation]
    |> inferPackage("consumer")(traitPackage)
    |> (given (_) -> Unit)

let rejectFullyForeignImplementation modelPackage unit =
    match inferProgramFromPackage("consumer")(modelPackage)(ProgramSyntax(items = [Unit
    |> ownedTypeImplementation
    |> TopLevelImplementation], body = None)) with
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(OrphanTraitImplementation("Equal")) } -> Unit
        | _ -> test.fail("a package should not implement a foreign trait for a foreign nominal type")

let expectSameNameOwnedTypeImplementation modelPackage unit =
    [TopLevelType(cardType), Unit
    |> ownedTypeImplementation
    |> TopLevelImplementation]
    |> inferPackage("consumer")(modelPackage)
    |> (given (_) -> Unit)

let rejectNestedLocalOrphan traitPackage unit =
    (let implementation = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeApplied("List")([TypeNamed("Card")])], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
    in
        match inferProgramFromPackage("consumer")(traitPackage)(ProgramSyntax(items = [TopLevelType(cardType), TopLevelImplementation(implementation)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(OrphanTraitImplementation("Equal")) } -> Unit
            | _ -> test.fail("a local type nested under a structural head should not satisfy the orphan rule"))

let expectOneOwnedHead traitPackage unit =
    (let implementation = TraitImplementationDecl(traitName = "PairEqual", typeArguments = [TypeNamed("Int"), TypeNamed("Card")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = pairEqualImplementation)])
    in
        [TopLevelType(cardType), TopLevelImplementation(implementation)]
        |> inferPackage("consumer")(traitPackage)
        |> (given (_) -> Unit))

let expectTraitImplementationOrphanRule unit =
    (let traitPackage =
        inferPackage("traits")(emptyTypeEnvironmentForPackage("traits"))([TopLevelTrait(eqDeclaration), TopLevelTrait(pairEqualDeclaration)])
    in
        let modelPackage = inferPackage("models")(traitPackage)([TopLevelType(cardType)])
        in
            unit
            |> expectOwnedTraitImplementation(traitPackage)
            |> expectOwnedTypeImplementation(traitPackage)
            |> rejectFullyForeignImplementation(modelPackage)
            |> expectSameNameOwnedTypeImplementation(modelPackage)
            |> rejectNestedLocalOrphan(traitPackage)
            |> expectOneOwnedHead(traitPackage))

let reportTraitInferenceSuccess unit = Ashes.IO.print("all self-hosted trait inference tests passed")

let runTraitInferenceTests unit =
    unit
    |> expectTraitRegistration
    |> expectForwardSupertraitRegistration
    |> expectInvalidTraitDeclarations
    |> expectInvalidSupertraits
    |> expectDefaultMethodValidation
    |> expectDefaultMethodDependencyCycles
    |> expectTraitImplementationRegistration
    |> expectTraitImplementationDefaults
    |> expectInvalidTraitImplementations
    |> expectInvalidTraitImplementationRequirements
    |> expectTraitImplementationRequirementTermination
    |> expectTraitConstraintCanonicalization
    |> expectInferredConstraintCanonicalization
    |> expectWrittenBindingRequirements
    |> expectTraitImplementationBodyValidation
    |> expectTraitImplementationCapabilityRows
    |> expectTraitImplementationCoherence
    |> expectTraitImplementationOrphanRule
    |> expectTraitEvidenceResolution
    |> reportTraitInferenceSuccess
