import Ashes.Collection.List.reverse as reverseList
import Ashes.Internal.deepCopy as deepCopy
import AshesCompiler.Frontend.ImportHeader
export (
    type ModuleImportExport(..),
    type ModuleImportInterface(..),
    type ResolvedImport(..),
    type ImportResolutionError(..),
    value resolveImports,
)

type ModuleImportExport =
    | ImportValueExport(Str)
    | ImportTypeExport(Str)
    | ImportConstructorExport(Str)
    | ImportModuleExport(Str)
    deriving {Eq, Show}

type ModuleImportInterface =
    | name: Str
    | exports: List(ModuleImportExport)
    deriving {Eq, Show}

type ResolvedImport =
    | ResolvedModuleImport(Str, Maybe(Str), Int, Str)
    | ResolvedValueImport(Str, Str, Str, Int, Str)
    | ResolvedTypeImport(Str, Str, Str, Int, Str)
    deriving {Eq, Show}

type ImportResolutionError =
    | UnknownImportModule(Int, Str)
    | UnknownImportExport(Int, Str, Str)
    | ConflictingResolvedImport(Int, Str)
    deriving {Eq, Show}

let recursive findModule name (interfaces: List(ModuleImportInterface)) =
    match interfaces with
        | [] -> None
        | interface :: rest ->
            if interface.name == name
            then Some(deepCopy(interface))
            else findModule(name)(rest)

let recursive hasValueExport name exports =
    match exports with
        | [] -> false
        | ImportValueExport(candidate) :: rest ->
            if candidate == name
            then true
            else hasValueExport(name)(rest)
        | _export :: rest -> hasValueExport(name)(rest)

let recursive hasTypeExport name exports =
    match exports with
        | [] -> false
        | ImportTypeExport(candidate) :: rest ->
            if candidate == name
            then true
            else hasTypeExport(name)(rest)
        | _export :: rest -> hasTypeExport(name)(rest)

let appendModuleSegment prefix segment =
    if prefix == ""
    then deepCopy(segment)
    else prefix + "." + segment

let recursive parentAndLeafSegments prefix segments =
    match segments with
        | [] -> None
        | _only :: [] -> None
        | parent :: leaf :: [] -> Some((appendModuleSegment(prefix)(parent), deepCopy(leaf)))
        | segment :: rest -> parentAndLeafSegments(appendModuleSegment(prefix)(segment))(rest)

let parentAndLeaf modulePath = parentAndLeafSegments("")(Ashes.Text.split(modulePath)("."))

let selectorLocalName selectorName alias =
    match alias with
        | Some(name) -> deepCopy(name)
        | None -> deepCopy(selectorName)

let resolveValueSelector (entry: ImportHeaderEntry) exportName (interface: ModuleImportInterface) =
    if hasValueExport(exportName)(interface.exports)
    then Ok(ResolvedValueImport(deepCopy(entry.modulePath))(deepCopy(exportName))(selectorLocalName(exportName)(entry.alias))(entry.sourceLine)(deepCopy(entry.written)))
    else Error(UnknownImportExport(entry.sourceLine)(deepCopy(entry.modulePath))(deepCopy(exportName)))

let resolveSelector (entry: ImportHeaderEntry) exportName interfaces =
    match findModule(entry.modulePath)(interfaces) with
        | None -> Error(UnknownImportModule(entry.sourceLine)(deepCopy(entry.modulePath)))
        | Some(interface) -> resolveValueSelector(entry)(exportName)(interface)

let resolveTypeSelector (entry: ImportHeaderEntry) parent leaf (interface: ModuleImportInterface) =
    if hasTypeExport(leaf)(interface.exports)
    then Ok(ResolvedTypeImport(deepCopy(parent))(deepCopy(leaf))(selectorLocalName(leaf)(entry.alias))(entry.sourceLine)(deepCopy(entry.written)))
    else Error(UnknownImportExport(entry.sourceLine)(deepCopy(parent))(deepCopy(leaf)))

let resolveUppercaseFallback (entry: ImportHeaderEntry) interfaces =
    match parentAndLeaf(entry.modulePath) with
        | None -> Error(UnknownImportModule(entry.sourceLine)(deepCopy(entry.modulePath)))
        | Some((parent, leaf)) ->
            match findModule(parent)(interfaces) with
                | None -> Error(UnknownImportModule(entry.sourceLine)(deepCopy(entry.modulePath)))
                | Some(interface) -> resolveTypeSelector(entry)(parent)(leaf)(interface)

