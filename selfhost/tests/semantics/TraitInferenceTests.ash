import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.TypeInference
let equalMethod = TraitMethodDecl(name = "equal", signature = TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None))([])(None), defaultImplementation = None)

let eqDeclaration = TraitDecl(name = "Equal", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [equalMethod])

let ordDeclaration = TraitDecl(name = "Ordered", typeParameters = [TypeParameter(name = "a")], supertraits = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeNamed("a")])], methods = [TraitMethodDecl(name = "compare", signature = TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("a"))(TypeNamed("Int"))([])(None))([])(None), defaultImplementation = None)])

let recursive containsConstraint name constraints =
    match constraints with
        | [] -> false
        | TraitConstraint { traitName = candidateName, typeArguments = _typeArguments } :: tail ->
            if name == candidateName
            then true
            else containsConstraint(name)(tail)

let expectTraitRegistration unit =
    match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration)], body = Some(ExprQualifiedVar("Equal")("equal")))) with
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
    (let forwardedEqual = ExprLambda("left")(ExprLambda("right")(ExprCall(ExprCall(ExprQualifiedVar("Equal")("equal"))(ExprVar("left"))(false))(ExprVar("right"))(false))(None))(None)
    in
        let valid = TraitDecl(name = "Equal", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [equalMethod, TraitMethodDecl(name = "same", signature = TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None))([])(None), defaultImplementation = Some(forwardedEqual))])
        in
            let validChecked =
                match inferProgram(ProgramSyntax(items = [TopLevelTrait(valid)], body = None)) with
                    | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = None } -> Unit
                    | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("well-typed defaults should infer: " + Ashes.Trait.Show.show(error))
            in
                let invalid = TraitDecl(name = "Rendered", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [TraitMethodDecl(name = "render", signature = TypeArrow(TypeNamed("a"))(TypeNamed("Str"))([])(None), defaultImplementation = Some(ExprLambda("value")(ExprInt(42))(None)))])
                in
                    match inferProgram(ProgramSyntax(items = [TopLevelTrait(invalid)], body = None)) with
                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InferenceUnificationError(TypeMismatch(_left, _right)))) } -> Unit
                        | _ -> test.fail("default implementations should match their method signatures"))

let trueEqualImplementation = ExprLambda("left")(ExprLambda("right")(ExprBool(true))(None))(None)

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
    (let defaultedMethod = TraitMethodDecl(name = "same", signature = TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None))([])(None), defaultImplementation = Some(trueEqualImplementation))
    in
        let defaultedTrait = TraitDecl(name = "Comparable", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [TraitMethodDecl(name = "equal", signature = TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None))([])(None), defaultImplementation = None), defaultedMethod])
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

let expectInvalidTraitImplementations unit =
    (let missingTrait = TraitImplementationDecl(traitName = "Missing", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
    in
        let missingTraitChecked =
            match inferProgram(ProgramSyntax(items = [TopLevelImplementation(missingTrait)], body = None)) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(UnknownTraitImplementation("Missing")) } -> Unit
                | _ -> test.fail("implementations of unknown traits should be rejected")
        in
            let badArity = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("Int"), TypeNamed("Str")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
            in
                let badArityChecked =
                    match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(badArity)], body = None)) with
                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(TraitImplementationArityMismatch("Equal", 1, 2)) } -> Unit
                        | _ -> test.fail("implementation arity should be checked")
                in
                    let unknownMethod = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "other", implementation = trueEqualImplementation)])
                    in
                        let unknownMethodChecked =
                            match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(unknownMethod)], body = None)) with
                                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(UnknownTraitImplementationMethod("Equal", "other")) } -> Unit
                                | _ -> test.fail("unknown implementation methods should be rejected")
                        in
                            let duplicateMethod = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation), TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
                            in
                                let duplicateMethodChecked =
                                    match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(duplicateMethod)], body = None)) with
                                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(DuplicateTraitImplementationMethod("Equal", "equal")) } -> Unit
                                        | _ -> test.fail("duplicate implementation methods should be rejected")
                                in
                                    let missingMethod = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [])
                                    in
                                        let missingMethodChecked =
                                            match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(missingMethod)], body = None)) with
                                                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(MissingTraitImplementationMethod("Equal", "equal")) } -> Unit
                                                | _ -> test.fail("required implementation methods should be checked")
                                        in
                                            let escapedRequirement = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeApplied("List")([TypeNamed("a")])], requirements = [TraitConstraintSyntax(traitName = "Equal", typeArguments = [TypeNamed("b")])], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = trueEqualImplementation)])
                                            in
                                                match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(escapedRequirement)], body = None)) with
                                                    | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(TraitImplementationRequirementVariableEscapes("Equal", "b")) } -> Unit
                                                    | _ -> test.fail("implementation requirement variables should occur in the head"))

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

