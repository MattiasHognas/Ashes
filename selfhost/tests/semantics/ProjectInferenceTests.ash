import Ashes.Test as test
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.ModuleSemanticStitching
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.ProjectInference
import AshesCompiler.Semantics.ProjectSyntaxStitching
import AshesCompiler.Semantics.StandardTraits
import AshesCompiler.Semantics.Types
export (
    value runProjectInferenceTests,
)

let binding name value = LetBindingSyntax(name = name, value = value, sugarParameters = [], typeAnnotation = None, requirements = [])

let interface name exports = ModuleImportInterface(name = name, exports = exports)

let requireStitched units =
    match stitchProjectSyntax(units) with
        | Ok(project) -> project
        | Error(error) -> test.fail("project syntax should stitch before inference: " + Ashes.Trait.Show.show(error))

let expectImportedValueInference unit =
    (let values =
        SemanticStitchUnit(name = "Values", packageId = "values@1.0.0", sourcePath = "/deps/Values.ash", imports = [], interface = interface("Values")([ImportValueExport("seed")]), program = ProgramSyntax(items = [TopLevelLet(binding("seed")(ExprInt(41)))(false)], body = None), isEntry = false)
    in
        let main =
            SemanticStitchUnit(name = "Main", packageId = "app@1.0.0", sourcePath = "/app/Main.ash", imports = [ResolvedModuleImport("Values")(None)(1)("import Values")], interface = interface("Main")([]), program = ProgramSyntax(items = [], body = ExprInt(1)
            |> ExprAdd(ExprVar("seed"))
            |> Some), isEntry = true)
        in
            match [values, main]
            |> requireStitched
            |> inferStitchedProject with
                | ProgramInferenceResult { semanticType = SemInt, error = None } -> Unit
                | ProgramInferenceResult { error = Some(error) } -> test.fail("stitched imported values should infer: " + Ashes.Trait.Show.show(error))
                | _ -> test.fail("the stitched entry expression should infer Int"))

let comparableMethod =
    TraitMethodDecl(name = "same", signature = TypeArrow(TypeNamed("a"))(TypeArrow(TypeNamed("a"))(TypeNamed("Bool"))([])(None))([])(None), defaultImplementation = None)

let comparableTrait = TraitDecl(name = "Comparable", typeParameters = [TypeParameter(name = "a")], supertraits = [], methods = [comparableMethod])

let tokenType = TypeDecl(name = "Token", typeParameters = [], constructors = [TypeConstructor(name = "Token", parameters = [], fieldNames = [])], isRecord = false, derivingTraits = [])

let comparableImplementation =
    TraitImplementationDecl(traitName = "Comparable", typeArguments = [TypeNamed("Token")], requirements = [], bindings = [TraitImplementationMethodBinding(methodName = "same", implementation = ExprLambda("left")(ExprLambda("right")(ExprBool(true))(None))(None))])

let expectOrphanProjectShape project =
    match project with
        | StitchedSyntaxProject { program = ProgramSyntax { items = TopLevelTrait(TraitDecl { name = declaredTrait }) :: TopLevelType(TypeDecl { name = declaredType }) :: TopLevelImplementation(TraitImplementationDecl { traitName = implementedTrait, typeArguments = TypeNamed(implementedType) :: [] }) :: [] }, moduleRegions = StitchedModuleRegion { itemStart = 0, itemEnd = 2 } :: StitchedModuleRegion { itemStart = 2, itemEnd = 3 } :: [] } ->
            declaredTrait
            |> test.assertEqual(implementedTrait)
            |> (given (_) -> test.assertEqual(declaredType)(implementedType))
            |> (given (_) -> project)
        | _ -> test.fail("stitched trait/type declarations and their imported implementation should align")

