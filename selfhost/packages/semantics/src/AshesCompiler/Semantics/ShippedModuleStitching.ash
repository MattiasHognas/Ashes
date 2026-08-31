// Stitches an entry program together with the shipped standard-library modules its import header
// reaches, from in-memory sources: header split, parse, interface, dependency-ordered plan, then
// the same semantic/syntax stitching a multi-module project goes through.
//
// Invariants:
// - Only `Ashes.*` imports are resolved, and only against the supplied shipped module texts.
// - Reachable modules are loaded transitively through their own import headers, each once.
// - The entry alone contributes a trailing expression; shipped modules contribute declarations.
// - Sources are never read from disk here; the caller supplies every text it wants resolvable.

import Ashes.Collection.List.reverse as reverseList
import AshesCompiler.Frontend.ImportHeader
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.ModuleInterface
import AshesCompiler.Frontend.ModulePlan
import AshesCompiler.Frontend.ModuleSource
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.ModuleSemanticStitching
import AshesCompiler.Semantics.ProjectSyntaxStitching
export (
    type ShippedModuleText(..),
    type ShippedStitchError(..),
    value stitchWithShippedModules,
)

type ShippedModuleText =
    | moduleName: Str
    | sourcePath: Str
    | source: Str
    deriving {Eq, Show}

type ShippedStitchError =
    | ShippedModuleMissing(Str, Str)
    | ShippedImportHeaderError(Str, ImportHeaderError)
    | ShippedParseError(Str, Str)
    | ShippedInterfaceError(Str, ModuleInterfaceBuildError)
    | ShippedPlanError(ModulePlanError)
    | ShippedSyntaxStitchError(ProjectSyntaxStitchError)
    | UnsupportedNonShippedImport(Str, Str)
    deriving {Eq, Show}

type LoadedShippedModule =
    | name: Str
    | sourcePath: Str
    | imports: List(ImportHeaderEntry)
    | program: ProgramSyntax
    | interface: ModuleImportInterface

let shippedPackageId = "ashes-std"

let recursive findShipped (name: Str) (shipped: List(ShippedModuleText)) =
    match shipped with
        | [] -> None
        | candidate :: rest ->
            if candidate.moduleName == name
            then Some(candidate)
            else findShipped(name)(rest)

let recursive findLoaded (name: Str) (loaded: List(LoadedShippedModule)) =
    match loaded with
        | [] -> None
        | candidate :: rest ->
            if candidate.name == name
            then Some(candidate)
            else findLoaded(name)(rest)

let recursive importedModuleNames (entries: List(ImportHeaderEntry)) =
    match entries with
        | [] -> []
        | ImportHeaderEntry { modulePath = modulePath } :: rest -> modulePath :: importedModuleNames(rest)

// Splits the header off, parses the remainder, and builds the module's interface — the same
// three steps `ProjectCompilationPlanning` performs per project module.
let loadModuleText name path source =
    match parseImportHeader(source) with
        | Error(error) -> Error(ShippedImportHeaderError(path)(error))
        | Ok(ParsedImportHeader { imports = imports, sourceWithoutImports = body }) ->
            match parseProgram(body) with
                | ProgramParseResult { program = program, diagnostics = [] } ->
                    match buildModuleInterface(name)([])(program) with
                        | Error(error) -> Error(ShippedInterfaceError(path)(error))
                        | Ok(interface) -> Ok(LoadedShippedModule(name = name, sourcePath = path, imports = imports, program = program, interface = interface))
                | ProgramParseResult { diagnostics = diagnostics } -> Error(ShippedParseError(path)(Ashes.Trait.Show.show(diagnostics)))

