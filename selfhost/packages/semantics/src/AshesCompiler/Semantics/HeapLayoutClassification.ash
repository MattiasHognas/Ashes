// Classifies a resolved SemanticType's heap layout for ownership, Perceus drop-insertion, and
// reuse-specialization purposes: whether the value transitively contains a declared resource type
// or an unresolved type variable, how the whole graph can be copied (inline word copy, shallow cell
// copy, deep structural copy, or not at all), whether every owned child can be dropped, whether
// the runtime can reuse the outer cell in place, and — for list, tuple, and named-ADT shapes — the
// per-child type, drop operation kind, and copy kind the drop and reuse machinery will need. A
// zero-cost newtype is erased to its runtime representation before classification, at every
// level, not only at the top.
//
// Invariants:
// - A scalar type (Int, UInt, Float, Rune, Bool) never needs a drop: it has no heap payload.
// - Recursive ADT cycles are guarded by symbol id; the path back to an already-visited symbol
//   reports neither "resource" nor "unresolved" — only a fact reachable via a fresh, non-cyclic
//   path counts, matching the stage-0 reference classifier's own cycle-guard convention. The copy
//   and reuse rules apply the same guard: a cycle is not deep-copyable, is droppable, and does not
//   qualify a child for the record or owned-child reuse layouts.
// - A named type's constructor field types are instantiated against its own concrete type
//   arguments (positionally, via the constructor's generalized result type), never returned raw
//   with generic type-parameter placeholders still attached.
// - Every conservative outcome is explicit: the rejection flags name why runtime reuse of the outer
//   cell is declined rather than leaving the reason to be inferred from a missing positive fact.

import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeGraphIndex
import AshesCompiler.Semantics.TypeGraphQueries
import Ashes.Collection.List.append
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import Ashes.Collection.List.length
export (
    value classifyHeapResourceIndexed,
    value classifyHeapOwnedChildrenDroppableIndexed,
    value classifyHeapArenaDeepCopyIndexed,
    value classifyHeapChildrenIndexed,
    value classifyHeapChildTypesIndexed,
    value classifyHeapShallowCopyIndexed,
    value classifyHeapAdmissibilityIndexed,
    value classifyHeapCopyPlanIndexed,
    value classifyHeapDropperIndexed,
    value classifyHeapLayoutIndexed,
    value heapRuntimeRecordAdtLayoutIndexed,
    value heapRuntimeOwnedFieldLayoutIndexed,
    value heapRuntimeOwnedTupleElementLayoutIndexed,
    value heapRuntimeOwnedChildAdtLayoutIndexed,
    value heapRuntimeTcoOwnedChildAdtLayoutIndexed,
    value heapRuntimePositionalAdtLayoutIndexed,
    value heapRuntimeTcoListElementLayoutIndexed,
    value heapRuntimeRecursiveCopyAdtLayoutIndexed,
    value heapAdtShallowCopySupportedIndexed,
    type HeapChildDropKind(..),
    type HeapStructuralCopyKind(..),
    type HeapLayoutChild(..),
    type HeapLayoutRejections(..),
    type HeapLayoutFacts(..),
    type HeapAdmissibilityFacts(..),
    type HeapCopyPlanChild(..),
    type HeapCopyPlanFacts(..),
    value canArenaResetLayout,
    value heapRuntimeRecursiveCopyAdtLayout,
    value heapRuntimeOwnedFieldLayout,
    value heapRuntimeOwnedTupleElementLayout,
    value heapNamedTypeConstructors,
    value heapConstructorFieldGroups,
    value heapExtendConstructorEnvironment,
    value classifyHeapLayout,
    type HeapDropperChild(..),
    type HeapDropperFacts(..),
    value projectHeapDropperFacts,
    value classifyHeapDropper,
    value classifyHeapChildren,
    value classifyHeapChildTypes,
    value classifyHeapResource,
    value classifyHeapOwnedChildrenDroppable,
    value classifyHeapArenaDeepCopy,
    value classifyHeapAdmissibility,
    value classifyHeapShallowCopy,
    value classifyHeapCopyPlan,
    value projectHeapCopyPlanFacts,
    value projectHeapCopyPlanChildren,
)

type HeapChildDropKind =
    | NoChildDrop
    | DropString
    | DropBytes
    | DropBigInt
    | DropList
    | DropTuple
    | DropAdt
    | DropClosure
    | UnsupportedChildDrop
    deriving {Eq, Show}

// The structural copy operation a value graph supports: a scalar word, a single cell whose
// payload words are all scalars, a full structural walk, or none.
type HeapStructuralCopyKind =
    | NoStructuralCopy
    | InlineCopy
    | ShallowCopy
    | DeepCopy
    deriving {Eq, Show}

type HeapLayoutChild =
    | constructorName: Maybe(Str)
    | fieldIndex: Int
    | childType: SemanticType
    | dropKind: HeapChildDropKind
    | copyKind: HeapStructuralCopyKind
    deriving {Eq, Show}

// Stable reasons why a layout's runtime reuse capability is conservative.
type HeapLayoutRejections =
    | resourceOrBorrowedViewContainment: Bool
    | unsupportedChildDropLayout: Bool
    | unresolvedType: Bool
    | unsupportedOuterCellReuse: Bool
    deriving {Eq, Show}

