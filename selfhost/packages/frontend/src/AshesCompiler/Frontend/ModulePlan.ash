// Plans the reachable module graph from an entry module and its resolved imports.
//
// Invariants:
// - Every planned import is resolved against a module interface before compilation.
// - Dependencies precede dependents and otherwise retain deterministic source order.
// - Missing modules and import cycles are reported instead of producing partial plans.

import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.reverse as reverseList
import Ashes.Internal.deepCopy as deepCopy
import AshesCompiler.Frontend.ImportHeader
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.ModuleSource
export (
    type ModulePlanUnit(..),
    type PlannedModule(..),
    type ModulePlanError(..),
    value buildModulePlan,
)

type ModulePlanUnit =
    | name: Str
    | source: ResolvedModuleSource
    | imports: List(ImportHeaderEntry)
    | interface: ModuleImportInterface
    | dependencies: List(Str)
    deriving {Eq, Show}

type PlannedModule =
    | name: Str
    | source: ResolvedModuleSource
    | imports: List(ResolvedImport)
    | interface: ModuleImportInterface
    deriving {Eq, Show}

type ModulePlanError =
    | UnknownPlanEntry(Str)
    | DuplicatePlanModule(Str)
    | ModuleInterfaceNameMismatch(Str, Str)
    | ModulePlanImportError(Str, ImportResolutionError)
    | ModuleImportCycle(List(Str))
    | UnexportedNestedModule(Str, Str, Str)
    deriving {Eq, Show}

type ModulePlanState =
    | plannedNames: List(Str)
    | visitingNames: List(Str)
    | reversedModules: List(PlannedModule)

let planned n s i m = PlannedModule(name = n, source = s, imports = i, interface = m)

let st p v r = ModulePlanState(plannedNames = p, visitingNames = v, reversedModules = r)

let recursive containsName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest ->
            if candidate == name
            then true
            else containsName(name)(rest)

let publishUnit unit =
    match unit with
        | ModulePlanUnit { name = _name } as candidate -> deepCopy(candidate)

let recursive findUnit (name: Str) (units: List(ModulePlanUnit)) =
    match units with
        | [] -> None
        | (ModulePlanUnit { name = unitName } as unit) :: rest ->
            if unitName == name
            then
                unit
                |> publishUnit
                |> Some
            else findUnit(name)(rest)

let recursive interfacesFromUnits (units: List(ModulePlanUnit)) =
    match units with
        | [] -> []
        | ModulePlanUnit { interface = moduleInterface } :: rest ->
            deepCopy(
                moduleInterface
            ) :: interfacesFromUnits(rest)

let recursive validateUnits (units: List(ModulePlanUnit)) (seen: List(Str)) =
    match units with
        | [] -> None
        | ModulePlanUnit { name = unitName, interface = ModuleImportInterface { name = interfaceName } } :: rest ->
            if containsName(unitName)(seen)
            then
                Some(unitName
                |> deepCopy
                |> DuplicatePlanModule)
            else
                if interfaceName != unitName
                then
                    interfaceName
                    |> deepCopy
                    |> ModuleInterfaceNameMismatch(deepCopy(unitName))
                    |> Some
                else validateUnits(rest)(unitName :: seen)

let resolvedModuleName (resolved: ResolvedImport) =
    match resolved with
        | ResolvedModuleImport(moduleName, _alias, _sourceLine, _written) -> moduleName
        | ResolvedValueImport(moduleName, _exportName, _localName, _sourceLine, _written) -> moduleName
        | ResolvedTypeImport(moduleName, _exportName, _localName, _sourceLine, _written) -> moduleName

let recursive lastPart parts =
    match parts with
        | [] -> ""
        | value :: [] -> value
        | _head :: rest -> lastPart(rest)

let recursive dropLast parts =
    match parts with
        | [] -> []
        | _last :: [] -> []
        | head :: rest -> head :: dropLast(rest)

let parentAndChild name =
    (let parts = Ashes.Text.split(name)(".")
    in
        match parts
        |> dropLast
        |> Ashes.Text.join(".") with
            | "" -> None
            | parent -> Some((parent, lastPart(parts))))

let recursive interfaceExportsModule child exports =
    match exports with
        | [] -> false
        | ImportModuleExport(name) :: rest ->
            if name == child
            then true
            else interfaceExportsModule(child)(rest)
        | _export :: rest -> interfaceExportsModule(child)(rest)

let recursive findInterface name interfaces =
    match interfaces with
        | [] -> None
        | ModuleImportInterface { name = candidate, exports = exports } :: rest ->
            if candidate == name
            then Some(exports)
            else findInterface(name)(rest)

let isInlineModuleSource source =
    match source with
        | InlineModuleSource(_path, _text) -> true
        | _ -> false

let recursive findUnitSource name units =
    match units with
        | [] -> None
        | ModulePlanUnit { name = candidate, source = source } :: rest ->
            if candidate == name
            then Some(source)
            else findUnitSource(name)(rest)

let importerIsInside parent importer =
    if importer == parent
    then true
    else Ashes.Text.startsWith(importer)(parent + ".")

