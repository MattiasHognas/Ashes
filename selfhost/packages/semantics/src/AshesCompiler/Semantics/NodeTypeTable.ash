// Holds what type inference recorded against identified syntax nodes.
//
// Invariants:
// - Every map is keyed by node identity.
// - A stored type is resolved under the substitution of the top-level declaration it was recorded
//   in. A variable left in it is either quantified by an enclosing binding's scheme, with the same
//   id the scheme lists, or constrained by nothing.
// - A node keeps the youngest type recorded for it.

import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeSchemes
import Ashes.Collection.Map.MapTree
export (
    type NodeOperatorInstance(..),
    type NodeOperator(..),
    type NodeInstantiation(..),
    type NodeTypeTable(..),
    value emptyNodeTypeTable,
    value settleNodeNotes,
    value nodeTypeAt,
    value nodeOperatorAt,
    value nodeInstantiationAt,
    value nodeEvidenceStagesAt,
    value nodeEvaluationPure,
    value nodeBindingsAt,
    value nodeDeclarationAt,
    value settledInstantiationArguments,
)

// How an operator's instance is reached at its recorded operand type: a primitive instruction, an
// implementation the program or a library declares or derives (whose own requirements may still
// need dictionaries when the operand type holds a variable), or the dictionary a quantified
// variable's requirement passes in.
type NodeOperatorInstance =
    | PrimitiveInstance
    | DeclaredInstance
    | DictionaryInstance

// The trait an operator node maps to, the type of its operands and how its instance is reached. A
// concrete operand type names the instance; a variable is one the enclosing binding quantifies,
// whose requirements carry the dictionary.
type NodeOperator =
    | traitName: Str
    | operandType: SemanticType
    | instance: NodeOperatorInstance

// A reference to a generic binding: the type each quantified variable of the binding's scheme has
// at this reference, and the scheme's trait requirements at those types. `dictionaries` holds the
// requirements that mention a quantified variable of the scheme, in the scheme's own order: a
// requirement the scheme states at a concrete type names an instance and passes nothing.
type NodeInstantiation =
    | arguments: List((Int, SemanticType))
    | requirements: List(TraitConstraint)
    | dictionaries: List(TraitConstraint)
    // Instantiated requirements paired with the defining scheme's source demand stages.
    | evidenceDemands: List((TraitConstraint, Int))

type NodeTypeTable =
    | types: MapTree(Int, SemanticType)
    | operators: MapTree(Int, NodeOperator)
    | instantiations: MapTree(Int, NodeInstantiation)
    // Per canonical scheme requirement at the source RHS of a binding.
    | evidenceStages: MapTree(Int, List(Int))
    // The row evaluating a top-level value performs, keyed by the root node of the value.
    | evaluationRows: MapTree(Int, SemanticType)
    // The names a node brings into scope: at a `let` its binding, at an arm guard or body the arm's pattern
    // variables.
    | bindings: MapTree(Int, List((Str, TypeScheme)))
    // A top-level binding's name and scheme, keyed by the root node of its value.
    | declarations: MapTree(Int, (Str, TypeScheme))
    // How many nodes have a type, and how many times a node that had one was given another.
    | typeCount: Int
    | repeatedCount: Int

let emptyNodeTypeTable : NodeTypeTable = NodeTypeTable(types = Ashes.Collection.Map.empty, operators = Ashes.Collection.Map.empty, instantiations = Ashes.Collection.Map.empty, evidenceStages = Ashes.Collection.Map.empty, evaluationRows = Ashes.Collection.Map.empty, bindings = Ashes.Collection.Map.empty, declarations = Ashes.Collection.Map.empty, typeCount = 0, repeatedCount = 0)

let nodeTypeAt (node: Int) (table: NodeTypeTable) = Ashes.Collection.Map.getInt(node)(table.types)

let nodeOperatorAt (node: Int) (table: NodeTypeTable) = Ashes.Collection.Map.getInt(node)(table.operators)

let nodeInstantiationAt (node: Int) (table: NodeTypeTable) = Ashes.Collection.Map.getInt(node)(table.instantiations)

let nodeEvidenceStagesAt (node: Int) (table: NodeTypeTable) = Ashes.Collection.Map.getInt(node)(table.evidenceStages)

// Whether a row lists no capability, through any row its tail has resolved to.
let recursive rowPerformsNothing (row: SemanticType) =
    match row with
        | SemRow([], None) -> true
        | SemRow([], Some(tail)) -> rowPerformsNothing(tail)
        | SemVariable(_id) -> true
        | _ -> false

