// Turns a resolved project graph into a dependency-ordered module compilation plan.
//
// Invariants:
// - Project, include, dependency, and shipped sources retain their namespace boundaries.
// - Imports are parsed and resolved against built interfaces before modules are ordered.
// - Ambiguous, missing, or cyclic modules fail planning rather than selecting an arbitrary source.
// - Reachable parse diagnostics retain source attribution and deterministic discovery order.
// - A reserved `Ashes.*` import resolves only against the supplied shipped module texts (or an
//   intrinsic builtin module, which has no source): the module itself when a text carries its
//   name, otherwise its parent module (a type import such as `Ashes.IO.Path.Style`), and every
//   shipped module's own imports are followed the same way. Without shipped texts only the
//   intrinsic modules resolve.
// - The plan keeps every parsed program beside its planned module, so a consumer that stitches
//   the project never parses a source twice.

import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.reverse as reverseList
import Ashes.IO.Path
import Ashes.Internal.deepCopy as deepCopy
import AshesCompiler.Frontend.ImportHeader
import AshesCompiler.Frontend.ImportHeader.ImportHeaderError
import AshesCompiler.Frontend.InlineModules
import AshesCompiler.Frontend.ModuleInterface
import AshesCompiler.Frontend.ModuleInterface.ModuleInterfaceBuildError
import AshesCompiler.Frontend.ModulePlan
import AshesCompiler.Frontend.ModulePlan.ModulePlanError
import AshesCompiler.Frontend.ModulePlan.ModulePlanUnit
import AshesCompiler.Frontend.ModulePlan.PlannedModule
import AshesCompiler.Frontend.ModuleSource
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.ProjectDependencyGraph
import AshesCompiler.Semantics.ProjectDiagnostics
import AshesCompiler.Semantics.ProjectDiscovery
import AshesCompiler.Semantics.ProjectSourceEnumeration
import AshesCompiler.Semantics.ProjectSourceEnumeration.ProjectSourceEnumerationError
import AshesCompiler.Semantics.ShippedModuleStitching
export (
    type PlannedModuleProgram(..),
    type ProjectCompilationPlan(..),
    type ProjectCompilationError(..),
    value plannedProgram,
    value buildProjectCompilationPlan,
    value buildProjectCompilationPlanWithShipped,
)

type PlannedModuleProgram =
    | name: Str
    | program: ProgramSyntax

// `dependencies` is the resolved graph the plan was built over, so a consumer can attribute each
// planned module to the package whose source root contains it without resolving the graph again.
type ProjectCompilationPlan =
    | sourceFiles: List(Str)
    | modules: List(PlannedModule)
    | programs: List(PlannedModuleProgram)
    | dependencies: List(ResolvedProjectDependency)

type ProjectCompilationError =
    | ProjectCompilationDependencyGraphError(ProjectDependencyGraphError)
    | ProjectCompilationSourceEnumerationError(ProjectSourceEnumerationError)
    | ProjectCompilationUnmappedSource(Str)
    | ProjectCompilationMissingModule(Str, List(Str))
    | ProjectCompilationAmbiguousModule(Str, List(Str))
    | ProjectCompilationReadError(Str, Str)
    | ProjectCompilationImportHeaderError(Str, ImportHeaderError)
    | ProjectCompilationParseError(List(ProjectDiagnostic))
    | ProjectCompilationInterfaceError(Str, ModuleInterfaceBuildError)
    | ProjectCompilationInlineModuleError(Str, InlineModuleError)
    | ProjectCompilationInlineFileCollision(Str, Str)
    | ProjectCompilationReservedInlineModule(Str, Str)
    | ProjectCompilationModulePlanError(ModulePlanError)
    deriving {Eq, Show}

type IndexedProjectSource =
    | name: Str
    | path: Str

type LoadedProjectModule =
    | units: List(ModulePlanUnit)
    | programs: List(PlannedModuleProgram)
    | dependencies: List(Str)
    | names: List(Str)
    | diagnosticSources: List(ProjectDiagnosticSource)

type ReachableModuleSet =
    | names: List(Str)
    | units: List(ModulePlanUnit)
    | programs: List(PlannedModuleProgram)
    | diagnosticSources: List(ProjectDiagnosticSource)

