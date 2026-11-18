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

type NodeTypeTable =
    | types: MapTree(Int, SemanticType)
    | operators: MapTree(Int, NodeOperator)
    | instantiations: MapTree(Int, NodeInstantiation)
    // The names a node brings into scope: at a `let` its binding, at an arm body the arm's pattern
    // variables.
    | bindings: MapTree(Int, List((Str, TypeScheme)))
    // A top-level binding's name and scheme, keyed by the root node of its value.
    | declarations: MapTree(Int, (Str, TypeScheme))
    // How many nodes have a type, and how many times a node that had one was given another.
    | typeCount: Int
    | repeatedCount: Int

let emptyNodeTypeTable : NodeTypeTable = NodeTypeTable(types = Ashes.Collection.Map.empty, operators = Ashes.Collection.Map.empty, instantiations = Ashes.Collection.Map.empty, bindings = Ashes.Collection.Map.empty, declarations = Ashes.Collection.Map.empty, typeCount = 0, repeatedCount = 0)

let nodeTypeAt (node: Int) (table: NodeTypeTable) = Ashes.Collection.Map.getInt(node)(table.types)

let nodeOperatorAt (node: Int) (table: NodeTypeTable) = Ashes.Collection.Map.getInt(node)(table.operators)

let nodeInstantiationAt (node: Int) (table: NodeTypeTable) = Ashes.Collection.Map.getInt(node)(table.instantiations)

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
        | NodeInstantiationNote(node, arguments, requirements, dictionaries) ->
            match Ashes.Collection.Map.getInt(node)(table.instantiations) with
                | Some(_younger) -> table
                | None ->
                    table with instantiations = Ashes.Collection.Map.setInt(node)(
                        NodeInstantiation(arguments = resolveArguments(substitution)(arguments), requirements = resolveRequirements(substitution)(requirements), dictionaries = resolveRequirements(substitution)(dictionaries))
                    )(table.instantiations)
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

// One instantiation's arguments weighed against the settled arguments so far: a closed argument
// settles its variable unless another reference instantiated it differently; an open one, or two
// references that disagree, leave it unsettled.
let recursive weighArguments (arguments: List((Int, SemanticType))) (weighed: (MapTree(Int, SemanticType), MapTree(Int, Bool))) =
    match (arguments, weighed) with
        | ([], _) -> weighed
        | ((variable, semanticType) :: rest, (settled, unsettled)) ->
            match Ashes.Collection.Map.getInt(variable)(unsettled) with
                | Some(_unsettled) -> weighArguments(rest)(weighed)
                | None ->
                    if freeTypeVariables(semanticType) == []
                    then
                        match Ashes.Collection.Map.getInt(variable)(settled) with
                            | None -> weighArguments(rest)((Ashes.Collection.Map.setInt(variable)(semanticType)(settled), unsettled))
                            | Some(previous) ->
                                if previous == semanticType
                                then weighArguments(rest)(weighed)
                                else weighArguments(rest)((settled, Ashes.Collection.Map.setInt(variable)(true)(unsettled)))
                    else weighArguments(rest)((settled, Ashes.Collection.Map.setInt(variable)(true)(unsettled)))

let weighInstantiation (weighed: (MapTree(Int, SemanticType), MapTree(Int, Bool))) (_node: Int) (instantiation: NodeInstantiation) =
    match instantiation with
        | NodeInstantiation { arguments = arguments } -> weighArguments(arguments)(weighed)

let keepSettled (unsettled: MapTree(Int, Bool)) (kept: MapTree(Int, SemanticType)) (variable: Int) (semanticType: SemanticType) =
    match Ashes.Collection.Map.getInt(variable)(unsettled) with
        | Some(_unsettled) -> kept
        | None -> Ashes.Collection.Map.setInt(variable)(semanticType)(kept)

// The closed type of every quantified variable that all references to its binding instantiate at
// that one type. A binding the program uses at a single type is settled by its uses there; a
// variable instantiated at two types, or at a type still holding a variable, is left out.
let settledInstantiationArguments (table: NodeTypeTable) =
    match Ashes.Collection.Map.foldLeft(weighInstantiation)((Ashes.Collection.Map.empty, Ashes.Collection.Map.empty))(table.instantiations) with
        | (settled, unsettled) ->
            Ashes.Collection.Map.foldLeft(keepSettled(unsettled))(Ashes.Collection.Map.empty)(settled)