// The named-ADT reuse layouts the runtime can manage for an outer cell.
type HeapLayoutFacts =
    | containsResource: Bool
    | containsUnresolvedType: Bool
    | containsOwnedChild: Bool
    | structuralCopy: HeapStructuralCopyKind
    | arenaDeepCopySupported: Bool
    | ownedChildrenDroppable: Bool
    | runtimeOuterCellReuseSupported: Bool
    | runtimeCopyAdtSupported: Bool
    | runtimeRecordAdtSupported: Bool
    | runtimeOwnedChildAdtSupported: Bool
    | runtimeTcoOwnedChildAdtSupported: Bool
    | runtimeTcoListElementSupported: Bool
    | runtimePositionalAdtSupported: Bool
    | children: List(HeapLayoutChild)
    | rejections: HeapLayoutRejections
    deriving {Eq, Show}

// General RC admissibility needs containment and child drop eligibility, not copy or reuse
// layouts. Keep this view independent so asking for it does not build those recursive facts.
type HeapAdmissibilityFacts =
    | containsResource: Bool
    | containsUnresolvedType: Bool
    | children: List((SemanticType, HeapChildDropKind))
    deriving {Eq, Show}

let canArenaResetLayout (semanticType: SemanticType) =
    match semanticType with
        | SemInt -> true
        | SemUInt(_bits) -> true
        | SemFloat -> true
        | SemRune -> true
        | SemBool -> true
        | SemNever -> true
        | _ -> false

let heapBuiltinResourceTypeName (name: Str) = name == "Socket" || name == "TlsSocket" || name == "Process" || name == "FileHandle" || name == "JoinHandle"

// Types the compiler provides itself rather than user source declares.
let heapBuiltinTypeName (name: Str) = name == "Unit" || name == "List" || name == "Maybe" || name == "Result" || name == "Task" || heapBuiltinResourceTypeName(name)

// Whether a named type is already on a classification walk's path. A type is identified by its
// symbol id and its name together: the lowering's own type layer names every declared type with
// id 0 and tells them apart by name alone, so an id-only guard would take a nested record for a
// cycle back into its parent.
let recursive heapPathContains (symbolId: Int) (name: Str) (path: List((Int, Str))) =
    match path with
        | [] -> false
        | (candidateId, candidateName) :: rest ->
            if candidateId == symbolId && candidateName == name
            then true
            else heapPathContains(symbolId)(name)(rest)

let recursive heapSameArity (arity: Int) (grouped: List((Str, List(SemanticType)))) =
    match grouped with
        | [] -> true
        | (_constructorName, fieldTypes) :: rest ->
            if length(fieldTypes) == arity
            then heapSameArity(arity)(rest)
            else false

let heapConstructorFieldGroups constructors = typeGraphConstructorFieldGroups(constructors)

let heapExtendConstructorEnvironment environment definitions = typeGraphExtendConstructorEnvironment(environment)(definitions)

let heapNamedTypeConstructors named environment = typeGraphNamedTypeConstructors(named)(environment)

let recursive heapFlattenFieldGroups grouped =
    match grouped with
        | [] -> []
        | (_name, fields) :: rest ->
            rest
            |> heapFlattenFieldGroups
            |> append(fields)

let heapMonomorphicUserAdt (named: SemanticType) =
    match named with
        | SemNamed(_symbolId, name, arguments) -> !heapBuiltinTypeName(name) && length(arguments) == 0
        | _ -> false

// Stage 0's `IsOpaqueHandleWord` beside the scalar layouts: a value of an external type with no
// destructor is a foreign handle the program only passes along, one word copied like an integer
// that needs no release; a declared resource owns what its handle names.
type HeapCopyPlanChild =
    | constructorName: Maybe(Str)
    | fieldIndex: Int
    | childType: SemanticType
    | dropKind: HeapChildDropKind
    deriving {Eq, Show}

type HeapCopyPlanFacts =
    | shallowCopy: Bool
    | arenaDeepCopySupported: Bool
    | runtimeRecordAdtSupported: Bool
    | runtimeOwnedChildAdtSupported: Bool
    | runtimeTcoOwnedChildAdtSupported: Bool
    | runtimePositionalAdtSupported: Bool
    | children: List(HeapCopyPlanChild)
    deriving {Eq, Show}

let recursive projectHeapCopyPlanChildren (children: List(HeapLayoutChild)) =
    match children with
        | [] -> []
        | HeapLayoutChild { constructorName = name, fieldIndex = index, childType = childType, dropKind = dropKind } :: rest -> HeapCopyPlanChild(constructorName = name, fieldIndex = index, childType = childType, dropKind = dropKind) :: projectHeapCopyPlanChildren(rest)

