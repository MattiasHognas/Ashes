import Ashes.Test as test
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.ModuleSemanticStitching
import AshesCompiler.Semantics.StandardTraits
import AshesCompiler.Semantics.StandardTraitSourceBinding
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Types
export (
    value runStandardTraitSourceBindingTests,
)

let binaryType argument result =
    SemFunction(argument)(SemFunction(argument)(result)(None))(None)

let equalScheme = TypeScheme(quantified = [(9000, "a")], body = binaryType(SemVariable(9000))(SemBool), constraints = [TraitConstraint(traitName = "Eq", typeArguments = [SemVariable(9000)])])

let equalMethod = TraitMethodInferenceDefinition(name = "equal", scheme = equalScheme, defaultImplementation = None)

let intEqualType = binaryType(SemInt)(SemBool)

let listParameter = SemParameter(2000)("item")

let listHead = SemList(listParameter)

let listEqualType = binaryType(listHead)(SemBool)

let resultProblem = SemParameter(2001)("problem")

let resultValue = SemParameter(2000)("value")

let resultHead = SemNamed(2)("Result")([resultProblem, resultValue])

let resultEqualType = binaryType(resultHead)(SemBool)

let placeholderMethod traitName methodName head semanticType =
    TraitImplementationMethodInferenceDefinition(name = methodName, implementation = head
    |> standardTraitImplementationBindingName(traitName)(methodName)
    |> ExprVar, semanticType = semanticType)

let sourceBindingEnvironment unit =
    "ashes-standard-library"
    |> emptyTypeEnvironmentForPackage
    |> addTraitBinding("Eq")(1)([SemVariable(9000)])([equalMethod])([])
    |> addTraitImplementation("Eq")([SemInt])([])([placeholderMethod("Eq")("equal")(SemInt)(intEqualType)])
    |> addTraitImplementation("Eq")([listHead])([TraitConstraint(traitName = "Eq", typeArguments = [listParameter])])([placeholderMethod("Eq")("equal")(listHead)(listEqualType)])
    |> addTraitImplementation("Eq")([resultHead])([TraitConstraint(traitName = "Eq", typeArguments = [resultProblem]), TraitConstraint(traitName = "Eq", typeArguments = [resultValue])])([placeholderMethod("Eq")("equal")(resultHead)(resultEqualType)])

let binding name value = LetBindingSyntax(name = name, value = value, sugarParameters = [], typeAnnotation = None, requirements = [])

let equalDeclaration =
    TraitDecl(name = "Eq", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [TraitMethodDecl(name = "equal", signature = TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None))([])(None), defaultImplementation = None)])

let intImplementation = TraitImplementationDecl(traitName = "Eq", typeArguments = [TypeNamed("Int")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = ExprVar("primitiveEqual"))])

let listImplementation = TraitImplementationDecl(traitName = "Eq", typeArguments = [TypeApplied("List")([TypeNamed("item")])], requirements = [TraitConstraintSyntax(traitName = "Eq", typeArguments = [TypeNamed("item")])], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = ExprQualifiedVar("Eq")("equal"))])

let resultImplementation = TraitImplementationDecl(traitName = "Eq", typeArguments = [TypeApplied("Result")([TypeNamed("failure"), TypeNamed("success")])], requirements = [TraitConstraintSyntax(traitName = "Eq", typeArguments = [TypeNamed("failure")]), TraitConstraintSyntax(traitName = "Eq", typeArguments = [TypeNamed("success")])], bindings = [TraitImplementationMethodBinding(methodName = "equal", implementation = ExprQualifiedVar("Eq")("equal"))])

let traitUnit implementations =
    SemanticStitchUnit(name = "Ashes.Trait", packageId = "ashes-core", sourcePath = "<std:Ashes.Trait>", imports = [], interface = ModuleImportInterface(name = "Ashes.Trait", exports = [ImportTypeExport("Eq")]), program = ProgramSyntax(items = TopLevelLet(None
    |> ExprLambda("left")(ExprLambda("right")(ExprBool(true))(None))
    |> binding("primitiveEqual"))(false) :: TopLevelTrait(equalDeclaration) :: implementations, body = None), isEntry = false)

let entryUnit = SemanticStitchUnit(name = "Main", packageId = "app", sourcePath = "/app/Main.ash", imports = [], interface = ModuleImportInterface(name = "Main", exports = []), program = ProgramSyntax(items = [], body = None), isEntry = true)

let requireProject units =
    match buildStitchedSemanticProject(units) with
        | Ok(project) -> project
        | Error(error) -> test.fail("standard trait source project should stitch: " + Ashes.Trait.Show.show(error))

