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
import Ashes.Collection.Map.MapTree
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeSchemes.freeTypeVariables
import AshesCompiler.Semantics.TypeSchemes.schemeIsClosed
import AshesCompiler.Semantics.NodeTypeTable
import AshesCompiler.Semantics.NodeIdentity.nodeIdentityOf
import AshesCompiler.Semantics.NodeIdentity.synthesizedNodeSource
import AshesCompiler.Semantics.NodeIdentity.foldExpressionNodes
import AshesCompiler.Semantics.NodeIdentity.foldItemNodes
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.LetBindingSyntax
import AshesCompiler.Frontend.Syntax.TopLevelItem
import AshesCompiler.Frontend.Syntax.ProgramSyntax
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.CoreConstrainedSpecialization.concreteConstrainedArguments
import AshesCompiler.Semantics.CoreConstrainedSpecialization.preparePureConcreteScheme
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
    value recordedConcreteCallType,
    value recordedOperatorGenericScheme,
    value recordedUnsettledOperatorGeneric,
    value schemeHasUnsettledOperatorRequirement,
    value rawOperatorDeclarationSchemes,
    value recordedDeferredEvaluation,
    value deferredEvaluationKey,
    value withRawOperatorDeclarationSchemes,
    value recordedSettledType,
    value recordedSettledTypeAt,
    value shadowSiteType,
    value shadowSiteTypeText,
    value shadowSiteInstance,
    value shadowSiteOperandText,
    value shadowGoalTypeText,
    value shadowLambdaParameterType,
    value shadowDictionaryGoalTypeText,
    value recordedSettledDictionariesAt,
    value recordedDeclarationAt,
    value recordedNestedOperatorGenericScheme,
    value rawOperatorSchemesWithNestedLets,
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

// The node whose recorded instantiation a reference reads: its own, or, for a reference lowering
// synthesized (a prefix or resume call of a staged binding), the source reference it stands for.
// The readers bind only what matches the call's own requirements by trait and type, so a record
// that does not fit leaves the call to lowering's own inference.
let instantiationSourceNode (node: Int) =
    match synthesizedNodeSource(node) with
        | Some((source, _role)) -> source
        | None -> node

// The dictionaries the reference with this identity was recorded to pass, as trait names with closed
// type arguments in the scheme's order; requirements with an open or unreadable argument are left
// out.
let recordedDictionariesAt (node: Int) (state: CoreLoweringState) =
    match stateNodeTypes(state) with
        | Some(table) ->
            match nodeInstantiationAt(instantiationSourceNode(node))(table) with
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

// The argument nodes and the callee come from the same numbered call. An
// unnumbered replacement argument has no entry and keeps speculative inference.
let recursive recordedArgumentsMatch expected arguments table state =
    match (expected, arguments) with
        | ([], []) -> true
        | (parameter :: rest, argument :: tail) ->
            match nodeTypeAt(nodeIdentityOf(argument))(table) with
                | Some(recorded) -> instantiatedRecordedType(state)(recorded) == parameter && recordedArgumentsMatch(rest)(tail)(table)(state)
                | None -> false
        | _ -> false

// An inferred arrow can still have a bare row variable at a pure use. The
// source may choose the empty row only when that variable was quantified by
// its declaration; preparePureConcreteScheme checks this on the original type.
let recursive copyRowsAllowPureInstance arity called =
    if arity == 0
    then true
    else
        match called with
            | SemFunction(_parameter, result, None) -> copyRowsAllowPureInstance(arity - 1)(result)
            | SemFunction(_parameter, result, Some(SemRow([], None))) -> copyRowsAllowPureInstance(arity - 1)(result)
            | SemFunction(_parameter, result, Some(SemVariable(_open))) -> copyRowsAllowPureInstance(arity - 1)(result)
            | _ -> false

