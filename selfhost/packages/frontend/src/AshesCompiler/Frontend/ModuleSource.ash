// Maps module names to source files across project, include, dependency, and shipped roots.
//
// Invariants:
// - Module A.B maps to A/B.ash beneath each eligible root.
// - Multiple matching files are an ambiguity error; root ordering is not a precedence rule.
// - The reserved Ashes namespace resolves only from the shipped library root.

import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.reverse as reverseList
import Ashes.Internal.deepCopy as deepCopy
import Ashes.Text.join
export (
    type ModuleSourceRoots(..),
    type ResolvedModuleSource(..),
    type ModuleSourceError(..),
    value moduleRelativePath,
    value resolveModuleSource,
)

type ModuleSourceRoots =
    | sourceRoots: List(Str)
    | includeRoots: List(Str)
    | dependencyRoots: List(Str)
    | shippedRoot: Maybe(Str)
    deriving {Eq, Show}

type ResolvedModuleSource =
    | ProjectModuleSource(Str)
    | ShippedModuleSource(Str)
    | InlineModuleSource(Str, Str)
    deriving {Eq, Show}

type ModuleSourceError =
    | MissingModuleSource(Str, List(Str))
    | AmbiguousModuleSource(Str, List(Str))
    | ReservedModuleSource(Str)
    deriving {Eq, Show}

let moduleRelativePath moduleName = join("/")(Ashes.Text.split(moduleName)(".")) + ".ash"

let rootedPath root relativePath =
    if Ashes.Text.startsWith(relativePath)("/")
    then root + relativePath
    else root + "/" + relativePath

let recursive containsPath path paths =
    match paths with
        | [] -> false
        | candidate :: rest ->
            if candidate == path
            then true
            else containsPath(path)(rest)

let recursive collectMatches roots relativePath available seen reversed =
    match roots with
        | [] -> (seen, reversed)
        | root :: rest ->
            if containsPath(rootedPath(root)(relativePath))(available)
            then
                if containsPath(rootedPath(root)(relativePath))(seen)
                then collectMatches(rest)(relativePath)(available)(seen)(reversed)
                else
                    collectMatches(
                        rest,
                        relativePath,
                        available,
                        rootedPath(root)(relativePath) :: seen,
                        rootedPath(root)(relativePath) :: reversed
                    )
            else collectMatches(rest)(relativePath)(available)(seen)(reversed)

let allProjectRoots (roots: ModuleSourceRoots) =
    match roots with
        | ModuleSourceRoots { sourceRoots = s, includeRoots = i, dependencyRoots = d } ->
            appendList(
                s,
                appendList(i)(d)
            )

let projectMatches roots relativePath available =
    match collectMatches(allProjectRoots(roots))(relativePath)(available)([])([]) with
        | (_seen, reversed) -> reverseList(reversed)

let recursive projectAttempts roots relativePath =
    match roots with
        | [] -> []
        | root :: rest -> rootedPath(root)(relativePath) :: projectAttempts(rest)(relativePath)

let shippedCandidate relativePath shippedRoot =
    match shippedRoot with
        | None -> None
        | Some(root) -> Some(rootedPath(root)(relativePath))

let attemptedPaths roots relativePath =
    match shippedCandidate(relativePath)(roots.shippedRoot) with
        | None -> projectAttempts(allProjectRoots(roots))(relativePath)
        | Some(candidate) -> appendList(projectAttempts(allProjectRoots(roots))(relativePath))([candidate])

let missingReserved moduleName relativePath roots =
    match shippedCandidate(relativePath)(roots.shippedRoot) with
        | None -> Error(MissingModuleSource(deepCopy(moduleName))([]))
        | Some(candidate) -> Error(MissingModuleSource(deepCopy(moduleName))([candidate]))

let resolveReserved moduleName relativePath roots available =
    match shippedCandidate(relativePath)(roots.shippedRoot) with
        | None -> missingReserved(moduleName)(relativePath)(roots)
        | Some(candidate) ->
            if containsPath(candidate)(available)
            then Ok(ShippedModuleSource(candidate))
            else missingReserved(moduleName)(relativePath)(roots)

let resolveUnreserved moduleName relativePath roots available =
    match projectMatches(roots)(relativePath)(available) with
        | first :: second :: rest -> Error(AmbiguousModuleSource(deepCopy(moduleName))(first :: second :: rest))
        | source :: [] -> Ok(ProjectModuleSource(source))
        | [] ->
            match shippedCandidate(relativePath)(roots.shippedRoot) with
                | Some(candidate) ->
                    if containsPath(candidate)(available)
                    then Ok(ShippedModuleSource(candidate))
                    else Error(MissingModuleSource(deepCopy(moduleName))(attemptedPaths(roots)(relativePath)))
                | None -> Error(MissingModuleSource(deepCopy(moduleName))(attemptedPaths(roots)(relativePath)))

let resolveModuleSourcePath moduleName roots available relativePath =
    if Ashes.Text.startsWith(moduleName)("Ashes.")
    then resolveReserved(moduleName)(relativePath)(roots)(available)
    else resolveUnreserved(moduleName)(relativePath)(roots)(available)

let resolveModuleSource moduleName roots available =
    if moduleName == "Ashes"
    then Error(ReservedModuleSource("Ashes"))
    else resolveModuleSourcePath(moduleName)(roots)(available)(moduleRelativePath(moduleName))