let expectBoundImplementations environment =
    match resolveTraitImplementations("Eq")(environment) with
        | TraitImplementationInferenceDefinition { typeArguments = SemNamed(_resultId, "Result", _resultArguments) :: [], methods = TraitImplementationMethodInferenceDefinition { implementation = ExprQualifiedVar("Ashes_Trait_Eq", "equal") } :: [] } :: TraitImplementationInferenceDefinition { typeArguments = SemList(_parameter) :: [], methods = TraitImplementationMethodInferenceDefinition { implementation = ExprQualifiedVar("Ashes_Trait_Eq", "equal") } :: [] } :: TraitImplementationInferenceDefinition { typeArguments = SemInt :: [], methods = TraitImplementationMethodInferenceDefinition { implementation = ExprVar("__ashes_private_value_Ashes_Trait_primitiveEqual") } :: [] } :: [] -> environment
        | _ -> test.fail("source binding should preserve the seeded implementation order and heads")

let expectCompilerTraitAlias environment =
    match environment with
        | TypeEnvironment { bindings = bindings } ->
            match (resolveTraitBinding("Ashes_Trait_Eq")(environment), bindings) with
                | (Some(TraitInferenceDefinition { name = "Ashes_Trait_Eq" }), ("Ashes_Trait_Eq.equal", _scheme) :: _tail) -> Unit
                | _ -> test.fail("rewritten standard trait references should resolve through a compiler-name alias")

let expectBindings bindings =
    match bindings with
        | StandardTraitSourceBinding { traitName = "Eq", methodName = "equal", headKey = "int", implementation = ExprVar("__ashes_private_value_Ashes_Trait_primitiveEqual") } :: StandardTraitSourceBinding { traitName = "Eq", methodName = "equal", headKey = "list_parameter0", implementation = ExprQualifiedVar("Ashes_Trait_Eq", "equal") } :: StandardTraitSourceBinding { traitName = "Eq", methodName = "equal", headKey = "Result_parameter0_parameter1", implementation = ExprQualifiedVar("Ashes_Trait_Eq", "equal") } :: [] -> Unit
        | _ -> test.fail("source bindings should use alpha-normalized structural implementation heads")

let expectSourceBinding unit =
    (let units = [traitUnit([TopLevelImplementation(intImplementation), TopLevelImplementation(listImplementation), TopLevelImplementation(resultImplementation)]), entryUnit]
    in
        match Unit
        |> sourceBindingEnvironment
        |> bindStandardTraitImplementationSources(units)(requireProject(units)) with
            | Error(error) -> test.fail("standard trait source binding should succeed: " + Ashes.Trait.Show.show(error))
            | Ok(StandardTraitSourceBindingResult { environment = environment, bindings = bindings }) ->
                bindings
                |> expectBindings
                |> (given (_) -> expectBoundImplementations(environment))
                |> (given (boundEnvironment) -> expectCompilerTraitAlias(boundEnvironment)))

let expectMissingSourceRejection unit =
    (let units = [traitUnit([TopLevelImplementation(intImplementation), TopLevelImplementation(resultImplementation)]), entryUnit]
    in
        match Unit
        |> sourceBindingEnvironment
        |> bindStandardTraitImplementationSources(units)(requireProject(units)) with
            | Error(MissingStandardTraitSourceBinding("__ashes_standard_trait_Eq_equal_list_parameter2000")) -> Unit
            | Error(error) -> test.fail("unexpected missing source result: " + Ashes.Trait.Show.show(error))
            | Ok(_result) -> test.fail("every seeded standard implementation must bind to source"))

let expectDuplicateSourceRejection unit =
    (let units = [traitUnit([TopLevelImplementation(intImplementation), TopLevelImplementation(intImplementation), TopLevelImplementation(listImplementation), TopLevelImplementation(resultImplementation)]), entryUnit]
    in
        match Unit
        |> sourceBindingEnvironment
        |> bindStandardTraitImplementationSources(units)(requireProject(units)) with
            | Error(DuplicateStandardTraitSourceBinding("Eq.equal:int")) -> Unit
            | Error(error) -> test.fail("unexpected duplicate source result: " + Ashes.Trait.Show.show(error))
            | Ok(_result) -> test.fail("duplicate standard source implementations should fail deterministically"))

let runStandardTraitSourceBindingTests unit =
    unit
    |> expectSourceBinding
    |> expectMissingSourceRejection
    |> expectDuplicateSourceRejection
    |> (given (_) -> Ashes.IO.print("all self-hosted standard trait source binding tests passed"))