let recordedCopyAtInstance arity arguments substitutions definition called table state =
    (let applied = applySubstitution(substitutions)(definition)
    in
        if definition == applied || applied != called || !copyRowsAllowPureInstance(arity)(called)
        then None
        else
            let quantified =
                Ashes.Collection.List.map(given (entry) ->
                    match entry with
                        | (id, _actual) -> (id, ""))(substitutions)
            in
                match preparePureConcreteScheme(TypeScheme(quantified = quantified, body = definition, constraints = []))(arity) with
                    | Some(pureScheme) ->
                        let instance = applySubstitution(substitutions)(pureScheme.body)
                        in
                            match concreteConstrainedArguments(arity)(instance) with
                                | Some(parameters) ->
                                    if recordedArgumentsMatch(parameters)(arguments)(table)(state)
                                    then
                                        match translateRecordedTypeAsIs(state)(instance) with
                                            | Ok(translated) -> Some(translated)
                                            | Error(_message) -> None
                                    else None
                                | None -> None
                    | None -> None)

// Validate the original effect rows before translation: lowering drops rows
// from function types. Both the named source body and the numbered use must
// agree at this reference's own instantiation, including its result layout.
let recordedConcreteCallType reference source arity arguments (state: CoreLoweringState) =
    match stateNodeTypes(state) with
        | None -> None
        | Some(table) ->
            match (nodeInstantiationAt(reference)(table), nodeTypeAt(source)(table), nodeTypeAt(reference)(table)) with
                | (Some(NodeInstantiation { arguments = substitutions }), Some(definition), Some(useType)) ->
                    recordedCopyAtInstance(arity)(arguments)(instantiatedArguments(state)(substitutions))(definition)(instantiatedRecordedType(state)(useType))(table)(state)
                | _ -> None

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

// A scheme the standalone inference recorded, as lowering writes it, when it quantifies every
// variable it holds and requires an operator trait lowering holds monomorphic at a quantified
// variable: a binding the inference generalizes where lowering's own inference does not.
let operatorGenericScheme (recorded: TypeScheme) (state: CoreLoweringState) =
    match recorded with
        | TypeScheme { quantified = quantified, body = body, constraints = constraints } ->
            match (translateRecordedTypeAsIs(state)(body), translateRecordedRequirements(state)(constraints)([])) with
                | (Ok(translatedBody), Ok(translatedConstraints)) ->
                    let scheme = TypeScheme(quantified = quantified, body = translatedBody, constraints = translatedConstraints)
                    in
                        if schemeIsClosed(scheme) && hasQuantifiedOperatorRequirement(quantified)(translatedConstraints)
                        then Some(scheme)
                        else None
                | _ -> None

// The key under which the raw-source schemes mark a top-level binding whose value lowering may
// evaluate once per use: no top-level name contains a colon.
let deferredEvaluationKey (name: Str) = "$deferred-evaluation:" + name

let recursive everyRecordedStageLatent (stages: List(Int)) =
    match stages with
        | [] -> true
        | stage :: rest -> stage != 0 && everyRecordedStageLatent(rest)

// Whether evaluating the top-level value rooted at `node` performs no capability and needs none of
// its binding's requirements: evaluating it again at each use observes nothing new.
let recordedEvaluationDeferrable (node: Int) (table: NodeTypeTable) =
    nodeEvaluationPure(node)(table) && (match nodeEvidenceStagesAt(node)(table) with
        | Some(stages) -> everyRecordedStageLatent(stages)
        | None -> true)

// Raw source has no stable expression IDs. Top-level declarations have unique names, so retain
// their operator-polymorphic schemes from validation without enabling expression-table reads, and
// mark each constrained binding whose evaluation can be deferred to its uses.
let rawOperatorDeclarationSchemes (table: NodeTypeTable) =
    Ashes.Collection.Map.foldLeft(given (schemes) ->
        given (node) ->
            given (entry) ->
                match entry with
                    | (name, TypeScheme { quantified = quantified, constraints = constraints } as scheme) ->
                        let marked =
                            if constraints != [] && recordedEvaluationDeferrable(node)(table)
                            then
                                Ashes.Collection.Map.setStr(deferredEvaluationKey(name))(scheme)(schemes)
                            else schemes
                        in
                            if hasQuantifiedOperatorRequirement(quantified)(constraints) && schemeIsClosed(scheme)
                            then Ashes.Collection.Map.setStr(name)(scheme)(marked)
                            else marked)(Ashes.Collection.Map.empty)(table.declarations)