let projectHeapCopyPlanFacts (facts: HeapLayoutFacts) =
    HeapCopyPlanFacts(
        shallowCopy = facts.structuralCopy == ShallowCopy,
        arenaDeepCopySupported = facts.arenaDeepCopySupported,
        runtimeRecordAdtSupported = facts.runtimeRecordAdtSupported,
        runtimeOwnedChildAdtSupported = facts.runtimeOwnedChildAdtSupported,
        runtimeTcoOwnedChildAdtSupported = facts.runtimeTcoOwnedChildAdtSupported,
        runtimePositionalAdtSupported = facts.runtimePositionalAdtSupported,
        children = projectHeapCopyPlanChildren(facts.children)
    )

let recursive heapAnyOwnedChild (children: List(HeapLayoutChild)) =
    match children with
        | [] -> false
        | HeapLayoutChild { dropKind = dropKind } :: rest ->
            if dropKind == NoChildDrop
            then heapAnyOwnedChild(rest)
            else true

// Only a named type has an outer cell the runtime can reuse; every other shape declines.
let heapRejections (containsResource: Bool) (ownedChildrenDroppable: Bool) (containsUnresolvedType: Bool) (runtimeOuterCellReuseSupported: Bool) =
    HeapLayoutRejections(
        resourceOrBorrowedViewContainment = containsResource,
        unsupportedChildDropLayout = !ownedChildrenDroppable,
        unresolvedType = containsUnresolvedType,
        unsupportedOuterCellReuse = !runtimeOuterCellReuseSupported
    )

type HeapDropperChild =
    | constructorName: Maybe(Str)
    | fieldIndex: Int
    | childType: SemanticType
    | dropKind: HeapChildDropKind
    deriving {Eq, Show}

type HeapDropperFacts =
    | containsResource: Bool
    | containsUnresolvedType: Bool
    | containsOwnedChild: Bool
    | shallowCopy: Bool
    | runtimeRecordAdtSupported: Bool
    | runtimeOwnedChildAdtSupported: Bool
    | runtimeTcoOwnedChildAdtSupported: Bool
    | runtimePositionalAdtSupported: Bool
    | children: List(HeapDropperChild)
    deriving {Eq, Show}

let projectDropperChild (child: HeapLayoutChild) = HeapDropperChild(constructorName = child.constructorName, fieldIndex = child.fieldIndex, childType = child.childType, dropKind = child.dropKind)

let projectHeapDropperFacts (facts: HeapLayoutFacts) =
    HeapDropperFacts(
        containsResource = facts.containsResource,
        containsUnresolvedType = facts.containsUnresolvedType,
        containsOwnedChild = facts.containsOwnedChild,
        shallowCopy = facts.structuralCopy == ShallowCopy,
        runtimeRecordAdtSupported = facts.runtimeRecordAdtSupported,
        runtimeOwnedChildAdtSupported = facts.runtimeOwnedChildAdtSupported,
        runtimeTcoOwnedChildAdtSupported = facts.runtimeTcoOwnedChildAdtSupported,
        runtimePositionalAdtSupported = facts.runtimePositionalAdtSupported,
        children = map(projectDropperChild)(facts.children)
    )

type IndexedLayoutGate =
    | IndexedReset
    | IndexedOwns
    | IndexedTcoField
    | IndexedArena
    | IndexedArenaAdt
    | IndexedShallow
    | IndexedOwnedField
    | IndexedOwnedTuple
    | IndexedRecord
    | IndexedOwnedChild
    | IndexedTcoOwnedChild
    | IndexedPositional
    | IndexedTcoList
    | IndexedCopyAdt
    | IndexedRecursiveCopy

let indexedResetIn (node: TypeGraphNode) =
    match node.resolvedType with
        | SemOpaque(_) -> !node.directResource
        | other -> canArenaResetLayout(other)

let recursive indexedAll gate (types: List(SemanticType)) path index =
    match types with
        | [] -> (true, index)
        | head :: rest ->
            match indexedGate(gate)(head)(path)(index) with
                | (false, next) -> (false, next)
                | (true, next) -> indexedAll(gate)(rest)(path)(next)
and indexedAny gate (types: List(SemanticType)) path index =
    match types with
        | [] -> (false, index)
        | head :: rest ->
            match indexedGate(gate)(head)(path)(index) with
                | (true, next) -> (true, next)
                | (false, next) -> indexedAny(gate)(rest)(path)(next)
and indexedFirst (gates: List(IndexedLayoutGate)) semanticType path index =
    match gates with
        | [] -> (false, index)
        | gate :: rest ->
            match indexedGate(gate)(semanticType)(path)(index) with
                | (true, next) -> (true, next)
                | (false, next) -> indexedFirst(rest)(semanticType)(path)(next)
and indexedRecursiveFields symbolId name (fields: List(SemanticType)) index =
    match fields with
        | [] -> (true, index)
        | field :: rest ->
            match typeGraphResolvedNode(field)(index) with
                | (node, next) ->
                    let allowed =
                        if indexedResetIn(node)
                        then true
                        else
                            match node.resolvedType with
                                | SemNamed(childId, childName, _) -> childId == symbolId && childName == name
                                | _ -> false
                    in
                        if allowed
                        then indexedRecursiveFields(symbolId)(name)(rest)(next)
                        else (false, next)
and indexedOwnedFields gate fields path index =
    match indexedAll(gate)(fields)(path)(index) with
        | (false, next) -> (false, next)
        | (true, next) -> indexedAny(IndexedOwns)(fields)(path)(next)
