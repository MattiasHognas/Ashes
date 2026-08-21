// Combines dependency-ordered rewritten modules into one semantic syntax program.
//
// Invariants:
// - Module declarations retain dependency-first and source declaration order.
// - Export declarations and non-entry trailing expressions are compile-time module structure and do
//   not enter the combined program; exactly one entry module supplies its trailing expression.
// - Original At wrappers remain attached to syntax, while module regions and definition placements
//   retain package, path, module, definition, and deterministic combined-item provenance.

import Ashes.Collection.List.reverse as reverseList
import Ashes.Internal.deepCopy as deepCopy
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.ModuleReferenceRewriting
import AshesCompiler.Semantics.ModuleSemanticStitching
export (
    type StitchedModuleRegion(..),
    type StitchedDefinitionPlacement(..),
    type StitchedSyntaxProject(..),
    type ProjectSyntaxStitchError(..),
    value stitchProjectSyntax,
)

type StitchedModuleRegion =
    | moduleName: Str
    | packageId: Str
    | sourcePath: Str
    | itemStart: Int
    | itemEnd: Int
    | isEntry: Bool
    deriving {Eq, Show}

type StitchedDefinitionPlacement =
    | definition: StitchedDefinition
    | combinedItemIndex: Int
    deriving {Eq, Show}

type StitchedSyntaxProject =
    | semanticProject: StitchedSemanticProject
    | program: ProgramSyntax
    | moduleRegions: List(StitchedModuleRegion)
    | definitionPlacements: List(StitchedDefinitionPlacement)
    | entryModuleName: Str

type ProjectSyntaxStitchError =
    | ProjectSyntaxSemanticError(ModuleSemanticStitchError)
    | MissingProjectSyntaxEntry
    | MultipleProjectSyntaxEntries(Str, Str)
    | ProjectSyntaxScopeMismatch(Str, Str)
    deriving {Eq, Show}

type ProjectSyntaxCombinationState =
    | reversedItems: List(TopLevelItem)
    | reversedRegions: List(StitchedModuleRegion)
    | reversedPlacements: List(StitchedDefinitionPlacement)
    | nextItemIndex: Int
    | entryModuleName: Maybe(Str)
    | entryBody: Maybe(Expr)

let recursive isExportItem item =
    match item with
        | TopLevelAt(_span, inner) -> isExportItem(inner)
        | TopLevelExport(_declaration) -> true
        | _ -> false

let recursive addModuleItems items reversed nextIndex =
    match items with
        | [] -> (reversed, nextIndex)
        | item :: rest ->
            if isExportItem(item)
            then addModuleItems(rest)(reversed)(nextIndex)
            else addModuleItems(rest)(deepCopy(item) :: reversed)(nextIndex + 1)

let recursive combinedIndexForDeclaration items declarationOrder sourceOrder combinedIndex =
    match items with
        | [] -> combinedIndex
        | item :: rest ->
            if sourceOrder == declarationOrder
            then combinedIndex
            else
                if isExportItem(item)
                then combinedIndexForDeclaration(rest)(declarationOrder)(sourceOrder + 1)(combinedIndex)
                else combinedIndexForDeclaration(rest)(declarationOrder)(sourceOrder + 1)(combinedIndex + 1)

let recursive addDefinitionPlacements definitions items itemStart reversed =
    match definitions with
        | [] -> reversed
        | definition :: rest ->
            let itemIndex = itemStart + combinedIndexForDeclaration(items)(definition.declarationOrder)(0)(0)
            in addDefinitionPlacements(rest)(items)(itemStart)(StitchedDefinitionPlacement(definition = deepCopy(definition), combinedItemIndex = itemIndex) :: reversed)

let withEntry moduleName body state =
    match state with
        | ProjectSyntaxCombinationState { reversedItems = _reversedItems, reversedRegions = _reversedRegions, reversedPlacements = _reversedPlacements, nextItemIndex = _nextItemIndex, entryModuleName = Some(existing), entryBody = _entryBody } ->
            moduleName
            |> MultipleProjectSyntaxEntries(existing)
            |> Error
        | ProjectSyntaxCombinationState { reversedItems = reversedItems, reversedRegions = reversedRegions, reversedPlacements = reversedPlacements, nextItemIndex = nextItemIndex, entryModuleName = None, entryBody = _entryBody } -> Ok(ProjectSyntaxCombinationState(reversedItems = reversedItems, reversedRegions = reversedRegions, reversedPlacements = reversedPlacements, nextItemIndex = nextItemIndex, entryModuleName = Some(moduleName), entryBody = body))

