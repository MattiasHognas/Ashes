// Builds deterministic semantic scopes for a dependency-ordered module plan.
//
// This phase deliberately stops before combining module syntax trees. It assigns stable compiler
// names and definition identities, realizes resolved imports as semantic bindings, and records the
// top-level visibility boundary of every declaration. Later physical stitching can therefore rewrite
// syntax without rediscovering namespace or export rules.

import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.reverse as reverseList
import Ashes.Internal.deepCopy as deepCopy
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token
export (
    type StitchedNameKind(..),
    type SemanticStitchUnit(..),
    type StitchedDefinition(..),
    type StitchedImportBinding(..),
    type StitchedModuleScope(..),
    type StitchedSemanticProject(..),
    type ModuleSemanticStitchError(..),
    value buildStitchedSemanticProject,
    value resolveStitchedUnqualified,
    value resolveStitchedQualified,
    value resolveStitchedModuleAlias,
)

type StitchedNameKind =
    | StitchedValue
    | StitchedType
    | StitchedConstructor
    | StitchedCapability
    | StitchedTrait
    | StitchedExternal
    deriving {Eq, Show}

type SemanticStitchUnit =
    | name: Str
    | packageId: Str
    | sourcePath: Str
    | imports: List(ResolvedImport)
    | interface: ModuleImportInterface
    | program: ProgramSyntax
    | isEntry: Bool

type StitchedDefinition =
    | id: Int
    | sourceName: Str
    | qualifiedName: Str
    | compilerName: Str
    | moduleName: Str
    | packageId: Str
    | sourcePath: Str
    | kind: StitchedNameKind
    | definitionSpan: Maybe(TextSpan)
    | declarationOrder: Int
    | visibleFrom: Int
    | exported: Bool
    deriving {Eq, Show}

type StitchedImportBinding =
    | localName: Str
    | qualifier: Maybe(Str)
    | target: StitchedDefinition
    deriving {Eq, Show}

type StitchedModuleScope =
    | name: Str
    | packageId: Str
    | sourcePath: Str
    | imports: List(StitchedImportBinding)
    | moduleAliases: List((Str, Str))
    | definitions: List(StitchedDefinition)
    deriving {Eq, Show}

type StitchedSemanticProject =
    | scopes: List(StitchedModuleScope)
    | nextDefinitionId: Int
    deriving {Eq, Show}

type ModuleSemanticStitchError =
    | DuplicateStitchedModule(Str)
    | DuplicateModuleDeclaration(Str, Str)
    | MissingStitchedImportModule(Str, Str)
    | MissingStitchedImportExport(Str, Str, Str)
    | ConflictingStitchedImport(Str, Str)
    | ConflictingModuleQualifier(Str, Str)
    | CompilerPrivateNameCollision(Str, Str, Str)
    deriving {Eq, Show}

type PendingDefinition =
    | name: Str
    | kind: StitchedNameKind
    | span: Maybe(TextSpan)
    | order: Int
    | recursiveVisible: Bool

type DefinitionCollection =
    | reversed: List(PendingDefinition)
    | nextOrder: Int

type StitchState =
    | reversedModules: List(StitchedModuleScope)
    | nextDefinitionId: Int

let both left right =
    if left
    then right
    else false

let sameNamespace left right =
    match (left, right) with
        | (StitchedValue, StitchedValue) -> true
        | (StitchedValue, StitchedConstructor) -> true
        | (StitchedValue, StitchedExternal) -> true
        | (StitchedConstructor, StitchedValue) -> true
        | (StitchedConstructor, StitchedConstructor) -> true
        | (StitchedConstructor, StitchedExternal) -> true
        | (StitchedExternal, StitchedValue) -> true
        | (StitchedExternal, StitchedConstructor) -> true
        | (StitchedExternal, StitchedExternal) -> true
        | (StitchedType, StitchedType) -> true
        | (StitchedType, StitchedCapability) -> true
        | (StitchedType, StitchedTrait) -> true
        | (StitchedCapability, StitchedType) -> true
        | (StitchedCapability, StitchedCapability) -> true
        | (StitchedCapability, StitchedTrait) -> true
        | (StitchedTrait, StitchedType) -> true
        | (StitchedTrait, StitchedCapability) -> true
        | (StitchedTrait, StitchedTrait) -> true
        | _ -> false