and indexedNamed gate (named: SemanticType) path (index: TypeGraphIndex) =
    match typeGraphNode(named)(index) with
        | (node, next) ->
            let groups = node.sourceConstructorGroups
            in
                let fields =
                    if node.sourceType == node.resolvedType
                    then node.fields
                    else heapFlattenFieldGroups(groups)
                in
                    match named with
                        | SemNamed(symbolId, name, _) ->
                            match gate with
                                | IndexedArenaAdt ->
                                    if heapPathContains(symbolId)(name)(path)
                                    then (false, next)
                                    else
                                        match typeGraphResource(named)(next) with
                                            | (true, afterResource) -> (false, afterResource)
                                            | (false, afterResource) ->
                                                match groups with
                                                    | [] -> (false, afterResource)
                                                    | _ -> indexedAll(IndexedArena)(fields)((symbolId, name) :: path)(afterResource)
                                | IndexedShallow ->
                                    match groups with
                                        | [] -> (false, next)
                                        | (_, firstFields) :: _ ->
                                            match typeGraphResource(named)(next) with
                                                | (true, afterResource) -> (false, afterResource)
                                                | (false, afterResource) ->
                                                    if heapSameArity(length(firstFields))(groups)
                                                    then indexedAll(IndexedReset)(fields)(path)(afterResource)
                                                    else (false, afterResource)
                                | IndexedRecord ->
                                    match groups with
                                        | (_, onlyFields) :: [] ->
                                            if !node.sourceIsRecord
                                            then (false, next)
                                            else
                                                match typeGraphResource(named)(next) with
                                                    | (true, afterResource) -> (false, afterResource)
                                                    | (false, afterResource) ->
                                                        if heapPathContains(symbolId)(name)(path)
                                                        then (false, afterResource)
                                                        else indexedAll(IndexedOwnedField)(onlyFields)((symbolId, name) :: path)(afterResource)
                                        | _ -> (false, next)
                                | IndexedPositional ->
                                    match groups with
                                        | (_, onlyFields) :: [] ->
                                            if heapBuiltinTypeName(name) || node.sourceIsRecord
                                            then (false, next)
                                            else
                                                match typeGraphResource(named)(next) with
                                                    | (true, afterResource) -> (false, afterResource)
                                                    | (false, afterResource) ->
                                                        if heapPathContains(symbolId)(name)(path)
                                                        then (false, afterResource)
                                                        else indexedOwnedFields(IndexedOwnedField)(onlyFields)((symbolId, name) :: path)(afterResource)
                                        | _ -> (false, next)
                                | IndexedOwnedChild ->
                                    if heapBuiltinResourceTypeName(name) || !(length(groups) >= 2)
                                    then (false, next)
                                    else
                                        match typeGraphResource(named)(next) with
                                            | (true, afterResource) -> (false, afterResource)
                                            | (false, afterResource) ->
                                                if heapPathContains(symbolId)(name)(path)
                                                then (false, afterResource)
                                                else indexedOwnedFields(IndexedOwnedField)(fields)((symbolId, name) :: path)(afterResource)
                                | IndexedTcoOwnedChild ->
                                    match groups with
                                        | (_, onlyFields) :: [] ->
                                            if node.sourceIsRecord
                                            then indexedNamed(IndexedOwnedChild)(named)(path)(next)
                                            else
                                                if !heapMonomorphicUserAdt(named)
                                                then (false, next)
                                                else
                                                    match typeGraphResource(named)(next) with
                                                        | (true, afterResource) -> (false, afterResource)
                                                        | (false, afterResource) -> indexedOwnedFields(IndexedTcoField)(onlyFields)(path)(afterResource)
                                        | _ -> indexedNamed(IndexedOwnedChild)(named)(path)(next)
                                | IndexedCopyAdt ->
                                    match groups with
                                        | [] -> (false, next)
                                        | _ ->
                                            match typeGraphResource(named)(next) with
                                                | (true, afterResource) -> (false, afterResource)
                                                | (false, afterResource) -> indexedAll(IndexedReset)(fields)(path)(afterResource)
                                | IndexedRecursiveCopy ->
                                    match groups with
                                        | [] -> (false, next)
                                        | _ ->
                                            if !heapMonomorphicUserAdt(named)
                                            then (false, next)
                                            else
                                                match typeGraphResource(named)(next) with
                                                    | (true, afterResource) -> (false, afterResource)
                                                    | (false, afterResource) ->
                                                        match indexedRecursiveFields(symbolId)(name)(fields)(afterResource) with
                                                            | (false, afterFields) -> (false, afterFields)
                                                            | (true, afterFields) -> indexedAny(IndexedOwns)(fields)(path)(afterFields)
                                | _ -> (false, next)
                        | _ -> (false, next)
