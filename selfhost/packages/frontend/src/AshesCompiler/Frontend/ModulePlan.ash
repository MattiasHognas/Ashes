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
    deriving {Eq, Show}

type ModulePlanState =
    | plannedNames: List(Str)
    | visitingNames: List(Str)
    | reversedModules: List(PlannedModule)

let recursive containsName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest ->
            if candidate == name
            then true
            else containsName(name)(rest)

let recursive findUnit (name: Str) (units: List(ModulePlanUnit)) =
    match units with
        | [] -> None
        | (ModulePlanUnit { name = unitName, source = _source, imports = _imports, interface = _moduleInterface } as unit) :: rest ->
            if unitName == name
            then Some(deepCopy(unit))
            else findUnit(name)(rest)

let recursive interfacesFromUnits (units: List(ModulePlanUnit)) =
    match units with
        | [] -> []
        | ModulePlanUnit { name = _name, source = _source, imports = _imports, interface = moduleInterface } :: rest -> deepCopy(moduleInterface) :: interfacesFromUnits(rest)

let recursive validateUnits (units: List(ModulePlanUnit)) (seen: List(Str)) =
    match units with
        | [] -> None
        | ModulePlanUnit { name = unitName, source = _source, imports = _imports, interface = ModuleImportInterface { name = interfaceName, exports = _exports } } :: rest ->
            if containsName(unitName)(seen)
            then Some(DuplicatePlanModule(deepCopy(unitName)))
            else
                if interfaceName != unitName
                then Some(ModuleInterfaceNameMismatch(deepCopy(unitName))(deepCopy(interfaceName)))
                else validateUnits(rest)(unitName :: seen)

let resolvedModuleName (resolved: ResolvedImport) =
    match resolved with
        | ResolvedModuleImport(moduleName, _alias, _sourceLine, _written) -> moduleName
        | ResolvedValueImport(moduleName, _exportName, _localName, _sourceLine, _written) -> moduleName
        | ResolvedTypeImport(moduleName, _exportName, _localName, _sourceLine, _written) -> moduleName

let recursive collectDependencyNames (imports: List(ResolvedImport)) (seen: List(Str)) (reversed: List(Str)) =
    match imports with
        | [] -> reverseList(reversed)
        | resolved :: rest ->
            if containsName(resolvedModuleName(resolved))(seen)
            then collectDependencyNames(rest)(seen)(reversed)
            else collectDependencyNames(rest)(resolvedModuleName(resolved) :: seen)(resolvedModuleName(resolved) :: reversed)

let dependencyNames (imports: List(ResolvedImport)) = collectDependencyNames(imports)([])([])

let recursive dropBefore (name: Str) (names: List(Str)) =
    match names with
        | [] -> []
        | candidate :: rest ->
            if candidate == name
            then candidate :: rest
            else dropBefore(name)(rest)

let cycleChain (name: Str) (visiting: List(Str)) = appendList(dropBefore(name)(reverseList(visiting)))([deepCopy(name)])

let finalizeModule (unit: ModulePlanUnit) resolved originalVisiting (state: ModulePlanState) =
    match unit with
        | ModulePlanUnit { name = unitName, source = unitSource, imports = _unitImports, interface = unitInterface } -> Ok(ModulePlanState(plannedNames = unitName :: state.plannedNames, visitingNames = originalVisiting, reversedModules = PlannedModule(name = unitName, source = unitSource, imports = resolved, interface = unitInterface) :: state.reversedModules))

let recursive visitResolved (unit: ModulePlanUnit) (resolved: List(ResolvedImport)) (units: List(ModulePlanUnit)) (interfaces: List(ModuleImportInterface)) (originalVisiting: List(Str)) (state: ModulePlanState) =
    match visitDependencies(dependencyNames(resolved))(units)(interfaces)(state) with
        | Error(error) -> Error(error)
        | Ok(afterDependencies) -> finalizeModule(unit)(resolved)(originalVisiting)(afterDependencies)
and visitFound (unit: ModulePlanUnit) (units: List(ModulePlanUnit)) (interfaces: List(ModuleImportInterface)) (state: ModulePlanState) =
    match unit with
        | ModulePlanUnit { name = unitName, source = _unitSource, imports = unitImports, interface = _unitInterface } ->
            match resolveImports(interfaces)(deepCopy(unitImports)) with
                | Error(error) -> Error(ModulePlanImportError(deepCopy(unitName))(error))
                | Ok(resolved) -> visitResolved(unit)(resolved)(units)(interfaces)(state.visitingNames)(ModulePlanState(plannedNames = state.plannedNames, visitingNames = unitName :: state.visitingNames, reversedModules = state.reversedModules))
and visitModule (name: Str) (units: List(ModulePlanUnit)) (interfaces: List(ModuleImportInterface)) (state: ModulePlanState) =
    if containsName(name)(state.plannedNames)
    then Ok(state)
    else
        if containsName(name)(state.visitingNames)
        then Error(ModuleImportCycle(cycleChain(name)(state.visitingNames)))
        else
            match findUnit(name)(units) with
                | None -> Error(UnknownPlanEntry(deepCopy(name)))
                | Some(unit) -> visitFound(unit)(units)(interfaces)(state)
and visitDependencies (names: List(Str)) (units: List(ModulePlanUnit)) (interfaces: List(ModuleImportInterface)) (state: ModulePlanState) =
    match names with
        | [] -> Ok(state)
        | name :: rest ->
            match visitModule(name)(units)(interfaces)(state) with
                | Error(error) -> Error(error)
                | Ok(next) -> visitDependencies(rest)(units)(interfaces)(next)

let buildWithInterfaces (entry: Str) (units: List(ModulePlanUnit)) (interfaces: List(ModuleImportInterface)) =
    match visitModule(entry)(units)(interfaces)(ModulePlanState(plannedNames = [], visitingNames = [], reversedModules = [])) with
        | Error(error) -> Error(error)
        | Ok(state) -> Ok(reverseList(state.reversedModules))

let buildModulePlan (entry: Str) (units: List(ModulePlanUnit)) =
    match validateUnits(units)([]) with
        | Some(error) -> Error(error)
        | None ->
            match findUnit(entry)(units) with
                | None -> Error(UnknownPlanEntry(deepCopy(entry)))
                | Some(_unit) -> buildWithInterfaces(entry)(units)(interfacesFromUnits(units))
