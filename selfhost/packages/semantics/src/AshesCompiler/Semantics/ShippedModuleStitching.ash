// Stitches an entry program together with the shipped standard-library modules its import header
// reaches, from in-memory sources: header split, parse, interface, dependency-ordered plan, then
// the same semantic/syntax stitching a multi-module project goes through.
//
// Invariants:
// - Only `Ashes.*` imports and the entry's own inline modules are resolved: the shipped modules
//   against the supplied shipped module texts, an inline module as project planning lifts it.
// - Reachable modules are loaded transitively through their own import headers and through
//   their bare qualified references to shipped members (`Ashes.Text.join` with no import,
//   `QualifiedShippedReferences`), each once; a qualified-only module precedes its referrer in
//   the plan like an imported one.
// - The entry alone contributes a trailing expression; shipped modules contribute declarations.
// - Sources are never read from disk here; the caller supplies every text it wants resolvable.

import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.reverse as reverseList
import AshesCompiler.Frontend.ImportHeader
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.InlineModules
import AshesCompiler.Frontend.ModuleInterface
import AshesCompiler.Frontend.ModulePlan
import AshesCompiler.Frontend.ModuleSource
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.ModuleSemanticStitching
import AshesCompiler.Semantics.ProjectSyntaxStitching
import AshesCompiler.Semantics.QualifiedShippedReferences
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

// `qualifiedModules` are the shipped modules the body reaches only through bare qualified
// references; they are loaded like imports and ordered ahead of this module in the plan.
type LoadedShippedModule =
    | name: Str
    | sourcePath: Str
    | imports: List(ImportHeaderEntry)
    | qualifiedModules: List(Str)
    | program: ProgramSyntax
    | interface: ModuleImportInterface

let recursive shippedModuleNames (shipped: List(ShippedModuleText)) =
    match shipped with
        | [] -> []
        | ShippedModuleText { moduleName = moduleName } :: rest -> moduleName :: shippedModuleNames(rest)

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

// Every module a loaded module reaches: its imports, then the shipped modules its body names
// through bare qualified references.
let reachedModuleNames (module: LoadedShippedModule) =
    appendList(importedModuleNames(module.imports))(module.qualifiedModules)

// Splits the header off, parses the remainder, and builds the module's interface — the same
// three steps `ProjectCompilationPlanning` performs per project module.
let loadModuleText (shipped: List(ShippedModuleText)) name path source =
    match parseImportHeader(source) with
        | Error(error) ->
            error
            |> ShippedImportHeaderError(path)
            |> Error
        | Ok(ParsedImportHeader { imports = imports, sourceWithoutImports = body }) ->
            match parseProgram(body) with
                | ProgramParseResult { program = program, diagnostics = [] } ->
                    match buildModuleInterface(name)([])(program) with
                        | Error(error) ->
                            error
                            |> ShippedInterfaceError(path)
                            |> Error
                        | Ok(interface) ->
                            Ok(LoadedShippedModule(
                                name = name,
                                sourcePath = path,
                                imports = imports,
                                qualifiedModules = qualifiedShippedModulesNeedingSource(shippedModuleNames(shipped))(body),
                                program = program,
                                interface = interface
                            ))
                | ProgramParseResult { diagnostics = diagnostics } ->
                    diagnostics
                    |> Ashes.Trait.Show.show
                    |> ShippedParseError(path)
                    |> Error

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
                    then
                        name
                        |> UnsupportedNonShippedImport(importer)
                        |> Error
                    else
                        match findShipped(name)(shipped) with
                            | None ->
                                if isIntrinsicBuiltinModule(name)
                                then
                                    let program = ProgramSyntax(items = [], body = None)
                                    in
                                        match buildModuleInterface(name)([])(program) with
                                            | Error(error) ->
                                                error
                                                |> ShippedInterfaceError("<builtin>")
                                                |> Error
                                            | Ok(interface) ->
                                                loadReachable(name)(rest)(
                                                    LoadedShippedModule(
                                                        name = name,
                                                        sourcePath = "<builtin>",
                                                        imports = [],
                                                        qualifiedModules = [],
                                                        program = program,
                                                        interface = interface
                                                    ) :: loaded
                                                )(shipped)
                                else
                                    name
                                    |> ShippedModuleMissing(importer)
                                    |> Error
                            | Some(ShippedModuleText { sourcePath = path, source = source }) ->
                                match loadModuleText(shipped)(name)(path)(source) with
                                    | Error(error) -> Error(error)
                                    | Ok(module) ->
                                        loadReachable(name)(appendList(reachedModuleNames(module))(rest))(module :: loaded)(shipped)

