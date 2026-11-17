import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.ModuleSemanticStitching
import FrozenStitching
let mix (left: Int) (right: Int) = (left * 1103515245 + right * 12345 + 1013904223) % 2147483647

let roll (seed: Int) (slot: Int) (bound: Int) =
    mix(mix(seed)(slot))(slot * 31 + 7) / 8 % bound

let recursive every check items =
    match items with
        | [] -> true
        | item :: rest -> check(item) && every(check)(rest)

let recursive countFrom (first: Int) (last: Int) =
    if first > last
    then []
    else first :: countFrom(first + 1)(last)

// Every name is a fresh literal: the generated syntax owns its strings outright.
let nameAt (index: Int) =
    match index with
        | 0 -> "a"
        | 1 -> "b"
        | 2 -> "A"
        | 3 -> "B"
        | 4 -> "deepCopy"
        | 5 -> "Util_a"
        | 6 -> "c"
        | _ -> "C"

let moduleNameAt (index: Int) =
    match index with
        | 0 -> "Lib.Util"
        | 1 -> "Other.Util"
        | 2 -> "Ashes.Fake"
        | 3 -> "Lib"
        | 4 -> "Ashes.Internal"
        | _ -> "Solo"

let aliasAt (index: Int) =
    match index with
        | 0 -> "Util"
        | 1 -> "u"
        | 2 -> "Lib"
        | 3 -> "Lib.Util"
        | 4 -> "Other.Util"
        | _ -> "A"

let queriedModules = ["Lib.Util", "Other.Util", "Ashes.Fake", "Lib", "Ashes.Internal", "Solo", "Main", "Nowhere"]

let queriedNames = ["a", "b", "A", "B", "deepCopy", "Util_a", "c", "C", "missing"]

let queriedQualifiers = ["Lib.Util", "Other.Util", "Ashes.Fake", "Lib", "Ashes.Internal", "Solo", "Util", "u", "A", "Fake", "Internal"]

let kinds = [StitchedValue, StitchedType, StitchedConstructor, StitchedCapability, StitchedTrait, StitchedExternal]

let boundaries = countFrom(-1)(6)

let binding (name: Str) (value: Expr) = LetBindingSyntax(name = name, value = value, sugarParameters = [], typeAnnotation = None, requirements = [], declarationStart = -1)

let constructor (name: Str) = TypeConstructor(name = name, parameters = [], fieldNames = [])

// A position draws its first name from two pool entries of its own, and its second from the two of
// the position two further on, so same-module duplicates occur without dominating.
let slotName (seed: Int) (slot: Int) (position: Int) = nameAt((position * 2 + roll(seed)(slot)(2)) % 8)

// Every use of a generated name builds the string anew, so no two syntax nodes share one.
let firstName (seed: Int) (slot: Int) (position: Int) = slotName(seed)(slot + 1)(position)

let secondName (seed: Int) (slot: Int) (position: Int) = nameAt((position * 2 + 4 + roll(seed)(slot + 2)(2)) % 8)

let valueItem (seed: Int) (slot: Int) (position: Int) (isRecursive: Bool) =
    TopLevelLet(binding(firstName(seed)(slot)(position))(ExprInt(1)))(isRecursive)

