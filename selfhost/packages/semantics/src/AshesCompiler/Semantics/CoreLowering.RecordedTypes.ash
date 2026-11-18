// Reads the types the standalone inference recorded against numbered nodes as lowering's own.
//
// Invariants:
// - Lowering names every type it registers by its stitched name and gives every named type the
//   symbol id 0; the inference numbers its named types itself. A recorded named type is read by
//   its name, which both sides take from the same stitched declaration, and given id 0.
// - A name lowering does not know, a type parameter outside the generic implementation method being
//   lowered, and a capability or row outside a function type are not read: the translation names
//   what it could not map.
// - A function type's capability row is dropped; lowering keeps no rows in the types it unifies.
// - Type variables are kept as they are; a caller that needs a closed type checks for them.
// - Inside a generic body lowered at a concrete instance, a recorded type is read through that
//   instance's instantiation: the body's quantified variables take their types there first. A head
//   parameter of a generic implementation is read as the variable lowering pinned it to.

import Ashes.Collection.List.reverse
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeSchemes.freeTypeVariables
import AshesCompiler.Semantics.TypeSchemes.schemeIsClosed
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
    value recordedCopyInstantiation,
    value recordedOperatorGenericScheme,
    value recordedSettledType,
    value recordedSettledTypeAt,
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

// The type a generic implementation's head parameter has while one of its methods is lowered: the
// variable lowering pinned it to, by the parameter's name.
let recursive activeTypeParameterNamed (name: Str) (parameters: List((Str, SemanticType))) =
    match parameters with
        | [] -> None
        | (candidate, semanticType) :: rest ->
            if candidate == name
            then Some(semanticType)
            else activeTypeParameterNamed(name)(rest)

// The recorded type as lowering writes it, or what could not be mapped.
let recursive translateRecordedTypeAsIs (state: CoreLoweringState) (semanticType: SemanticType) =
    match semanticType with
        | SemList(element) ->
            match translateRecordedTypeAsIs(state)(element) with
                | Ok(translated) -> Ok(SemList(translated))
                | failed -> failed
        | SemTuple(elements) ->
            match translateRecordedTypes(state)(elements)([]) with
                | Ok(translated) -> Ok(SemTuple(translated))
                | Error(message) -> Error(message)
        | SemFunction(parameter, result, _row) ->
            match (translateRecordedTypeAsIs(state)(parameter), translateRecordedTypeAsIs(state)(result)) with
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
            match translateRecordedTypeAsIs(state)(target) with
                | Ok(translated) -> Ok(SemPointer(translated))
                | failed -> failed
        | SemParameter(_id, name) ->
            match state
            |> stateActiveTypeParameters
            |> activeTypeParameterNamed(name) with
                | Some(variable) -> Ok(variable)
                | None -> Error("type parameter: " + name)
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
            match translateRecordedTypeAsIs(state)(head) with
                | Ok(translated) -> translateRecordedTypes(state)(rest)(translated :: reversed)
                | Error(message) -> Error(message)

// A recorded type read where it is lowered: inside a generic body lowered at a concrete instance,
// the body's quantified variables take their types at that instance first.
let instantiatedRecordedType (state: CoreLoweringState) (semanticType: SemanticType) =
    match stateNodeInstantiation(state) with
        | [] -> semanticType
        | instantiation -> applySubstitution(instantiation)(semanticType)

// The recorded type as lowering writes it, read through the instantiation of the body being
// lowered, or what could not be mapped.
let translateRecordedType (state: CoreLoweringState) (semanticType: SemanticType) =
    semanticType
    |> instantiatedRecordedType(state)
    |> translateRecordedTypeAsIs(state)

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

let recursive instantiatedArguments (state: CoreLoweringState) (arguments: List((Int, SemanticType))) =
    match arguments with
        | [] -> []
        | (variable, semanticType) :: rest -> (variable, instantiatedRecordedType(state)(semanticType)) :: instantiatedArguments(state)(rest)

// The instantiation a generic body is lowered at for a call whose callee reference has the identity
// `reference`: the arguments recorded at the reference, read through the instantiation of the body
// being lowered. Empty unless the recorded type of the body's root node `source`, read through it,
// is closed and is `functionType`, the type lowering pinned the copy to.
let recordedCopyInstantiation (reference: Int) (source: Int) (functionType: SemanticType) (state: CoreLoweringState) =
    match stateNodeTypes(state) with
        | None -> []
        | Some(table) ->
            match (nodeInstantiationAt(reference)(table), nodeTypeAt(source)(table)) with
                | (Some(NodeInstantiation { arguments = arguments }), Some(recorded)) ->
                    let instantiation = instantiatedArguments(state)(arguments)
                    in
                        match (recorded
                        |> applySubstitution(instantiation)
                        |> translateRecordedTypeAsIs(state), translateRecordedTypeAsIs(state)(functionType)) with
                            | (Ok(instantiated), Ok(pinned)) ->
                                if freeTypeVariables(instantiated) == [] && instantiated == pinned
                                then instantiation
                                else []
                            | _ -> []
                | _ -> []