let recursive inlineSourceText (name: Str) (inlineModules: List(InlineModuleInfo)) =
    match inlineModules with
        | [] -> None
        | InlineModuleInfo { name = candidate, source = source } :: rest ->
            if candidate == name
            then Some(source)
            else inlineSourceText(name)(rest)

let recursive planUnits (entryName: Str) (inlineModules: List(InlineModuleInfo)) (loaded: List(LoadedShippedModule)) =
    match loaded with
        | [] -> []
        | LoadedShippedModule { name = name, sourcePath = path, imports = imports, qualifiedModules = qualifiedModules, interface = interface } :: rest ->
            let source =
                if name == entryName
                then ProjectModuleSource(path)
                else
                    match inlineSourceText(name)(inlineModules) with
                        | Some(text) -> InlineModuleSource(path)(text)
                        | None -> ShippedModuleSource(path)
            in ModulePlanUnit(name = name, source = source, imports = imports, interface = interface, dependencies = qualifiedModules) :: planUnits(entryName)(inlineModules)(rest)

let isAppSource (source: ResolvedModuleSource) =
    match source with
        | ShippedModuleSource(_path) -> false
        | _ -> true

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
                | None ->
                    name
                    |> ShippedModuleMissing(entryName)
                    |> Error
                | Some(module) ->
                    match stitchUnits(entryName)(loaded)(rest) with
                        | Error(error) -> Error(error)
                        | Ok(units) ->
                            Ok(SemanticStitchUnit(
                                name = name,
                                packageId = if isAppSource(source)
                                then "app"
                                else shippedPackageId,
                                sourcePath = plannedSourcePath(source),
                                imports = imports,
                                interface = interface,
                                program = module.program,
                                isEntry = name == entryName
                            ) :: units)

// The module one level below `parent` that `candidate` names: `Geometry` below the file for
// `Geometry`, `Inner` below `Geometry` for `Geometry.Inner`.
let inlineChildName (parent: Str) (candidate: Str) =
    if parent == ""
    then
        if Ashes.Text.contains(candidate)(".")
        then None
        else Some(candidate)
    else
        let prefix = parent + "."
        in
            if Ashes.Text.startsWith(candidate)(prefix)
            then
                let remainder =
                    prefix
                    |> Ashes.Text.length
                    |> Ashes.Text.drop(candidate)
                in
                    if Ashes.Text.contains(remainder)(".")
                    then None
                    else Some(remainder)
            else None

// The short names and the full paths of the inline modules directly below `parent`.
let recursive inlineChildNames (parent: Str) (names: List(Str)) =
    match names with
        | [] -> []
        | name :: rest ->
            match inlineChildName(parent)(name) with
                | None -> inlineChildNames(parent)(rest)
                | Some(child) -> child :: inlineChildNames(parent)(rest)

let recursive inlineChildPaths (parent: Str) (names: List(Str)) =
    match names with
        | [] -> []
        | name :: rest ->
            match inlineChildName(parent)(name) with
                | None -> inlineChildPaths(parent)(rest)
                | Some(_child) -> name :: inlineChildPaths(parent)(rest)

let recursive inlineNames (inlineModules: List(InlineModuleInfo)) =
    match inlineModules with
        | [] -> []
        | InlineModuleInfo { name = name } :: rest -> name :: inlineNames(rest)

// One of the entry's inline modules, loaded as project planning loads it: no import header, an
// interface that exports its nested modules, and those nested modules ordered ahead of it.
let loadInlineModule (shipped: List(ShippedModuleText)) (entryPath: Str) (names: List(Str)) (inline: InlineModuleInfo) =
    match inline with
        | InlineModuleInfo { name = name, source = source } ->
            let path = entryPath + "#" + name
            in
                match parseProgram(source) with
                    | ProgramParseResult { program = program, diagnostics = [] } ->
                        match buildModuleInterface(name)(inlineChildNames(name)(names))(program) with
                            | Error(error) ->
                                error
                                |> ShippedInterfaceError(path)
                                |> Error
                            | Ok(interface) ->
                                Ok(LoadedShippedModule(
                                    name = name,
                                    sourcePath = path,
                                    imports = [],
                                    qualifiedModules = names
                                    |> inlineChildPaths(name)
                                    |> appendList(qualifiedShippedModulesNeedingSource(shippedModuleNames(shipped))(source)),
                                    program = program,
                                    interface = interface
                                ))
                    | ProgramParseResult { diagnostics = diagnostics } ->
                        diagnostics
                        |> Ashes.Trait.Show.show
                        |> ShippedParseError(path)
                        |> Error