let itemsAt (seed: Int) (slot: Int) (position: Int) =
    match roll(seed)(slot)(14) with
        | 0 -> []
        | 1 -> [valueItem(seed)(slot)(position)(false)]
        | 2 -> [valueItem(seed)(slot)(position)(true)]
        | 3 ->
            [TopLevelType(TypeDecl(name = firstName(seed)(slot)(position), typeParameters = [], constructors = [position
            |> firstName(seed)(slot)
            |> constructor, position
            |> secondName(seed)(slot)
            |> constructor], isRecord = false, derivingTraits = []))]
        | 4 -> [TopLevelTypeAlias(TypeAliasDecl(name = firstName(seed)(slot)(position), typeParameters = [], target = TypeNamed("Int")))]
        | 5 ->
            [TopLevelZeroCostType(ZeroCostTypeDecl(name = firstName(seed)(slot)(position), typeParameters = [], constructor = position
            |> secondName(seed)(slot)
            |> constructor, derivingTraits = []))]
        | 6 ->
            [None
            |> ExternalOpaqueType(firstName(seed)(slot)(position))
            |> TopLevelExternal]
        | 7 ->
            [None
            |> ExternalFunction(firstName(seed)(slot)(position))([])(ParsedNamed("Int"))(None)([])
            |> TopLevelExternal]
        | 8 -> [TopLevelCapability(CapabilityDecl(name = firstName(seed)(slot)(position), typeParameters = [], operations = []))]
        | 9 -> [TopLevelTrait(TraitDecl(name = firstName(seed)(slot)(position), typeParameters = [], supertraits = [], methods = []))]
        | 10 ->
            [TopLevelRecursiveGroup([binding(firstName(seed)(slot)(position))(ExprInt(1)), binding(secondName(seed)(slot)(position))(ExprInt(2))])]
        | 11 ->
            [false
            |> valueItem(seed)(slot)(position)
            |> TopLevelAt(TextSpan(start = slot, end = slot + 1))]
        | 12 ->
            [TopLevelLet(position
            |> secondName(seed)(slot)
            |> ExprVar
            |> binding(firstName(seed)(slot)(position)))(false)]
        | _ ->
            [None
            |> ExternalFunction("a")([])(ParsedNamed("Int"))(None)([])
            |> TopLevelExternal]

let recursive itemsFrom (seed: Int) (slot: Int) (position: Int) (count: Int) =
    if position >= count
    then []
    else
        count
        |> itemsFrom(seed)(slot)(position + 1)
        |> Ashes.Collection.List.append(itemsAt(seed)(slot + position * 5)(position))

let consIf (keep: Bool) (entry: ModuleImportExport) (rest: List(ModuleImportExport)) =
    if keep
    then entry :: rest
    else rest

let recursive exportsOf (seed: Int) (slot: Int) (index: Int) =
    if index >= 8
    then []
    else
        index + 1
        |> exportsOf(seed)(slot + 4)
        |> consIf(roll(seed)(slot + 3)(5) == 0)(index
        |> nameAt
        |> ImportModuleExport)
        |> consIf(roll(seed)(slot + 2)(6) != 0)(index
        |> nameAt
        |> ImportConstructorExport)
        |> consIf(roll(seed)(slot + 1)(6) != 0)(index
        |> nameAt
        |> ImportTypeExport)
        |> consIf(roll(seed)(slot)(6) != 0)(index
        |> nameAt
        |> ImportValueExport)

// The module a dependency slot names: three distinct pool entries per seed.
let dependencyName (seed: Int) (index: Int) = moduleNameAt((roll(seed)(1)(6) + index * (1 + roll(seed)(2)(2) * 4)) % 6)

// How an item roll declares its first name: 1 as a value, 2 in the type namespace, 0 in no form a
// selector import can name.
let slotNamespace (kind: Int) =
    if kind == 1 || kind == 2 || kind == 10 || kind == 11 || kind == 12
    then 1
    else
        if kind == 3 || kind == 4 || kind == 5 || kind == 6 || kind == 8 || kind == 9
        then 2
        else 0

let targetIndex (seed: Int) (slot: Int) (available: Int) = roll(seed)(slot + 1)(available)

let targetOf (seed: Int) (slot: Int) (available: Int) =
    available
    |> targetIndex(seed)(slot)
    |> dependencyName(seed)

// Seven times in eight a selector names the first declaration of its target module.
let exportedOf (seed: Int) (slot: Int) (available: Int) =
    if roll(seed)(slot + 3)(8) == 0
    then
        3
        |> roll(seed)(slot + 8)
        |> slotName(seed)(slot + 2)
    else slotName(seed)(1000 * (targetIndex(seed)(slot)(available) + 1) + 11)(0)

let localOf (seed: Int) (slot: Int) (available: Int) =
    if roll(seed)(slot + 4)(3) == 0
    then
        8
        |> roll(seed)(slot + 5)
        |> nameAt
    else exportedOf(seed)(slot)(available)

let wholeImport (seed: Int) (slot: Int) (available: Int) =
    ResolvedModuleImport(targetOf(seed)(slot)(available))(None)(slot)("import")