// Whether evaluating the top-level value rooted at `node` performs no capability: its recorded row
// lists none, whatever variable it is still open in. A value with no recorded row is not known to
// be pure.
let nodeEvaluationPure (node: Int) (table: NodeTypeTable) =
    match Ashes.Collection.Map.getInt(node)(table.evaluationRows) with
        | Some(row) -> rowPerformsNothing(row)
        | None -> false

let nodeBindingsAt (node: Int) (table: NodeTypeTable) =
    match Ashes.Collection.Map.getInt(node)(table.bindings) with
        | Some(bindings) -> bindings
        | None -> []

let nodeDeclarationAt (node: Int) (table: NodeTypeTable) = Ashes.Collection.Map.getInt(node)(table.declarations)

let isNumericOperand (operandType: SemanticType) =
    match operandType with
        | SemInt -> true
        | SemUInt(_bits) -> true
        | SemFloat -> true
        | SemBigInt -> true
        | _ -> false

let isIntegralOperand (operandType: SemanticType) =
    match operandType with
        | SemInt -> true
        | SemUInt(_bits) -> true
        | _ -> false

let isTextOperand (operandType: SemanticType) =
    match operandType with
        | SemString -> true
        | _ -> false

let isScalarOperand (operandType: SemanticType) =
    match operandType with
        | SemRune -> true
        | SemBool -> true
        | _ -> false

let isRuneOperand (operandType: SemanticType) =
    match operandType with
        | SemRune -> true
        | _ -> false

let isBoolOperand (operandType: SemanticType) =
    match operandType with
        | SemBool -> true
        | _ -> false

// Whether lowering emits the operator as a primitive instruction at this operand type, by the
// same type sets its resolved operator emitters match.
let primitiveOperatorInstance (traitName: Str) (operandType: SemanticType) =
    match traitName with
        | "Add" -> isNumericOperand(operandType) || isTextOperand(operandType)
        | "Subtract" -> isNumericOperand(operandType)
        | "Multiply" -> isNumericOperand(operandType)
        | "Divide" -> isNumericOperand(operandType)
        | "Negate" -> isNumericOperand(operandType)
        | "Remainder" ->
            match operandType with
                | SemBigInt -> true
                | other -> isIntegralOperand(other)
        | "BitAnd" -> isIntegralOperand(operandType)
        | "BitOr" -> isIntegralOperand(operandType)
        | "BitXor" -> isIntegralOperand(operandType)
        | "BitwiseNot" -> isIntegralOperand(operandType)
        | "ShiftLeft" -> isIntegralOperand(operandType)
        | "ShiftRight" -> isIntegralOperand(operandType)
        | "Eq" -> isNumericOperand(operandType) || isTextOperand(operandType) || isScalarOperand(operandType)
        | "Ord" -> isNumericOperand(operandType) || isRuneOperand(operandType)
        | "Not" -> isBoolOperand(operandType)
        | _ -> false

let operatorInstanceOf (traitName: Str) (operandType: SemanticType) =
    match operandType with
        | SemVariable(_id) -> DictionaryInstance
        | other ->
            if primitiveOperatorInstance(traitName)(other)
            then PrimitiveInstance
            else DeclaredInstance

let recursive resolveArguments (substitution: MapTree(Int, SemanticType)) (arguments: List((Int, SemanticType))) =
    match arguments with
        | [] -> []
        | (variable, semanticType) :: tail -> (variable, applySubstitutionMap(substitution)(semanticType)) :: resolveArguments(substitution)(tail)

let recursive resolveTypes (substitution: MapTree(Int, SemanticType)) (semanticTypes: List(SemanticType)) =
    match semanticTypes with
        | [] -> []
        | head :: tail -> applySubstitutionMap(substitution)(head) :: resolveTypes(substitution)(tail)

let recursive resolveRequirements (substitution: MapTree(Int, SemanticType)) (requirements: List(TraitConstraint)) =
    match requirements with
        | [] -> []
        | TraitConstraint { traitName = traitName, typeArguments = typeArguments } :: tail -> TraitConstraint(traitName = traitName, typeArguments = resolveTypes(substitution)(typeArguments)) :: resolveRequirements(substitution)(tail)

let recursive resolveDemands substitution demands =
    match demands with
        | [] -> []
        | (TraitConstraint { traitName = name, typeArguments = arguments }, stage) :: rest -> (TraitConstraint(traitName = name, typeArguments = resolveTypes(substitution)(arguments)), stage) :: resolveDemands(substitution)(rest)