let recursive findModule (name: Str) (modules: List(StitchedModuleScope)) =
    match modules with
        | [] -> None
        | (StitchedModuleScope { name = candidate, packageId = _packageId, sourcePath = _sourcePath, imports = _imports, definitions = _definitions } as moduleScope) :: rest ->
            if candidate == name
            then Some(deepCopy(moduleScope))
            else findModule(name)(rest)

let hasModule (name: Str) (modules: List(StitchedModuleScope)) =
    match findModule(name)(modules) with
        | Some(_moduleScope) -> true
        | None -> false

let recursive findExportedDefinition (name: Str) (kind: StitchedNameKind) (definitions: List(StitchedDefinition)) =
    match definitions with
        | [] -> None
        | (StitchedDefinition { sourceName = candidate, kind = candidateKind, exported = exported, id = _id, qualifiedName = _qualifiedName, compilerName = _compilerName, moduleName = _moduleName, packageId = _packageId, sourcePath = _sourcePath, definitionSpan = _span, declarationOrder = _order, visibleFrom = _visibleFrom } as definition) :: rest ->
            if both(exported)(both(candidate == name)(sameNamespace(kind)(candidateKind)))
            then Some(deepCopy(definition))
            else findExportedDefinition(name)(kind)(rest)

let recursive hasPendingDefinition name kind definitions =
    match definitions with
        | [] -> false
        | PendingDefinition { name = candidate, kind = candidateKind, span = _span, order = _order, recursiveVisible = _recursiveVisible } :: rest ->
            if both(candidate == name)(sameNamespace(kind)(candidateKind))
            then true
            else hasPendingDefinition(name)(kind)(rest)

let addPending name kind span order recursiveVisible collection =
    match collection with
        | DefinitionCollection { reversed = reversed, nextOrder = nextOrder } -> DefinitionCollection(reversed = PendingDefinition(name = name, kind = kind, span = span, order = order, recursiveVisible = recursiveVisible) :: reversed, nextOrder = nextOrder)

let recursive addConstructors constructors span order collection =
    match constructors with
        | [] -> collection
        | TypeConstructor { name = name, parameters = _parameters, fieldNames = _fieldNames } :: rest ->
            addConstructors(
                rest,
                span,
                order,
                addPending(name)(StitchedConstructor)(span)(order)(false)(collection)
            )

let addTypeDeclaration declaration span order collection =
    match declaration with
        | TypeDecl { name = name, typeParameters = _parameters, constructors = constructors, isRecord = _isRecord, derivingTraits = _derivingTraits } ->
            addConstructors(
                constructors,
                span,
                order,
                addPending(name)(StitchedType)(span)(order)(true)(collection)
            )

let addZeroCostDeclaration declaration span order collection =
    match declaration with
        | ZeroCostTypeDecl { name = name, typeParameters = _parameters, constructor = TypeConstructor { name = constructorName, parameters = _constructorParameters, fieldNames = _fieldNames }, derivingTraits = _derivingTraits } ->
            addPending(
                constructorName,
                StitchedConstructor,
                span,
                order,
                false,
                addPending(name)(StitchedType)(span)(order)(true)(collection)
            )

let recursive addBindings bindings span order recursiveVisible collection =
    match bindings with
        | [] -> collection
        | LetBindingSyntax { name = name, value = _value, sugarParameters = _parameters, typeAnnotation = _annotation, requirements = _requirements } :: rest ->
            addBindings(
                rest,
                span,
                order,
                recursiveVisible,
                addPending(name)(StitchedValue)(span)(order)(recursiveVisible)(collection)
            )

let addExternal declaration span order collection =
    match declaration with
        | ExternalOpaqueType(name, _resource) -> addPending(name)(StitchedType)(span)(order)(false)(collection)
        | ExternalFunction(name, _parameters, _result, _symbol, _ownership, _needs) ->
            addPending(
                name,
                StitchedExternal,
                span,
                order,
                false,
                collection
            )