let aliasedImport (seed: Int) (slot: Int) (available: Int) =
    ResolvedModuleImport(targetOf(seed)(slot)(available))(6
    |> roll(seed)(slot + 7)
    |> aliasAt
    |> Some)(slot)("import")

let selectorImport (seed: Int) (slot: Int) (available: Int) =
    match 14
    |> roll(seed)(1000 * (targetIndex(seed)(slot)(available) + 1) + 10)
    |> slotNamespace with
        | 1 ->
            ResolvedValueImport(targetOf(seed)(slot)(available))(exportedOf(seed)(slot)(available))(localOf(seed)(slot)(available))(slot)("import")
        | 2 ->
            ResolvedTypeImport(targetOf(seed)(slot)(available))(exportedOf(seed)(slot)(available))(localOf(seed)(slot)(available))(slot)("import")
        | _ -> wholeImport(seed)(slot)(available)

// One module under another module's full name, then that module under an alias of its own: the
// second import still registers its full name as a qualifier, so two modules answer to it and
// the first binding in import order has to win.
let sharedQualifierImports (seed: Int) (slot: Int) (available: Int) =
    [ResolvedModuleImport(available
    |> targetIndex(seed)(slot)
    |> dependencyName(seed))((targetIndex(seed)(slot)(available) + 1) % available
    |> dependencyName(seed)
    |> Some)(slot)("import"), ResolvedModuleImport(dependencyName(seed)((targetIndex(seed)(slot)(available) + 1) % available))(Some("u"))(slot)("import")]

let importAt (seed: Int) (slot: Int) (available: Int) =
    if available <= 0
    then []
    else
        if roll(seed)(slot + 6)(60) == 0
        then [ResolvedModuleImport("Nowhere")(None)(slot)("import Nowhere")]
        else
            match roll(seed)(slot)(14) with
                | 0 -> []
                | 4 -> [aliasedImport(seed)(slot)(available)]
                | 5 -> [aliasedImport(seed)(slot)(available)]
                | 12 -> [aliasedImport(seed)(slot)(available)]
                | 6 -> [selectorImport(seed)(slot)(available)]
                | 8 -> [selectorImport(seed)(slot)(available)]
                | 7 ->
                    [ResolvedValueImport(targetOf(seed)(slot)(available))("deepCopy")(localOf(seed)(slot)(available))(slot)("import")]
                | 13 -> [selectorImport(seed)(slot)(available), wholeImport(seed)(slot)(available)]
                | 9 -> sharedQualifierImports(seed)(slot)(available)
                | _ -> [wholeImport(seed)(slot)(available)]

let recursive importsFrom (seed: Int) (slot: Int) (available: Int) (count: Int) =
    if count <= 0
    then []
    else
        count - 1
        |> importsFrom(seed)(slot + 9)(available)
        |> Ashes.Collection.List.append(importAt(seed)(slot)(available))

let dependencyPath (seed: Int) (index: Int) =
    if roll(seed)(1000 * (index + 1))(4) == 0
    then "/Main.ash#" + dependencyName(seed)(index)
    else "/" + dependencyName(seed)(index) + ".ash"

let dependencyUnit (seed: Int) (index: Int) = SemanticStitchUnit(name = dependencyName(seed)(index), packageId = "pkg", sourcePath = dependencyPath(seed)(index), imports = importsFrom(seed)(1000 * (index + 1) + 500)(index)(1), interface = ModuleImportInterface(name = dependencyName(seed)(index), exports = exportsOf(seed)(1000 * (index + 1) + 100)(0)), program = ProgramSyntax(items = itemsFrom(seed)(1000 * (index + 1) + 10)(0)(1 + roll(seed)(1000 * (index + 1) + 1)(4)), body = None), isEntry = false)