type ParsedProjectModuleResult =
    | unit: Maybe(ModulePlanUnit)
    | program: Maybe(PlannedModuleProgram)
    | diagnosticSource: Maybe(ProjectDiagnosticSource)

type ParsedProjectModuleCollection =
    | units: List(ModulePlanUnit)
    | programs: List(PlannedModuleProgram)
    | diagnosticSources: List(ProjectDiagnosticSource)

type ExpandedProjectModuleContext =
    | name: Str
    | scope: Str
    | path: Str
    | sources: List(IndexedProjectSource)
    | imports: List(ImportHeaderEntry)
    | outerSource: Str
    | inlineNames: List(Str)

type ParsedProjectModule =
    | name: Str
    | source: ResolvedModuleSource
    | imports: List(ImportHeaderEntry)
    | dependencies: List(Str)
    | directModules: List(Str)

let cu n s i m d = deepCopy(ModulePlanUnit(name = n, source = s, imports = i, interface = m, dependencies = d))

let projectEntryPath (layout: ProjectLayout) =
    match layout with
        | ProjectLayout { entryPath = entryPath } -> entryPath

let projectEntryModuleName (layout: ProjectLayout) =
    match layout with
        | ProjectLayout { entryModuleName = entryModuleName } -> entryModuleName

let recursive containsText (value: Str) (values: List(Str)) =
    match values with
        | [] -> false
        | candidate :: rest ->
            if candidate == value
            then true
            else containsText(value)(rest)

let recursive pathsForModule (name: Str) (sources: List(IndexedProjectSource)) =
    match sources with
        | [] -> []
        | IndexedProjectSource { name = candidate, path = path } :: rest ->
            if candidate == name
            then deepCopy(path) :: pathsForModule(name)(rest)
            else pathsForModule(name)(rest)

let recursive indexedModuleNames (sources: List(IndexedProjectSource)) =
    match sources with
        | [] -> []
        | IndexedProjectSource { name = name, path = _path } :: rest -> name :: indexedModuleNames(rest)

let isOutsideRoot (style: Style) (relative: Str) =
    if relative == ".."
    then true
    else Ashes.Text.startsWith(relative)(".." + separator(style))

let recursive relativeSourcePath (style: Style) (roots: List(Str)) (path: Str) =
    match roots with
        | [] -> None
        | root :: rest ->
            match path
            |> deepCopy
            |> relativeTo(style)(root) with
                | relative ->
                    if isOutsideRoot(style)(relative)
                    then relativeSourcePath(style)(rest)(path)
                    else Some(relative)

let moduleNameFromRelative (style: Style) (relative: Str) =
    match Ashes.Text.take(deepCopy(relative))(Ashes.Text.length(relative) - 4) with
        | withoutExtension ->
            style
            |> separator
            |> Ashes.Text.split(withoutExtension)
            |> Ashes.Text.join(".")

let indexedSource (style: Style) (roots: List(Str)) (entryPath: Str) (entryModuleName: Str) (path: Str) =
    if path == entryPath
    then Ok(IndexedProjectSource(name = deepCopy(entryModuleName), path = path))
    else
        match path
        |> deepCopy
        |> relativeSourcePath(style)(roots) with
            | None -> Error(ProjectCompilationUnmappedSource(path))
            | Some(relative) -> Ok(IndexedProjectSource(name = moduleNameFromRelative(style)(relative), path = path))

let recursive indexSources style roots entryPath entryModuleName paths =
    match paths with
        | [] -> Ok([])
        | path :: rest ->
            match indexedSource(style)(roots)(entryPath)(entryModuleName)(path) with
                | Error(error) -> Error(error)
                | Ok(source) ->
                    match indexSources(style)(roots)(entryPath)(entryModuleName)(rest) with
                        | Error(error) -> Error(error)
                        | Ok(sources) -> Ok(source :: sources)

let ensureEntrySource (layout: ProjectLayout) (paths: List(Str)) =
    if containsText(projectEntryPath(layout))(paths)
    then paths
    else projectEntryPath(layout) :: paths

let recursive dropLast (parts: List(Str)) =
    match parts with
        | [] -> []
        | _last :: [] -> []
        | head :: rest -> head :: dropLast(rest)

let parentModuleName (name: Str) =
    match "."
    |> Ashes.Text.split(name)
    |> dropLast with
        | parts ->
            match Ashes.Text.join(".")(parts) with
                | "" -> None
                | parent -> Some(parent)

