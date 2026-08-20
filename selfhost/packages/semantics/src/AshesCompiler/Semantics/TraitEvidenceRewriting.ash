// Elaborates constrained values into ordinary syntax with hidden dictionary parameters.
//
// Invariants:
// - Dictionary parameters, method fields, and supertrait fields follow the planned ABI order.
// - Qualified method references rewrite only when exactly one active dictionary provides the method.
// - Same-trait evidence at different types stays unresolved until typed lowering can distinguish it.

import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TraitEvidenceAbi
import AshesCompiler.Semantics.TraitEvidenceThreading
import Ashes.Collection.List.append as appendList
export (
    type TraitConstrainedReferenceRewriting(..),
    value rewriteTraitConstrainedValue,
    value rewriteTraitConstrainedReference,
)

type TraitMethodRewriteResolution =
    | TraitMethodRewriteMissing
    | TraitMethodRewriteUnique(Str)
    | TraitMethodRewriteAmbiguous

type TraitConstrainedReferenceRewriting =
    | expression: Maybe(Expr)
    | error: Maybe(TraitEvidenceForwardingError)

let recursive lastQualifiedNamePart parts =
    match parts with
        | [] -> ""
        | value :: [] -> value
        | _head :: tail -> lastQualifiedNamePart(tail)

let traitLeafName name =
    "."
    |> Ashes.Text.split(name)
    |> lastQualifiedNamePart

let traitEvidenceParameterName parameterIndex = "__trait_evidence_" + Ashes.Text.fromInt(parameterIndex)

let traitMethodParameterName parameterIndex path traitName methodName = "__trait_" + Ashes.Text.fromInt(parameterIndex) + "_" + path + "_" + traitLeafName(traitName) + "_" + methodName

let traitRawMethodParameterName parameterIndex path traitName methodName = traitMethodParameterName(parameterIndex)(path)(traitName)(methodName) + "_raw"

let traitSuperDictionaryParameterName parameterIndex path ordinal = "__trait_" + Ashes.Text.fromInt(parameterIndex) + "_" + path + "_super_" + Ashes.Text.fromInt(ordinal)

let recursive containsMethodName target methods =
    match methods with
        | [] -> false
        | head :: tail ->
            if target == head
            then true
            else containsMethodName(target)(tail)

let combineTraitMethodResolution left right =
    match (left, right) with
        | (TraitMethodRewriteMissing, resolution) -> resolution
        | (resolution, TraitMethodRewriteMissing) -> resolution
        | (TraitMethodRewriteAmbiguous, _resolution) -> TraitMethodRewriteAmbiguous
        | (_resolution, TraitMethodRewriteAmbiguous) -> TraitMethodRewriteAmbiguous
        | (TraitMethodRewriteUnique(_leftName), TraitMethodRewriteUnique(_rightName)) -> TraitMethodRewriteAmbiguous

let recursive findTraitMethodInSupertraits shapes qualifier methodName parentPath ordinal =
    match shapes with
        | [] -> TraitMethodRewriteMissing
        | head :: tail ->
            let path = parentPath + "_" + Ashes.Text.fromInt(ordinal)
            in
                ordinal + 1
                |> findTraitMethodInSupertraits(tail)(qualifier)(methodName)(parentPath)
                |> combineTraitMethodResolution(findTraitMethodInShape(head)(qualifier)(methodName)(path))
and findTraitMethodInShape shape qualifier methodName path =
    match shape with
        | TraitDictionaryAbiShape { parameterIndex = parameterIndex, constraint = TraitConstraint { traitName = traitName, typeArguments = _typeArguments }, methods = methods, supertraits = supertraits } ->
            let local =
                if qualifier == traitName
                then
                    if containsMethodName(methodName)(methods)
                    then
                        methodName
                        |> traitMethodParameterName(parameterIndex)(path)(traitName)
                        |> TraitMethodRewriteUnique
                    else TraitMethodRewriteMissing
                else TraitMethodRewriteMissing
            in
                0
                |> findTraitMethodInSupertraits(supertraits)(qualifier)(methodName)(path)
                |> combineTraitMethodResolution(local)