let mainUnit (seed: Int) =
    (let body =
        match roll(seed)(7000)(3) with
            | 0 -> None
            | 1 ->
                0
                |> slotName(seed)(1011)
                |> ExprVar
                |> Some
            | _ ->
                8
                |> roll(seed)(7001)
                |> nameAt
                |> ExprVar
                |> Some
    in
        SemanticStitchUnit(name = "Main", packageId = "app", sourcePath = "/Main.ash", imports = importsFrom(seed)(7500)(3)(1 + roll(seed)(7002)(4)), interface = ModuleImportInterface(name = "Main", exports = exportsOf(seed)(7100)(0)), program = ProgramSyntax(items = 3
        |> roll(seed)(7003)
        |> itemsFrom(seed)(7010)(0), body = body), isEntry = true))

let unitsOf (seed: Int) =
    match roll(seed)(3)(40) with
        | 0 -> [dependencyUnit(seed)(0), dependencyUnit(seed)(1), dependencyUnit(seed)(0), mainUnit(seed)]
        | 1 -> [dependencyUnit(seed)(0), mainUnit(seed), dependencyUnit(seed)(1), dependencyUnit(seed)(2)]
        | _ -> [dependencyUnit(seed)(0), dependencyUnit(seed)(1), dependencyUnit(seed)(2), mainUnit(seed)]

let recursive scopesAgree (old: List(FrozenModuleScope)) (new: List(StitchedModuleScope)) =
    match (old, new) with
        | ([], []) -> true
        | (FrozenModuleScope { name = oldName, packageId = oldPackage, sourcePath = oldPath, imports = oldImports, moduleAliases = oldAliases, plainWholeModuleImports = oldPlain, intrinsicSelectors = oldSelectors, definitions = oldDefinitions } :: oldRest, StitchedModuleScope { name = newName, packageId = newPackage, sourcePath = newPath, imports = newImports, moduleAliases = newAliases, plainWholeModuleImports = newPlain, intrinsicSelectors = newSelectors, definitions = newDefinitions } :: newRest) -> oldName == newName && oldPackage == newPackage && oldPath == newPath && oldImports == newImports && oldAliases == newAliases && oldPlain == newPlain && oldSelectors == newSelectors && oldDefinitions == newDefinitions && scopesAgree(oldRest)(newRest)
        | _ -> false

let unqualifiedAgree (old: FrozenSemanticProject) (new: StitchedSemanticProject) =
    every(given (moduleName) ->
        every(given (name) ->
            every(given (kind) ->
                every(given (boundary) -> frozenResolveStitchedUnqualified(moduleName)(boundary)(kind)(name)(old) == resolveStitchedUnqualified(moduleName)(boundary)(kind)(name)(new))(boundaries))(kinds))(queriedNames))(queriedModules)

let qualifiedAgree (old: FrozenSemanticProject) (new: StitchedSemanticProject) =
    every(given (moduleName) ->
        every(given (qualifier) ->
            every(given (name) ->
                every(given (kind) -> frozenResolveStitchedQualified(moduleName)(qualifier)(kind)(name)(old) == resolveStitchedQualified(moduleName)(qualifier)(kind)(name)(new))(kinds))(queriedNames) && frozenResolveStitchedModuleAlias(moduleName)(qualifier)(old) == resolveStitchedModuleAlias(moduleName)(qualifier)(new))(queriedQualifiers))(queriedModules)

let remainingAgree (old: FrozenSemanticProject) (new: StitchedSemanticProject) =
    every(given (name) ->
        frozenResolveStitchedShippedType(name)(old) == resolveStitchedShippedType(name)(new) && every(given (moduleName) -> frozenResolveStitchedIntrinsicSelector(moduleName)(name)(old) == resolveStitchedIntrinsicSelector(moduleName)(name)(new))(queriedModules))(queriedNames) && every(given (moduleName) -> frozenStitchedModulePlainImports(moduleName)(old) == stitchedModulePlainImports(moduleName)(new))(queriedModules)

let recursive total score items =
    match items with
        | [] -> 0
        | item :: rest -> score(item) + total(score)(rest)

let found (resolved: Maybe(StitchedDefinition)) =
    match resolved with
        | Some(_definition) -> 1
        | None -> 0