let addCombinedModule unit scope state =
    match (unit, scope, state) with
        | (SemanticStitchUnit { name = moduleName, packageId = packageId, sourcePath = sourcePath, imports = _imports, interface = _interface, program = ProgramSyntax { items = items, body = body }, isEntry = isEntry }, StitchedModuleScope { name = scopeName, packageId = _scopePackageId, sourcePath = _scopeSourcePath, imports = _scopeImports, definitions = definitions }, ProjectSyntaxCombinationState { reversedItems = currentItems, reversedRegions = currentRegions, reversedPlacements = currentPlacements, nextItemIndex = itemStart, entryModuleName = entryModuleName, entryBody = entryBody }) ->
            if moduleName != scopeName
            then
                scopeName
                |> ProjectSyntaxScopeMismatch(moduleName)
                |> Error
            else
                match addModuleItems(items)(currentItems)(itemStart) with
                    | (reversedItems, itemEnd) ->
                        let region = StitchedModuleRegion(moduleName = moduleName, packageId = packageId, sourcePath = sourcePath, itemStart = itemStart, itemEnd = itemEnd, isEntry = isEntry)
                        in
                            let next = ProjectSyntaxCombinationState(reversedItems = reversedItems, reversedRegions = region :: currentRegions, reversedPlacements = addDefinitionPlacements(definitions)(items)(itemStart)(currentPlacements), nextItemIndex = itemEnd, entryModuleName = entryModuleName, entryBody = entryBody)
                            in
                                if isEntry
                                then withEntry(moduleName)(body)(next)
                                else Ok(next)

let finishCombinedProject semanticProject state =
    match state with
        | ProjectSyntaxCombinationState { reversedItems = reversedItems, reversedRegions = reversedRegions, reversedPlacements = reversedPlacements, nextItemIndex = _nextItemIndex, entryModuleName = None, entryBody = _entryBody } -> Error(MissingProjectSyntaxEntry)
        | ProjectSyntaxCombinationState { reversedItems = reversedItems, reversedRegions = reversedRegions, reversedPlacements = reversedPlacements, nextItemIndex = _nextItemIndex, entryModuleName = Some(entryModuleName), entryBody = entryBody } -> Ok(StitchedSyntaxProject(semanticProject = semanticProject, program = ProgramSyntax(items = reverseList(reversedItems), body = entryBody), moduleRegions = reverseList(reversedRegions), definitionPlacements = reverseList(reversedPlacements), entryModuleName = entryModuleName))

let recursive combineRewrittenModules units scopes semanticProject state =
    match (units, scopes) with
        | ([], []) -> finishCombinedProject(semanticProject)(state)
        | (SemanticStitchUnit { name = name } :: _rest, []) ->
            ""
            |> ProjectSyntaxScopeMismatch(name)
            |> Error
        | ([], StitchedModuleScope { name = name } :: _rest) ->
            name
            |> ProjectSyntaxScopeMismatch("")
            |> Error
        | (unit :: rest, scope :: remainingScopes) ->
            match addCombinedModule(unit)(scope)(state) with
                | Error(error) -> Error(error)
                | Ok(next) -> combineRewrittenModules(rest)(remainingScopes)(semanticProject)(next)

let combineRewrittenProject units (semanticProject: StitchedSemanticProject) =
    units
    |> rewriteStitchedProjectReferences(semanticProject)
    |> (given (rewritten) -> combineRewrittenModules(rewritten)(semanticProject.scopes)(semanticProject)(ProjectSyntaxCombinationState(reversedItems = [], reversedRegions = [], reversedPlacements = [], nextItemIndex = 0, entryModuleName = None, entryBody = None)))

let continueProjectSyntaxStitching units result =
    match result with
        | Error(error) -> Error(ProjectSyntaxSemanticError(error))
        | Ok(semanticProject) -> combineRewrittenProject(units)(semanticProject)

let stitchProjectSyntax units =
    units
    |> deepCopy
    |> buildStitchedSemanticProject
    |> continueProjectSyntaxStitching(units)