let recursive findTraitMethodInShapes shapes qualifier methodName =
    match shapes with
        | [] -> TraitMethodRewriteMissing
        | head :: tail ->
            methodName
            |> findTraitMethodInShapes(tail)(qualifier)
            |> combineTraitMethodResolution(findTraitMethodInShape(head)(qualifier)(methodName)("root"))

let recursive rewriteOptionalTraitMethodExpression shapes expression =
    match expression with
        | None -> None
        | Some(value) ->
            value
            |> rewriteTraitMethodReferences(shapes)
            |> Some
and rewriteTraitMethodExpressions shapes expressions =
    match expressions with
        | [] -> []
        | head :: tail -> rewriteTraitMethodReferences(shapes)(head) :: rewriteTraitMethodExpressions(shapes)(tail)
and rewriteTraitMethodFields shapes fields =
    match fields with
        | [] -> []
        | (name, value) :: tail -> (name, rewriteTraitMethodReferences(shapes)(value)) :: rewriteTraitMethodFields(shapes)(tail)
and rewriteTraitMethodCases shapes cases =
    match cases with
        | [] -> []
        | (pattern, body, guard) :: tail -> (pattern, rewriteTraitMethodReferences(shapes)(body), rewriteOptionalTraitMethodExpression(shapes)(guard)) :: rewriteTraitMethodCases(shapes)(tail)
and rewriteTraitMethodHandlerArms shapes arms =
    match arms with
        | [] -> []
        | (instance, operation, patterns, body) :: tail -> (instance, operation, patterns, rewriteTraitMethodReferences(shapes)(body)) :: rewriteTraitMethodHandlerArms(shapes)(tail)