// Whether the top-level binding `name`, whose value is `value`, may be evaluated once per use: the
// standalone inference recorded its evaluation as performing no capability and needing none of its
// requirements. Without a record of the program it is not.
let recordedDeferredEvaluation (name: Str) (value: Expr) (state: CoreLoweringState) =
    match stateNodeTypes(state) with
        | Some(table) ->
            recordedEvaluationDeferrable(nodeIdentityOf(value))(table)
        | None ->
            let registry = stateConcreteRegistry(state)
            in
                match Ashes.Collection.Map.getStr(deferredEvaluationKey(name))(registry.rawDeclarationSchemes) with
                    | Some(_scheme) -> true
                    | None -> false

let withRawOperatorDeclarationSchemes schemes state =
    (let registry = stateConcreteRegistry(state)
    in withStateConcreteRegistry((registry with rawDeclarationSchemes = schemes))(state))

// The operator-generic scheme the standalone inference recorded for the top-level binding whose
// value has the identity `node`.
let recordedOperatorGenericScheme name (node: Int) (state: CoreLoweringState) =
    match stateNodeTypes(state) with
        | None ->
            let registry = stateConcreteRegistry(state)
            in
                match Ashes.Collection.Map.getStr(name)(registry.rawDeclarationSchemes) with
                    | Some(scheme) -> operatorGenericScheme(scheme)(state)
                    | None -> None
        | Some(table) ->
            match nodeDeclarationAt(node)(table) with
                | Some((_name, recorded)) -> operatorGenericScheme(recorded)(state)
                | None -> None

let recursive anyUnsettledVariable (variables: List(Int)) (settled: MapTree(Int, SemanticType)) =
    match variables with
        | [] -> false
        | variable :: rest ->
            match Ashes.Collection.Map.getInt(variable)(settled) with
                | Some(_type) -> anyUnsettledVariable(rest)(settled)
                | None -> true

let recursive anyUnsettledOperatorRequirement (constraints: List(TraitConstraint)) (settled: MapTree(Int, SemanticType)) =
    match constraints with
        | [] -> false
        | TraitConstraint { traitName = traitName, typeArguments = arguments } :: rest ->
            isMonomorphicOperatorTrait(traitName) && anyUnsettledVariable(freeTypeVariables(SemTuple(arguments)))(settled) || anyUnsettledOperatorRequirement(rest)(settled)

// Whether one of a recorded operator-generic scheme's operator requirements is at a variable the
// program's uses leave unsettled: the binding is used at more than one type through it.
let schemeHasUnsettledOperatorRequirement (scheme: TypeScheme) (state: CoreLoweringState) =
    match scheme with
        | TypeScheme { constraints = constraints } ->
            state
            |> stateSettledNodeVariables
            |> anyUnsettledOperatorRequirement(constraints)

// The operator-generic scheme recorded for a top-level binding, under typed lowering only, when one
// of its operator requirements is at a variable the program's uses do not settle to one type: the
// binding is used at more than one type through that requirement (`twice` at Int and at Float). A
// binding used at one type is answered by its settled uses instead.
let recordedUnsettledOperatorGeneric name (node: Int) (state: CoreLoweringState) =
    match stateNodeTypes(state) with
        | None -> false
        | Some(_table) ->
            match recordedOperatorGenericScheme(name)(node)(state) with
                | Some(TypeScheme { constraints = constraints }) ->
                    state
                    |> stateSettledNodeVariables
                    |> anyUnsettledOperatorRequirement(constraints)
                | None -> false

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

let recursive settledRecordedRequirements (requirements: List(TraitConstraint)) (state: CoreLoweringState) =
    match requirements with
        | [] -> []
        | TraitConstraint { traitName = traitName, typeArguments = argument :: [] } :: rest ->
            match recordedSettledType(argument)(state) with
                | Some(settled) -> (traitName, Some(settled)) :: settledRecordedRequirements(rest)(state)
                | None -> (traitName, None) :: settledRecordedRequirements(rest)(state)
        | TraitConstraint { traitName = traitName } :: rest -> (traitName, None) :: settledRecordedRequirements(rest)(state)

