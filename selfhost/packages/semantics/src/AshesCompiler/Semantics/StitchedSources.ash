// The source text each region of a stitched program's spans are offsets into, and the source
// regions lowering resolves a stitched program's spans with, so every span is located in its own
// file: a module's file, or for an inline module (a region named `<file>#<module>`) the module's
// text lifted from its file again, which keeps the file's lines and columns.

import Ashes.Collection.List.reverse
import Ashes.Collection.Map.MapTree
import AshesCompiler.Frontend.InlineModules
import AshesCompiler.Semantics.ModuleSemanticStitching.StitchedDefinition
import AshesCompiler.Semantics.ProjectCompilationPlanning.readSourceText
import AshesCompiler.Semantics.ProjectSyntaxStitching.StitchedDefinitionPlacement
import AshesCompiler.Semantics.ProjectSyntaxStitching.StitchedModuleRegion
import AshesCompiler.Semantics.ProjectSyntaxStitching.StitchedSyntaxProject
import AshesCompiler.Semantics.SourceContext
export (
    value regionFilePath,
    value regionSourceText,
    value stitchedSourceRegions,
)

// The file a region's path names.
let regionFilePath (path: Str) =
    (let index = Ashes.Text.indexOf(path)("#")
    in
        if index < 0
        then path
        else Ashes.Text.take(path)(index))

let inlineModuleName (path: Str) =
    (let index = Ashes.Text.indexOf(path)("#")
    in
        if index < 0
        then ""
        else Ashes.Text.drop(path)(index + 1))

let fileText (entryPath: Str) (entrySource: Str) (path: Str) =
    if path == entryPath
    then Some(entrySource)
    else
        match readSourceText(path) with
            | Ok(text) -> Some(text)
            | Error(_) -> None

let textEndsWith (text: Str) (suffix: Str) =
    if Ashes.Text.length(suffix) > Ashes.Text.length(text)
    then false
    else Ashes.Text.drop(text)(Ashes.Text.length(text) - Ashes.Text.length(suffix)) == suffix

let recursive inlineModuleText (modules: List(InlineModuleInfo)) (name: Str) =
    match modules with
        | [] -> None
        | InlineModuleInfo { name = candidate, source = source } :: rest ->
            if candidate == name || textEndsWith(candidate)("." + name) || textEndsWith(name)("." + candidate)
            then Some(source)
            else inlineModuleText(rest)(name)

// The text a region's spans are offsets into: the file's, or an inline module's lifted text. The
// entry file's text is the one given; every other file is read.
let regionSourceText (entryPath: Str) (entrySource: Str) (sourcePath: Str) =
    match sourcePath
    |> regionFilePath
    |> fileText(entryPath)(entrySource) with
        | None -> None
        | Some(text) ->
            match inlineModuleName(sourcePath) with
                | "" -> Some(text)
                | name ->
                    match expandInlineModules("")(text) with
                        | Ok(InlineModuleExpansion { modules = modules }) -> inlineModuleText(modules)(name)
                        | Error(_) -> None

// Each module region's path with its text, and one item region per module region, numbered in
// region order.
let recursive regionTexts (entryPath: Str) (entrySource: Str) (regions: List(StitchedModuleRegion)) (index: Int) (texts: List((Str, Str))) (itemRegions: List(StitchedItemRegion)) =
    match regions with
        | [] -> (reverse(texts), reverse(itemRegions))
        | region :: rest ->
            let text =
                match regionSourceText(entryPath)(entrySource)(region.sourcePath) with
                    | Some(found) -> found
                    | None -> ""
            in regionTexts(entryPath)(entrySource)(rest)(index + 1)((region.sourcePath, text) :: texts)(StitchedItemRegion(itemFilePath = region.sourcePath, itemStart = index, itemEnd = index + 1) :: itemRegions)

let recursive moduleRegionIndexes (regions: List(StitchedModuleRegion)) (index: Int) (indexes: MapTree(Str, Int)) (entry: Int) =
    match regions with
        | [] -> (indexes, entry)
        | region :: rest ->
            let nextEntry =
                if region.isEntry
                then index
                else entry
            in
                moduleRegionIndexes(rest)(index + 1)(Ashes.Collection.Map.setStr(region.moduleName)(index)(indexes))(nextEntry)

let recursive definitionRegions (placements: List(StitchedDefinitionPlacement)) (moduleIndexes: MapTree(Str, Int)) (regionOfName: MapTree(Str, Int)) =
    match placements with
        | [] -> regionOfName
        | StitchedDefinitionPlacement { definition = definition } :: rest ->
            match Ashes.Collection.Map.getStr(definition.moduleName)(moduleIndexes) with
                | Some(region) ->
                    regionOfName
                    |> Ashes.Collection.Map.setStr(definition.compilerName)(region)
                    |> definitionRegions(rest)(moduleIndexes)
                | None -> definitionRegions(rest)(moduleIndexes)(regionOfName)

// The source regions of a stitched program: every module region's text, and the region each
// top-level definition's spans resolve in, with the trailing expression in the entry module's.
let stitchedSourceRegions (stitched: StitchedSyntaxProject) (entryPath: Str) (entrySource: Str) =
    match regionTexts(entryPath)(entrySource)(stitched.moduleRegions)(0)([])([]) with
        | (texts, itemRegions) ->
            match moduleRegionIndexes(stitched.moduleRegions)(0)(Ashes.Collection.Map.empty)(0) with
                | (moduleIndexes, entryRegion) ->
                    SourceRegions(context = createStitchedSourceContext(texts)(itemRegions)(entryPath), regionOfName = Ashes.Collection.Map.empty
                    |> definitionRegions(stitched.definitionPlacements)(moduleIndexes)
                    |> Ashes.Collection.Map.setStr("")(entryRegion))