let hasIndexedModule (name: Str) (sources: List(IndexedProjectSource)) =
    match pathsForModule(name)(sources) with
        | [] -> false
        | _ -> true

let recursive nearestIndexedAncestor (name: Str) (sources: List(IndexedProjectSource)) =
    match parentModuleName(name) with
        | None -> None
        | Some(parent) ->
            if hasIndexedModule(parent)(sources)
            then Some(parent)
            else nearestIndexedAncestor(parent)(sources)

let dependencyLoadNames (sources: List(IndexedProjectSource)) (available: List(Str)) (entry: ImportHeaderEntry) =
    if containsText(entry.modulePath)(available)
    then [deepCopy(entry.modulePath)]
    else
        match (hasIndexedModule(entry.modulePath)(sources), nearestIndexedAncestor(entry.modulePath)(sources)) with
            | (true, Some(_ancestor)) -> [deepCopy(entry.modulePath)]
            | (true, None) -> [deepCopy(entry.modulePath)]
            | (false, Some(ancestor)) -> [ancestor]
            | (false, None) -> [deepCopy(entry.modulePath)]

let recursive deps (sources: List(IndexedProjectSource)) (available: List(Str)) (imports: List(ImportHeaderEntry)) =
    match imports with
        | [] -> []
        | entry :: rest ->
            appendList(
                dependencyLoadNames(sources)(available)(entry),
                deps(sources)(available)(rest)
            )

let directChildName (parent: Str) (candidate: Str) =
    if parent == ""
    then
        if Ashes.Text.contains(candidate)(".")
        then None
        else
            candidate
            |> deepCopy
            |> Some
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

let recursive directModuleNames (parent: Str) (names: List(Str)) =
    match names with
        | [] -> []
        | name :: rest ->
            match directChildName(parent)(name) with
                | None -> directModuleNames(parent)(rest)
                | Some(child) -> child :: directModuleNames(parent)(rest)

let recursive inlineModuleNames (modules: List(InlineModuleInfo)) =
    match modules with
        | [] -> []
        | InlineModuleInfo { name = name } :: rest -> name :: inlineModuleNames(rest)

let recursive directModulePaths (parent: Str) (names: List(Str)) =
    match names with
        | [] -> []
        | name :: rest ->
            match directChildName(parent)(name) with
                | None -> directModulePaths(parent)(rest)
                | Some(_child) -> deepCopy(name) :: directModulePaths(parent)(rest)

let finishParsedProjectModule path module parsed =
    match (module, parsed) with
        | (ParsedProjectModule { name = n, source = s, imports = i, dependencies = d, directModules = m }, ProgramParseResult { program = program, diagnostics = [] }) ->
            match buildModuleInterface(n)(m)(program) with
                | Error(error) ->
                    error
                    |> ProjectCompilationInterfaceError(path)
                    |> Error
                | Ok(moduleInterface) ->
                    Some(d
                    |> deepCopy
                    |> cu(deepCopy(n))(deepCopy(s))(deepCopy(i))(
                        deepCopy(moduleInterface)
                    ))
                    |> (given (unit) -> ParsedProjectModuleResult(unit = unit, program = Some(PlannedModuleProgram(name = deepCopy(n), program = program)), diagnosticSource = None))
                    |> Ok
        | (_module, ProgramParseResult { diagnostics = diagnostics }) ->
            Ok(ParsedProjectModuleResult(
                unit = None,
                program = None,
                diagnosticSource = Some(ProjectDiagnosticSource(
                    sourcePath = path,
                    diagnostics = diagnostics
                ))
            ))

let parseProjectModule path module text =
    text
    |> parseProgram
    |> finishParsedProjectModule(path)(module)

let inlineParsedProjectModule path names name text =
    ParsedProjectModule(
        name = name,
        source = InlineModuleSource(path + "#" + name)(text),
        imports = [],
        dependencies = directModulePaths(name)(names),
        directModules = directModuleNames(name)(names)
    )

let parsedProjectModuleUnits result =
    match result with
        | ParsedProjectModuleResult { unit = Some(unit) } -> [unit]
        | ParsedProjectModuleResult { unit = None } -> []