// The dictionaries the reference with this identity was recorded to pass, in the scheme's order,
// each with its type argument closed through the instantiation of the body being lowered and the
// variables the program's uses settle, or None when that leaves it open or it does not map.
let recordedSettledDictionariesAt (node: Int) (state: CoreLoweringState) =
    match stateNodeTypes(state) with
        | Some(table) ->
            match nodeInstantiationAt(instantiationSourceNode(node))(table) with
                | Some(NodeInstantiation { dictionaries = dictionaries }) -> settledRecordedRequirements(dictionaries)(state)
                | None -> []
        | None -> []

// The type recorded for the node with this identity, closed by the variables the program's uses
// settle.
let recordedSettledTypeAt (node: Int) (state: CoreLoweringState) =
    match stateNodeTypes(state) with
        | Some(table) ->
            match nodeTypeAt(node)(table) with
                | Some(recorded) -> recordedSettledType(recorded)(state)
                | None -> None
        | None -> None

// The step-8 shadow comparison's final type for the site being lowered: the type recorded at its
// node, closed by the body's instantiation and the variables the program's uses settle. None at a
// node lowering synthesized, or where that leaves a variable.
let shadowSiteType (state: CoreLoweringState) =
    match state.currentNode with
        | Some(node) -> recordedSettledTypeAt(node)(state)
        | None -> None

let shadowSiteTypeText (state: CoreLoweringState) =
    match shadowSiteType(state) with
        | Some(settled) ->
            settled
            |> Ashes.Trait.Show.show
            |> Some
        | None -> None

// How the table records the operator at the site being lowered is reached: "evidence" through a
// dictionary the body carries, "direct" through a primitive or a declared implementation.
let shadowSiteInstance (state: CoreLoweringState) =
    match (stateNodeTypes(state), state.currentNode) with
        | (Some(table), Some(node)) ->
            match nodeOperatorAt(node)(table) with
                | Some(NodeOperator { instance = DictionaryInstance }) -> Some("evidence")
                | Some(_operator) -> Some("direct")
                | None -> None
        | _ -> None

let settledText (recorded: SemanticType) (state: CoreLoweringState) =
    match recordedSettledType(recorded)(state) with
        | Some(settled) ->
            settled
            |> Ashes.Trait.Show.show
            |> Some
        | None -> None

// The operand type the table records for the operator at the site being lowered, closed as
// shadowSiteType closes a type.
let shadowSiteOperandText (state: CoreLoweringState) =
    match (stateNodeTypes(state), state.currentNode) with
        | (Some(table), Some(node)) ->
            match nodeOperatorAt(node)(table) with
                | Some(NodeOperator { operandType = operandType }) -> settledText(operandType)(state)
                | None -> None
        | _ -> None

let recursive firstSettledOfTrait (traitName: Str) (dictionaries: List((Str, Maybe(SemanticType)))) =
    match dictionaries with
        | [] -> None
        | (name, Some(settled)) :: rest ->
            if name == traitName
            then
                settled
                |> Ashes.Trait.Show.show
                |> Some
            else firstSettledOfTrait(traitName)(rest)
        | _open :: rest -> firstSettledOfTrait(traitName)(rest)

// The type the table answers a trait goal at the site being lowered with: the operand of the
// operator recorded there for that trait, or the first closed dictionary of that trait the
// reference there was recorded to pass. `key` is the goal key, `start:end:Trait:method`.
let shadowGoalTypeText (key: Str) (state: CoreLoweringState) =
    match (stateNodeTypes(state), state.currentNode, Ashes.Text.split(key)(":")) with
        | (Some(table), Some(node), _start :: _end :: traitName :: _method) ->
            match nodeOperatorAt(node)(table) with
                | Some(NodeOperator { traitName = recordedTrait, operandType = operandType }) ->
                    if recordedTrait == traitName
                    then settledText(operandType)(state)
                    else
                        state
                        |> recordedSettledDictionariesAt(node)
                        |> firstSettledOfTrait(traitName)
                | None ->
                    state
                    |> recordedSettledDictionariesAt(node)
                    |> firstSettledOfTrait(traitName)
        | _ -> None

// The type the table answers a constrained call's dictionary goal with: the first closed dictionary
// of the goal's trait recorded at the call's root reference.
let shadowDictionaryGoalTypeText (key: Str) (root: Int) (state: CoreLoweringState) =
    match Ashes.Text.split(key)(":") with
        | _start :: _end :: traitName :: _method ->
            state
            |> recordedSettledDictionariesAt(root)
            |> firstSettledOfTrait(traitName)
        | _ -> None