// How many of the compared name resolutions found a definition.
let resolvedIn (new: StitchedSemanticProject) =
    total(given (moduleName) ->
        total(given (name) ->
            total(given (kind) ->
                total(given (boundary) ->
                    new
                    |> resolveStitchedUnqualified(moduleName)(boundary)(kind)(name)
                    |> found)(boundaries) + total(given (qualifier) ->
                    new
                    |> resolveStitchedQualified(moduleName)(qualifier)(kind)(name)
                    |> found)(queriedQualifiers))(kinds))(queriedNames))(queriedModules)

// 8 modules by 9 names by 6 kinds by 8 boundaries; 8 modules by 11 qualifiers by (9 names by 6
// kinds, and one alias lookup); 9 shipped types, 9 names by 8 modules of intrinsic selectors, 8
// plain-import lists; and the build result itself.
let comparisonsPerProject = 8 * 9 * 6 * 8 + 8 * 11 * (9 * 6 + 1) + 9 + 9 * 8 + 8 + 1

type Tally =
    | projects: Int
    | duplicateModule: Int
    | duplicateDeclaration: Int
    | missingModule: Int
    | missingExport: Int
    | conflictingImport: Int
    | conflictingQualifier: Int
    | nameCollision: Int
    | comparisons: Int
    | resolved: Int

let countError (error: ModuleSemanticStitchError) (tally: Tally) =
    match (error, tally) with
        | (DuplicateStitchedModule(_), Tally { projects = projects, duplicateModule = duplicateModule, duplicateDeclaration = duplicateDeclaration, missingModule = missingModule, missingExport = missingExport, conflictingImport = conflictingImport, conflictingQualifier = conflictingQualifier, nameCollision = nameCollision, comparisons = comparisons, resolved = resolved }) -> Tally(projects = projects, duplicateModule = duplicateModule + 1, duplicateDeclaration = duplicateDeclaration, missingModule = missingModule, missingExport = missingExport, conflictingImport = conflictingImport, conflictingQualifier = conflictingQualifier, nameCollision = nameCollision, comparisons = comparisons + 1, resolved = resolved)
        | (DuplicateModuleDeclaration(_, _), Tally { projects = projects, duplicateModule = duplicateModule, duplicateDeclaration = duplicateDeclaration, missingModule = missingModule, missingExport = missingExport, conflictingImport = conflictingImport, conflictingQualifier = conflictingQualifier, nameCollision = nameCollision, comparisons = comparisons, resolved = resolved }) -> Tally(projects = projects, duplicateModule = duplicateModule, duplicateDeclaration = duplicateDeclaration + 1, missingModule = missingModule, missingExport = missingExport, conflictingImport = conflictingImport, conflictingQualifier = conflictingQualifier, nameCollision = nameCollision, comparisons = comparisons + 1, resolved = resolved)
        | (MissingStitchedImportModule(_, _), Tally { projects = projects, duplicateModule = duplicateModule, duplicateDeclaration = duplicateDeclaration, missingModule = missingModule, missingExport = missingExport, conflictingImport = conflictingImport, conflictingQualifier = conflictingQualifier, nameCollision = nameCollision, comparisons = comparisons, resolved = resolved }) -> Tally(projects = projects, duplicateModule = duplicateModule, duplicateDeclaration = duplicateDeclaration, missingModule = missingModule + 1, missingExport = missingExport, conflictingImport = conflictingImport, conflictingQualifier = conflictingQualifier, nameCollision = nameCollision, comparisons = comparisons + 1, resolved = resolved)
        | (MissingStitchedImportExport(_, _, _), Tally { projects = projects, duplicateModule = duplicateModule, duplicateDeclaration = duplicateDeclaration, missingModule = missingModule, missingExport = missingExport, conflictingImport = conflictingImport, conflictingQualifier = conflictingQualifier, nameCollision = nameCollision, comparisons = comparisons, resolved = resolved }) -> Tally(projects = projects, duplicateModule = duplicateModule, duplicateDeclaration = duplicateDeclaration, missingModule = missingModule, missingExport = missingExport + 1, conflictingImport = conflictingImport, conflictingQualifier = conflictingQualifier, nameCollision = nameCollision, comparisons = comparisons + 1, resolved = resolved)
        | (ConflictingStitchedImport(_, _), Tally { projects = projects, duplicateModule = duplicateModule, duplicateDeclaration = duplicateDeclaration, missingModule = missingModule, missingExport = missingExport, conflictingImport = conflictingImport, conflictingQualifier = conflictingQualifier, nameCollision = nameCollision, comparisons = comparisons, resolved = resolved }) -> Tally(projects = projects, duplicateModule = duplicateModule, duplicateDeclaration = duplicateDeclaration, missingModule = missingModule, missingExport = missingExport, conflictingImport = conflictingImport + 1, conflictingQualifier = conflictingQualifier, nameCollision = nameCollision, comparisons = comparisons + 1, resolved = resolved)
        | (ConflictingModuleQualifier(_, _), Tally { projects = projects, duplicateModule = duplicateModule, duplicateDeclaration = duplicateDeclaration, missingModule = missingModule, missingExport = missingExport, conflictingImport = conflictingImport, conflictingQualifier = conflictingQualifier, nameCollision = nameCollision, comparisons = comparisons, resolved = resolved }) -> Tally(projects = projects, duplicateModule = duplicateModule, duplicateDeclaration = duplicateDeclaration, missingModule = missingModule, missingExport = missingExport, conflictingImport = conflictingImport, conflictingQualifier = conflictingQualifier + 1, nameCollision = nameCollision, comparisons = comparisons + 1, resolved = resolved)
        | (CompilerPrivateNameCollision(_, _, _), Tally { projects = projects, duplicateModule = duplicateModule, duplicateDeclaration = duplicateDeclaration, missingModule = missingModule, missingExport = missingExport, conflictingImport = conflictingImport, conflictingQualifier = conflictingQualifier, nameCollision = nameCollision, comparisons = comparisons, resolved = resolved }) -> Tally(projects = projects, duplicateModule = duplicateModule, duplicateDeclaration = duplicateDeclaration, missingModule = missingModule, missingExport = missingExport, conflictingImport = conflictingImport, conflictingQualifier = conflictingQualifier, nameCollision = nameCollision + 1, comparisons = comparisons + 1, resolved = resolved)