and indexedGate gate (semanticType: SemanticType) path index =
    match gate with
        | IndexedArenaAdt -> indexedNamed(gate)(semanticType)(path)(index)
        | IndexedShallow -> indexedNamed(gate)(semanticType)(path)(index)
        | IndexedRecord -> indexedNamed(gate)(semanticType)(path)(index)
        | IndexedOwnedChild -> indexedNamed(gate)(semanticType)(path)(index)
        | IndexedTcoOwnedChild -> indexedNamed(gate)(semanticType)(path)(index)
        | IndexedPositional -> indexedNamed(gate)(semanticType)(path)(index)
        | IndexedCopyAdt -> indexedNamed(gate)(semanticType)(path)(index)
        | IndexedRecursiveCopy -> indexedNamed(gate)(semanticType)(path)(index)
        | _ ->
            match typeGraphResolvedNode(semanticType)(index) with
                | (node, next) ->
                    let resolved = node.resolvedType
                    in
                        match gate with
                            | IndexedReset -> (canArenaResetLayout(resolved), next)
                            | IndexedOwns -> (!canArenaResetLayout(resolved), next)
                            | IndexedOwnedTuple ->
                                match resolved with
                                    | SemFunction(_, _, _) -> (false, next)
                                    | _ -> indexedGate(IndexedOwnedField)(resolved)(path)(next)
                            | IndexedTcoField ->
                                if indexedResetIn(node)
                                then (true, next)
                                else
                                    match resolved with
                                        | SemList(element) -> indexedGate(IndexedReset)(element)(path)(next)
                                        | _ -> (false, next)
                            | _ ->
                                if indexedResetIn(node)
                                then (true, next)
                                else
                                    match resolved with
                                        | SemString -> (true, next)
                                        | SemBytes -> (true, next)
                                        | SemBigInt ->
                                            let accepted =
                                                match gate with
                                                    | IndexedArena -> false
                                                    | _ -> true
                                            in (accepted, next)
                                        | SemList(element) -> indexedGate(gate)(element)(path)(next)
                                        | SemTuple(elements) ->
                                            match gate with
                                                | IndexedOwnedField -> indexedAll(IndexedOwnedTuple)(elements)(path)(next)
                                                | _ -> indexedAll(gate)(elements)(path)(next)
                                        | SemFunction(_, _, _) ->
                                            let accepted =
                                                match gate with
                                                    | IndexedOwnedField -> true
                                                    | _ -> false
                                            in (accepted, next)
                                        | SemNamed(_, _, _) ->
                                            match gate with
                                                | IndexedArena -> indexedNamed(IndexedArenaAdt)(resolved)(path)(next)
                                                | IndexedTcoList ->
                                                    match indexedGate(IndexedShallow)(resolved)(path)(next) with
                                                        | (true, afterShallow) -> (true, afterShallow)
                                                        | (false, afterShallow) ->
                                                            match indexedGate(IndexedRecord)(resolved)([])(afterShallow) with
                                                                | (true, afterRecord) -> (true, afterRecord)
                                                                | (false, afterRecord) -> indexedFirst([IndexedOwnedChild, IndexedPositional])(resolved)(path)(afterRecord)
                                                | _ -> indexedFirst([IndexedShallow, IndexedRecord, IndexedOwnedChild, IndexedTcoOwnedChild, IndexedPositional])(resolved)(path)(next)
                                        | _ -> (false, next)

let indexedDropKind semanticType index =
    match typeGraphResolvedNode(semanticType)(index) with
        | (node, next) ->
            let kind =
                if indexedResetIn(node)
                then NoChildDrop
                else
                    match node.resolvedType with
                        | SemString -> DropString
                        | SemBytes -> DropBytes
                        | SemBigInt -> DropBigInt
                        | SemList(_) -> DropList
                        | SemTuple(_) -> DropTuple
                        | SemNamed(_, _, _) -> DropAdt
                        | SemFunction(_, _, _) -> DropClosure
                        | _ -> UnsupportedChildDrop
            in (kind, next)

let indexedCopyKind semanticType index =
    match typeGraphResolvedNode(semanticType)(index) with
        | (node, next) ->
            if indexedResetIn(node)
            then (InlineCopy, next)
            else
                match node.resolvedType with
                    | SemString -> (ShallowCopy, next)
                    | SemBytes -> (ShallowCopy, next)
                    | SemBigInt -> (ShallowCopy, next)
                    | SemList(element) ->
                        match indexedGate(IndexedArena)(element)([])(next) with
                            | (allowed, final) ->
                                (if allowed
                                then DeepCopy
                                else NoStructuralCopy, final)
                    | SemTuple(_) ->
                        match indexedGate(IndexedArena)(node.resolvedType)([])(next) with
                            | (allowed, final) ->
                                (if allowed
                                then DeepCopy
                                else NoStructuralCopy, final)
                    | SemNamed(_, _, _) ->
                        match node.constructorGroups with
                            | [] -> (NoStructuralCopy, next)
                            | _ ->
                                match typeGraphResource(node.resolvedType)(next) with
                                    | (true, afterResource) -> (NoStructuralCopy, afterResource)
                                    | (false, afterResource) ->
                                        match indexedGate(IndexedShallow)(node.resolvedType)([])(afterResource) with
                                            | (true, afterShallow) -> (ShallowCopy, afterShallow)
                                            | (false, afterShallow) ->
                                                match indexedGate(IndexedArenaAdt)(node.resolvedType)([])(afterShallow) with
                                                    | (allowed, final) ->
                                                        (if allowed
                                                        then DeepCopy
                                                        else NoStructuralCopy, final)
                    | _ -> (NoStructuralCopy, next)