let recursive boundSchemeNamed (name: Str) (bindings: List((Str, TypeScheme))) =
    match bindings with
        | [] -> None
        | (bound, scheme) :: rest ->
            if bound == name
            then Some(scheme)
            else boundSchemeNamed(name)(rest)

// The type the table records for a lambda's parameter: the binding the inference notes at the
// lambda's own node, closed as shadowSiteType closes a type. Ok(None) when the node in effect
// binds no parameter of that name (it is not the lambda's node: an applied lambda is lowered
// under its call's node), Ok(Some) with the type, Error when the binding's type stays open.
let shadowLambdaParameterType (parameter: Str) (state: CoreLoweringState) =
    match (stateNodeTypes(state), state.currentNode) with
        | (Some(table), Some(node)) ->
            match table
            |> nodeBindingsAt(node)
            |> boundSchemeNamed(parameter) with
                | Some(TypeScheme { body = body }) ->
                    match recordedSettledType(body)(state) with
                        | Some(settled) -> Ok(Some(settled))
                        | None -> Error("open or untranslatable type")
                | None -> Ok(None)
        | _ -> Ok(None)

// The name and scheme the standalone inference gave the top-level binding whose value is the node
// with this identity, as lowering writes it. The scheme keeps the inference's quantified variable
// ids, so a reader instantiates it before unifying it with lowering's own types. None when nothing
// is recorded there or a type in the scheme does not map.
let recordedDeclarationAt (node: Int) (state: CoreLoweringState) =
    match stateNodeTypes(state) with
        | Some(table) ->
            match nodeDeclarationAt(node)(table) with
                | Some((name, TypeScheme { quantified = quantified, body = body, constraints = constraints })) ->
                    match (translateRecordedTypeAsIs(state)(body), translateRecordedRequirements(state)(constraints)([])) with
                        | (Ok(translatedBody), Ok(translatedConstraints)) -> Some((name, TypeScheme(quantified = quantified, body = translatedBody, constraints = translatedConstraints)))
                        | _ -> None
                | None -> None
        | None -> None

let recursive recordedSchemeForValue (name: Str) (valueType: SemanticType) (bindings: List((Str, TypeScheme))) =
    match bindings with
        | [] -> None
        | (candidate, scheme) :: rest ->
            if candidate == name && scheme.body == valueType
            then Some(scheme)
            else recordedSchemeForValue(name)(valueType)(rest)

// The key a `let` below top level is known by in raw source, which has no node identities: the
// source region of the top-level item holding it, the span directly around it and the name it
// binds. A colon never occurs in a compiler name, so the key cannot meet a top-level binding's.
let nestedLetSchemeKey (region: Int) (span: TextSpan) (name: Str) =
    match span with
        | TextSpan { start = start, end = end } -> "let:" + Ashes.Text.fromInt(region) + ":" + Ashes.Text.fromInt(start) + ":" + Ashes.Text.fromInt(end) + ":" + name

let recursive valueIsLambda (value: Expr) =
    match value with
        | ExprAt(_span, inner) -> valueIsLambda(inner)
        | ExprNode(_node, inner) -> valueIsLambda(inner)
        | ExprLambda(_parameter, _body, _annotation) -> true
        | _ -> false

// A located `let` below top level whose lambda value the standalone inference generalized over an
// operator trait lowering holds monomorphic, added under its raw-source key, with the same checks
// the numbered reading makes: the scheme recorded at the `let` node for its name has the value's
// recorded type as its body.
let nestedLetSchemeVisit (table: NodeTypeTable) (region: Int) (schemes: MapTree(Str, TypeScheme)) (node: Int) (span: Maybe(TextSpan)) (expression: Expr) =
    match (span, expression) with
        | (Some(located), ExprLet(name, value, _body, _parameters, _annotation, _requirements)) ->
            if valueIsLambda(value)
            then
                match nodeTypeAt(nodeIdentityOf(value))(table) with
                    | Some(valueType) ->
                        match table
                        |> nodeBindingsAt(node)
                        |> recordedSchemeForValue(name)(valueType) with
                            | Some(TypeScheme { quantified = quantified, constraints = constraints } as scheme) ->
                                if hasQuantifiedOperatorRequirement(quantified)(constraints) && schemeIsClosed(scheme)
                                then
                                    Ashes.Collection.Map.setStr(nestedLetSchemeKey(region)(located)(name))(scheme)(schemes)
                                else schemes
                            | None -> schemes
                    | None -> schemes
            else schemes
        | _ -> schemes