let parsedProjectModulePrograms result =
    match result with
        | ParsedProjectModuleResult { program = Some(program) } -> [program]
        | ParsedProjectModuleResult { program = None } -> []

let parsedProjectModuleDiagnostics result =
    match result with
        | ParsedProjectModuleResult { diagnosticSource = Some(source) } -> [source]
        | ParsedProjectModuleResult { diagnosticSource = None } -> []

let prependParsedProjectModule result collection =
    match collection with
        | ParsedProjectModuleCollection { units = units, programs = programs, diagnosticSources = diagnosticSources } ->
            ParsedProjectModuleCollection(
                units = appendList(parsedProjectModuleUnits(result))(units),
                programs = appendList(parsedProjectModulePrograms(result))(programs),
                diagnosticSources = appendList(parsedProjectModuleDiagnostics(result))(diagnosticSources)
            )

let continueInlineModuleParsing parsed tailResult =
    match tailResult with
        | Error(error) -> Error(error)
        | Ok(collection) ->
            collection
            |> prependParsedProjectModule(parsed)
            |> Ok

let recursive parseInlineModules (path: Str) (names: List(Str)) (modules: List(InlineModuleInfo)) =
    match modules with
        | [] -> Ok(ParsedProjectModuleCollection(units = [], programs = [], diagnosticSources = []))
        | InlineModuleInfo { name = name, source = text } :: rest ->
            text
            |> parseProjectModule(path + "#" + name)(inlineParsedProjectModule(path)(names)(name)(text))
            |> continueParsedInlineModule(path)(names)(rest)
and continueParsedInlineModule path names rest result =
    match result with
        | Error(error) -> Error(error)
        | Ok(parsed) ->
            rest
            |> parseInlineModules(path)(names)
            |> continueInlineModuleParsing(parsed)

let recursive validateInlineModuleSources path sources modules =
    match modules with
        | [] -> Ok(Unit)
        | InlineModuleInfo { name = name } :: rest ->
            match (name == "Ashes", Ashes.Text.startsWith(name)("Ashes.")) with
                | (true, _) ->
                    name
                    |> ProjectCompilationReservedInlineModule(path)
                    |> Error
                | (_, true) ->
                    name
                    |> ProjectCompilationReservedInlineModule(path)
                    |> Error
                | (false, false) ->
                    match pathsForModule(name)(sources) with
                        | collision :: _rest ->
                            collision
                            |> ProjectCompilationInlineFileCollision(name)
                            |> Error
                        | [] -> validateInlineModuleSources(path)(sources)(rest)

let appendOuterParsedModule outer collection =
    match collection with
        | ParsedProjectModuleCollection { units = units, programs = programs, diagnosticSources = diagnosticSources } ->
            ParsedProjectModuleCollection(
                units = outer
                |> parsedProjectModuleUnits
                |> appendList(units),
                programs = outer
                |> parsedProjectModulePrograms
                |> appendList(programs),
                diagnosticSources = outer
                |> parsedProjectModuleDiagnostics
                |> appendList(diagnosticSources)
            )

let completeExpandedModule context collection =
    match (context, collection) with
        | (ExpandedProjectModuleContext { name = name, sources = sources, imports = imports, inlineNames = inlineNames }, ParsedProjectModuleCollection { units = units, programs = programs, diagnosticSources = diagnosticSources }) ->
            LoadedProjectModule(
                units = units,
                programs = programs,
                dependencies = deps(sources)(name :: deepCopy(inlineNames))(imports),
                names = appendList(inlineNames)([name]),
                diagnosticSources = diagnosticSources
            )
            |> deepCopy
            |> Ok

let finishOuterProjectModule context collection result =
    match result with
        | Error(error) -> Error(error)
        | Ok(outer) ->
            collection
            |> appendOuterParsedModule(outer)
            |> completeExpandedModule(context)

let outerParsedProjectModule context =
    match context with
        | ExpandedProjectModuleContext { name = name, scope = scope, path = path, imports = imports, inlineNames = inlineNames } ->
            ParsedProjectModule(
                name = name,
                source = ProjectModuleSource(path),
                imports = imports,
                dependencies = deepCopy(inlineNames),
                directModules = inlineNames
                |> deepCopy
                |> directModuleNames(scope)
            )