let addUnspannedItem item span collection =
    match collection with
        | DefinitionCollection { reversed = _reversed, nextOrder = order } ->
            let next =
                match item with
                    | TopLevelType(declaration) -> addTypeDeclaration(declaration)(span)(order)(collection)
                    | TopLevelTypeAlias(TypeAliasDecl { name = name, typeParameters = _parameters, target = _target }) ->
                        addPending(
                            name,
                            StitchedType,
                            span,
                            order,
                            false,
                            collection
                        )
                    | TopLevelZeroCostType(declaration) -> addZeroCostDeclaration(declaration)(span)(order)(collection)
                    | TopLevelExternal(declaration) -> addExternal(declaration)(span)(order)(collection)
                    | TopLevelCapability(CapabilityDecl { name = name, typeParameters = _parameters, operations = _operations }) ->
                        addPending(
                            name,
                            StitchedCapability,
                            span,
                            order,
                            true,
                            collection
                        )
                    | TopLevelTrait(TraitDecl { name = name, typeParameters = _parameters, supertraits = _supertraits, methods = _methods }) ->
                        addPending(
                            name,
                            StitchedTrait,
                            span,
                            order,
                            true,
                            collection
                        )
                    | TopLevelLet(LetBindingSyntax { name = name, value = _value, sugarParameters = _parameters, typeAnnotation = _annotation, requirements = _requirements }, isRecursive) ->
                        addPending(
                            name,
                            StitchedValue,
                            span,
                            order,
                            isRecursive,
                            collection
                        )
                    | TopLevelRecursiveGroup(bindings) -> addBindings(bindings)(span)(order)(true)(collection)
                    | _ -> collection
            in DefinitionCollection(reversed = next.reversed, nextOrder = order + 1)

let addItem item collection =
    match item with
        | TopLevelAt(span, inner) -> addUnspannedItem(inner)(Some(span))(collection)
        | _ -> addUnspannedItem(item)(None)(collection)

let recursive collectPendingDefinitions items collection =
    match items with
        | [] -> reverseList(collection.reversed)
        | item :: rest -> collectPendingDefinitions(rest)(addItem(item)(collection))

let recursive interfaceExports (name: Str) (kind: StitchedNameKind) (exports: List(ModuleImportExport)) =
    match exports with
        | [] -> false
        | ImportValueExport(candidate) :: rest ->
            if both(candidate == name)(kind == StitchedValue)
            then true
            else interfaceExports(name)(kind)(rest)
        | ImportTypeExport(candidate) :: rest ->
            if both(candidate == name)(sameNamespace(kind)(StitchedType))
            then true
            else interfaceExports(name)(kind)(rest)
        | ImportConstructorExport(candidate) :: rest ->
            if both(candidate == name)(kind == StitchedConstructor)
            then true
            else interfaceExports(name)(kind)(rest)
        | ImportModuleExport(_candidate) :: rest -> interfaceExports(name)(kind)(rest)

let sanitizeModuleName : Str -> Str =
    given (name) -> Ashes.Text.join("_")(Ashes.Text.split(name)("."))

let privateCompilerName : Str -> Str -> StitchedNameKind -> Str =
    given (moduleName) ->
        given (sourceName) ->
            given (kind) ->
                let prefix =
                    match kind with
                        | StitchedValue -> "__ashes_private_value_"
                        | StitchedExternal -> "__ashes_private_external_"
                        | StitchedConstructor -> "AshesPrivateConstructor_"
                        | _ -> "AshesPrivateType_"
                in prefix + sanitizeModuleName(moduleName) + "_" + sourceName

let publicCompilerName : Str -> Str -> Bool -> Str =
    given (moduleName) ->
        given (sourceName) ->
            given (isEntry) ->
                if isEntry
                then deepCopy(sourceName)
                else sanitizeModuleName(moduleName) + "_" + sourceName

let compilerName : Str -> Str -> StitchedNameKind -> Bool -> Bool -> Str =
    given (moduleName) ->
        given (sourceName) ->
            given (kind) ->
                given (isEntry) ->
                    given (exported) ->
                        if exported
                        then publicCompilerName(moduleName)(sourceName)(isEntry)
                        else privateCompilerName(moduleName)(sourceName)(kind)