let resolveWholeOrType (entry: ImportHeaderEntry) interfaces =
    match findModule(entry.modulePath)(interfaces) with
        | Some(_interface) -> Ok(ResolvedModuleImport(deepCopy(entry.modulePath))(deepCopy(entry.alias))(entry.sourceLine)(deepCopy(entry.written)))
        | None -> resolveUppercaseFallback(entry)(interfaces)

let resolveImport (entry: ImportHeaderEntry) interfaces =
    match entry.selector with
        | Some(exportName) -> resolveSelector(entry)(exportName)(interfaces)
        | None -> resolveWholeOrType(entry)(interfaces)

let differentResolvedTarget candidateModule candidateExport existingModule existingExport =
    if candidateModule != existingModule
    then true
    else candidateExport != existingExport

let resolvedImportConflicts candidate existing =
    match (candidate, existing) with
        | (ResolvedValueImport(candidateModule, candidateExport, candidateLocal, _candidateLine, _candidateWritten), ResolvedValueImport(existingModule, existingExport, existingLocal, _existingLine, _existingWritten)) ->
            if candidateLocal != existingLocal
            then false
            else differentResolvedTarget(candidateModule)(candidateExport)(existingModule)(existingExport)
        | (ResolvedValueImport(candidateModule, candidateExport, candidateLocal, _candidateLine, _candidateWritten), ResolvedTypeImport(existingModule, existingExport, existingLocal, _existingLine, _existingWritten)) ->
            if candidateLocal != existingLocal
            then false
            else differentResolvedTarget(candidateModule)(candidateExport)(existingModule)(existingExport)
        | (ResolvedTypeImport(candidateModule, candidateExport, candidateLocal, _candidateLine, _candidateWritten), ResolvedValueImport(existingModule, existingExport, existingLocal, _existingLine, _existingWritten)) ->
            if candidateLocal != existingLocal
            then false
            else differentResolvedTarget(candidateModule)(candidateExport)(existingModule)(existingExport)
        | (ResolvedTypeImport(candidateModule, candidateExport, candidateLocal, _candidateLine, _candidateWritten), ResolvedTypeImport(existingModule, existingExport, existingLocal, _existingLine, _existingWritten)) ->
            if candidateLocal != existingLocal
            then false
            else differentResolvedTarget(candidateModule)(candidateExport)(existingModule)(existingExport)
        | _ -> false

let recursive hasResolvedConflict candidate resolved =
    match resolved with
        | [] -> false
        | existing :: rest ->
            if resolvedImportConflicts(candidate)(existing)
            then true
            else hasResolvedConflict(candidate)(rest)

let resolvedLocalName resolved =
    match resolved with
        | ResolvedValueImport(_modulePath, _exportName, localName, _sourceLine, _written) -> localName
        | ResolvedTypeImport(_modulePath, _exportName, localName, _sourceLine, _written) -> localName
        | ResolvedModuleImport(_modulePath, _alias, _sourceLine, _written) -> ""

let resolvedSourceLine resolved =
    match resolved with
        | ResolvedValueImport(_modulePath, _exportName, _localName, sourceLine, _written) -> sourceLine
        | ResolvedTypeImport(_modulePath, _exportName, _localName, sourceLine, _written) -> sourceLine
        | ResolvedModuleImport(_modulePath, _alias, sourceLine, _written) -> sourceLine

let recursive resolveImportsFrom remaining interfaces reversed =
    match remaining with
        | [] -> Ok(reverseList(reversed))
        | entry :: rest ->
            match resolveImport(entry)(interfaces) with
                | Error(error) -> Error(error)
                | Ok(resolved) ->
                    if hasResolvedConflict(resolved)(reversed)
                    then Error(ConflictingResolvedImport(resolvedSourceLine(resolved))(deepCopy(resolvedLocalName(resolved))))
                    else resolveImportsFrom(rest)(interfaces)(resolved :: reversed)

let resolveImports interfaces imports = resolveImportsFrom(imports)(interfaces)([])