let parseOuterProjectModule context collection =
    match context with
        | ExpandedProjectModuleContext { path = path, outerSource = outerSource } ->
            outerSource
            |> parseProjectModule(path)(outerParsedProjectModule(context))
            |> finishOuterProjectModule(context)(collection)

let finishInlineProjectModules context result =
    match result with
        | Error(error) -> Error(error)
        | Ok(collection) -> parseOuterProjectModule(context)(collection)

let finishValidatedInlineModules inlineModules context result =
    match (context, result) with
        | (_context, Error(error)) -> Error(error)
        | (ExpandedProjectModuleContext { path = path, inlineNames = inlineNames }, Ok(_)) ->
            inlineModules
            |> deepCopy
            |> parseInlineModules(path)(deepCopy(inlineNames))
            |> finishInlineProjectModules(context)

let validateExpandedInlineModules inlineModules context =
    match context with
        | ExpandedProjectModuleContext { path = path, sources = sources } ->
            inlineModules
            |> validateInlineModuleSources(path)(sources)
            |> finishValidatedInlineModules(inlineModules)(context)

let finishExpandedModule name scope path sources imports expansion =
    match expansion with
        | InlineModuleExpansion { source = outerSource, modules = inlineModules } ->
            validateExpandedInlineModules(inlineModules)(ExpandedProjectModuleContext(
                name = name,
                scope = scope,
                path = path,
                sources = sources,
                imports = imports,
                outerSource = outerSource,
                inlineNames = inlineModuleNames(inlineModules)
            ))

let expandLoadedSource name scope path sources imports source =
    match expandInlineModules(scope)(source) with
        | Error(error) ->
            error
            |> ProjectCompilationInlineModuleError(path)
            |> Error
        | Ok(expansion) -> finishExpandedModule(name)(scope)(path)(sources)(imports)(expansion)

let expandLoadedModule (entryModuleName: Str) (name: Str) =
    (let scope =
        if name == entryModuleName
        then ""
        else name
    in expandLoadedSource(name)(scope))

let parseLoadedModule entryModuleName name path sources source =
    match parseImportHeader(source) with
        | Error(error) ->
            error
            |> ProjectCompilationImportHeaderError(path)
            |> Error
        | Ok(ParsedImportHeader { imports = imports, sourceWithoutImports = sourceWithoutImports }) ->
            expandLoadedModule(entryModuleName)(name)(path)(sources)(deepCopy(imports))(sourceWithoutImports)

let readIndexedModule entryModuleName name path sources =
    match Ashes.IO.File.readText(path) with
        | Error(error) ->
            error
            |> ProjectCompilationReadError(path)
            |> Error
        | Ok(source) -> parseLoadedModule(entryModuleName)(name)(path)(sources)(source)

let recursive findShippedText (name: Str) (shipped: List(ShippedModuleText)) =
    match shipped with
        | [] -> None
        | candidate :: rest ->
            if candidate.moduleName == name
            then Some(candidate)
            else findShippedText(name)(rest)

let isShippedOrIntrinsic (name: Str) (shipped: List(ShippedModuleText)) =
    match findShippedText(name)(shipped) with
        | Some(_) -> true
        | None -> isIntrinsicBuiltinModule(name)

// The module a reserved import name loads: the name itself when it is a shipped or intrinsic
// module, otherwise its parent (an `Ashes.IO.Path.Style`-style type import names a type of the
// parent module), otherwise the name itself so the load reports it as missing.
let shippedLoadTarget (name: Str) (shipped: List(ShippedModuleText)) =
    if isShippedOrIntrinsic(name)(shipped)
    then name
    else
        match parentModuleName(name) with
            | Some(parent) ->
                if isShippedOrIntrinsic(parent)(shipped)
                then parent
                else name
            | None -> name

let recursive importedModulePaths (entries: List(ImportHeaderEntry)) =
    match entries with
        | [] -> []
        | ImportHeaderEntry { modulePath = modulePath } :: rest -> deepCopy(modulePath) :: importedModulePaths(rest)

let shippedUnit name path imports program =
    match buildModuleInterface(name)([])(program) with
        | Error(error) ->
            error
            |> ProjectCompilationInterfaceError(path)
            |> Error
        | Ok(moduleInterface) ->
            LoadedProjectModule(
                units = [cu(deepCopy(name))(ShippedModuleSource(path))(deepCopy(imports))(deepCopy(moduleInterface))([])],
                programs = [PlannedModuleProgram(name = deepCopy(name), program = program)],
                dependencies = importedModulePaths(imports),
                names = [deepCopy(name)],
                diagnosticSources = []
            ) |> Ok

