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
)

// The trait an operator node maps to and the type of its operands. A concrete operand type names
// the instance; a variable is one the enclosing binding quantifies, whose requirements carry the
// dictionary.
type NodeOperator =
    | traitName: Str
    | operandType: SemanticType

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
                    table with operators = Ashes.Collection.Map.setInt(node)(
                        NodeOperator(traitName = traitName, operandType = applySubstitutionMap(substitution)(operandType))
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