let recursive itemBindingName (item: TopLevelItem) =
    match item with
        | TopLevelAt(_span, inner) -> itemBindingName(inner)
        | TopLevelLet(LetBindingSyntax { name = name }, _isRecursive) -> Some(name)
        | TopLevelRecursiveGroup(LetBindingSyntax { name = name } :: _bindings) -> Some(name)
        | _ -> None

// The source region lowering moves to at an item binding `name`, as `withStateSourceRegionOf`
// moves: the region recorded for the name, or the current one.
let regionAt (regionOfName: MapTree(Str, Int)) (name: Maybe(Str)) (current: Int) =
    match name with
        | Some(bound) ->
            match Ashes.Collection.Map.getStr(bound)(regionOfName) with
                | Some(region) -> region
                | None -> current
        | None -> current

let recursive nestedLetSchemesOfItems (table: NodeTypeTable) (regionOfName: MapTree(Str, Int)) (region: Int) (items: List(TopLevelItem)) (schemes: MapTree(Str, TypeScheme)) =
    match items with
        | [] -> (region, schemes)
        | item :: rest ->
            let itemRegion =
                regionAt(regionOfName)(itemBindingName(item))(region)
            in
                item
                |> foldItemNodes(nestedLetSchemeVisit(table)(itemRegion))(schemes)
                |> nestedLetSchemesOfItems(table)(regionOfName)(itemRegion)(rest)

// The raw-source schemes of `rawOperatorDeclarationSchemes`, together with the operator-generic
// `let` bindings below top level, read from the numbered form of the same program. Each item's
// nodes are keyed in the region lowering is in while it lowers that item: the region of the
// trailing expression's empty name to begin with, then the region of each item's binding.
let rawOperatorSchemesWithNestedLets (regionOfName: MapTree(Str, Int)) (numbered: ProgramSyntax) (table: NodeTypeTable) =
    match numbered with
        | ProgramSyntax { items = items, body = body } ->
            match table
            |> rawOperatorDeclarationSchemes
            |> nestedLetSchemesOfItems(table)(regionOfName)(regionAt(regionOfName)(Some(""))(0))(items) with
                | (region, schemes) ->
                    match body with
                        | Some(trailing) ->
                            foldExpressionNodes(region
                            |> regionAt(regionOfName)(Some(""))
                            |> nestedLetSchemeVisit(table))(schemes)(trailing)
                        | None -> schemes

// The operator-generic scheme raw-source lowering keeps for the `let` being lowered, by its key.
let rawNestedOperatorGenericScheme (name: Str) (state: CoreLoweringState) =
    match state.currentSpan with
        | Some(span) ->
            let registry = stateConcreteRegistry(state)
            in
                match Ashes.Collection.Map.getStr(nestedLetSchemeKey(stateCurrentItem(state))(span)(name))(registry.rawDeclarationSchemes) with
                    | Some(scheme) -> operatorGenericScheme(scheme)(state)
                    | None -> None
        | None -> None

// The operator-generic scheme the standalone inference recorded for `name` at the `let` node
// `node` below top level, when the type recorded for the value node `valueNode` is the scheme's
// body: the binding the `let` introduces, not a parameter or pattern variable of the same name.
// Without the node table it is the scheme raw-source lowering keeps for that `let`.
let recordedNestedOperatorGenericScheme (node: Int) (name: Str) (valueNode: Int) (state: CoreLoweringState) =
    match stateNodeTypes(state) with
        | None -> rawNestedOperatorGenericScheme(name)(state)
        | Some(table) ->
            match nodeTypeAt(valueNode)(table) with
                | Some(valueType) ->
                    match table
                    |> nodeBindingsAt(node)
                    |> recordedSchemeForValue(name)(valueType) with
                        | Some(recorded) -> operatorGenericScheme(recorded)(state)
                        | None -> None
                | None -> None
