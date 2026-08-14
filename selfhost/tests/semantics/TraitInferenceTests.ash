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
                    in Ashes.IO.print("all self-hosted trait declaration inference tests passed"))