let countProject (hits: Int) (tally: Tally) =
    match tally with
        | Tally { projects = projects, duplicateModule = duplicateModule, duplicateDeclaration = duplicateDeclaration, missingModule = missingModule, missingExport = missingExport, conflictingImport = conflictingImport, conflictingQualifier = conflictingQualifier, nameCollision = nameCollision, comparisons = comparisons, resolved = resolved } -> Tally(projects = projects + 1, duplicateModule = duplicateModule, duplicateDeclaration = duplicateDeclaration, missingModule = missingModule, missingExport = missingExport, conflictingImport = conflictingImport, conflictingQualifier = conflictingQualifier, nameCollision = nameCollision, comparisons = comparisons + comparisonsPerProject, resolved = resolved + hits)

let differs (seed: Int) (what: Str) = Ashes.IO.panic("indexed stitching differs from the frozen oracle at seed " + Ashes.Text.fromInt(seed) + ": " + what)

let checkSeed (seed: Int) (tally: Tally) =
    (let units = unitsOf(seed)
    in
        match (frozenBuildStitchedSemanticProject(units), buildStitchedSemanticProject(units)) with
            | (Error(old), Error(new)) ->
                if old == new
                then countError(new)(tally)
                else differs(seed)("error")
            | (Ok(old), Ok(new)) ->
                if old.nextDefinitionId == new.nextDefinitionId && scopesAgree(old.scopes)(new.scopes)
                then
                    if unqualifiedAgree(old)(new)
                    then
                        if qualifiedAgree(old)(new)
                        then
                            if remainingAgree(old)(new)
                            then
                                countProject(resolvedIn(new))(tally)
                            else differs(seed)("shipped type, intrinsic selector or plain imports")
                        else differs(seed)("qualified resolution")
                    else differs(seed)("unqualified resolution")
                else differs(seed)("scopes")
            | _ -> differs(seed)("outcome"))