let recursive materializeDefinitions pending (unit: SemanticStitchUnit) nextId reversed =
    match unit with
        | SemanticStitchUnit { name = moduleName, packageId = packageId, sourcePath = sourcePath, imports = _imports, interface = ModuleImportInterface { name = _interfaceName, exports = exports }, program = _program, isEntry = isEntry } ->
            match pending with
                | [] -> (reverseList(reversed), nextId)
                | PendingDefinition { name = name, kind = kind, span = span, order = order, recursiveVisible = recursiveVisible } :: rest ->
                    let exported = interfaceExports(name)(kind)(exports)
                    in
                        let definition =
                            StitchedDefinition(id = nextId, sourceName = name, qualifiedName = moduleName + "." + name, compilerName = compilerName(
                                moduleName,
                                name,
                                kind,
                                isEntry,
                                exported
                            ), moduleName = moduleName, packageId = packageId, sourcePath = sourcePath, kind = kind, definitionSpan = span, declarationOrder = order, visibleFrom = if recursiveVisible
                            then order
                            else order + 1, exported = exported)
                        in materializeDefinitions(rest)(unit)(nextId + 1)(definition :: reversed)

let recursive duplicateDefinition definitions seen =
    match definitions with
        | [] -> None
        | PendingDefinition { name = name, kind = kind, span = _span, order = _order, recursiveVisible = _recursiveVisible } :: rest ->
            if hasPendingDefinition(name)(kind)(seen)
            then Some(name)
            else
                duplicateDefinition(
                    rest,
                    PendingDefinition(name = name, kind = kind, span = None, order = 0, recursiveVisible = false) :: seen
                )

let recursive compilerNameOwner name definitions =
    match definitions with
        | [] -> None
        | StitchedDefinition { compilerName = candidate, qualifiedName = qualifiedName, id = _id, sourceName = _sourceName, moduleName = _moduleName, packageId = _packageId, sourcePath = _sourcePath, kind = _kind, definitionSpan = _span, declarationOrder = _order, visibleFrom = _visibleFrom, exported = _exported } :: rest ->
            if candidate == name
            then Some(qualifiedName)
            else compilerNameOwner(name)(rest)

let recursive allDefinitions modules =
    match modules with
        | [] -> []
        | StitchedModuleScope { name = _name, packageId = _packageId, sourcePath = _sourcePath, imports = _imports, definitions = definitions } :: rest ->
            appendList(
                definitions,
                allDefinitions(rest)
            )

let recursive validateCompilerNames definitions existing =
    match definitions with
        | [] -> None
        | StitchedDefinition { compilerName = compilerName, qualifiedName = qualifiedName, id = _id, sourceName = _sourceName, moduleName = _definitionModule, packageId = _packageId, sourcePath = _sourcePath, kind = _kind, definitionSpan = _span, declarationOrder = _order, visibleFrom = _visibleFrom, exported = _exported } :: rest ->
            match compilerNameOwner(compilerName)(existing) with
                | Some(owner) -> Some(CompilerPrivateNameCollision(compilerName)(owner)(qualifiedName))
                | None -> validateCompilerNames(rest)(existing)

let resolvedModule resolved =
    match resolved with
        | ResolvedValueImport(moduleName, _exportName, _localName, _line, _written) -> moduleName
        | ResolvedTypeImport(moduleName, _exportName, _localName, _line, _written) -> moduleName
        | ResolvedModuleImport(moduleName, _alias, _line, _written) -> moduleName

let recursive exportedBindings qualifier definitions =
    match definitions with
        | [] -> []
        | (StitchedDefinition { sourceName = sourceName, exported = true, id = _id, qualifiedName = _qualifiedName, compilerName = _compilerName, moduleName = _moduleName, packageId = _packageId, sourcePath = _sourcePath, kind = _kind, definitionSpan = _span, declarationOrder = _order, visibleFrom = _visibleFrom } as definition) :: rest ->
            StitchedImportBinding(localName = sourceName, qualifier = qualifier, target = deepCopy(
                definition
            )) :: exportedBindings(
                qualifier,
                rest
            )
        | _definition :: rest -> exportedBindings(qualifier)(rest)