let settleNote (substitution: MapTree(Int, SemanticType)) (table: NodeTypeTable) (note: NodeTypeNote) =
    match note with
        | NodeTypeNote(node, semanticType) ->
            match Ashes.Collection.Map.getInt(node)(table.types) with
                | Some(_younger) -> table with repeatedCount = table.repeatedCount + 1
                | None ->
                    table with types = Ashes.Collection.Map.setInt(node)(applySubstitutionMap(substitution)(semanticType))(table.types), typeCount = table.typeCount + 1
        | NodeOperatorNote(node, traitName, operandType) ->
            match Ashes.Collection.Map.getInt(node)(table.operators) with
                | Some(_younger) -> table
                | None ->
                    let resolvedOperand = applySubstitutionMap(substitution)(operandType)
                    in
                        table with operators = Ashes.Collection.Map.setInt(node)(
                            NodeOperator(traitName = traitName, operandType = resolvedOperand, instance = operatorInstanceOf(traitName)(resolvedOperand))
                        )(table.operators)
        | NodeInstantiationNote(node, arguments, requirements, dictionaries, demands) ->
            match Ashes.Collection.Map.getInt(node)(table.instantiations) with
                | Some(_younger) -> table
                | None ->
                    table with instantiations = Ashes.Collection.Map.setInt(node)(
                        NodeInstantiation(arguments = resolveArguments(substitution)(arguments), requirements = resolveRequirements(substitution)(requirements), dictionaries = resolveRequirements(substitution)(dictionaries), evidenceDemands = resolveDemands(substitution)(demands))
                    )(table.instantiations)
        | NodeEvidenceProfileNote(node, stages) ->
            match Ashes.Collection.Map.getInt(node)(table.evidenceStages) with
                | Some(_younger) -> table
                | None -> table with evidenceStages = Ashes.Collection.Map.setInt(node)(stages)(table.evidenceStages)
        | NodeEvaluationRowNote(node, row) ->
            match Ashes.Collection.Map.getInt(node)(table.evaluationRows) with
                | Some(_younger) -> table
                | None ->
                    table with evaluationRows = Ashes.Collection.Map.setInt(node)(applySubstitutionMap(substitution)(row))(table.evaluationRows)
        | NodeDeclarationNote(node, name, scheme) ->
            table with declarations = Ashes.Collection.Map.setInt(node)(
                (name, applySchemeSubstitutionMap(substitution)(scheme))
            )(table.declarations)
        | NodeBindingNote(node, name, scheme) ->
            table with bindings = Ashes.Collection.Map.setInt(node)(
                (name, applySchemeSubstitutionMap(substitution)(scheme)) :: nodeBindingsAt(node)(table)
            )(table.bindings)

let recursive settleNotes (substitution: MapTree(Int, SemanticType)) (notes: List(NodeTypeNote)) (table: NodeTypeTable) =
    match notes with
        | [] -> table
        | head :: tail ->
            head
            |> settleNote(substitution)(table)
            |> settleNotes(substitution)(tail)

// Moves a finished declaration's notes into `table`, resolved under the declaration's
// `substitution`. `notes` holds the youngest first, so the first note met for a node is the one
// that stays.
let settleNodeNotes (substitution: List((Int, SemanticType))) (notes: List(NodeTypeNote)) (table: NodeTypeTable) =
    match notes with
        | [] -> table
        | _ ->
            settleNotes(extendSubstitutionMap(substitution)(Ashes.Collection.Map.empty))(notes)(table)

// What the references to generic bindings say about each quantified variable so far: the one
// closed type its closed arguments agree on, the variables two references instantiate differently,
// and the arguments still holding a variable, which another binding's settled variables may close.
// `changed` records whether a round closed any argument.
type ArgumentSettlement =
    | settled: MapTree(Int, SemanticType)
    | unsettled: MapTree(Int, Bool)
    | pending: MapTree(Int, List(SemanticType))
    | changed: Bool

let isUnsettled (variable: Int) (weighed: ArgumentSettlement) =
    match Ashes.Collection.Map.getInt(variable)(weighed.unsettled) with
        | Some(_unsettled) -> true
        | None -> false

let pendingArgumentsOf (variable: Int) (weighed: ArgumentSettlement) =
    match Ashes.Collection.Map.getInt(variable)(weighed.pending) with
        | Some(arguments) -> arguments
        | None -> []

// A closed argument settles its variable unless another reference instantiated it differently.
let weighClosedArgument (variable: Int) (semanticType: SemanticType) (weighed: ArgumentSettlement) =
    match Ashes.Collection.Map.getInt(variable)(weighed.settled) with
        | None -> weighed with settled = Ashes.Collection.Map.setInt(variable)(semanticType)(weighed.settled)
        | Some(previous) ->
            if previous == semanticType
            then weighed
            else weighed with unsettled = Ashes.Collection.Map.setInt(variable)(true)(weighed.unsettled)