// Stops after a seed that ended in a missing module: compiled by stage 0, the frozen module does
// not survive a further build in the same process after reporting one, so the caller resumes at
// the seed returned in a new process.
let recursive checkSeeds (seed: Int) (last: Int) (tally: Tally) =
    if seed > last || tally.missingModule > 0
    then (seed, tally)
    else
        tally
        |> checkSeed(seed)
        |> checkSeeds(seed + 1)(last)

let report (outcome: (Int, Tally)) =
    match outcome with
        | (next, Tally { projects = projects, duplicateModule = duplicateModule, duplicateDeclaration = duplicateDeclaration, missingModule = missingModule, missingExport = missingExport, conflictingImport = conflictingImport, conflictingQualifier = conflictingQualifier, nameCollision = nameCollision, comparisons = comparisons, resolved = resolved }) -> Ashes.IO.print("ok next=" + Ashes.Text.fromInt(next) + " comparisons=" + Ashes.Text.fromInt(comparisons) + " projects=" + Ashes.Text.fromInt(projects) + " duplicateModule=" + Ashes.Text.fromInt(duplicateModule) + " duplicateDeclaration=" + Ashes.Text.fromInt(duplicateDeclaration) + " missingModule=" + Ashes.Text.fromInt(missingModule) + " missingExport=" + Ashes.Text.fromInt(missingExport) + " conflictingImport=" + Ashes.Text.fromInt(conflictingImport) + " conflictingQualifier=" + Ashes.Text.fromInt(conflictingQualifier) + " nameCollision=" + Ashes.Text.fromInt(nameCollision) + " resolved=" + Ashes.Text.fromInt(resolved))

// The benchmark project: `modules` dependencies of `size` exported values each, ten of them under
// names every dependency shares, and an entry module of `size * 4` definitions that imports every
// dependency whole, every other one under an alias.
let recursive valueItems (prefix: Str) (index: Int) (count: Int) =
    if index >= count
    then []
    else
        TopLevelLet(binding(prefix + Ashes.Text.fromInt(index))(ExprInt(index)))(false) :: valueItems(prefix)(index + 1)(count)

let recursive valueExports (prefix: Str) (index: Int) (count: Int) =
    if index >= count
    then []
    else ImportValueExport(prefix + Ashes.Text.fromInt(index)) :: valueExports(prefix)(index + 1)(count)

let benchModuleName (index: Int) = "Bench.M" + Ashes.Text.fromInt(index)

let benchPrefix (index: Int) = "m" + Ashes.Text.fromInt(index) + "_v"

let benchDependency (size: Int) (index: Int) =
    SemanticStitchUnit(name = benchModuleName(index), packageId = "pkg", sourcePath = "/" + benchModuleName(index) + ".ash", imports = [], interface = ModuleImportInterface(name = benchModuleName(index), exports = size
    |> valueExports(benchPrefix(index))(0)
    |> Ashes.Collection.List.append(valueExports("common")(0)(10))), program = ProgramSyntax(items = size
    |> valueItems(benchPrefix(index))(0)
    |> Ashes.Collection.List.append(valueItems("common")(0)(10)), body = None), isEntry = false)

let recursive benchDependencies (size: Int) (index: Int) (modules: Int) =
    if index >= modules
    then []
    else benchDependency(size)(index) :: benchDependencies(size)(index + 1)(modules)

let recursive benchImports (index: Int) (modules: Int) =
    if index >= modules
    then []
    else
        ResolvedModuleImport(benchModuleName(index))(if index % 2 == 0
        then None
        else Some("alias" + Ashes.Text.fromInt(index)))(index)("import") :: benchImports(index + 1)(modules)

let benchUnits (modules: Int) (size: Int) =
    Ashes.Collection.List.append(benchDependencies(size)(0)(modules))([SemanticStitchUnit(name = "Main", packageId = "app", sourcePath = "/Main.ash", imports = benchImports(0)(modules), interface = ModuleImportInterface(name = "Main", exports = []), program = ProgramSyntax(items = valueItems("local")(0)(size * 4), body = None), isEntry = true)])

let idOf (resolved: Maybe(StitchedDefinition)) =
    match resolved with
        | Some(StitchedDefinition { id = id }) -> id + 1
        | None -> 0

let benchQualifier (index: Int) =
    if index % 2 == 0
    then benchModuleName(index)
    else "alias" + Ashes.Text.fromInt(index)