let moduleLeaf name =
    match reverseList(Ashes.Text.split(name)(".")) with
        | leaf :: _rest -> leaf
        | [] -> name

let recursive qualifierOwner qualifier bindings =
    match bindings with
        | [] -> None
        | StitchedImportBinding { localName = _localName, qualifier = Some(candidate), target = StitchedDefinition { moduleName = moduleName, id = _id, sourceName = _sourceName, qualifiedName = _qualifiedName, compilerName = _compilerName, packageId = _packageId, sourcePath = _sourcePath, kind = _kind, definitionSpan = _span, declarationOrder = _order, visibleFrom = _visibleFrom, exported = _exported } } :: rest ->
            if candidate == qualifier
            then Some(moduleName)
            else qualifierOwner(qualifier)(rest)
        | _binding :: rest -> qualifierOwner(qualifier)(rest)

let qualifierConflicts qualifier importedName bindings =
    match qualifierOwner(qualifier)(bindings) with
        | None -> false
        | Some(owner) -> owner != importedName

let recursive unqualifiedConflict (binding: StitchedImportBinding) bindings =
    match binding with
        | StitchedImportBinding { localName = bindingName, qualifier = bindingQualifier, target = StitchedDefinition { qualifiedName = bindingTargetName, kind = bindingTargetKind, id = _bindingId, sourceName = _bindingSourceName, compilerName = _bindingCompilerName, moduleName = _bindingModuleName, packageId = _bindingPackageId, sourcePath = _bindingSourcePath, definitionSpan = _bindingSpan, declarationOrder = _bindingOrder, visibleFrom = _bindingVisibleFrom, exported = _bindingExported } } ->
            match bindings with
                | [] -> false
                | StitchedImportBinding { localName = existingName, qualifier = existingQualifier, target = StitchedDefinition { qualifiedName = existingTargetName, kind = existingTargetKind, id = _existingId, sourceName = _existingSourceName, compilerName = _existingCompilerName, moduleName = _existingModuleName, packageId = _existingPackageId, sourcePath = _existingSourcePath, definitionSpan = _existingSpan, declarationOrder = _existingOrder, visibleFrom = _existingVisibleFrom, exported = _existingExported } } :: rest ->
                    if both(
                        bindingQualifier == None,
                        both(
                            existingQualifier == None,
                            both(
                                bindingName == existingName,
                                both(
                                    bindingTargetName != existingTargetName,
                                    sameNamespace(bindingTargetKind)(existingTargetKind)
                                )
                            )
                        )
                    )
                    then true
                    else unqualifiedConflict(binding)(rest)

let recursive addImportBindings moduleName additions current =
    match additions with
        | [] -> Ok(current)
        | (StitchedImportBinding { localName = bindingName, qualifier = _qualifier, target = _target } as binding) :: rest ->
            if unqualifiedConflict(binding)(current)
            then Error(ConflictingStitchedImport(moduleName)(bindingName))
            else addImportBindings(moduleName)(rest)(binding :: current)

let addWholeModuleImport ownerModule (imported: StitchedModuleScope) alias bindings =
    match imported with
        | StitchedModuleScope { name = importedName, packageId = _packageId, sourcePath = _sourcePath, imports = _imports, definitions = definitions } ->
            let primaryQualifier =
                match alias with
                    | Some(name) -> name
                    | None -> importedName
            in
                if qualifierConflicts(primaryQualifier)(importedName)(bindings)
                then Error(ConflictingModuleQualifier(ownerModule)(primaryQualifier))
                else
                    let qualified = exportedBindings(Some(primaryQualifier))(definitions)
                    in
                        let withShort =
                            match alias with
                                | Some(_name) -> qualified
                                | None ->
                                    let leaf = moduleLeaf(importedName)
                                    in
                                        if leaf == importedName
                                        then qualified
                                        else
                                            if qualifierConflicts(leaf)(importedName)(bindings)
                                            then []
                                            else appendList(qualified)(exportedBindings(Some(leaf))(definitions))
                        in
                            match alias with
                                | Some(_name) ->
                                    addImportBindings(
                                        ownerModule,
                                        appendList(exportedBindings(None)(definitions))(withShort),
                                        bindings
                                    )
                                | None ->
                                    let leaf = moduleLeaf(importedName)
                                    in
                                        if both(leaf != importedName)(qualifierConflicts(leaf)(importedName)(bindings))
                                        then Error(ConflictingModuleQualifier(ownerModule)(leaf))
                                        else
                                            addImportBindings(
                                                ownerModule,
                                                appendList(exportedBindings(None)(definitions))(withShort),
                                                bindings
                                            )