let recursive validateInlineImportPath importer name units interfaces =
    match findUnitSource(name)(units) with
        | Some(source) ->
            if isInlineModuleSource(source)
            then
                match parentAndChild(name) with
                    | None -> Ok(Unit)
                    | Some((parent, child)) ->
                        if importerIsInside(parent)(importer)
                        then validateInlineImportPath(importer)(parent)(units)(interfaces)
                        else
                            match findInterface(parent)(interfaces) with
                                | None -> validateInlineImportPath(importer)(parent)(units)(interfaces)
                                | Some(exports) ->
                                    if interfaceExportsModule(child)(exports)
                                    then validateInlineImportPath(importer)(parent)(units)(interfaces)
                                    else
                                        child
                                        |> UnexportedNestedModule(importer)(parent)
                                        |> Error
            else Ok(Unit)
        | None -> Ok(Unit)

let recursive validateInlineImports importer imports units interfaces =
    match imports with
        | [] -> Ok(Unit)
        | resolved :: rest ->
            match validateInlineImportPath(importer)(resolvedModuleName(resolved))(units)(interfaces) with
                | Error(error) -> Error(error)
                | Ok(_) -> validateInlineImports(importer)(rest)(units)(interfaces)

let recursive collectDependencyNames (imports: List(ResolvedImport)) (seen: List(Str)) (reversed: List(Str)) =
    match imports with
        | [] -> reverseList(reversed)
        | resolved :: rest ->
            if containsName(resolvedModuleName(resolved))(seen)
            then collectDependencyNames(rest)(seen)(reversed)
            else
                collectDependencyNames(
                    rest,
                    resolvedModuleName(resolved) :: seen,
                    resolvedModuleName(resolved) :: reversed
                )

let dependencyNames (imports: List(ResolvedImport)) = collectDependencyNames(imports)([])([])

let recursive dropBefore (name: Str) (names: List(Str)) =
    match names with
        | [] -> []
        | candidate :: rest ->
            if candidate == name
            then candidate :: rest
            else dropBefore(name)(rest)

let cycleChain (name: Str) (visiting: List(Str)) =
    appendList(visiting
    |> reverseList
    |> dropBefore(name))([deepCopy(name)])

let finalizeModule unit resolved originalVisiting state =
    match unit with
        | ModulePlanUnit { name = unitName, source = unitSource, interface = unitInterface } ->
            let module = planned(unitName)(unitSource)(resolved)(unitInterface)
            in
                module :: state.reversedModules
                |> st(unitName :: state.plannedNames)(originalVisiting)
                |> Ok

let recursive visitResolved unit resolved units interfaces originalVisiting state =
    match unit with
        | ModulePlanUnit { name = unitName, dependencies = unitDependencies } ->
            match validateInlineImports(unitName)(resolved)(units)(interfaces) with
                | Error(error) -> Error(error)
                | Ok(_) ->
                    match visitDependencies(
                        resolved
                        |> dependencyNames
                        |> appendList(unitDependencies),
                        units,
                        interfaces,
                        state
                    ) with
                        | Error(error) -> Error(error)
                        | Ok(afterDependencies) -> finalizeModule(unit)(resolved)(originalVisiting)(afterDependencies)
and visitFound unit units interfaces state =
    match unit with
        | ModulePlanUnit { name = unitName, imports = unitImports } ->
            match unitImports
            |> deepCopy
            |> resolveImports(interfaces) with
                | Error(error) ->
                    error
                    |> ModulePlanImportError(deepCopy(unitName))
                    |> Error
                | Ok(resolved) ->
                    visitResolved(
                        unit,
                        resolved,
                        units,
                        interfaces,
                        state.visitingNames,
                        st(state.plannedNames)(unitName :: state.visitingNames)(state.reversedModules)
                    )
and visitModule name units interfaces state =
    if containsName(name)(state.plannedNames)
    then Ok(state)
    else
        if containsName(name)(state.visitingNames)
        then
            Error(state.visitingNames
            |> cycleChain(name)
            |> ModuleImportCycle)
        else
            match findUnit(name)(units) with
                | None ->
                    Error(name
                    |> deepCopy
                    |> UnknownPlanEntry)
                | Some(unit) -> visitFound(unit)(units)(interfaces)(state)
and visitDependencies names units interfaces state =
    match names with
        | [] -> Ok(state)
        | name :: rest ->
            match visitModule(name)(units)(interfaces)(state) with
                | Error(error) -> Error(error)
                | Ok(next) -> visitDependencies(rest)(units)(interfaces)(next)

let buildWithInterfaces (entry: Str) (units: List(ModulePlanUnit)) (interfaces: List(ModuleImportInterface)) =
    match visitModule(
        entry,
        units,
        interfaces,
        st([])([])([])
    ) with
        | Error(error) -> Error(error)
        | Ok(state) ->
            state.reversedModules
            |> reverseList
            |> Ok

let buildModulePlan (entry: Str) (units: List(ModulePlanUnit)) =
    match validateUnits(units)([]) with
        | Some(error) -> Error(error)
        | None ->
            match findUnit(entry)(units) with
                | None ->
                    Error(entry
                    |> deepCopy
                    |> UnknownPlanEntry)
                | Some(_unit) ->
                    units
                    |> interfacesFromUnits
                    |> buildWithInterfaces(entry)(units)