let recursive indexedNamedGroups groups =
    match groups with
        | [] -> []
        | (name, fields) :: rest -> (Some(name), fields) :: indexedNamedGroups(rest)

let indexedChildGroups (node: TypeGraphNode) =
    match node.resolvedType with
        | SemList(element) -> [(None, [element, node.resolvedType])]
        | SemTuple(elements) -> [(None, elements)]
        | SemNamed(_, _, _) -> indexedNamedGroups(node.constructorGroups)
        | _ -> []

let recursive indexedFields includeCopy name fields ordinal index =
    match fields with
        | [] -> ([], index)
        | field :: rest ->
            match indexedDropKind(field)(index) with
                | (dropKind, afterDrop) ->
                    let copied =
                        if includeCopy
                        then indexedCopyKind(field)(afterDrop)
                        else (NoStructuralCopy, afterDrop)
                    in
                        match copied with
                            | (copyKind, afterCopy) ->
                                match indexedFields(includeCopy)(name)(rest)(ordinal + 1)(afterCopy) with
                                    | (tail, final) -> (HeapLayoutChild(constructorName = name, fieldIndex = ordinal, childType = field, dropKind = dropKind, copyKind = copyKind) :: tail, final)

let recursive indexedGroups includeCopy groups index =
    match groups with
        | [] -> ([], index)
        | (name, fields) :: rest ->
            match indexedFields(includeCopy)(name)(fields)(0)(index) with
                | (head, afterFields) ->
                    match indexedGroups(includeCopy)(rest)(afterFields) with
                        | (tail, final) -> (append(head)(tail), final)

let indexedChildren includeCopy semanticType index =
    match typeGraphResolvedNode(semanticType)(index) with
        | (node, next) ->
            indexedGroups(includeCopy)(indexedChildGroups(node))(next)

let classifyHeapResourceIndexed (semanticType: SemanticType) (index: TypeGraphIndex) = typeGraphResource(semanticType)(index)

let classifyHeapOwnedChildrenDroppableIndexed (semanticType: SemanticType) (index: TypeGraphIndex) = typeGraphDroppable(semanticType)(index)

let classifyHeapArenaDeepCopyIndexed (semanticType: SemanticType) (index: TypeGraphIndex) = indexedGate(IndexedArena)(semanticType)([])(index)

let classifyHeapChildrenIndexed (semanticType: SemanticType) (index: TypeGraphIndex) = indexedChildren(true)(semanticType)(index)

let classifyHeapChildTypesIndexed (semanticType: SemanticType) (index: TypeGraphIndex) =
    match typeGraphResolvedNode(semanticType)(index) with
        | (node, next) ->
            (node
            |> indexedChildGroups
            |> heapFlattenFieldGroups, next)

let classifyHeapShallowCopyIndexed (semanticType: SemanticType) (index: TypeGraphIndex) =
    match typeGraphResolvedNode(semanticType)(index) with
        | (node, next) ->
            if indexedResetIn(node)
            then (false, next)
            else
                match node.resolvedType with
                    | SemString -> (true, next)
                    | SemBytes -> (true, next)
                    | SemBigInt -> (true, next)
                    | SemNamed(_, _, _) -> indexedGate(IndexedShallow)(node.resolvedType)([])(next)
                    | _ -> (false, next)

let classifyHeapAdmissibilityIndexed (semanticType: SemanticType) (index: TypeGraphIndex) =
    match typeGraphResource(semanticType)(index) with
        | (resource, afterResource) ->
            match typeGraphUnresolved(semanticType)(afterResource) with
                | (unresolved, afterUnresolved) ->
                    match indexedChildren(false)(semanticType)(afterUnresolved) with
                        | (children, final) ->
                            (HeapAdmissibilityFacts(containsResource = resource, containsUnresolvedType = unresolved, children = map(given (child: HeapLayoutChild) -> (child.childType, child.dropKind))(children)), final)

let classifyHeapCopyPlanIndexed (semanticType: SemanticType) (index: TypeGraphIndex) =
    match typeGraphResolvedNode(semanticType)(index) with
        | (node, next) ->
            match classifyHeapShallowCopyIndexed(node.resolvedType)(next) with
                | (shallow, afterShallow) ->
                    match indexedGate(IndexedArena)(node.resolvedType)([])(afterShallow) with
                        | (arena, afterArena) ->
                            match indexedGate(IndexedRecord)(node.resolvedType)([])(afterArena) with
                                | (record, afterRecord) ->
                                    match indexedGate(IndexedOwnedChild)(node.resolvedType)([])(afterRecord) with
                                        | (owned, afterOwned) ->
                                            match indexedGate(IndexedTcoOwnedChild)(node.resolvedType)([])(afterOwned) with
                                                | (tcoOwned, afterTco) ->
                                                    match indexedGate(IndexedPositional)(node.resolvedType)([])(afterTco) with
                                                        | (positional, afterPositional) ->
                                                            match indexedChildren(false)(node.resolvedType)(afterPositional) with
                                                                | (children, final) -> (HeapCopyPlanFacts(shallowCopy = shallow, arenaDeepCopySupported = arena, runtimeRecordAdtSupported = record, runtimeOwnedChildAdtSupported = owned, runtimeTcoOwnedChildAdtSupported = tcoOwned, runtimePositionalAdtSupported = positional, children = projectHeapCopyPlanChildren(children)), final)