let expectCrossPackageOrphanRejection unit =
    (let domain = SemanticStitchUnit(name = "Domain", packageId = "domain@1.0.0", sourcePath = "/deps/Domain.ash", imports = [], interface = interface("Domain")([ImportTypeExport("Comparable"), ImportTypeExport("Token"), ImportConstructorExport("Token")]), program = ProgramSyntax(items = [TopLevelTrait(comparableTrait), TopLevelType(tokenType)], body = None), isEntry = false)
    in
        let main = SemanticStitchUnit(name = "Main", packageId = "app@1.0.0", sourcePath = "/app/Main.ash", imports = [ResolvedModuleImport("Domain")(None)(1)("import Domain")], interface = interface("Main")([]), program = ProgramSyntax(items = [TopLevelImplementation(comparableImplementation)], body = None), isEntry = true)
        in
            match [domain, main]
            |> requireStitched
            |> expectOrphanProjectShape
            |> inferStitchedProject with
                | ProgramInferenceResult { error = Some(OrphanTraitImplementation("Domain_Comparable")) } -> Unit
                | ProgramInferenceResult { error = Some(error) } -> test.fail("unexpected cross-package orphan result: " + Ashes.Trait.Show.show(error))
                | _ -> test.fail("an importing package must not implement a foreign trait for a foreign nominal type"))

let expectProgramGlobalCoherence unit =
    (let domain = SemanticStitchUnit(name = "Domain", packageId = "domain@1.0.0", sourcePath = "/deps/Domain.ash", imports = [], interface = interface("Domain")([ImportTypeExport("Comparable"), ImportTypeExport("Token"), ImportConstructorExport("Token")]), program = ProgramSyntax(items = [TopLevelTrait(comparableTrait), TopLevelType(tokenType), TopLevelImplementation(comparableImplementation)], body = None), isEntry = false)
    in
        let extension = SemanticStitchUnit(name = "Extension", packageId = "domain@1.0.0", sourcePath = "/deps/Extension.ash", imports = [ResolvedModuleImport("Domain")(None)(1)("import Domain")], interface = interface("Extension")([]), program = ProgramSyntax(items = [TopLevelImplementation(comparableImplementation)], body = None), isEntry = false)
        in
            let main = SemanticStitchUnit(name = "Main", packageId = "app@1.0.0", sourcePath = "/app/Main.ash", imports = [], interface = interface("Main")([]), program = ProgramSyntax(items = [], body = None), isEntry = true)
            in
                match [domain, extension, main]
                |> requireStitched
                |> inferStitchedProject with
                    | ProgramInferenceResult { error = Some(OverlappingTraitImplementations("Domain_Comparable")) } -> Unit
                    | ProgramInferenceResult { error = Some(error) } -> test.fail("unexpected global coherence result: " + Ashes.Trait.Show.show(error))
                    | _ -> test.fail("overlapping implementations in sibling modules of one package should conflict globally"))

let derivedBoxType = TypeDecl(name = "Box", typeParameters = [], constructors = [TypeConstructor(name = "Box", parameters = [TypeNamed("Int")], fieldNames = [])], isRecord = false, derivingTraits = ["Eq"])

let expectDerivingUsesOwningPackage unit =
    (let models = SemanticStitchUnit(name = "Models", packageId = "models@1.0.0", sourcePath = "/deps/Models.ash", imports = [], interface = interface("Models")([ImportTypeExport("Box"), ImportConstructorExport("Box")]), program = ProgramSyntax(items = [TopLevelType(derivedBoxType)], body = None), isEntry = false)
    in
        let main = SemanticStitchUnit(name = "Main", packageId = "app@1.0.0", sourcePath = "/app/Main.ash", imports = [], interface = interface("Main")([]), program = ProgramSyntax(items = [], body = Some(ExprInt(0))), isEntry = true)
        in
            match [models, main]
            |> requireStitched
            |> inferStitchedProjectFrom(standardTraitEnvironment(Unit)) with
                | ProgramInferenceResult { semanticType = SemInt, error = None } -> Unit
                | ProgramInferenceResult { error = Some(error) } -> test.fail("derived implementations should inherit their module package: " + Ashes.Trait.Show.show(error))
                | _ -> test.fail("the project with a dependency-owned derived implementation should infer"))

let runProjectInferenceTests unit =
    unit
    |> expectImportedValueInference
    |> expectCrossPackageOrphanRejection
    |> expectProgramGlobalCoherence
    |> expectDerivingUsesOwningPackage