let parseShippedModule name path source =
    match parseImportHeader(source) with
        | Error(error) ->
            error
            |> ProjectCompilationImportHeaderError(path)
            |> Error
        | Ok(ParsedImportHeader { imports = imports, sourceWithoutImports = sourceWithoutImports }) ->
            match parseProgram(sourceWithoutImports) with
                | ProgramParseResult { program = program, diagnostics = [] } -> shippedUnit(name)(path)(imports)(program)
                | ProgramParseResult { diagnostics = diagnostics } ->
                    Ok(LoadedProjectModule(
                        units = [],
                        programs = [],
                        dependencies = [],
                        names = [deepCopy(name)],
                        diagnosticSources = [ProjectDiagnosticSource(sourcePath = path, diagnostics = diagnostics)]
                    ))

// An intrinsic builtin module has no source: its members are reached through qualified access,
// so it plans as an empty module whose interface exports nothing.
let intrinsicModule name = shippedUnit(name)("<builtin>")([])(ProgramSyntax(items = [], body = None))

let loadShippedModule (name: Str) (shipped: List(ShippedModuleText)) (sources: List(IndexedProjectSource)) =
    match findShippedText(name)(shipped) with
        | Some(ShippedModuleText { sourcePath = path, source = source }) -> parseShippedModule(name)(path)(source)
        | None ->
            if isIntrinsicBuiltinModule(name)
            then intrinsicModule(name)
            else
                sources
                |> indexedModuleNames
                |> ProjectCompilationMissingModule(name)
                |> Error

let loadNamedModule entryModuleName shipped (name: Str) (sources: List(IndexedProjectSource)) =
    if Ashes.Text.startsWith(name)("Ashes.")
    then loadShippedModule(name)(shipped)(sources)
    else
        match pathsForModule(name)(sources) with
            | [] ->
                sources
                |> indexedModuleNames
                |> ProjectCompilationMissingModule(name)
                |> Error
            | path :: [] -> readIndexedModule(entryModuleName)(name)(path)(sources)
            | paths ->
                paths
                |> ProjectCompilationAmbiguousModule(name)
                |> Error

let loadTargetName (name: Str) (shipped: List(ShippedModuleText)) =
    if Ashes.Text.startsWith(name)("Ashes.")
    then shippedLoadTarget(name)(shipped)
    else name

let recursive loadReachableModules entryModuleName shipped pending loaded reversedUnits reversedPrograms reversedDiagnosticSources sources =
    match pending with
        | [] ->
            Ok(ReachableModuleSet(
                names = loaded,
                units = reverseList(reversedUnits),
                programs = reverseList(reversedPrograms),
                diagnosticSources = reverseList(reversedDiagnosticSources)
            ))
        | pendingName :: rest ->
            let name = loadTargetName(pendingName)(shipped)
            in
                if containsText(name)(loaded)
                then
                    loadReachableModules(
                        entryModuleName,
                        shipped,
                        rest,
                        loaded,
                        reversedUnits,
                        reversedPrograms,
                        reversedDiagnosticSources,
                        sources
                    )
                else
                    match loadNamedModule(deepCopy(entryModuleName))(shipped)(name)(sources) with
                        | Error(error) -> Error(error)
                        | Ok(LoadedProjectModule { units = units, programs = programs, dependencies = dependencies, names = names, diagnosticSources = diagnosticSources }) ->
                            loadReachableModules(
                                entryModuleName,
                                shipped,
                                appendList(deepCopy(dependencies))(rest),
                                appendList(deepCopy(names))(loaded),
                                appendList(units
                                |> deepCopy
                                |> reverseList)(reversedUnits),
                                appendList(reverseList(programs))(reversedPrograms),
                                appendList(
                                    diagnosticSources
                                    |> deepCopy
                                    |> reverseList,
                                    reversedDiagnosticSources
                                ),
                                sources
                            )

let recursive lastModuleName names =
    match names with
        | [] -> None
        | name :: [] ->
            name
            |> deepCopy
            |> Some
        | _name :: rest -> lastModuleName(rest)