let classifyHeapDropperIndexed (semanticType: SemanticType) (index: TypeGraphIndex) =
    match typeGraphResolvedNode(semanticType)(index) with
        | (node, next) ->
            match indexedChildren(false)(node.resolvedType)(next) with
                | (children, afterChildren) ->
                    match typeGraphResource(node.resolvedType)(afterChildren) with
                        | (resource, afterResource) ->
                            match typeGraphUnresolved(node.resolvedType)(afterResource) with
                                | (unresolved, afterUnresolved) ->
                                    match classifyHeapShallowCopyIndexed(node.resolvedType)(afterUnresolved) with
                                        | (shallow, afterShallow) ->
                                            match indexedGate(IndexedRecord)(node.resolvedType)([])(afterShallow) with
                                                | (record, afterRecord) ->
                                                    match indexedGate(IndexedOwnedChild)(node.resolvedType)([])(afterRecord) with
                                                        | (owned, afterOwned) ->
                                                            match indexedGate(IndexedTcoOwnedChild)(node.resolvedType)([])(afterOwned) with
                                                                | (tcoOwned, afterTco) ->
                                                                    match indexedGate(IndexedPositional)(node.resolvedType)([])(afterTco) with
                                                                        | (positional, final) -> (HeapDropperFacts(containsResource = resource, containsUnresolvedType = unresolved, containsOwnedChild = heapAnyOwnedChild(children), shallowCopy = shallow, runtimeRecordAdtSupported = record, runtimeOwnedChildAdtSupported = owned, runtimeTcoOwnedChildAdtSupported = tcoOwned, runtimePositionalAdtSupported = positional, children = map(projectDropperChild)(children)), final)

let classifyHeapLayoutIndexed (semanticType: SemanticType) (index: TypeGraphIndex) =
    match typeGraphResolvedNode(semanticType)(index) with
        | (node, next) ->
            match indexedGate(IndexedCopyAdt)(node.resolvedType)([])(next) with
                | (copyAdt, afterCopy) ->
                    match indexedGate(IndexedRecord)(node.resolvedType)([])(afterCopy) with
                        | (record, afterRecord) ->
                            match indexedGate(IndexedOwnedChild)(node.resolvedType)([])(afterRecord) with
                                | (owned, afterOwned) ->
                                    match indexedGate(IndexedTcoOwnedChild)(node.resolvedType)([])(afterOwned) with
                                        | (tcoOwned, afterTco) ->
                                            match indexedGate(IndexedRecursiveCopy)(node.resolvedType)([])(afterTco) with
                                                | (recursiveCopy, afterRecursive) ->
                                                    match indexedChildren(true)(node.resolvedType)(afterRecursive) with
                                                        | (children, afterChildren) ->
                                                            match typeGraphResource(node.resolvedType)(afterChildren) with
                                                                | (resource, afterResource) ->
                                                                    match typeGraphDroppable(node.resolvedType)(afterResource) with
                                                                        | (droppable, afterDroppable) ->
                                                                            match typeGraphUnresolved(node.resolvedType)(afterDroppable) with
                                                                                | (unresolved, afterUnresolved) ->
                                                                                    match indexedCopyKind(node.resolvedType)(afterUnresolved) with
                                                                                        | (copyKind, afterCopyKind) ->
                                                                                            match indexedGate(IndexedArena)(node.resolvedType)([])(afterCopyKind) with
                                                                                                | (arena, afterArena) ->
                                                                                                    match indexedGate(IndexedTcoList)(node.resolvedType)([])(afterArena) with
                                                                                                        | (tcoList, afterTcoList) ->
                                                                                                            match indexedGate(IndexedPositional)(node.resolvedType)([])(afterTcoList) with
                                                                                                                | (positional, final) ->
                                                                                                                    let reuse = copyAdt || record || owned || recursiveCopy
                                                                                                                    in (HeapLayoutFacts(containsResource = resource, containsUnresolvedType = unresolved, containsOwnedChild = heapAnyOwnedChild(children), structuralCopy = copyKind, arenaDeepCopySupported = arena, ownedChildrenDroppable = droppable, runtimeOuterCellReuseSupported = reuse, runtimeCopyAdtSupported = copyAdt, runtimeRecordAdtSupported = record, runtimeOwnedChildAdtSupported = owned, runtimeTcoOwnedChildAdtSupported = tcoOwned, runtimeTcoListElementSupported = tcoList, runtimePositionalAdtSupported = positional, children = children, rejections = heapRejections(resource)(droppable)(unresolved)(reuse)), final)

let heapRuntimeRecordAdtLayoutIndexed (semanticType: SemanticType) (path: List((Int, Str))) (index: TypeGraphIndex) = indexedGate(IndexedRecord)(semanticType)(path)(index)