// One instantiation's arguments weighed against the settlement so far: a closed argument is
// weighed at once, an open one waits until the variables it holds are settled.
let recursive weighArguments (arguments: List((Int, SemanticType))) (weighed: ArgumentSettlement) =
    match arguments with
        | [] -> weighed
        | (variable, semanticType) :: rest ->
            if isUnsettled(variable)(weighed)
            then weighArguments(rest)(weighed)
            else
                if freeTypeVariables(semanticType) == []
                then
                    weighed
                    |> weighClosedArgument(variable)(semanticType)
                    |> weighArguments(rest)
                else weighArguments(rest)((weighed with pending = Ashes.Collection.Map.setInt(variable)(semanticType :: pendingArgumentsOf(variable)(weighed))(weighed.pending)))

let weighInstantiation (weighed: ArgumentSettlement) (_node: Int) (instantiation: NodeInstantiation) =
    match instantiation with
        | NodeInstantiation { arguments = arguments } -> weighArguments(arguments)(weighed)

// A settled variable no reference contradicts and no open argument still waits on.
let keepFinal (weighed: ArgumentSettlement) (kept: MapTree(Int, SemanticType)) (variable: Int) (semanticType: SemanticType) =
    if isUnsettled(variable)(weighed) || pendingArgumentsOf(variable)(weighed) != []
    then kept
    else Ashes.Collection.Map.setInt(variable)(semanticType)(kept)

let finalSettlement (weighed: ArgumentSettlement) =
    Ashes.Collection.Map.foldLeft(keepFinal(weighed))(Ashes.Collection.Map.empty)(weighed.settled)

let withoutPendingArguments (variable: Int) (weighed: ArgumentSettlement) = weighed with pending = Ashes.Collection.Map.setInt(variable)([])(weighed.pending)

let settlementChanged (weighed: ArgumentSettlement) = weighed.changed

// A variable's open arguments read through the final settlement: each one that closes is weighed,
// the rest wait for a later round. A contradiction leaves the variable unsettled and waiting on
// nothing.
let recursive settleOpenArguments (variable: Int) (substitution: MapTree(Int, SemanticType)) (arguments: List(SemanticType)) (waiting: List(SemanticType)) (weighed: ArgumentSettlement) =
    match arguments with
        | [] -> weighed with pending = Ashes.Collection.Map.setInt(variable)(waiting)(weighed.pending)
        | argument :: rest ->
            let resolved = applySubstitutionMap(substitution)(argument)
            in
                if freeTypeVariables(resolved) == []
                then
                    let next = weighClosedArgument(variable)(resolved)(weighed)
                    in
                        if isUnsettled(variable)(next)
                        then withoutPendingArguments(variable)(next) with changed = true
                        else settleOpenArguments(variable)(substitution)(rest)(waiting)((next with changed = true))
                else settleOpenArguments(variable)(substitution)(rest)(resolved :: waiting)(weighed)

let settlePendingEntry (substitution: MapTree(Int, SemanticType)) (weighed: ArgumentSettlement) (variable: Int) (_snapshot: List(SemanticType)) =
    match pendingArgumentsOf(variable)(weighed) with
        | [] -> weighed
        | arguments ->
            if isUnsettled(variable)(weighed)
            then withoutPendingArguments(variable)(weighed)
            else settleOpenArguments(variable)(substitution)(arguments)([])(weighed)

// Rounds until no open argument closes: each round reads the open arguments through the variables
// settled for good so far, so a binding used only from another binding the program uses at one
// type is settled by that use too.
let recursive settlePendingArguments (weighed: ArgumentSettlement) =
    (let round =
        Ashes.Collection.Map.foldLeft(weighed
        |> finalSettlement
        |> settlePendingEntry)((weighed with changed = false))(weighed.pending)
    in
        if settlementChanged(round)
        then settlePendingArguments(round)
        else round)

// The closed type of every quantified variable that all references to its binding instantiate at
// that one type. A binding the program uses at a single type is settled by its uses there; so is a
// binding whose references instantiate it at variables of enclosing bindings that are themselves
// settled. A variable instantiated at two types, or at a type that stays open, is left out.
let settledInstantiationArguments (table: NodeTypeTable) =
    (let weighed = Ashes.Collection.Map.foldLeft(weighInstantiation)(ArgumentSettlement(settled = Ashes.Collection.Map.empty, unsettled = Ashes.Collection.Map.empty, pending = Ashes.Collection.Map.empty, changed = false))(table.instantiations)
    in
        weighed
        |> settlePendingArguments
        |> finalSettlement)