let expectTraitImplementationBodyValidation unit =
    (let invalidBody = ExprLambda("left")(ExprLambda("right")(ExprInt(42))(None))(None)
    in
        let invalid = TraitImplementationDecl(traitName = "Equal", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = invalidBody)])
        in
            match inferProgram(ProgramSyntax(items = [TopLevelTrait(eqDeclaration), TopLevelImplementation(invalid)], body = None)) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InferenceUnificationError(TypeMismatch(_left, _right)))) } -> Unit
                | _ -> test.fail("implementation bodies should match substituted method signatures"))

let expectTraitImplementationCapabilityRows unit =
    (let clock = CapabilityDecl(name = "TraitClock", typeParameters = [], operations = [CapabilityOperation(name = "now", signature = Some(TypeArrow(TypeUnit)(TypeNamed("Int"))([])(None)))])
    in
        let log = CapabilityDecl(name = "TraitLog", typeParameters = [], operations = [CapabilityOperation(name = "write", signature = Some(TypeArrow(TypeUnit)(TypeNamed("Int"))([])(None)))])
        in
            let timed = TraitDecl(name = "Timed", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [TraitMethodDecl(name = "time", signature = TypeArrow(TypeNamed("a"))(TypeNamed("Int"))([("TraitClock", [])])(None), defaultImplementation = None)])
            in
                let clockBody = ExprLambda("value")(ExprCall(ExprQualifiedVar("TraitClock")("now"))(ExprTuple([]))(false))(None)
                in
                    let valid = TraitImplementationDecl(traitName = "Timed", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "time", implementation = clockBody)])
                    in
                        let validChecked =
                            match inferProgram(ProgramSyntax(items = [TopLevelCapability(clock), TopLevelCapability(log), TopLevelTrait(timed), TopLevelImplementation(valid)], body = None)) with
                                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = None } -> Unit
                                | _ -> test.fail("implementation bodies should retain matching capability rows")
                        in
                            let logBody = ExprLambda("value")(ExprCall(ExprQualifiedVar("TraitLog")("write"))(ExprTuple([]))(false))(None)
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
                        let pairTrait = TraitDecl(name = "PairEqual", typeParameters = [TypeParameter(name = "a"), TypeParameter(name = "b")], supertraits = [], methods = [TraitMethodDecl(name = "equal", signature = TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("b"))(TypeNamed("Bool"))([])(None))([])(None), defaultImplementation = None)])
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

let runTraitInferenceTests unit =
    (let registrationChecked = expectTraitRegistration(Unit)
    in
        let forwardSupertraitChecked = expectForwardSupertraitRegistration(Unit)
        in
            let invalidDeclarationsChecked = expectInvalidTraitDeclarations(Unit)
            in
                let invalidSupertraitsChecked = expectInvalidSupertraits(Unit)
                in
                    let defaultMethodChecked = expectDefaultMethodValidation(Unit)
                    in
                        let implementationChecked = expectTraitImplementationRegistration(Unit)
                        in
                            let defaultsChecked = expectTraitImplementationDefaults(Unit)
                            in
                                let invalidImplementationsChecked = expectInvalidTraitImplementations(Unit)
                                in
                                    let invalidRequirementsChecked = expectInvalidTraitImplementationRequirements(Unit)
                                    in
                                        let implementationBodyChecked = expectTraitImplementationBodyValidation(Unit)
                                        in
                                            let implementationCapabilitiesChecked = expectTraitImplementationCapabilityRows(Unit)
                                            in
                                                let implementationCoherenceChecked = expectTraitImplementationCoherence(Unit)
                                                in Ashes.IO.print("all self-hosted trait inference tests passed"))