let heapRuntimeOwnedFieldLayoutIndexed (semanticType: SemanticType) (path: List((Int, Str))) (index: TypeGraphIndex) = indexedGate(IndexedOwnedField)(semanticType)(path)(index)

let heapRuntimeOwnedTupleElementLayoutIndexed (semanticType: SemanticType) (path: List((Int, Str))) (index: TypeGraphIndex) = indexedGate(IndexedOwnedTuple)(semanticType)(path)(index)

let heapRuntimeOwnedChildAdtLayoutIndexed (semanticType: SemanticType) (path: List((Int, Str))) (index: TypeGraphIndex) = indexedGate(IndexedOwnedChild)(semanticType)(path)(index)

let heapRuntimeTcoOwnedChildAdtLayoutIndexed (semanticType: SemanticType) (path: List((Int, Str))) (index: TypeGraphIndex) = indexedGate(IndexedTcoOwnedChild)(semanticType)(path)(index)

let heapRuntimePositionalAdtLayoutIndexed (semanticType: SemanticType) (path: List((Int, Str))) (index: TypeGraphIndex) = indexedGate(IndexedPositional)(semanticType)(path)(index)

let heapRuntimeTcoListElementLayoutIndexed (semanticType: SemanticType) (path: List((Int, Str))) (index: TypeGraphIndex) = indexedGate(IndexedTcoList)(semanticType)(path)(index)

let heapRuntimeRecursiveCopyAdtLayoutIndexed (semanticType: SemanticType) (index: TypeGraphIndex) = indexedGate(IndexedRecursiveCopy)(semanticType)([])(index)

let heapAdtShallowCopySupportedIndexed (semanticType: SemanticType) (index: TypeGraphIndex) = indexedGate(IndexedShallow)(semanticType)([])(index)

let classifyHeapResource (semanticType: SemanticType) (environment: TypeEnvironment) =
    match environment
    |> emptyTypeGraphIndex
    |> classifyHeapResourceIndexed(semanticType) with
        | (facts, _) -> facts

let classifyHeapOwnedChildrenDroppable (semanticType: SemanticType) (environment: TypeEnvironment) =
    match environment
    |> emptyTypeGraphIndex
    |> classifyHeapOwnedChildrenDroppableIndexed(semanticType) with
        | (facts, _) -> facts

let classifyHeapArenaDeepCopy (semanticType: SemanticType) (environment: TypeEnvironment) =
    match environment
    |> emptyTypeGraphIndex
    |> classifyHeapArenaDeepCopyIndexed(semanticType) with
        | (facts, _) -> facts

let classifyHeapChildren (semanticType: SemanticType) (environment: TypeEnvironment) =
    match environment
    |> emptyTypeGraphIndex
    |> classifyHeapChildrenIndexed(semanticType) with
        | (facts, _) -> facts

let classifyHeapChildTypes (semanticType: SemanticType) (environment: TypeEnvironment) =
    match environment
    |> emptyTypeGraphIndex
    |> classifyHeapChildTypesIndexed(semanticType) with
        | (facts, _) -> facts

let classifyHeapShallowCopy (semanticType: SemanticType) (environment: TypeEnvironment) =
    match environment
    |> emptyTypeGraphIndex
    |> classifyHeapShallowCopyIndexed(semanticType) with
        | (facts, _) -> facts

let classifyHeapAdmissibility (semanticType: SemanticType) (environment: TypeEnvironment) =
    match environment
    |> emptyTypeGraphIndex
    |> classifyHeapAdmissibilityIndexed(semanticType) with
        | (facts, _) -> facts

let classifyHeapCopyPlan (semanticType: SemanticType) (environment: TypeEnvironment) =
    match environment
    |> emptyTypeGraphIndex
    |> classifyHeapCopyPlanIndexed(semanticType) with
        | (facts, _) -> facts

let classifyHeapDropper (semanticType: SemanticType) (environment: TypeEnvironment) =
    match environment
    |> emptyTypeGraphIndex
    |> classifyHeapDropperIndexed(semanticType) with
        | (facts, _) -> facts

let classifyHeapLayout (semanticType: SemanticType) (environment: TypeEnvironment) =
    match environment
    |> emptyTypeGraphIndex
    |> classifyHeapLayoutIndexed(semanticType) with
        | (facts, _) -> facts

let heapRuntimeRecursiveCopyAdtLayout (semanticType: SemanticType) (environment: TypeEnvironment) =
    match environment
    |> emptyTypeGraphIndex
    |> heapRuntimeRecursiveCopyAdtLayoutIndexed(semanticType) with
        | (facts, _) -> facts

let heapRuntimeOwnedFieldLayout (semanticType: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) =
    match environment
    |> emptyTypeGraphIndex
    |> heapRuntimeOwnedFieldLayoutIndexed(semanticType)(path) with
        | (facts, _) -> facts

let heapRuntimeOwnedTupleElementLayout (semanticType: SemanticType) (environment: TypeEnvironment) (path: List((Int, Str))) =
    match environment
    |> emptyTypeGraphIndex
    |> heapRuntimeOwnedTupleElementLayoutIndexed(semanticType)(path) with
        | (facts, _) -> facts
