// Builds deterministic semantic scopes for a dependency-ordered module plan.
//
// This phase deliberately stops before combining module syntax trees. It assigns stable compiler
// names and definition identities, realizes resolved imports as semantic bindings, and records the
// top-level visibility boundary of every declaration. Later physical stitching can therefore rewrite
// syntax without rediscovering namespace or export rules.

import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.reverse as reverseList
import Ashes.Collection.Map.MapTree
import Ashes.Internal.deepCopy as deepCopy
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.CoreBuiltinLowering.coreBuiltinKind
import AshesCompiler.Semantics.CoreBuiltinLowering.isIntrinsicBuiltinModule
import AshesCompiler.Semantics.ExprMentions.programMentionsVariable
export (
    type StitchedNameKind(..),
    type SemanticStitchUnit(..),
    type StitchedDefinition(..),
    type StitchedImportBinding(..),
    type IntrinsicSelector(..),
    type StitchedModuleScope(..),
    type StitchedSemanticProject(..),
    type ModuleSemanticStitchError(..),
    value buildStitchedSemanticProject,
    value resolveStitchedUnqualified,
    value resolveStitchedQualified,
    value resolveStitchedShippedType,
    value resolveStitchedModuleAlias,
    value resolveStitchedIntrinsicSelector,
    value stitchedModulePlainImports,
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

// A selector import of an intrinsic builtin module's member (`import Ashes.Internal.deepCopy as
// copy`): the local name the importing module reads it by, the module, and the member, since the
// module has no definition to bind the name to.
type IntrinsicSelector =
    | localName: Str
    | moduleName: Str
    | memberName: Str
    deriving {Eq, Show}

type StitchedModuleScope =
    | name: Str
    | packageId: Str
    | sourcePath: Str
    | imports: List(StitchedImportBinding)
    | moduleAliases: List((Str, Str))
    | plainWholeModuleImports: List(Str)
    | intrinsicSelectors: List(IntrinsicSelector)
    | definitions: List(StitchedDefinition)
    | unqualifiedImportsByLocalName: MapTree(Str, List(StitchedImportBinding))
    | qualifiedImportsByQualifier: MapTree(Str, List(MapTree(Str, List(StitchedDefinition))))
    | definitionsBySourceName: MapTree(Str, List(StitchedDefinition))

type StitchedSemanticProject =
    | scopes: List(StitchedModuleScope)
    | nextDefinitionId: Int

type ModuleSemanticStitchError =
    | DuplicateStitchedModule(Str)
    | DuplicateModuleDeclaration(Str, Str)
    | MissingStitchedImportModule(Str, Str)
    | MissingStitchedImportExport(Str, Str, Str)
    | ConflictingStitchedImport(Str, Str)
    | ConflictingModuleQualifier(Str, Str)
    | CompilerPrivateNameCollision(Str, Str, Str)
    deriving {Eq, Show}

// `global` marks a definition whose compiler name is its source name in every module: an
// external opaque type, which stage 0 never renames (its private-member renaming covers the
// module's declared types only), so a bare reference to the handle type from a module that
// imports the declaring module whole names the same declaration.
type PendingDefinition =
    | name: Str
    | kind: StitchedNameKind
    | span: Maybe(TextSpan)
    | order: Int
    | recursiveVisible: Bool
    | global: Bool

type DefinitionCollection =
    | reversed: List(PendingDefinition)
    | nextOrder: Int

// `compilerNameOwners` maps every compiler name of the completed modules to the qualified name of
// the definition a walk over `reversedModules` would meet first: the first such definition of the
// most recently completed module declaring it.
type StitchState =
    | reversedModules: List(StitchedModuleScope)
    | nextDefinitionId: Int
    | compilerNameOwners: MapTree(Str, Str)

// A module's import bindings while they accumulate, newest first. `unqualifiedByLocalName` files
// the unqualified ones by local name, each group oldest first. `qualifiedByQualifier` lists, for
// each qualifier, the definition indexes of the modules whose exports were bound under it, in
// import order, and `qualifierOwners` names the last of those modules.
type ImportBindingAccumulator =
    | newestFirst: List(StitchedImportBinding)
    | unqualifiedByLocalName: MapTree(Str, List(StitchedImportBinding))
    | qualifiedByQualifier: MapTree(Str, List(MapTree(Str, List(StitchedDefinition))))
    | qualifierOwners: MapTree(Str, Str)

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

// The entries filed under a name, in the order the list they index holds them; a scan for that
// name over the whole list meets exactly these, in this order.
let definitionsNamed (name: Str) (index: MapTree(Str, List(StitchedDefinition))) =
    match Ashes.Collection.Map.getStr(name)(index) with
        | Some(definitions) -> definitions
        | None -> []

let bindingsNamed (name: Str) (index: MapTree(Str, List(StitchedImportBinding))) =
    match Ashes.Collection.Map.getStr(name)(index) with
        | Some(bindings) -> bindings
        | None -> []

// Files definitions by source name. Taking them last first leaves every group in list order.
let recursive indexDefinitionsBySourceName (reversedDefinitions: List(StitchedDefinition)) (index: MapTree(Str, List(StitchedDefinition))) =
    match reversedDefinitions with
        | [] -> index
        | (StitchedDefinition { sourceName = sourceName } as definition) :: rest ->
            index
            |> Ashes.Collection.Map.setStr(sourceName)(definition :: definitionsNamed(sourceName)(index))
            |> indexDefinitionsBySourceName(rest)

// The scope is returned as it sits in the list: the reference rewriter looks a module up once
// per name it resolves, and copying the scope with its thousands of definitions at every lookup
// cost the stage-1 compiler more memory than a machine has while rewriting its own sources.
let recursive findModule (name: Str) (modules: List(StitchedModuleScope)) =
    match modules with
        | [] -> None
        | (StitchedModuleScope { name = candidate, packageId = _packageId, sourcePath = _sourcePath, imports = _imports, definitions = _definitions } as moduleScope) :: rest ->
            if candidate == name
            then Some(moduleScope)
            else findModule(name)(rest)

let hasModule (name: Str) (modules: List(StitchedModuleScope)) =
    match findModule(name)(modules) with
        | Some(_moduleScope) -> true
        | None -> false

let recursive findExportedDefinition (name: Str) (kind: StitchedNameKind) (definitions: List(StitchedDefinition)) =
    match definitions with
        | [] -> None
        | (StitchedDefinition { sourceName = candidate, kind = candidateKind, exported = exported, id = _id, qualifiedName = _qualifiedName, compilerName = _compilerName, moduleName = _moduleName, packageId = _packageId, sourcePath = _sourcePath, definitionSpan = _span, declarationOrder = _order, visibleFrom = _visibleFrom } as definition) :: rest ->
            if candidateKind
            |> sameNamespace(kind)
            |> both(candidate == name)
            |> both(exported)
            then
                definition
                |> deepCopy
                |> Some
            else findExportedDefinition(name)(kind)(rest)

let addPendingWith (global: Bool) name kind span order recursiveVisible collection =
    match collection with
        | DefinitionCollection { reversed = reversed, nextOrder = nextOrder } -> DefinitionCollection(reversed = PendingDefinition(name = name, kind = kind, span = span, order = order, recursiveVisible = recursiveVisible, global = global) :: reversed, nextOrder = nextOrder)

let addPending name kind span order recursiveVisible collection = addPendingWith(false)(name)(kind)(span)(order)(recursiveVisible)(collection)

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
        | ExternalOpaqueType(name, _resource) -> addPendingWith(true)(name)(StitchedType)(span)(order)(false)(collection)
        | ExternalFunction(name, _parameters, _result, _symbol, _ownership, _needs) ->
            // Stage 0 hoists an `external` function program-wide under its own name (never
            // exported, never renamed), so a destructor reference inside a native-string
            // signature or a resource declaration keeps naming it.
            addPendingWith(true)(
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
        | item :: rest ->
            collection
            |> addItem(item)
            |> collectPendingDefinitions(rest)

let recursive interfaceExports (name: Str) (kind: StitchedNameKind) (exports: List(ModuleImportExport)) =
    match exports with
        | [] -> false
        | ImportValueExport(candidate) :: rest ->
            if both(candidate == name)(kind == StitchedValue)
            then true
            else interfaceExports(name)(kind)(rest)
        | ImportTypeExport(candidate) :: rest ->
            if StitchedType
            |> sameNamespace(kind)
            |> both(candidate == name)
            then true
            else interfaceExports(name)(kind)(rest)
        | ImportConstructorExport(candidate) :: rest ->
            if both(candidate == name)(kind == StitchedConstructor)
            then true
            else interfaceExports(name)(kind)(rest)
        | ImportModuleExport(_candidate) :: rest -> interfaceExports(name)(kind)(rest)

let importExportName (entry: ModuleImportExport) =
    match entry with
        | ImportValueExport(name) -> name
        | ImportTypeExport(name) -> name
        | ImportConstructorExport(name) -> name
        | ImportModuleExport(name) -> name

let exportsNamed (name: Str) (index: MapTree(Str, List(ModuleImportExport))) =
    match Ashes.Collection.Map.getStr(name)(index) with
        | Some(exports) -> exports
        | None -> []

// Files an interface's exports by name. Taking them last first leaves every group in list order.
let recursive indexExportsByName (reversedExports: List(ModuleImportExport)) (index: MapTree(Str, List(ModuleImportExport))) =
    match reversedExports with
        | [] -> index
        | entry :: rest ->
            let name = importExportName(entry)
            in
                index
                |> Ashes.Collection.Map.setStr(name)(entry :: exportsNamed(name)(index))
                |> indexExportsByName(rest)

let sanitizeModuleName : Str -> Str =
    given (name) ->
        "."
        |> Ashes.Text.split(name)
        |> Ashes.Text.join("_")

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

let recursive materializeDefinitions (exportsByName: MapTree(Str, List(ModuleImportExport))) pending (unit: SemanticStitchUnit) nextId reversed =
    match unit with
        | SemanticStitchUnit { name = moduleName, packageId = packageId, sourcePath = sourcePath, imports = _imports, interface = _interface, program = _program, isEntry = isEntry } ->
            match pending with
                | [] -> (reverseList(reversed), nextId)
                | PendingDefinition { name = name, kind = kind, span = span, order = order, recursiveVisible = recursiveVisible, global = global } :: rest ->
                    let exported =
                        exportsByName
                        |> exportsNamed(name)
                        |> interfaceExports(name)(kind)
                    in
                        let definition =
                            StitchedDefinition(id = nextId, sourceName = name, qualifiedName = moduleName + "." + name, compilerName = if global
                            then deepCopy(name)
                            else
                                compilerName(
                                    moduleName,
                                    name,
                                    kind,
                                    isEntry,
                                    exported
                                ), moduleName = moduleName, packageId = packageId, sourcePath = sourcePath, kind = kind, definitionSpan = span, declarationOrder = order, visibleFrom = if recursiveVisible
                            then order
                            else order + 1, exported = exported)
                        in materializeDefinitions(exportsByName)(rest)(unit)(nextId + 1)(definition :: reversed)

let recursive hasEarlierNamesake (id: Int) (kind: StitchedNameKind) (namesakes: List(StitchedDefinition)) =
    match namesakes with
        | [] -> false
        | StitchedDefinition { id = candidateId, kind = candidateKind } :: rest ->
            if candidateId == id
            then false
            else
                if sameNamespace(kind)(candidateKind)
                then true
                else hasEarlierNamesake(id)(kind)(rest)

// The first definition an earlier one of the same name and namespace precedes. A definition's
// namesakes are filed in declaration order, so the earlier ones are those ahead of it there.
let recursive duplicateDefinition (definitions: List(StitchedDefinition)) (bySourceName: MapTree(Str, List(StitchedDefinition))) =
    match definitions with
        | [] -> None
        | StitchedDefinition { id = id, sourceName = name, kind = kind } :: rest ->
            if bySourceName
            |> definitionsNamed(name)
            |> hasEarlierNamesake(id)(kind)
            then Some(name)
            else duplicateDefinition(rest)(bySourceName)

// The completed modules' owner of each compiler name a new module's definitions carry.
let recursive validateCompilerNames definitions (owners: MapTree(Str, Str)) =
    match definitions with
        | [] -> None
        | StitchedDefinition { compilerName = compilerName, qualifiedName = qualifiedName, id = _id, sourceName = _sourceName, moduleName = _definitionModule, packageId = _packageId, sourcePath = _sourcePath, kind = _kind, definitionSpan = _span, declarationOrder = _order, visibleFrom = _visibleFrom, exported = _exported } :: rest ->
            match Ashes.Collection.Map.getStr(compilerName)(owners) with
                | Some(owner) ->
                    qualifiedName
                    |> CompilerPrivateNameCollision(compilerName)(owner)
                    |> Some
                | None -> validateCompilerNames(rest)(owners)

// Records a completed module's compiler names over the earlier modules'. Taking the definitions
// last first leaves each name with the module's first definition carrying it.
let recursive recordCompilerNames (reversedDefinitions: List(StitchedDefinition)) (owners: MapTree(Str, Str)) =
    match reversedDefinitions with
        | [] -> owners
        | StitchedDefinition { compilerName = compilerName, qualifiedName = qualifiedName } :: rest ->
            owners
            |> Ashes.Collection.Map.setStr(compilerName)(qualifiedName)
            |> recordCompilerNames(rest)

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
    match "."
    |> Ashes.Text.split(name)
    |> reverseList with
        | leaf :: _rest -> leaf
        | [] -> name

let noImportBindings = ImportBindingAccumulator(newestFirst = [], unqualifiedByLocalName = Ashes.Collection.Map.empty, qualifiedByQualifier = Ashes.Collection.Map.empty, qualifierOwners = Ashes.Collection.Map.empty)

// The module of the newest binding carrying the qualifier.
let qualifierOwner (qualifier: Str) (bindings: ImportBindingAccumulator) = Ashes.Collection.Map.getStr(qualifier)(bindings.qualifierOwners)

let definitionIndexesUnder (qualifier: Str) (index: MapTree(Str, List(MapTree(Str, List(StitchedDefinition))))) =
    match Ashes.Collection.Map.getStr(qualifier)(index) with
        | Some(indexes) -> indexes
        | None -> []

let ownedBy (moduleName: Str) (owner: Maybe(Str)) =
    match owner with
        | Some(name) -> name == moduleName
        | None -> false

// Adds a qualified binding as the newest one. A whole-module import binds the module's exports
// under a qualifier in one run, in definition order, so the module's definition index joins the
// qualifier's list where a run starts: where the qualifier's owner is not that module already. A
// run that follows another of the same module adds nothing, the index being listed last already.
let pushQualifiedBinding (qualifier: Str) (binding: StitchedImportBinding) (importedDefinitions: MapTree(Str, List(StitchedDefinition))) (bindings: ImportBindingAccumulator) =
    match (binding, bindings) with
        | (StitchedImportBinding { target = StitchedDefinition { moduleName = moduleName } }, ImportBindingAccumulator { newestFirst = newestFirst, unqualifiedByLocalName = unqualifiedByLocalName, qualifiedByQualifier = qualifiedByQualifier, qualifierOwners = qualifierOwners }) ->
            if qualifierOwners
            |> Ashes.Collection.Map.getStr(qualifier)
            |> ownedBy(moduleName)
            then ImportBindingAccumulator(newestFirst = binding :: newestFirst, unqualifiedByLocalName = unqualifiedByLocalName, qualifiedByQualifier = qualifiedByQualifier, qualifierOwners = qualifierOwners)
            else
                ImportBindingAccumulator(newestFirst = binding :: newestFirst, unqualifiedByLocalName = unqualifiedByLocalName, qualifiedByQualifier = Ashes.Collection.Map.setStr(qualifier)(appendList(definitionIndexesUnder(qualifier)(qualifiedByQualifier))([importedDefinitions]))(qualifiedByQualifier), qualifierOwners = Ashes.Collection.Map.setStr(qualifier)(moduleName)(qualifierOwners))

// Adds an unqualified binding as the newest one; `namesakes` are the unqualified bindings already
// filed under its local name.
let pushUnqualifiedBinding (binding: StitchedImportBinding) (namesakes: List(StitchedImportBinding)) (bindings: ImportBindingAccumulator) =
    match (binding, bindings) with
        | (StitchedImportBinding { localName = localName }, ImportBindingAccumulator { newestFirst = newestFirst, unqualifiedByLocalName = unqualifiedByLocalName, qualifiedByQualifier = qualifiedByQualifier, qualifierOwners = qualifierOwners }) ->
            ImportBindingAccumulator(newestFirst = binding :: newestFirst, unqualifiedByLocalName = Ashes.Collection.Map.setStr(localName)(appendList(namesakes)([binding]))(unqualifiedByLocalName), qualifiedByQualifier = qualifiedByQualifier, qualifierOwners = qualifierOwners)

let qualifierConflicts qualifier importedName (bindings: ImportBindingAccumulator) =
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

let recursive localDefinitionShadows name kind definitions =
    match definitions with
        | [] -> false
        | definition :: rest ->
            if definition.kind
            |> sameNamespace(kind)
            |> both(definition.sourceName == name)
            then true
            else localDefinitionShadows(name)(kind)(rest)

let valueNamespace kind =
    match kind with
        | StitchedValue -> true
        | StitchedConstructor -> true
        | StitchedExternal -> true
        | _ -> false

// Whether an unqualified import binding that collides with an earlier one is an error for this
// module. Language.md makes unqualified ACCESS to a name two imported modules both export the
// error, not the pair of imports themselves (`import Ashes.Collection.List as list` beside
// `import Ashes.Text as text` is routine, and both export `length`): a value-namespace collision
// is fatal only where the module's own top-level values or trailing expression mention the name,
// the surface stage 0's referenced-name walk covers, and never when a local top-level definition
// of the same namespace shadows every import. A type-namespace collision stays an error at the
// import, since a type name's uses are not scanned here.
let importConflictIsFatal (program: ProgramSyntax) definitions (binding: StitchedImportBinding) =
    match binding with
        | StitchedImportBinding { localName = name, qualifier = _qualifier, target = target } ->
            if localDefinitionShadows(name)(target.kind)(definitions)
            then false
            else
                if valueNamespace(target.kind)
                then programMentionsVariable(name)(program)
                else true

// An unused collision keeps the first import's binding; `fatalConflict` decides the rest. Only
// unqualified bindings of one local name can conflict, so a qualified binding is added outright
// and an unqualified one is compared with the unqualified bindings filed under its name.
// `importedDefinitions` is the definition index of the module the additions come from.
let recursive addImportBindings moduleName fatalConflict (importedDefinitions: MapTree(Str, List(StitchedDefinition))) additions (current: ImportBindingAccumulator) =
    match additions with
        | [] -> Ok(current)
        | (StitchedImportBinding { localName = bindingName, qualifier = bindingQualifier, target = _target } as binding) :: rest ->
            match bindingQualifier with
                | Some(qualifier) ->
                    current
                    |> pushQualifiedBinding(qualifier)(binding)(importedDefinitions)
                    |> addImportBindings(moduleName)(fatalConflict)(importedDefinitions)(rest)
                | None ->
                    let namesakes = bindingsNamed(bindingName)(current.unqualifiedByLocalName)
                    in
                        if unqualifiedConflict(binding)(namesakes)
                        then
                            if fatalConflict(binding)
                            then
                                bindingName
                                |> ConflictingStitchedImport(moduleName)
                                |> Error
                            else addImportBindings(moduleName)(fatalConflict)(importedDefinitions)(rest)(current)
                        else
                            current
                            |> pushUnqualifiedBinding(binding)(namesakes)
                            |> addImportBindings(moduleName)(fatalConflict)(importedDefinitions)(rest)

let addWholeModuleImport ownerModule fatalConflict (imported: StitchedModuleScope) alias (bindings: ImportBindingAccumulator) =
    match imported with
        | StitchedModuleScope { name = importedName, packageId = _packageId, sourcePath = _sourcePath, imports = _imports, definitions = definitions, definitionsBySourceName = bySourceName } ->
            let primaryQualifier =
                match alias with
                    | Some(name) -> name
                    | None -> importedName
            in
                if qualifierConflicts(primaryQualifier)(importedName)(bindings)
                then
                    primaryQualifier
                    |> ConflictingModuleQualifier(ownerModule)
                    |> Error
                else
                    let qualified = exportedBindings(Some(primaryQualifier))(definitions)
                    in
                        let withShort =
                            match alias with
                                // A qualified reference through the module's own canonical name
                                // (`Ashes.Collection.List.length(xs)`) must resolve regardless of
                                // whatever alias the import chose, matching stage 0's algorithmic
                                // qualifier resolution (`ResolveModuleAlias`, consulted only to
                                // rewrite an alias to its target before mangling — a canonical
                                // name that is not itself a registered alias resolves unchanged).
                                // Registered unconditionally, with no `qualifierConflicts` check:
                                // stage 0 has none either for a directly-written canonical name.
                                | Some(_name) ->
                                    if importedName == primaryQualifier
                                    then qualified
                                    else
                                        definitions
                                        |> exportedBindings(Some(importedName))
                                        |> appendList(qualified)
                                | None ->
                                    let leaf = moduleLeaf(importedName)
                                    in
                                        if leaf == importedName
                                        then qualified
                                        else
                                            if qualifierConflicts(leaf)(importedName)(bindings)
                                            then []
                                            else
                                                definitions
                                                |> exportedBindings(Some(leaf))
                                                |> appendList(qualified)
                        in
                            match alias with
                                | Some(_name) ->
                                    addImportBindings(
                                        ownerModule,
                                        fatalConflict,
                                        bySourceName,
                                        appendList(exportedBindings(None)(definitions))(withShort),
                                        bindings
                                    )
                                | None ->
                                    let leaf = moduleLeaf(importedName)
                                    in
                                        if bindings
                                        |> qualifierConflicts(leaf)(importedName)
                                        |> both(leaf != importedName)
                                        then
                                            leaf
                                            |> ConflictingModuleQualifier(ownerModule)
                                            |> Error
                                        else
                                            addImportBindings(
                                                ownerModule,
                                                fatalConflict,
                                                bySourceName,
                                                appendList(exportedBindings(None)(definitions))(withShort),
                                                bindings
                                            )

// A selector import of an intrinsic builtin module's member binds no definition: the module has
// none, and the reference rewriter resolves the local name through the scope's
// `intrinsicSelectors` instead.
let isIntrinsicMember (moduleName: Str) (memberName: Str) =
    if isIntrinsicBuiltinModule(moduleName)
    then
        match coreBuiltinKind(moduleName)(memberName) with
            | Some(_kind) -> true
            | None -> false
    else false

let addSelectorImport ownerModule fatalConflict (imported: StitchedModuleScope) exportName localName kind (bindings: ImportBindingAccumulator) =
    match imported with
        | StitchedModuleScope { name = importedName, packageId = _packageId, sourcePath = _sourcePath, imports = _imports, definitionsBySourceName = bySourceName } ->
            match bySourceName
            |> definitionsNamed(exportName)
            |> findExportedDefinition(exportName)(kind) with
                | None ->
                    if isIntrinsicMember(importedName)(exportName)
                    then Ok(bindings)
                    else
                        exportName
                        |> MissingStitchedImportExport(ownerModule)(importedName)
                        |> Error
                | Some(definition) ->
                    addImportBindings(
                        ownerModule,
                        fatalConflict,
                        bySourceName,
                        [StitchedImportBinding(localName = localName, qualifier = None, target = definition)],
                        bindings
                    )

// Stage 0 normalizes a type selector import (`import M.Type`) into a whole-module import of `M`
// beside the selector (`NormalizeTypeSelectors`), so the module's exports, its constructors
// among them, are in scope unqualified and qualified as by `import M`; a module the scope
// already imports wholesale is not imported again.
let addTypeSelectorImport ownerModule fatalConflict (imported: StitchedModuleScope) exportName localName (bindings: ImportBindingAccumulator) =
    match imported with
        | StitchedModuleScope { name = importedName } ->
            let withModule =
                match qualifierOwner(importedName)(bindings) with
                    | Some(_owner) -> Ok(bindings)
                    | None -> addWholeModuleImport(ownerModule)(fatalConflict)(imported)(None)(bindings)
            in
                match withModule with
                    | Error(error) -> Error(error)
                    | Ok(moduleBindings) -> addSelectorImport(ownerModule)(fatalConflict)(imported)(exportName)(localName)(StitchedType)(moduleBindings)

let recursive buildImportBindings ownerModule fatalConflict imports completed (bindings: ImportBindingAccumulator) =
    match imports with
        | [] -> Ok(bindings)
        | resolved :: rest ->
            match findModule(resolvedModule(resolved))(completed) with
                | None ->
                    resolved
                    |> resolvedModule
                    |> MissingStitchedImportModule(ownerModule)
                    |> Error
                | Some(imported) ->
                    let added =
                        match resolved with
                            | ResolvedModuleImport(_moduleName, alias, _line, _written) ->
                                addWholeModuleImport(
                                    ownerModule,
                                    fatalConflict,
                                    imported,
                                    alias,
                                    bindings
                                )
                            | ResolvedValueImport(_moduleName, exportName, localName, _line, _written) ->
                                addSelectorImport(
                                    ownerModule,
                                    fatalConflict,
                                    imported,
                                    exportName,
                                    localName,
                                    StitchedValue,
                                    bindings
                                )
                            | ResolvedTypeImport(_moduleName, exportName, localName, _line, _written) ->
                                addTypeSelectorImport(
                                    ownerModule,
                                    fatalConflict,
                                    imported,
                                    exportName,
                                    localName,
                                    bindings
                                )
                    in
                        match added with
                            | Error(error) -> Error(error)
                            | Ok(next) -> buildImportBindings(ownerModule)(fatalConflict)(rest)(completed)(next)

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
                    | Some(name) ->
                        (deepCopy(name), deepCopy(importedName)) :: appendList(leafEntries)(collectModuleAliases(rest))
                    | None ->
                        rest
                        |> collectModuleAliases
                        |> appendList(leafEntries)
        | _ :: rest -> collectModuleAliases(rest)

// The selector imports naming an intrinsic builtin module's member, each as the local name the
// importing module reads it by, the module, and the member.
let recursive collectIntrinsicSelectors resolvedImports =
    match resolvedImports with
        | [] -> []
        | ResolvedValueImport(importedName, exportName, localName, _line, _written) :: rest ->
            if isIntrinsicMember(importedName)(exportName)
            then IntrinsicSelector(localName = deepCopy(localName), moduleName = deepCopy(importedName), memberName = deepCopy(exportName)) :: collectIntrinsicSelectors(rest)
            else collectIntrinsicSelectors(rest)
        | _ :: rest -> collectIntrinsicSelectors(rest)

let recursive findModuleAlias (qualifier: Str) (aliases: List((Str, Str))) =
    match aliases with
        | [] -> None
        | (alias, target) :: rest ->
            if alias == qualifier
            then
                target
                |> deepCopy
                |> Some
            else findModuleAlias(qualifier)(rest)

let resolveStitchedModuleAlias moduleName qualifier (project: StitchedSemanticProject) =
    match findModule(moduleName)(project.scopes) with
        | None -> None
        | Some(moduleScope) -> findModuleAlias(qualifier)(moduleScope.moduleAliases)

// A whole-module import with no `as` alias (`import Ashes.IO`, not `import Ashes.IO as io`)
// exposes its exported names unqualified as well as qualified (language.md's "Import and use
// unqualified names": `import M` | "module M (qualified access; exported names also unqualified)").
// `collectModuleAliases` above also records this SAME import's leaf-qualifier shorthand (`IO` ->
// `Ashes.IO`), but an aliased import (`import Ashes.IO as io`) gets a leaf entry too, so the alias
// map alone can't distinguish "this import exposes unqualified names" from "this import only
// exposes a leaf/alias qualifier" — this list tracks the narrower, alias-free set the reference
// rewriter needs for bare-name resolution.
let recursive collectPlainWholeModuleImports resolvedImports =
    match resolvedImports with
        | [] -> []
        | ResolvedModuleImport(importedName, None, _line, _written) :: rest -> deepCopy(importedName) :: collectPlainWholeModuleImports(rest)
        | _ :: rest -> collectPlainWholeModuleImports(rest)

let stitchedModulePlainImports moduleName (project: StitchedSemanticProject) =
    match findModule(moduleName)(project.scopes) with
        | None -> []
        | Some(moduleScope) -> moduleScope.plainWholeModuleImports

// The intrinsic module and member a module's selector import binds `localName` to, if any.
let recursive findIntrinsicSelector (localName: Str) (selectors: List(IntrinsicSelector)) =
    match selectors with
        | [] -> None
        | IntrinsicSelector { localName = candidate, moduleName = moduleName, memberName = memberName } :: rest ->
            if candidate == localName
            then Some((deepCopy(moduleName), deepCopy(memberName)))
            else findIntrinsicSelector(localName)(rest)

let resolveStitchedIntrinsicSelector moduleName localName (project: StitchedSemanticProject) =
    match findModule(moduleName)(project.scopes) with
        | None -> None
        | Some(moduleScope) -> findIntrinsicSelector(localName)(moduleScope.intrinsicSelectors)

let buildModule (unit: SemanticStitchUnit) (state: StitchState) =
    match (unit, state) with
        | (SemanticStitchUnit { name = moduleName, packageId = packageId, sourcePath = sourcePath, imports = resolvedImports, interface = ModuleImportInterface { name = _interfaceName, exports = exports }, program = program, isEntry = _isEntry }, StitchState { reversedModules = completedModules, nextDefinitionId = nextDefinitionId, compilerNameOwners = compilerNameOwners }) ->
            if hasModule(moduleName)(completedModules)
            then Error(DuplicateStitchedModule(moduleName))
            else
                match materializeDefinitions(indexExportsByName(reverseList(exports))(Ashes.Collection.Map.empty))(collectPendingDefinitions(program.items)(DefinitionCollection(reversed = [], nextOrder = 0)))(unit)(nextDefinitionId)([]) with
                    | (definitions, nextId) ->
                        let reversedDefinitions = reverseList(definitions)
                        in
                            let bySourceName = indexDefinitionsBySourceName(reversedDefinitions)(Ashes.Collection.Map.empty)
                            in
                                match duplicateDefinition(definitions)(bySourceName) with
                                    | Some(name) ->
                                        name
                                        |> DuplicateModuleDeclaration(moduleName)
                                        |> Error
                                    | None ->
                                        match compilerNameOwners |> validateCompilerNames(definitions) with
                                            | Some(error) -> Error(error)
                                            | None ->
                                                match buildImportBindings(
                                                    moduleName,
                                                    importConflictIsFatal(program)(definitions),
                                                    resolvedImports,
                                                    completedModules,
                                                    noImportBindings
                                                ) with
                                                    | Error(error) -> Error(error)
                                                    | Ok(bindings) ->
                                                        Ok(
                                                            StitchState(reversedModules = StitchedModuleScope(name = moduleName, packageId = packageId, sourcePath = sourcePath, imports = reverseList(bindings.newestFirst), moduleAliases = collectModuleAliases(resolvedImports), plainWholeModuleImports = collectPlainWholeModuleImports(resolvedImports), intrinsicSelectors = collectIntrinsicSelectors(resolvedImports), definitions = definitions, unqualifiedImportsByLocalName = bindings.unqualifiedByLocalName, qualifiedImportsByQualifier = bindings.qualifiedByQualifier, definitionsBySourceName = bySourceName) :: completedModules, nextDefinitionId = nextId, compilerNameOwners = recordCompilerNames(reversedDefinitions)(compilerNameOwners))
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
    given (units) -> buildModules(units)(StitchState(reversedModules = [], nextDefinitionId = 0, compilerNameOwners = Ashes.Collection.Map.empty))

let definitionVisibleAt boundary (definition: StitchedDefinition) = definition.visibleFrom <= boundary

let recursive resolveLocalAt name kind boundary definitions =
    match definitions with
        | [] -> None
        | definition :: rest ->
            if definition.sourceName == name && definitionVisibleAt(boundary)(definition) && sameNamespace(kind)(definition.kind)
            then
                definition
                |> deepCopy
                |> Some
            else resolveLocalAt(name)(kind)(boundary)(rest)

let recursive resolveImported name qualifier kind bindings =
    match bindings with
        | [] -> None
        | StitchedImportBinding { localName = localName, qualifier = bindingQualifier, target = target } :: rest ->
            if localName == name && sameNamespace(kind)(target.kind) && bindingQualifier == qualifier
            then
                target
                |> deepCopy
                |> Some
            else resolveImported(name)(qualifier)(kind)(rest)

// The first export of the name among the modules bound under a qualifier, in import order: the
// target of the first import binding carrying that qualifier and local name.
let recursive resolveQualifiedImport name kind (indexes: List(MapTree(Str, List(StitchedDefinition)))) =
    match indexes with
        | [] -> None
        | index :: rest ->
            match index
            |> definitionsNamed(name)
            |> findExportedDefinition(name)(kind) with
                | Some(definition) -> Some(definition)
                | None -> resolveQualifiedImport(name)(kind)(rest)

let resolveStitchedUnqualified moduleName boundary kind name (project: StitchedSemanticProject) =
    match findModule(moduleName)(project.scopes) with
        | None -> None
        | Some(moduleScope) ->
            match moduleScope.definitionsBySourceName
            |> definitionsNamed(name)
            |> resolveLocalAt(name)(kind)(boundary) with
                | Some(definition) -> Some(definition)
                | None ->
                    moduleScope.unqualifiedImportsByLocalName
                    |> bindingsNamed(name)
                    |> resolveImported(name)(None)(kind)

// A qualifier naming a shipped module outright (`Ashes.Text` in `Ashes.Text.join`) resolves to
// that module's export with no import in scope, language.md's "qualified access, no import
// required"; the reserved namespace keeps a user module from ever matching this way. The module
// is in the project only when a stitcher loaded it for exactly such a reference.
let resolveShippedModuleExport qualifier kind name (project: StitchedSemanticProject) =
    if Ashes.Text.startsWith(qualifier)("Ashes.")
    then
        match findModule(qualifier)(project.scopes) with
            | None -> None
            | Some(moduleScope) ->
                match moduleScope.definitionsBySourceName
                |> definitionsNamed(name)
                |> findExportedDefinition(name)(kind) with
                    | None -> None
                    | Some(definition) ->
                        definition
                        |> deepCopy
                        |> Some
    else None

// A type a shipped module exports is in scope by its bare name wherever the program reaches that
// module, imported or not, as stage 0's one type namespace holds every loaded standard-library
// type: `MapTree(Str, Bool)` names the map type in a program that reaches `Ashes.Collection.Map`
// through qualified references alone.
let recursive resolveShippedTypeIn name (scopes: List(StitchedModuleScope)) =
    match scopes with
        | [] -> None
        | moduleScope :: rest ->
            if Ashes.Text.startsWith(moduleScope.name)("Ashes.")
            then
                match moduleScope.definitionsBySourceName
                |> definitionsNamed(name)
                |> findExportedDefinition(name)(StitchedType) with
                    | Some(definition) ->
                        if definition.kind == StitchedType
                        then Some(definition)
                        else resolveShippedTypeIn(name)(rest)
                    | None -> resolveShippedTypeIn(name)(rest)
            else resolveShippedTypeIn(name)(rest)

let resolveStitchedShippedType name (project: StitchedSemanticProject) = resolveShippedTypeIn(name)(project.scopes)

// A file reaches its own inline modules by qualified path with no import (`Geometry.area` in the
// file that declares `module Geometry =`). Their scopes are named `<file>#<module>`.
let resolveOwnInlineModuleExport (referencing: StitchedModuleScope) qualifier kind name (project: StitchedSemanticProject) =
    match findModule(qualifier)(project.scopes) with
        | None -> None
        | Some(target) ->
            let separator = Ashes.Text.indexOf(referencing.sourcePath)("#")
            in
                let file =
                    if separator < 0
                    then referencing.sourcePath
                    else Ashes.Text.take(referencing.sourcePath)(separator)
                in
                    if target.sourcePath == file + "#" + qualifier
                    then
                        match target.definitionsBySourceName
                        |> definitionsNamed(name)
                        |> findExportedDefinition(name)(kind) with
                            | None -> None
                            | Some(definition) ->
                                definition
                                |> deepCopy
                                |> Some
                    else None

let resolveStitchedQualified moduleName qualifier kind name (project: StitchedSemanticProject) =
    match findModule(moduleName)(project.scopes) with
        | None -> None
        | Some(moduleScope) ->
            match moduleScope.qualifiedImportsByQualifier
            |> definitionIndexesUnder(qualifier)
            |> resolveQualifiedImport(name)(kind) with
                | Some(definition) -> Some(definition)
                | None ->
                    match resolveShippedModuleExport(qualifier)(kind)(name)(project) with
                        | Some(definition) -> Some(definition)
                        | None -> resolveOwnInlineModuleExport(moduleScope)(qualifier)(kind)(name)(project)
