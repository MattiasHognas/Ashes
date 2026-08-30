// Tracks hover type information, source spans, and public API capabilities for tooling.
//
// Invariants:
// - Hover information retains exact source spans and parameter names in curried order.
// - Public authority reflects runtime capabilities present in exported top-level binding types.
// - Traversal of cyclic or deep semantic types is bounded.

import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import Ashes.Collection.List.sort
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.Types
export (
    type HoverTypeInfo(..),
    type ExternalOwnershipInfo(..),
    type PublicAuthorityRecord(..),
    value recordHoverType,
    value findHoverAtOffset,
    value collectTypeCapabilities,
    value capturePublicAuthority,
)

let recursive containsText (target: Str) (items: List(Str)) =
    match items with
        | [] -> false
        | head :: tail ->
            if head == target
            then true
            else containsText(target)(tail)

// `isDefinition` marks the binding's own definition site, whose `inferredType` is the generalized
// scheme body; a use site records that use's instantiation instead, whose open capability row has
// been unified with the calling context's, so it also lists everything the caller performs.
type HoverTypeInfo =
    | span: TextSpan
    | name: Maybe(Str)
    | inferredType: SemanticType
    | constraints: List(TraitConstraint)
    | parameterNames: List(Str)
    | isParameter: Bool
    | isDefinition: Bool
    deriving {Eq, Show}

type ExternalOwnershipInfo =
    | isResourceType: Bool
    | destructor: Maybe(Str)
    | parameterOwnerships: List(Str)
    | returnsOwnedResource: Bool
    deriving {Eq, Show}

type PublicAuthorityRecord =
    | bindingName: Str
    | capabilities: List(Str)
    deriving {Eq, Show}

let recordHoverType (span: TextSpan) (name: Maybe(Str)) (inferredType: SemanticType) (constraints: List(TraitConstraint)) (parameterNames: List(Str)) (isParameter: Bool) (isDefinition: Bool) (hoverList: List(HoverTypeInfo)) =
    HoverTypeInfo(
        span = span,
        name = name,
        inferredType = inferredType,
        constraints = constraints,
        parameterNames = parameterNames,
        isParameter = isParameter,
        isDefinition = isDefinition
    ) :: hoverList

let recursive findHoverAtOffset (offset: Int) (hoverList: List(HoverTypeInfo)) =
    match hoverList with
        | [] -> None
        | head :: tail ->
            match head with
                | HoverTypeInfo { span = TextSpan(start, end) } ->
                    if offset >= start
                    then
                        if offset <= end
                        then Some(head)
                        else findHoverAtOffset(offset)(tail)
                    else findHoverAtOffset(offset)(tail)

let recursive collectCaps (semType: SemanticType) (depth: Int) (acc: List(Str)) =
    if depth > 128
    then acc
    else
        match semType with
            | SemCapability(name, args) ->
                let accWithName =
                    if containsText(name)(acc)
                    then acc
                    else name :: acc
                in
                    let recursive goArgs remaining currentAcc =
                        match remaining with
                            | [] -> currentAcc
                            | head :: tail -> goArgs(tail)(collectCaps(head)(depth + 1)(currentAcc))
                    in goArgs(args)(accWithName)
            | SemFunction(arg, ret, row) ->
                let accArg = collectCaps(arg)(depth + 1)(acc)
                in
                    let accRet = collectCaps(ret)(depth + 1)(accArg)
                    in
                        match row with
                            | Some(r) -> collectCaps(r)(depth + 1)(accRet)
                            | None -> accRet
            | SemRow(caps, tail) ->
                let recursive goCaps remaining currentAcc =
                    match remaining with
                        | [] -> currentAcc
                        | head :: rest -> goCaps(rest)(collectCaps(head)(depth + 1)(currentAcc))
                in
                    let accCaps = goCaps(caps)(acc)
                    in
                        match tail with
                            | Some(t) -> collectCaps(t)(depth + 1)(accCaps)
                            | None -> accCaps
            | SemList(elem) -> collectCaps(elem)(depth + 1)(acc)
            | SemTuple(elements) ->
                let recursive goElems remaining currentAcc =
                    match remaining with
                        | [] -> currentAcc
                        | head :: tail -> goElems(tail)(collectCaps(head)(depth + 1)(currentAcc))
                in goElems(elements)(acc)
            | SemNamed(_id, _name, args) ->
                let recursive goArgs remaining currentAcc =
                    match remaining with
                        | [] -> currentAcc
                        | head :: tail -> goArgs(tail)(collectCaps(head)(depth + 1)(currentAcc))
                in goArgs(args)(acc)
            | SemPointer(pointee) -> collectCaps(pointee)(depth + 1)(acc)
            | _ -> acc

let collectTypeCapabilities (semType: SemanticType) = sort(collectCaps(semType)(0)([]))

let recursive dedupeSorted (items: List(Str)) =
    match items with
        | [] -> []
        | first :: second :: rest ->
            if first == second
            then dedupeSorted(second :: rest)
            else first :: dedupeSorted(second :: rest)
        | single :: [] -> [single]

// Merges `capabilities` into the record for `name`, keeping the records ordered by binding name
// and each capability list sorted and duplicate-free.
let recursive mergeAuthority (name: Str) (capabilities: List(Str)) (records: List(PublicAuthorityRecord)) =
    match records with
        | [] -> [PublicAuthorityRecord(bindingName = name, capabilities = capabilities)]
        | PublicAuthorityRecord { bindingName = existing, capabilities = existingCaps } :: rest ->
            if existing == name
            then PublicAuthorityRecord(bindingName = name, capabilities = dedupeSorted(sort(append(existingCaps)(capabilities)))) :: rest
            else
                if Ashes.Text.compare(name)(existing) < 0
                then PublicAuthorityRecord(bindingName = name, capabilities = capabilities) :: records
                else PublicAuthorityRecord(bindingName = existing, capabilities = existingCaps) :: mergeAuthority(name)(capabilities)(rest)

// The capabilities in each exported (top-level) binding's inferred type, read from DEFINITION
// entries only: a use site records that use's instantiation, whose open row has been unified with
// the caller's context and so also lists every capability the caller performs — reading those
// would charge a helper with its callers' authority.
let recursive capturePublicAuthorityAux (topLevelBindingNames: List(Str)) (hoverList: List(HoverTypeInfo)) (acc: List(PublicAuthorityRecord)) =
    match hoverList with
        | [] -> acc
        | head :: tail ->
            match head with
                | HoverTypeInfo { name = Some(name), inferredType = semType, isDefinition = true } ->
                    if containsText(name)(topLevelBindingNames)
                    then capturePublicAuthorityAux(topLevelBindingNames)(tail)(mergeAuthority(name)(collectTypeCapabilities(semType))(acc))
                    else capturePublicAuthorityAux(topLevelBindingNames)(tail)(acc)
                | _ -> capturePublicAuthorityAux(topLevelBindingNames)(tail)(acc)

let capturePublicAuthority (topLevelBindingNames: List(Str)) (hoverList: List(HoverTypeInfo)) = capturePublicAuthorityAux(topLevelBindingNames)(hoverList)([])