// The operator traits whose operators lowering holds monomorphic: it emits their integer form over
// open operands and settles it once the type is known, so it gives a binding using them no
// requirement and no dictionary.
let isMonomorphicOperatorTrait (name: Str) =
    match name with
        | "Add" -> true
        | "Subtract" -> true
        | "Multiply" -> true
        | "Divide" -> true
        | "Remainder" -> true
        | "Negate" -> true
        | "BitAnd" -> true
        | "BitOr" -> true
        | "BitXor" -> true
        | "ShiftLeft" -> true
        | "ShiftRight" -> true
        | "BitwiseNot" -> true
        | _ -> false

let recursive quantifiedVariableListed (variable: Int) (quantified: List((Int, Str))) =
    match quantified with
        | [] -> false
        | (candidate, _name) :: rest -> candidate == variable || quantifiedVariableListed(variable)(rest)

let recursive mentionsQuantifiedVariable (quantified: List((Int, Str))) (variables: List(Int)) =
    match variables with
        | [] -> false
        | variable :: rest -> quantifiedVariableListed(variable)(quantified) || mentionsQuantifiedVariable(quantified)(rest)

let recursive hasQuantifiedOperatorRequirement (quantified: List((Int, Str))) (requirements: List(TraitConstraint)) =
    match requirements with
        | [] -> false
        | TraitConstraint { traitName = traitName, typeArguments = arguments } :: rest ->
            isMonomorphicOperatorTrait(traitName) && mentionsQuantifiedVariable(quantified)(freeTypeVariables(SemTuple(arguments))) || hasQuantifiedOperatorRequirement(quantified)(rest)

let recursive translateRecordedRequirements (state: CoreLoweringState) (requirements: List(TraitConstraint)) (reversed: List(TraitConstraint)) =
    match requirements with
        | [] ->
            reversed
            |> reverse
            |> Ok
        | TraitConstraint { traitName = traitName, typeArguments = arguments } :: rest ->
            match translateRecordedTypes(state)(arguments)([]) with
                | Ok(translated) -> translateRecordedRequirements(state)(rest)(TraitConstraint(traitName = traitName, typeArguments = translated) :: reversed)
                | Error(message) -> Error(message)

// The scheme the standalone inference recorded for the top-level binding whose value has the
// identity `node`, as lowering writes it, when it quantifies every variable it holds and requires
// an operator trait lowering holds monomorphic at a quantified variable: a binding the inference
// generalizes where lowering's own inference does not.
let recordedOperatorGenericScheme (node: Int) (state: CoreLoweringState) =
    match stateNodeTypes(state) with
        | None -> None
        | Some(table) ->
            match nodeDeclarationAt(node)(table) with
                | Some((_name, TypeScheme { quantified = quantified, body = body, constraints = constraints })) ->
                    match (translateRecordedTypeAsIs(state)(body), translateRecordedRequirements(state)(constraints)([])) with
                        | (Ok(translatedBody), Ok(translatedConstraints)) ->
                            let scheme = TypeScheme(quantified = quantified, body = translatedBody, constraints = translatedConstraints)
                            in
                                if schemeIsClosed(scheme) && hasQuantifiedOperatorRequirement(quantified)(translatedConstraints)
                                then Some(scheme)
                                else None
                        | _ -> None
                | None -> None

// A recorded type, read through the instantiation of the body being lowered, with the variables the
// program's uses settle replaced by their settled types, as lowering writes it, when that leaves no
// variable. A body lowering holds at one type while the inference generalized it reads its
// quantified variables this way.
let recordedSettledType (recorded: SemanticType) (state: CoreLoweringState) =
    match recorded
    |> instantiatedRecordedType(state)
    |> applySubstitutionMap(stateSettledNodeVariables(state))
    |> translateRecordedTypeAsIs(state) with
        | Ok(translated) ->
            if freeTypeVariables(translated) == []
            then Some(translated)
            else None
        | Error(_message) -> None

// The type recorded for the node with this identity, closed by the variables the program's uses
// settle.
let recordedSettledTypeAt (node: Int) (state: CoreLoweringState) =
    match stateNodeTypes(state) with
        | Some(table) ->
            match nodeTypeAt(node)(table) with
                | Some(recorded) -> recordedSettledType(recorded)(state)
                | None -> None
        | None -> None