let recursive loadInlineModules (shipped: List(ShippedModuleText)) (entryPath: Str) (names: List(Str)) (inlineModules: List(InlineModuleInfo)) =
    match inlineModules with
        | [] -> Ok([])
        | inline :: rest ->
            match loadInlineModule(shipped)(entryPath)(names)(inline) with
                | Error(error) -> Error(error)
                | Ok(module) ->
                    match loadInlineModules(shipped)(entryPath)(names)(rest) with
                        | Error(error) -> Error(error)
                        | Ok(modules) -> Ok(module :: modules)

let recursive reachedFromAll (modules: List(LoadedShippedModule)) =
    match modules with
        | [] -> []
        | module :: rest ->
            rest
            |> reachedFromAll
            |> appendList(reachedModuleNames(module))

// The entry (`entryName`, its `entrySource` including any import header) plus every shipped module
// it reaches, as one stitched project whose `program` lowers like any single program.
// Stage 0 loads `Ashes.Trait` into every program whether or not the program names it: the
// standard trait implementations a trait-mapped operator dispatches through live there. The
// module joins the entry's qualified modules, so it is loaded and planned as a dependency of
// the entry rather than dropped by the plan as unreached.
let standardTraitModuleNames (shipped: List(ShippedModuleText)) =
    match findShipped("Ashes.Trait")(shipped) with
        | Some(_module) -> ["Ashes.Trait"]
        | None -> []

// The entry with its inline modules attached as project planning attaches them: its interface
// names its top-level inline modules as its own submodules, so `Geometry.one` resolves inside the
// file, and every inline module is ordered ahead of it.
let loadEntryWithInlineModules (shipped: List(ShippedModuleText)) (entryName: Str) (entryPath: Str) (outerSource: Str) (names: List(Str)) =
    match loadModuleText(shipped)(entryName)(entryPath)(outerSource) with
        | Error(error) -> Error(error)
        | Ok(loadedEntry) ->
            match buildModuleInterface(entryName)(inlineChildNames("")(names))(loadedEntry.program) with
                | Error(error) ->
                    error
                    |> ShippedInterfaceError(entryPath)
                    |> Error
                | Ok(interface) ->
                    Ok((loadedEntry with qualifiedModules = loadedEntry.qualifiedModules |> appendList(standardTraitModuleNames(shipped)) |> appendList(names), interface = interface))

let stitchExpandedEntry (entryName: Str) (entryPath: Str) (outerSource: Str) (inlineModules: List(InlineModuleInfo)) (shipped: List(ShippedModuleText)) =
    (let names = inlineNames(inlineModules)
    in
        match loadEntryWithInlineModules(shipped)(entryName)(entryPath)(outerSource)(names) with
            | Error(error) -> Error(error)
            | Ok(entry) ->
                match loadInlineModules(shipped)(entryPath)(names)(inlineModules) with
                    | Error(error) -> Error(error)
                    | Ok(inlines) ->
                        match loadReachable(entryName)(inlines
                        |> reachedFromAll
                        |> appendList(reachedModuleNames(entry)))(entry :: inlines)(shipped) with
                            | Error(error) -> Error(error)
                            | Ok(loaded) ->
                                match loaded
                                |> planUnits(entryName)(inlineModules)
                                |> buildModulePlan(entryName) with
                                    | Error(error) -> Error(ShippedPlanError(error))
                                    | Ok(planned) ->
                                        match stitchUnits(entryName)(loaded)(planned) with
                                            | Error(error) -> Error(error)
                                            | Ok(units) ->
                                                match stitchProjectSyntax(units) with
                                                    | Error(error) -> Error(ShippedSyntaxStitchError(error))
                                                    | Ok(project) -> Ok(project))

// The entry's inline `module` blocks are lifted first, exactly as project planning lifts them, so
// a single file may declare and import its own modules.
let stitchWithShippedModules (entryName: Str) (entryPath: Str) (entrySource: Str) (shipped: List(ShippedModuleText)) =
    match expandInlineModules("")(entrySource) with
        | Error(error) ->
            error
            |> Ashes.Trait.Show.show
            |> ShippedParseError(entryPath)
            |> Error
        | Ok(InlineModuleExpansion { source = outerSource, modules = inlineModules }) -> stitchExpandedEntry(entryName)(entryPath)(outerSource)(inlineModules)(shipped)