let addSelectorImport ownerModule (imported: StitchedModuleScope) exportName localName kind bindings =
    match imported with
        | StitchedModuleScope { name = importedName, packageId = _packageId, sourcePath = _sourcePath, imports = _imports, definitions = definitions } ->
            match findExportedDefinition(exportName)(kind)(definitions) with
                | None -> Error(MissingStitchedImportExport(ownerModule)(importedName)(exportName))
                | Some(definition) ->
                    addImportBindings(
                        ownerModule,
                        [StitchedImportBinding(localName = localName, qualifier = None, target = definition)],
                        bindings
                    )

let recursive buildImportBindings ownerModule imports completed bindings =
    match imports with
        | [] -> Ok(reverseList(bindings))
        | resolved :: rest ->
            match findModule(resolvedModule(resolved))(completed) with
                | None -> Error(MissingStitchedImportModule(ownerModule)(resolvedModule(resolved)))
                | Some(imported) ->
                    let added =
                        match resolved with
                            | ResolvedModuleImport(_moduleName, alias, _line, _written) ->
                                addWholeModuleImport(
                                    ownerModule,
                                    imported,
                                    alias,
                                    bindings
                                )
                            | ResolvedValueImport(_moduleName, exportName, localName, _line, _written) ->
                                addSelectorImport(
                                    ownerModule,
                                    imported,
                                    exportName,
                                    localName,
                                    StitchedValue,
                                    bindings
                                )
                            | ResolvedTypeImport(_moduleName, exportName, localName, _line, _written) ->
                                addSelectorImport(
                                    ownerModule,
                                    imported,
                                    exportName,
                                    localName,
                                    StitchedType,
                                    bindings
                                )
                    in
                        match added with
                            | Error(error) -> Error(error)
                            | Ok(next) -> buildImportBindings(ownerModule)(rest)(completed)(next)

// The qualifier shorthands a module's whole-module imports establish: an explicit alias
// (`import Ashes.IO as io` maps `io`) and the module-path leaf (`import Ashes.IO` maps `IO`),
// each to the imported module's full name. Consulted by the reference rewriter when a qualified
// name matches no stitched binding — an intrinsic builtin module contributes no bindings at all,
// so its members are reachable only by rewriting the alias back to the full module name and
// letting the no-import-required qualified-access path resolve them.
let recursive collectModuleAliases resolvedImports =
    match resolvedImports with
        | [] -> []
        | ResolvedModuleImport(importedName, alias, _line, _written) :: rest ->
            let leafEntries =
                let leaf = moduleLeaf(importedName)
                in
                    if leaf == importedName
                    then []
                    else [(deepCopy(leaf), deepCopy(importedName))]
            in
                match alias with
                    | Some(name) -> (deepCopy(name), deepCopy(importedName)) :: appendList(leafEntries)(collectModuleAliases(rest))
                    | None -> appendList(leafEntries)(collectModuleAliases(rest))
        | _ :: rest -> collectModuleAliases(rest)

let recursive findModuleAlias (qualifier: Str) (aliases: List((Str, Str))) =
    match aliases with
        | [] -> None
        | (alias, target) :: rest ->
            if alias == qualifier
            then Some(deepCopy(target))
            else findModuleAlias(qualifier)(rest)

let resolveStitchedModuleAlias moduleName qualifier (project: StitchedSemanticProject) =
    match findModule(moduleName)(project.scopes) with
        | None -> None
        | Some(moduleScope) -> findModuleAlias(qualifier)(moduleScope.moduleAliases)