and rewriteTraitMethodReferences shapes expression =
    match expression with
        | ExprAt(span, inner) ->
            inner
            |> rewriteTraitMethodReferences(shapes)
            |> ExprAt(span)
        | ExprQualifiedVar(qualifier, methodName) ->
            match findTraitMethodInShapes(shapes)(qualifier)(methodName) with
                | TraitMethodRewriteUnique(parameterName) -> ExprVar(parameterName)
                | _ -> expression
        | ExprAdd(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprAdd(rewriteTraitMethodReferences(shapes)(left))
        | ExprSubtract(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprSubtract(rewriteTraitMethodReferences(shapes)(left))
        | ExprMultiply(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprMultiply(rewriteTraitMethodReferences(shapes)(left))
        | ExprDivide(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprDivide(rewriteTraitMethodReferences(shapes)(left))
        | ExprModulo(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprModulo(rewriteTraitMethodReferences(shapes)(left))
        | ExprBitwiseAnd(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprBitwiseAnd(rewriteTraitMethodReferences(shapes)(left))
        | ExprBitwiseOr(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprBitwiseOr(rewriteTraitMethodReferences(shapes)(left))
        | ExprBitwiseXor(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprBitwiseXor(rewriteTraitMethodReferences(shapes)(left))
        | ExprShiftLeft(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprShiftLeft(rewriteTraitMethodReferences(shapes)(left))
        | ExprShiftRight(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprShiftRight(rewriteTraitMethodReferences(shapes)(left))
        | ExprBitwiseNot(operand) ->
            operand
            |> rewriteTraitMethodReferences(shapes)
            |> ExprBitwiseNot
        | ExprLogicalNot(operand) ->
            operand
            |> rewriteTraitMethodReferences(shapes)
            |> ExprLogicalNot
        | ExprGreaterThan(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprGreaterThan(rewriteTraitMethodReferences(shapes)(left))
        | ExprLessThan(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprLessThan(rewriteTraitMethodReferences(shapes)(left))
        | ExprGreaterOrEqual(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprGreaterOrEqual(rewriteTraitMethodReferences(shapes)(left))
        | ExprLessOrEqual(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprLessOrEqual(rewriteTraitMethodReferences(shapes)(left))
        | ExprEqual(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprEqual(rewriteTraitMethodReferences(shapes)(left))
        | ExprNotEqual(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprNotEqual(rewriteTraitMethodReferences(shapes)(left))
        | ExprResultPipe(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprResultPipe(rewriteTraitMethodReferences(shapes)(left))
        | ExprResultMapErrorPipe(left, right) ->
            right
            |> rewriteTraitMethodReferences(shapes)
            |> ExprResultMapErrorPipe(rewriteTraitMethodReferences(shapes)(left))
        | ExprLet(name, value, body, parameters, annotation, requirements) ->
            ExprLet(name)(rewriteTraitMethodReferences(shapes)(value))(rewriteTraitMethodReferences(shapes)(body))(parameters)(annotation)(requirements)
        | ExprLetResult(name, value, body) ->
            body
            |> rewriteTraitMethodReferences(shapes)
            |> ExprLetResult(name)(rewriteTraitMethodReferences(shapes)(value))
        | ExprLetRecursive(name, value, body, parameters, annotation, requirements) ->
            ExprLetRecursive(name)(rewriteTraitMethodReferences(shapes)(value))(rewriteTraitMethodReferences(shapes)(body))(parameters)(annotation)(requirements)
        | ExprIf(condition, thenBranch, elseBranch) ->
            elseBranch
            |> rewriteTraitMethodReferences(shapes)
            |> ExprIf(rewriteTraitMethodReferences(shapes)(condition))(rewriteTraitMethodReferences(shapes)(thenBranch))
        | ExprLambda(name, body, annotation) ->
            ExprLambda(name)(rewriteTraitMethodReferences(shapes)(body))(annotation)
        | ExprCall(function, argument, whitespace) ->
            ExprCall(rewriteTraitMethodReferences(shapes)(function))(rewriteTraitMethodReferences(shapes)(argument))(whitespace)
        | ExprTuple(elements) ->
            elements
            |> rewriteTraitMethodExpressions(shapes)
            |> ExprTuple
        | ExprList(elements) ->
            elements
            |> rewriteTraitMethodExpressions(shapes)
            |> ExprList
        | ExprCons(head, tail) ->
            tail
            |> rewriteTraitMethodReferences(shapes)
            |> ExprCons(rewriteTraitMethodReferences(shapes)(head))
        | ExprMatch(value, cases, offset) ->
            ExprMatch(rewriteTraitMethodReferences(shapes)(value))(rewriteTraitMethodCases(shapes)(cases))(offset)
        | ExprAwait(task) ->
            task
            |> rewriteTraitMethodReferences(shapes)
            |> ExprAwait
        | ExprRecord(name, fields) ->
            fields
            |> rewriteTraitMethodFields(shapes)
            |> ExprRecord(name)
        | ExprRecordUpdate(value, fields) ->
            fields
            |> rewriteTraitMethodFields(shapes)
            |> ExprRecordUpdate(rewriteTraitMethodReferences(shapes)(value))
        | ExprPerform(operation) ->
            operation
            |> rewriteTraitMethodReferences(shapes)
            |> ExprPerform
        | ExprHandle(body, arms) ->
            arms
            |> rewriteTraitMethodHandlerArms(shapes)
            |> ExprHandle(rewriteTraitMethodReferences(shapes)(body))
        | _ -> expression

let recursive traitMethodPatterns parameterIndex path traitName methods =
    match methods with
        | [] -> []
        | methodName :: tail ->
            PatternVar(traitRawMethodParameterName(parameterIndex)(path)(traitName)(methodName)) :: traitMethodPatterns(parameterIndex)(path)(traitName)(tail)

let recursive traitSupertraitPatterns parameterIndex path supertraits ordinal =
    match supertraits with
        | [] -> []
        | _head :: tail ->
            PatternVar(traitSuperDictionaryParameterName(parameterIndex)(path)(ordinal)) :: traitSupertraitPatterns(parameterIndex)(path)(tail)(ordinal + 1)

let traitDictionaryPattern parameterIndex path traitName methods supertraits =
    match 0
    |> traitSupertraitPatterns(parameterIndex)(path)(supertraits)
    |> appendList(traitMethodPatterns(parameterIndex)(path)(traitName)(methods)) with
        | field :: [] -> field
        | fields -> PatternTuple(fields)

let recursive bindTraitMethods parameterIndex path traitName methods body =
    match methods with
        | [] -> body
        | methodName :: tail ->
            ExprLet(traitMethodParameterName(parameterIndex)(path)(traitName)(methodName))(methodName
            |> traitRawMethodParameterName(parameterIndex)(path)(traitName)
            |> ExprVar)(bindTraitMethods(parameterIndex)(path)(traitName)(tail)(body))([])(None)([])

let recursive destructureTraitSupertraits parameterIndex parentPath supertraits ordinal body =
    match supertraits with
        | [] -> body
        | head :: tail ->
            let path = parentPath + "_" + Ashes.Text.fromInt(ordinal)
            in
                body
                |> destructureTraitSupertraits(parameterIndex)(parentPath)(tail)(ordinal + 1)
                |> destructureTraitDictionary(head)(path)(ordinal
                |> traitSuperDictionaryParameterName(parameterIndex)(parentPath)
                |> ExprVar)
and destructureTraitDictionary shape path dictionary body =
    match shape with
        | TraitDictionaryAbiShape { parameterIndex = parameterIndex, constraint = TraitConstraint { traitName = traitName, typeArguments = _typeArguments }, methods = methods, supertraits = supertraits } ->
            let withSupertraits = destructureTraitSupertraits(parameterIndex)(path)(supertraits)(0)(body)
            in
                let withMethods = bindTraitMethods(parameterIndex)(path)(traitName)(methods)(withSupertraits)
                in ExprMatch(dictionary)([(traitDictionaryPattern(parameterIndex)(path)(traitName)(methods)(supertraits), withMethods, None)])(None)

let recursive prependTraitDictionaryParameters shapes body =
    match shapes with
        | [] -> body
        | (TraitDictionaryAbiShape { parameterIndex = parameterIndex, constraint = _constraint, methods = _methods, supertraits = _supertraits } as shape) :: tail ->
            let parameterName = traitEvidenceParameterName(parameterIndex)
            in
                ExprLambda(parameterName)(body
                |> prependTraitDictionaryParameters(tail)
                |> destructureTraitDictionary(shape)("root")(ExprVar(parameterName)))(None)

let rewriteTraitConstrainedValue value constraints environment =
    match planTraitEvidenceAbi(constraints)(environment) with
        | [] -> value
        | shapes ->
            value
            |> rewriteTraitMethodReferences(shapes)
            |> prependTraitDictionaryParameters(shapes)

let recursive traitForwardedEvidenceNameFrom rootParameterIndex parentPath path =
    match path with
        | [] -> traitEvidenceParameterName(rootParameterIndex)
        | ordinal :: tail ->
            match tail with
                | [] -> traitSuperDictionaryParameterName(rootParameterIndex)(parentPath)(ordinal)
                | _ -> traitForwardedEvidenceNameFrom(rootParameterIndex)(parentPath + "_" + Ashes.Text.fromInt(ordinal))(tail)

let traitForwardedEvidenceName forwarding =
    match forwarding with
        | TraitEvidenceForwarding { rootParameterIndex = rootParameterIndex, supertraitPath = path } -> traitForwardedEvidenceNameFrom(rootParameterIndex)("root")(path)

let recursive applyForwardedTraitEvidence expression arguments =
    match arguments with
        | [] -> expression
        | TraitEvidenceForwardingArgument { shape = _shape, forwarding = forwarding } :: tail ->
            applyForwardedTraitEvidence(ExprCall(expression)(forwarding
            |> traitForwardedEvidenceName
            |> ExprVar)(false))(tail)

let rewriteTraitConstrainedReference reference requiredConstraints activeConstraints environment =
    match planTraitEvidenceForwarding(requiredConstraints)(activeConstraints)(environment) with
        | TraitEvidenceForwardingPlanning { arguments = arguments, error = None } ->
            TraitConstrainedReferenceRewriting(expression = arguments
            |> applyForwardedTraitEvidence(reference)
            |> Some, error = None)
        | TraitEvidenceForwardingPlanning { arguments = _arguments, error = Some(error) } -> TraitConstrainedReferenceRewriting(expression = None, error = Some(error))
