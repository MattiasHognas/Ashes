// Reads the types the standalone inference recorded against numbered nodes as lowering's own.
//
// Invariants:
// - Lowering names every type it registers by its stitched name and gives every named type the
//   symbol id 0; the inference numbers its named types itself. A recorded named type is read by
//   its name, which both sides take from the same stitched declaration, and given id 0.
// - A name lowering does not know, a type parameter, and a capability or row outside a function
//   type are not read: the translation names what it could not map.
// - A function type's capability row is dropped; lowering keeps no rows in the types it unifies.
// - Type variables are kept as they are; a caller that needs a closed type checks for them.

import Ashes.Collection.List.reverse
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeSchemes.freeTypeVariables
import AshesCompiler.Semantics.NodeTypeTable
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
export (
    value recordedTypeNameKnown,
    value translateRecordedType,
    value recordedNodeType,
    value recordedClosedNodeType,
    value untranslatableRecordedTypes,
    value recordedClosedTypeAt,
    value recordedDictionariesAt,
)

let recursive anyLayoutNamed (name: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> false
        | layout :: rest -> layoutResultName(layout) == Some(name) || anyLayoutNamed(name)(rest)

let recursive textListed (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest -> candidate == name || textListed(name)(rest)

let isLoweringBuiltinTypeName (name: Str) =
    match name with
        | "Int" -> true
        | "Str" -> true
        | "Bool" -> true
        | "Float" -> true
        | "BigInt" -> true
        | "Rune" -> true
        | "Bytes" -> true
        | "Unit" -> true
        | "List" -> true
        | "Maybe" -> true
        | "Result" -> true
        | "Socket" -> true
        | "TlsSocket" -> true
        | "Task" -> true
        | "JoinHandle" -> true
        | "Process" -> true
        | "FileHandle" -> true
        | "Ordering" -> true
        | _ -> false

// Whether lowering knows a named type by this name: a builtin, an external opaque type, a type the
// program declares, or one a registered constructor builds.
let recordedTypeNameKnown (name: Str) (state: CoreLoweringState) =
    if isLoweringBuiltinTypeName(name) || textListed(name)(state.externalOpaqueTypes)
    then true
    else
        let group = state.programState
        in
            match Ashes.Collection.Map.getStr(name)(group.declaredTypeNames) with
                | Some(_declared) -> true
                | None ->
                    state
                    |> stateConstructorLayouts
                    |> anyLayoutNamed(name)

// The recorded type as lowering writes it, or what could not be mapped.
let recursive translateRecordedType (state: CoreLoweringState) (semanticType: SemanticType) =
    match semanticType with
        | SemList(element) ->
            match translateRecordedType(state)(element) with
                | Ok(translated) -> Ok(SemList(translated))
                | failed -> failed
        | SemTuple(elements) ->
            match translateRecordedTypes(state)(elements)([]) with
                | Ok(translated) -> Ok(SemTuple(translated))
                | Error(message) -> Error(message)
        | SemFunction(parameter, result, _row) ->
            match (translateRecordedType(state)(parameter), translateRecordedType(state)(result)) with
                | (Ok(translatedParameter), Ok(translatedResult)) ->
                    None
                    |> SemFunction(translatedParameter)(translatedResult)
                    |> Ok
                | (Error(message), _) -> Error(message)
                | (_, Error(message)) -> Error(message)
        | SemNamed(_symbolId, name, arguments) ->
            if recordedTypeNameKnown(name)(state)
            then
                match translateRecordedTypes(state)(arguments)([]) with
                    | Ok(translated) ->
                        translated
                        |> SemNamed(0)(name)
                        |> Ok
                    | Error(message) -> Error(message)
            else Error("named type unknown to lowering: " + name)
        | SemPointer(target) ->
            match translateRecordedType(state)(target) with
                | Ok(translated) -> Ok(SemPointer(translated))
                | failed -> failed
        | SemParameter(_id, name) -> Error("type parameter: " + name)
        | SemCapability(name, _arguments) -> Error("capability outside a row: " + name)
        | SemRow(_entries, _tail) -> Error("row outside a function type")
        | other -> Ok(other)
and translateRecordedTypes (state: CoreLoweringState) (semanticTypes: List(SemanticType)) (reversed: List(SemanticType)) =
    match semanticTypes with
        | [] ->
            reversed
            |> reverse
            |> Ok
        | head :: rest ->
            match translateRecordedType(state)(head) with
                | Ok(translated) -> translateRecordedTypes(state)(rest)(translated :: reversed)
                | Error(message) -> Error(message)

// The type recorded for the node being lowered, as lowering writes it, when one is recorded and maps.
let recordedNodeType (state: CoreLoweringState) =
    match (stateNodeTypes(state), state.currentNode) with
        | (Some(table), Some(node)) ->
            match nodeTypeAt(node)(table) with
                | Some(recorded) ->
                    match translateRecordedType(state)(recorded) with
                        | Ok(translated) -> Some(translated)
                        | Error(_message) -> None
                | None -> None
        | _ -> None

// As recordedNodeType, only when the type holds no variable: a generic body's node keeps its
// binding's quantified variables, which only an instantiation closes.
let recordedClosedNodeType (state: CoreLoweringState) =
    match recordedNodeType(state) with
        | Some(recorded) ->
            if freeTypeVariables(recorded) == []
            then Some(recorded)
            else None
        | None -> None

// Every recorded node type lowering cannot read, by node, with the reason, and how many were read.
let untranslatableRecordedTypes (table: NodeTypeTable) (state: CoreLoweringState) =
    (let check (found: (Int, List((Int, Str)))) (node: Int) (recorded: SemanticType) =
        match (found, translateRecordedType(state)(recorded)) with
            | ((count, failures), Ok(_translated)) -> (count + 1, failures)
            | ((count, failures), Error(message)) -> (count, (node, message) :: failures)
    in Ashes.Collection.Map.foldLeft(check)((0, []))(table.types))

// The closed type recorded for the node with this identity, as lowering writes it.
let recordedClosedTypeAt (node: Int) (state: CoreLoweringState) =
    match stateNodeTypes(state) with
        | Some(table) ->
            match nodeTypeAt(node)(table) with
                | Some(recorded) ->
                    match translateRecordedType(state)(recorded) with
                        | Ok(translated) ->
                            if freeTypeVariables(translated) == []
                            then Some(translated)
                            else None
                        | Error(_message) -> None
                | None -> None
        | None -> None

let recursive closedRecordedRequirements (requirements: List(TraitConstraint)) (state: CoreLoweringState) =
    match requirements with
        | [] -> []
        | TraitConstraint { traitName = traitName, typeArguments = argument :: [] } :: rest ->
            match translateRecordedType(state)(argument) with
                | Ok(translated) ->
                    if freeTypeVariables(translated) == []
                    then (traitName, translated) :: closedRecordedRequirements(rest)(state)
                    else closedRecordedRequirements(rest)(state)
                | Error(_message) -> closedRecordedRequirements(rest)(state)
        | _requirement :: rest -> closedRecordedRequirements(rest)(state)

// The dictionaries the reference with this identity was recorded to pass, as trait names with closed
// type arguments in the scheme's order; requirements with an open or unreadable argument are left
// out.
let recordedDictionariesAt (node: Int) (state: CoreLoweringState) =
    match stateNodeTypes(state) with
        | Some(table) ->
            match nodeInstantiationAt(node)(table) with
                | Some(NodeInstantiation { dictionaries = dictionaries }) -> closedRecordedRequirements(dictionaries)(state)
                | None -> []
        | None -> []