let recursive oldReferences (project: FrozenSemanticProject) (modules: Int) (size: Int) (index: Int) (position: Int) (sum: Int) =
    if index >= modules
    then sum
    else
        if position >= size
        then oldReferences(project)(modules)(size)(index + 1)(0)(sum)
        else
            let name = benchPrefix(index) + Ashes.Text.fromInt(position)
            in
                oldReferences(project)(modules)(size)(index)(position + 1)((sum + idOf(frozenResolveStitchedUnqualified("Main")(size * 4)(StitchedValue)(name)(project)) + idOf(frozenResolveStitchedQualified("Main")(benchQualifier(index))(StitchedValue)(name)(project)) + idOf(frozenResolveStitchedUnqualified("Main")(size * 4)(StitchedValue)("local" + Ashes.Text.fromInt((index * size + position) % (size * 4)))(project)) + idOf(frozenResolveStitchedUnqualified("Main")(size * 4)(StitchedType)(name)(project))) % 1000000007)

let recursive newReferences (project: StitchedSemanticProject) (modules: Int) (size: Int) (index: Int) (position: Int) (sum: Int) =
    if index >= modules
    then sum
    else
        if position >= size
        then newReferences(project)(modules)(size)(index + 1)(0)(sum)
        else
            let name = benchPrefix(index) + Ashes.Text.fromInt(position)
            in
                newReferences(project)(modules)(size)(index)(position + 1)((sum + idOf(resolveStitchedUnqualified("Main")(size * 4)(StitchedValue)(name)(project)) + idOf(resolveStitchedQualified("Main")(benchQualifier(index))(StitchedValue)(name)(project)) + idOf(resolveStitchedUnqualified("Main")(size * 4)(StitchedValue)("local" + Ashes.Text.fromInt((index * size + position) % (size * 4)))(project)) + idOf(resolveStitchedUnqualified("Main")(size * 4)(StitchedType)(name)(project))) % 1000000007)

let benchOld (resolve: Bool) (modules: Int) (size: Int) =
    match size
    |> benchUnits(modules)
    |> frozenBuildStitchedSemanticProject with
        | Error(_error) -> Ashes.IO.panic("the benchmark project should stitch")
        | Ok(project) ->
            Ashes.IO.print("checksum " + Ashes.Text.fromInt(if resolve
            then oldReferences(project)(modules)(size)(0)(0)(project.nextDefinitionId)
            else project.nextDefinitionId))

let benchNew (resolve: Bool) (modules: Int) (size: Int) =
    match size
    |> benchUnits(modules)
    |> buildStitchedSemanticProject with
        | Error(_error) -> Ashes.IO.panic("the benchmark project should stitch")
        | Ok(project) ->
            Ashes.IO.print("checksum " + Ashes.Text.fromInt(if resolve
            then newReferences(project)(modules)(size)(0)(0)(project.nextDefinitionId)
            else project.nextDefinitionId))

let number (text: Str) =
    match Ashes.Text.parseInt(text) with
        | Ok(value) -> value
        | Error(_message) -> Ashes.IO.panic("expected a number")

match Ashes.IO.args with
    | "check" :: first :: last :: [] ->
        Tally(projects = 0, duplicateModule = 0, duplicateDeclaration = 0, missingModule = 0, missingExport = 0, conflictingImport = 0, conflictingQualifier = 0, nameCollision = 0, comparisons = 0, resolved = 0)
        |> checkSeeds(number(first))(number(last))
        |> report
    | "old-build" :: modules :: size :: [] ->
        size
        |> number
        |> benchOld(false)(number(modules))
    | "new-build" :: modules :: size :: [] ->
        size
        |> number
        |> benchNew(false)(number(modules))
    | "old" :: modules :: size :: [] ->
        size
        |> number
        |> benchOld(true)(number(modules))
    | "new" :: modules :: size :: [] ->
        size
        |> number
        |> benchNew(true)(number(modules))
    | _ -> Ashes.IO.panic("usage: stitching-lookup-index check <first seed> <last seed> | old|new|old-build|new-build <modules> <size>")
