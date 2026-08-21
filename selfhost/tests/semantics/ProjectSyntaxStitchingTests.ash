import Ashes.Test as test
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.ModuleSemanticStitching
import AshesCompiler.Semantics.ProjectSyntaxStitching
export (
    value runProjectSyntaxStitchingTests,
)

let binding name value = LetBindingSyntax(name = name, value = value, sugarParameters = [], typeAnnotation = None, requirements = [])

let moduleInterface name exportName = ModuleImportInterface(name = name, exports = [ImportValueExport(exportName)])

let coreUnit =
    SemanticStitchUnit(name = "Core", packageId = "dep-core@1.0.0", sourcePath = "/deps/Core.ash", imports = [], interface = moduleInterface("Core")("seed"), program = ProgramSyntax(items = [TopLevelExport(ExportDecl(items = [ExportValue("seed")])), false
    |> TopLevelLet(binding("seed")(ExprInt(1)))
    |> TopLevelAt(TextSpan(start = 10, end = 20))], body = Some(ExprInt(999))), isEntry = false)

let utilUnit =
    SemanticStitchUnit(name = "Util", packageId = "dep-util@2.0.0", sourcePath = "/deps/Util.ash", imports = [ResolvedModuleImport("Core")(None)(1)("import Core")], interface = moduleInterface("Util")("incremented"), program = ProgramSyntax(items = [TopLevelExport(ExportDecl(items = [ExportValue("incremented")])), false
    |> TopLevelLet(ExprInt(1)
    |> ExprAdd(ExprVar("seed"))
    |> binding("incremented"))
    |> TopLevelAt(TextSpan(start = 30, end = 45))], body = Some(ExprInt(888))), isEntry = false)

let mainUnit =
    SemanticStitchUnit(name = "Main", packageId = "app@1.0.0", sourcePath = "/app/Main.ash", imports = [ResolvedModuleImport("Util")(None)(1)("import Util")], interface = moduleInterface("Main")("result"), program = ProgramSyntax(items = [TopLevelExport(ExportDecl(items = [ExportValue("result")])), false
    |> TopLevelLet(binding("result")(ExprVar("incremented")))
    |> TopLevelAt(TextSpan(start = 50, end = 70))], body = Some(ExprVar("result"))), isEntry = true)

let requireProject result =
    match result with
        | Ok(project) -> project
        | Error(error) -> test.fail("project syntax stitching should succeed: " + Ashes.Trait.Show.show(error))

let expectCombinedProgram project =
    match project with
        | StitchedSyntaxProject { program = ProgramSyntax { items = TopLevelAt(TextSpan { start = 10, end = 20 }, TopLevelLet(LetBindingSyntax { name = "Core_seed", value = ExprInt(1) }, false)) :: TopLevelAt(TextSpan { start = 30, end = 45 }, TopLevelLet(LetBindingSyntax { name = "Util_incremented", value = ExprAdd(ExprVar("Core_seed"), ExprInt(1)) }, false)) :: TopLevelAt(TextSpan { start = 50, end = 70 }, TopLevelLet(LetBindingSyntax { name = "result", value = ExprVar("Util_incremented") }, false)) :: [], body = Some(ExprVar("result")) } } -> project
        | _ -> test.fail("combined syntax should retain dependency order, rewritten references, spans, and only the entry body")

let expectModuleRegions project =
    match project with
        | StitchedSyntaxProject { moduleRegions = regions } ->
            regions
            |> test.assertEqual([StitchedModuleRegion(moduleName = "Core", packageId = "dep-core@1.0.0", sourcePath = "/deps/Core.ash", itemStart = 0, itemEnd = 1, isEntry = false), StitchedModuleRegion(moduleName = "Util", packageId = "dep-util@2.0.0", sourcePath = "/deps/Util.ash", itemStart = 1, itemEnd = 2, isEntry = false), StitchedModuleRegion(moduleName = "Main", packageId = "app@1.0.0", sourcePath = "/app/Main.ash", itemStart = 2, itemEnd = 3, isEntry = true)])
            |> (given (_) -> project)

let expectDefinitionPlacements project =
    match project with
        | StitchedSyntaxProject { definitionPlacements = StitchedDefinitionPlacement { definition = StitchedDefinition { compilerName = "Core_seed", definitionSpan = Some(TextSpan { start = 10, end = 20 }) }, combinedItemIndex = 0 } :: StitchedDefinitionPlacement { definition = StitchedDefinition { compilerName = "Util_incremented", definitionSpan = Some(TextSpan { start = 30, end = 45 }) }, combinedItemIndex = 1 } :: StitchedDefinitionPlacement { definition = StitchedDefinition { compilerName = "result", definitionSpan = Some(TextSpan { start = 50, end = 70 }) }, combinedItemIndex = 2 } :: [] } -> project
        | _ -> test.fail("definition placements should retain source spans and deterministic combined item indices")

let emptyUnit name isEntry = SemanticStitchUnit(name = name, packageId = "app", sourcePath = "/" + name + ".ash", imports = [], interface = ModuleImportInterface(name = name, exports = []), program = ProgramSyntax(items = [], body = None), isEntry = isEntry)

let expectEntryValidation unit =
    match stitchProjectSyntax([emptyUnit("Library")(false)]) with
        | Error(MissingProjectSyntaxEntry) ->
            match stitchProjectSyntax([emptyUnit("First")(true), emptyUnit("Second")(true)]) with
                | Error(MultipleProjectSyntaxEntries("First", "Second")) -> Unit
                | _ -> test.fail("multiple entry modules should fail deterministically")
        | _ -> test.fail("a combined project without an entry module should fail")

let runProjectSyntaxStitchingTests unit =
    [coreUnit, utilUnit, mainUnit]
    |> stitchProjectSyntax
    |> requireProject
    |> expectCombinedProgram
    |> expectModuleRegions
    |> expectDefinitionPlacements
    |> (given (_) -> expectEntryValidation(Unit))