// Loads every module `pending` names that is not loaded yet, then whatever those modules import,
// until nothing new is reachable. An import outside the reserved namespace has no shipped source
// to resolve against and is an error rather than a silently unresolved name.
let recursive loadReachable (importer: Str) (pending: List(Str)) (loaded: List(LoadedShippedModule)) (shipped: List(ShippedModuleText)) =
    match pending with
        | [] -> Ok(loaded)
        | name :: rest ->
            match findLoaded(name)(loaded) with
                | Some(_) -> loadReachable(importer)(rest)(loaded)(shipped)
                | None ->
                    if Ashes.Text.startsWith(name)("Ashes.") == false
                    then Error(UnsupportedNonShippedImport(importer)(name))
                    else
                        match findShipped(name)(shipped) with
                            | None ->
                                if isIntrinsicBuiltinModule(name)
                                then
                                    let program = ProgramSyntax(items = [], body = None)
                                    in
                                        match buildModuleInterface(name)([])(program) with
                                            | Error(error) -> Error(ShippedInterfaceError("<builtin>")(error))
                                            | Ok(interface) ->
                                                loadReachable(name)(rest)(
                                                    LoadedShippedModule(
                                                        name = name,
                                                        sourcePath = "<builtin>",
                                                        imports = [],
                                                        program = program,
                                                        interface = interface
                                                    ) :: loaded
                                                )(shipped)
                                else Error(ShippedModuleMissing(importer)(name))
                            | Some(ShippedModuleText { sourcePath = path, source = source }) ->
                                match loadModuleText(name)(path)(source) with
                                    | Error(error) -> Error(error)
                                    | Ok(module) -> loadReachable(name)(Ashes.Collection.List.append(importedModuleNames(module.imports))(rest))(module :: loaded)(shipped)

let recursive planUnits (entryName: Str) (loaded: List(LoadedShippedModule)) =
    match loaded with
        | [] -> []
        | LoadedShippedModule { name = name, sourcePath = path, imports = imports, interface = interface } :: rest ->
            let source =
                if name == entryName
                then ProjectModuleSource(path)
                else ShippedModuleSource(path)
            in ModulePlanUnit(name = name, source = source, imports = imports, interface = interface, dependencies = []) :: planUnits(entryName)(rest)

let plannedSourcePath source =
    match source with
        | ProjectModuleSource(path) -> path
        | ShippedModuleSource(path) -> path
        | InlineModuleSource(path, _) -> path

let recursive stitchUnits (entryName: Str) (loaded: List(LoadedShippedModule)) (planned: List(PlannedModule)) =
    match planned with
        | [] -> Ok([])
        | PlannedModule { name = name, source = source, imports = imports, interface = interface } :: rest ->
            match findLoaded(name)(loaded) with
                | None -> Error(ShippedModuleMissing(entryName)(name))
                | Some(module) ->
                    match stitchUnits(entryName)(loaded)(rest) with
                        | Error(error) -> Error(error)
                        | Ok(units) ->
                            Ok(SemanticStitchUnit(
                                name = name,
                                packageId = if name == entryName
                                then "app"
                                else shippedPackageId,
                                sourcePath = plannedSourcePath(source),
                                imports = imports,
                                interface = interface,
                                program = module.program,
                                isEntry = name == entryName
                            ) :: units)

// The entry (`entryName`, its `entrySource` including any import header) plus every shipped module
// it reaches, as one stitched project whose `program` lowers like any single program.
let stitchWithShippedModules (entryName: Str) (entryPath: Str) (entrySource: Str) (shipped: List(ShippedModuleText)) =
    match loadModuleText(entryName)(entryPath)(entrySource) with
        | Error(error) -> Error(error)
        | Ok(entry) ->
            match loadReachable(entryName)(importedModuleNames(entry.imports))([entry])(shipped) with
                | Error(error) -> Error(error)
                | Ok(loaded) ->
                    match buildModulePlan(entryName)(planUnits(entryName)(loaded)) with
                        | Error(error) -> Error(ShippedPlanError(error))
                        | Ok(planned) ->
                            match stitchUnits(entryName)(loaded)(planned) with
                                | Error(error) -> Error(error)
                                | Ok(units) ->
                                    match stitchProjectSyntax(units) with
                                        | Error(error) -> Error(ShippedSyntaxStitchError(error))
                                        | Ok(project) -> Ok(project)