let buildModule (unit: SemanticStitchUnit) (state: StitchState) =
    match (unit, state) with
        | (SemanticStitchUnit { name = moduleName, packageId = packageId, sourcePath = sourcePath, imports = resolvedImports, interface = _moduleInterface, program = ProgramSyntax { items = items, body = _body }, isEntry = _isEntry }, StitchState { reversedModules = completedModules, nextDefinitionId = nextDefinitionId }) ->
            if hasModule(moduleName)(completedModules)
            then Error(DuplicateStitchedModule(moduleName))
            else
                let pending = collectPendingDefinitions(items)(DefinitionCollection(reversed = [], nextOrder = 0))
                in
                    match duplicateDefinition(pending)([]) with
                        | Some(name) -> Error(DuplicateModuleDeclaration(moduleName)(name))
                        | None ->
                            match materializeDefinitions(pending)(unit)(nextDefinitionId)([]) with
                                | (definitions, nextId) ->
                                    match validateCompilerNames(definitions)(allDefinitions(completedModules)) with
                                        | Some(error) -> Error(error)
                                        | None ->
                                            match buildImportBindings(
                                                moduleName,
                                                resolvedImports,
                                                completedModules,
                                                []
                                            ) with
                                                | Error(error) -> Error(error)
                                                | Ok(imports) ->
                                                    Ok(
                                                        StitchState(reversedModules = StitchedModuleScope(name = moduleName, packageId = packageId, sourcePath = sourcePath, imports = imports, moduleAliases = collectModuleAliases(resolvedImports), definitions = definitions) :: completedModules, nextDefinitionId = nextId)
                                                    )

let recursive buildModules (units: List(SemanticStitchUnit)) (state: StitchState) =
    match state with
        | StitchState { reversedModules = reversedModules, nextDefinitionId = nextDefinitionId } ->
            match units with
                | [] ->
                    Ok(
                        StitchedSemanticProject(scopes = reverseList(reversedModules), nextDefinitionId = nextDefinitionId)
                    )
                | unit :: rest ->
                    match buildModule(unit)(state) with
                        | Error(error) -> Error(error)
                        | Ok(next) -> buildModules(rest)(next)

let buildStitchedSemanticProject : List(SemanticStitchUnit) -> Result(ModuleSemanticStitchError, StitchedSemanticProject) =
    given (units) -> buildModules(units)(StitchState(reversedModules = [], nextDefinitionId = 0))

let definitionVisibleAt boundary (definition: StitchedDefinition) = definition.visibleFrom <= boundary

let recursive resolveLocalAt name kind boundary definitions =
    match definitions with
        | [] -> None
        | definition :: rest ->
            if both(
                definition.sourceName == name,
                both(sameNamespace(kind)(definition.kind))(definitionVisibleAt(boundary)(definition))
            )
            then Some(deepCopy(definition))
            else resolveLocalAt(name)(kind)(boundary)(rest)

let recursive resolveImported name qualifier kind bindings =
    match bindings with
        | [] -> None
        | StitchedImportBinding { localName = localName, qualifier = bindingQualifier, target = target } :: rest ->
            if both(localName == name)(both(bindingQualifier == qualifier)(sameNamespace(kind)(target.kind)))
            then Some(deepCopy(target))
            else resolveImported(name)(qualifier)(kind)(rest)

let resolveStitchedUnqualified moduleName boundary kind name (project: StitchedSemanticProject) =
    match findModule(moduleName)(project.scopes) with
        | None -> None
        | Some(moduleScope) ->
            match resolveLocalAt(name)(kind)(boundary)(moduleScope.definitions) with
                | Some(definition) -> Some(definition)
                | None -> resolveImported(name)(None)(kind)(moduleScope.imports)

let resolveStitchedQualified moduleName qualifier kind name (project: StitchedSemanticProject) =
    match findModule(moduleName)(project.scopes) with
        | None -> None
        | Some(moduleScope) -> resolveImported(name)(Some(qualifier))(kind)(moduleScope.imports)