let planIndexedSources (layout: ProjectLayout) shipped (dependencies: List(ResolvedProjectDependency)) (paths: List(Str)) (sources: List(IndexedProjectSource)) =
    (let loadLayout = deepCopy(layout)
    in
        match loadReachableModules(
            projectEntryModuleName(loadLayout),
            shipped,
            [projectEntryModuleName(layout)],
            [],
            [],
            [],
            [],
            sources
        ) with
            | Error(error) -> Error(error)
            | Ok(ReachableModuleSet { diagnosticSources = _diagnostic :: _rest as diagnosticSources }) ->
                diagnosticSources
                |> orderProjectDiagnostics
                |> ProjectCompilationParseError
                |> Error
            | Ok(ReachableModuleSet { names = names, units = units, programs = programs, diagnosticSources = [] }) ->
                match lastModuleName(names) with
                    | None ->
                        []
                        |> ProjectCompilationMissingModule("")
                        |> Error
                    | Some(entryModuleName) ->
                        match buildModulePlan(entryModuleName)(units) with
                            | Error(error) -> Error(ProjectCompilationModulePlanError(error))
                            | Ok(modules) -> Ok(ProjectCompilationPlan(sourceFiles = paths, modules = modules, programs = programs, dependencies = dependencies)))

let indexEnumeratedSources (style: Style) (layout: ProjectLayout) shipped dependencies (roots: List(Str)) (paths: List(Str)) =
    (let retainedLayout = deepCopy(layout)
    in
        match paths
        |> deepCopy
        |> indexSources(style)(roots)(layout
        |> deepCopy
        |> projectEntryPath)(projectEntryModuleName(layout)) with
            | Error(error) -> Error(error)
            | Ok(sources) -> planIndexedSources(retainedLayout)(shipped)(dependencies)(paths)(sources))

let projectSourceRoots (layout: ProjectLayout) =
    match layout with
        | ProjectLayout { sourceRoots = sourceRoots, includeRoots = includeRoots } ->
            appendList(
                sourceRoots,
                includeRoots
            )

let recursive dependencySourceRoots dependencies =
    match dependencies with
        | [] -> []
        | ResolvedProjectDependency { sourceRoots = roots } :: rest ->
            rest
            |> dependencySourceRoots
            |> appendList(roots)

let compilationSourceRoots (layout: ProjectLayout) (dependencies: List(ResolvedProjectDependency)) =
    dependencies
    |> dependencySourceRoots
    |> appendList(projectSourceRoots(layout))

let planCompilationRoots style (layout: ProjectLayout) shipped dependencies roots =
    match enumerateProjectSourceFiles(style)(roots) with
        | Error(error) -> Error(ProjectCompilationSourceEnumerationError(error))
        | Ok(enumerated) ->
            enumerated
            |> ensureEntrySource(deepCopy(layout))
            |> indexEnumeratedSources(style)(layout)(shipped)(dependencies)(roots)

let continueProjectDependencyGraph style (layout: ProjectLayout) shipped graphResult =
    match graphResult with
        | Error(error) -> Error(ProjectCompilationDependencyGraphError(error))
        | Ok(ProjectDependencyGraph { dependencies = dependencies }) ->
            dependencies
            |> compilationSourceRoots(deepCopy(layout))
            |> planCompilationRoots(style)(layout)(shipped)(dependencies)

// Plans the project with the reserved `Ashes.*` imports resolved against `shipped`, the in-memory
// standard-library texts a compile driver has read from the installed layout.
let buildProjectCompilationPlanWithShipped (style: Style) (layout: ProjectLayout) (shipped: List(ShippedModuleText)) =
    layout
    |> deepCopy
    |> resolveProjectDependencyGraph(style)
    |> continueProjectDependencyGraph(style)(layout)(shipped)

let buildProjectCompilationPlan (style: Style) (layout: ProjectLayout) = buildProjectCompilationPlanWithShipped(style)(layout)([])

// The parsed program planned under `name`, if the plan loaded one.
let recursive plannedProgram (name: Str) (programs: List(PlannedModuleProgram)) =
    match programs with
        | [] -> None
        | PlannedModuleProgram { name = candidate, program = program } :: rest ->
            if candidate == name
            then Some(program)
            else plannedProgram(name)(rest)
