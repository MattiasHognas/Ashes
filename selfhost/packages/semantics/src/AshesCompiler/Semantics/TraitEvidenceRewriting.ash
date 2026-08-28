// Elaborates constrained values into ordinary syntax with hidden dictionary parameters.
//
// Invariants:
// - Dictionary parameters, method fields, and supertrait fields follow the planned ABI order.
// - Qualified method references rewrite only when exactly one active dictionary provides the method.
// - Same-trait evidence at different types stays unresolved until typed lowering can distinguish it.

import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.ModuleReferenceRewriting.patternNames
import AshesCompiler.Semantics.ModuleReferenceRewriting.patternListNames
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.TraitEvidenceAbi
import AshesCompiler.Semantics.TraitEvidenceThreading
import Ashes.Collection.List.append as appendList
export (
    type TraitConstrainedReferenceRewriting(..),
    type TraitConstrainedTopLevelValueRewriting(..),
    value selectedTraitMethodBindingName,
    value rewriteTraitConstrainedValue,
    value rewriteTraitConstrainedReference,
    value rewriteSelectedTraitMethodImplementation,
    value bindingTraitConstraints,
    value rewriteTraitConstrainedTopLevelValue,
)

type TraitMethodRewriteResolution =
    | TraitMethodRewriteMissing
    | TraitMethodRewriteUnique(Str)
    | TraitMethodRewriteAmbiguous

type TraitMethodRewriteMode =
    | TraitMethodRewriteActive(List(TraitDictionaryAbiShape))
    | TraitMethodRewriteSelected(Str, Str)

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

let traitMethodParameterName parameterIndex path traitName methodName =
    "__trait_" + Ashes.Text.fromInt(
        parameterIndex
    ) + "_" + path + "_" + traitLeafName(traitName) + "_" + methodName

let traitRawMethodParameterName parameterIndex path traitName methodName =
    traitMethodParameterName(
        parameterIndex,
        path,
        traitName,
        methodName
    ) + "_raw"

let traitSuperDictionaryParameterName parameterIndex path ordinal =
    "__trait_" + Ashes.Text.fromInt(
        parameterIndex
    ) + "_" + path + "_super_" + Ashes.Text.fromInt(ordinal)

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

let selectedTraitMethodBindingName traitName methodName =
    "__trait_selected_" + traitLeafName(
        traitName
    ) + "_" + methodName

let selectedTraitSelfBindingName traitName methodName = "__trait_impl_" + traitLeafName(traitName) + "_" + methodName

let resolveTraitMethodRewrite mode qualifier methodName =
    match mode with
        | TraitMethodRewriteActive(shapes) -> findTraitMethodInShapes(shapes)(qualifier)(methodName)
        | TraitMethodRewriteSelected(traitName, selectedMethodName) ->
            if qualifier == traitName
            then
                if methodName == selectedMethodName
                then
                    methodName
                    |> selectedTraitSelfBindingName(traitName)
                    |> TraitMethodRewriteUnique
                else
                    methodName
                    |> selectedTraitMethodBindingName(traitName)
                    |> TraitMethodRewriteUnique
            else TraitMethodRewriteMissing

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
        | (name, value) :: tail ->
            (name, rewriteTraitMethodReferences(
                shapes,
                value
            )) :: rewriteTraitMethodFields(shapes)(tail)
and rewriteTraitMethodCases shapes cases =
    match cases with
        | [] -> []
        | (pattern, body, guard) :: tail ->
            (pattern, rewriteTraitMethodReferences(
                shapes,
                body
            ), rewriteOptionalTraitMethodExpression(
                shapes,
                guard
            )) :: rewriteTraitMethodCases(shapes)(tail)
and rewriteTraitMethodHandlerArms shapes arms =
    match arms with
        | [] -> []
        | (instance, operation, patterns, body) :: tail ->
            (instance, operation, patterns, rewriteTraitMethodReferences(
                shapes,
                body
            )) :: rewriteTraitMethodHandlerArms(
                shapes,
                tail
            )
and rewriteTraitMethodReferences shapes expression =
    match expression with
        | ExprAt(span, inner) ->
            inner
            |> rewriteTraitMethodReferences(shapes)
            |> ExprAt(span)
        | ExprQualifiedVar(qualifier, methodName) ->
            match resolveTraitMethodRewrite(shapes)(qualifier)(methodName) with
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
            ExprLet(
                name,
                rewriteTraitMethodReferences(shapes)(value),
                rewriteTraitMethodReferences(shapes)(body),
                parameters,
                annotation,
                requirements
            )
        | ExprLetResult(name, value, body) ->
            body
            |> rewriteTraitMethodReferences(shapes)
            |> ExprLetResult(name)(rewriteTraitMethodReferences(shapes)(value))
        | ExprLetRecursive(name, value, body, parameters, annotation, requirements) ->
            ExprLetRecursive(
                name,
                rewriteTraitMethodReferences(shapes)(value),
                rewriteTraitMethodReferences(shapes)(body),
                parameters,
                annotation,
                requirements
            )
        | ExprIf(condition, thenBranch, elseBranch) ->
            elseBranch
            |> rewriteTraitMethodReferences(shapes)
            |> ExprIf(rewriteTraitMethodReferences(shapes)(condition))(rewriteTraitMethodReferences(shapes)(thenBranch))
        | ExprLambda(name, body, annotation) ->
            ExprLambda(name)(rewriteTraitMethodReferences(shapes)(body))(annotation)
        | ExprCall(function, argument, whitespace, layout) ->
            ExprCall(
                rewriteTraitMethodReferences(shapes)(function),
                rewriteTraitMethodReferences(shapes)(argument),
                whitespace,
                layout
            )
        | ExprTuple(elements) ->
            elements
            |> rewriteTraitMethodExpressions(shapes)
            |> ExprTuple
        | ExprList(elements, isMultiline) ->
            elements
            |> rewriteTraitMethodExpressions(shapes)
            |> (given (rewrittenElements) -> ExprList(rewrittenElements)(isMultiline))
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
        | ExprRecord(name, fields, isMultiline) ->
            fields
            |> rewriteTraitMethodFields(shapes)
            |> (given (rewritten) -> ExprRecord(name)(rewritten)(isMultiline))
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
            PatternVar(
                traitRawMethodParameterName(parameterIndex)(path)(traitName)(methodName)
            ) :: traitMethodPatterns(parameterIndex)(path)(traitName)(tail)

let recursive traitSupertraitPatterns parameterIndex path supertraits ordinal =
    match supertraits with
        | [] -> []
        | _head :: tail ->
            PatternVar(
                traitSuperDictionaryParameterName(parameterIndex)(path)(ordinal)
            ) :: traitSupertraitPatterns(parameterIndex)(path)(tail)(ordinal + 1)

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
                in
                    ExprMatch(
                        dictionary,
                        [(traitDictionaryPattern(
                            parameterIndex,
                            path,
                            traitName,
                            methods,
                            supertraits
                        ), withMethods, None)],
                        None
                    )

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
            |> rewriteTraitMethodReferences(TraitMethodRewriteActive(shapes))
            |> prependTraitDictionaryParameters(shapes)

let rewriteSelectedTraitMethodImplementation traitName methodName implementation =
    (let rewritten =
        rewriteTraitMethodReferences(TraitMethodRewriteSelected(traitName)(methodName))(implementation)
    in
        if expressionDependsOnTraitMethod(traitName)(methodName)(implementation)
        then
            let selfName = selectedTraitSelfBindingName(traitName)(methodName)
            in ExprLetRecursive(selfName)(rewritten)(ExprVar(selfName))([])(None)([])
        else rewritten)

let recursive traitForwardedEvidenceNameFrom rootParameterIndex parentPath path =
    match path with
        | [] -> traitEvidenceParameterName(rootParameterIndex)
        | ordinal :: tail ->
            match tail with
                | [] -> traitSuperDictionaryParameterName(rootParameterIndex)(parentPath)(ordinal)
                | _ ->
                    traitForwardedEvidenceNameFrom(
                        rootParameterIndex,
                        parentPath + "_" + Ashes.Text.fromInt(ordinal),
                        tail
                    )

let traitForwardedEvidenceName forwarding =
    match forwarding with
        | TraitEvidenceForwarding { rootParameterIndex = rootParameterIndex, supertraitPath = path } ->
            traitForwardedEvidenceNameFrom(
                rootParameterIndex,
                "root",
                path
            )

let recursive applyForwardedTraitEvidence expression arguments =
    match arguments with
        | [] -> expression
        | TraitEvidenceForwardingArgument { shape = _shape, forwarding = forwarding } :: tail ->
            applyForwardedTraitEvidence(ExprCall(expression)(forwarding
            |> traitForwardedEvidenceName
            |> ExprVar)(false)(callArgumentsInline))(tail)

let rewriteTraitConstrainedReference reference requiredConstraints activeConstraints environment =
    match planTraitEvidenceForwarding(requiredConstraints)(activeConstraints)(environment) with
        | TraitEvidenceForwardingPlanning { arguments = arguments, error = None } ->
            TraitConstrainedReferenceRewriting(expression = arguments
            |> applyForwardedTraitEvidence(reference)
            |> Some, error = None)
        | TraitEvidenceForwardingPlanning { arguments = _arguments, error = Some(error) } ->
            TraitConstrainedReferenceRewriting(expression = None, error = Some(
                error
            ))

// Call-site trait-evidence forwarding: walks a lowered top-level value's body and, at every direct
// application of another top-level binding that itself carries trait constraints, forwards
// evidence from this binding's own active dictionary parameters via rewriteTraitConstrainedReference.
// This is the piece rewriteTraitConstrainedValue deliberately leaves undone (it only injects a
// binding's OWN dictionary parameters); pairing the two lets a constrained caller
// (`wrapper : a -> Str requires {Greet(a)}`) call another constrained callee (`describe`) with the
// caller's own evidence, matching stage-0's FindActiveTraitDictionaryParameter forwarding
// (Lowering.TraitEvidence.cs).
//
// A callee reference only qualifies when its argument is (syntactically) one of `ownParameters` —
// a name bound by this top-level binding's OWN outermost curried lambda chain
// (outermostLambdaParameterNames), fixed once per binding, never grown as the walk descends. This
// guards against a name that is merely equal-by-trait-name but semantically unrelated: `bindingTraitConstraints`
// reads a callee's raw, uninstantiated scheme constraint (e.g. `describe`'s own `Greet(a)`), which
// carries no per-call-site instantiation info, so two calls to the same trait can never be told
// apart by constraint shape alone — an active dictionary always looks like a match if only the
// trait name is compared. Restricting to the caller's own parameter is a sound (if conservative)
// proxy: only an argument that IS that parameter can actually carry the SAME still-abstract type
// the caller's dictionary was built for. A bare reference to a constrained binding (not applied to
// a qualifying argument, e.g. passed as a first-class value, or applied to a literal/local/concrete
// expression) is therefore left untouched rather than guessed at — under-approximating rather than
// ever forwarding evidence that doesn't actually apply. `locals` tracks lexical shadowing exactly
// as ModuleReferenceRewriting's rewriteExpression does (a same-named lambda/let/pattern binding
// always wins over a top-level constrained binding of the same name).
let recursive outermostLambdaParameterNames expression =
    match expression with
        | ExprAt(_span, inner) -> outermostLambdaParameterNames(inner)
        | ExprLambda(name, body, _annotation) -> name :: outermostLambdaParameterNames(body)
        | _ -> []

let recursive stripTraitCallSiteAt expression =
    match expression with
        | ExprAt(_span, inner) -> stripTraitCallSiteAt(inner)
        | _ -> expression

type TraitReferenceRewriting =
    | expression: Expr
    | error: Maybe(TraitEvidenceForwardingError)

type TraitReferenceRewritingOptional =
    | expression: Maybe(Expr)
    | error: Maybe(TraitEvidenceForwardingError)

type TraitReferenceRewritingList =
    | expressions: List(Expr)
    | error: Maybe(TraitEvidenceForwardingError)

type TraitReferenceRewritingFields =
    | fields: List((Str, Expr))
    | error: Maybe(TraitEvidenceForwardingError)

type TraitReferenceRewritingCases =
    | cases: List((Pattern, Expr, Maybe(Expr)))
    | error: Maybe(TraitEvidenceForwardingError)

type TraitReferenceRewritingArms =
    | arms: List((Maybe(Str), Str, List(Pattern), Expr))
    | error: Maybe(TraitEvidenceForwardingError)

let firstTraitEvidenceError left right =
    match left with
        | Some(error) -> Some(error)
        | None -> right

// Pure, no lowering: the trait constraints attached to a binding's own generalized TypeScheme, as
// recorded by inference in the TypeEnvironment's flat (name, TypeScheme) binding list. Moved here
// (from CoreLowering.ash) so both the value-side elaborator above and the call-site forwarding
// walker below can share one lookup; CoreLowering imports it back.
let recursive findBindingScheme name bindings =
    match bindings with
        | [] -> None
        | (candidate, scheme) :: rest ->
            if name == candidate
            then Some(scheme)
            else findBindingScheme(name)(rest)

let bindingTraitConstraints name environment =
    match environment with
        | TypeEnvironment { bindings = bindings } ->
            match findBindingScheme(name)(bindings) with
                | Some(TypeScheme { constraints = constraints }) -> constraints
                | None -> []

let recursive rewriteTraitCallSiteOptionalReference activeConstraints ownParameters locals environment expression =
    match expression with
        | None -> TraitReferenceRewritingOptional(expression = None, error = None)
        | Some(value) ->
            match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(locals)(environment)(value) with
                | TraitReferenceRewriting { expression = rewritten, error = error } -> TraitReferenceRewritingOptional(expression = Some(rewritten), error = error)
and rewriteTraitCallSiteReferenceList activeConstraints ownParameters locals environment expressions =
    match expressions with
        | [] -> TraitReferenceRewritingList(expressions = [], error = None)
        | head :: tail ->
            match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(locals)(environment)(head) with
                | TraitReferenceRewriting { expression = headExpr, error = headError } ->
                    match rewriteTraitCallSiteReferenceList(activeConstraints)(ownParameters)(locals)(environment)(tail) with
                        | TraitReferenceRewritingList { expressions = tailExprs, error = tailError } -> TraitReferenceRewritingList(expressions = headExpr :: tailExprs, error = firstTraitEvidenceError(headError)(tailError))
and rewriteTraitCallSiteReferenceFields activeConstraints ownParameters locals environment fields =
    match fields with
        | [] -> TraitReferenceRewritingFields(fields = [], error = None)
        | (name, value) :: tail ->
            match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(locals)(environment)(value) with
                | TraitReferenceRewriting { expression = valueExpr, error = valueError } ->
                    match rewriteTraitCallSiteReferenceFields(activeConstraints)(ownParameters)(locals)(environment)(tail) with
                        | TraitReferenceRewritingFields { fields = tailFields, error = tailError } -> TraitReferenceRewritingFields(fields = (name, valueExpr) :: tailFields, error = firstTraitEvidenceError(valueError)(tailError))
and rewriteTraitCallSiteReferenceCases activeConstraints ownParameters locals environment cases =
    match cases with
        | [] -> TraitReferenceRewritingCases(cases = [], error = None)
        | (pattern, body, guard) :: tail ->
            let caseLocals =
                appendList(patternNames(pattern))(locals)
            in
                match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(caseLocals)(environment)(body) with
                    | TraitReferenceRewriting { expression = bodyExpr, error = bodyError } ->
                        match rewriteTraitCallSiteOptionalReference(activeConstraints)(ownParameters)(caseLocals)(environment)(guard) with
                            | TraitReferenceRewritingOptional { expression = guardExpr, error = guardError } ->
                                match rewriteTraitCallSiteReferenceCases(activeConstraints)(ownParameters)(locals)(environment)(tail) with
                                    | TraitReferenceRewritingCases { cases = tailCases, error = tailError } ->
                                        TraitReferenceRewritingCases(
                                            cases = (pattern, bodyExpr, guardExpr) :: tailCases,
                                            error = firstTraitEvidenceError(firstTraitEvidenceError(bodyError)(guardError))(tailError)
                                        )
and rewriteTraitCallSiteReferenceArms activeConstraints ownParameters locals environment arms =
    match arms with
        | [] -> TraitReferenceRewritingArms(arms = [], error = None)
        | (instance, operation, patterns, body) :: tail ->
            let armLocals =
                appendList(patternListNames(patterns))(locals)
            in
                match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(armLocals)(environment)(body) with
                    | TraitReferenceRewriting { expression = bodyExpr, error = bodyError } ->
                        match rewriteTraitCallSiteReferenceArms(activeConstraints)(ownParameters)(locals)(environment)(tail) with
                            | TraitReferenceRewritingArms { arms = tailArms, error = tailError } ->
                                TraitReferenceRewritingArms(
                                    arms = (instance, operation, patterns, bodyExpr) :: tailArms,
                                    error = firstTraitEvidenceError(bodyError)(tailError)
                                )
and rewriteTraitCallSiteBinaryOperand activeConstraints ownParameters locals environment left right =
    match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(locals)(environment)(left) with
        | TraitReferenceRewriting { expression = leftExpr, error = leftError } ->
            match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(locals)(environment)(right) with
                | TraitReferenceRewriting { expression = rightExpr, error = rightError } -> (leftExpr, rightExpr, firstTraitEvidenceError(leftError)(rightError))
and rewriteTraitCallSiteForwardedCall activeConstraints ownParameters locals environment calleeName function argument whitespace layout =
    match bindingTraitConstraints(calleeName)(environment) with
        | [] -> None
        | requiredConstraints ->
            match stripTraitCallSiteAt(argument) with
                | ExprVar(argumentName) ->
                    if containsMethodName(argumentName)(ownParameters)
                    then
                        match rewriteTraitConstrainedReference(function)(requiredConstraints)(activeConstraints)(environment) with
                            | TraitConstrainedReferenceRewriting { expression = Some(rewrittenFunction), error = None } -> Some(TraitReferenceRewriting(expression = ExprCall(rewrittenFunction)(argument)(whitespace)(layout), error = None))
                            | TraitConstrainedReferenceRewriting { expression = _rewrittenFunction, error = Some(error) } -> Some(TraitReferenceRewriting(expression = ExprCall(function)(argument)(whitespace)(layout), error = Some(error)))
                            | TraitConstrainedReferenceRewriting { expression = None, error = None } -> None
                    else None
                | _ -> None
and rewriteTraitCallSiteReferences activeConstraints ownParameters locals environment expression =
    match expression with
        | ExprAt(span, inner) ->
            match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(locals)(environment)(inner) with
                | TraitReferenceRewriting { expression = innerExpr, error = error } -> TraitReferenceRewriting(expression = ExprAt(span)(innerExpr), error = error)
        | ExprAdd(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprAdd(leftExpr)(rightExpr), error = error)
        | ExprSubtract(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprSubtract(leftExpr)(rightExpr), error = error)
        | ExprMultiply(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprMultiply(leftExpr)(rightExpr), error = error)
        | ExprDivide(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprDivide(leftExpr)(rightExpr), error = error)
        | ExprModulo(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprModulo(leftExpr)(rightExpr), error = error)
        | ExprBitwiseAnd(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprBitwiseAnd(leftExpr)(rightExpr), error = error)
        | ExprBitwiseOr(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprBitwiseOr(leftExpr)(rightExpr), error = error)
        | ExprBitwiseXor(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprBitwiseXor(leftExpr)(rightExpr), error = error)
        | ExprShiftLeft(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprShiftLeft(leftExpr)(rightExpr), error = error)
        | ExprShiftRight(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprShiftRight(leftExpr)(rightExpr), error = error)
        | ExprBitwiseNot(operand) ->
            match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(locals)(environment)(operand) with
                | TraitReferenceRewriting { expression = operandExpr, error = error } -> TraitReferenceRewriting(expression = ExprBitwiseNot(operandExpr), error = error)
        | ExprLogicalNot(operand) ->
            match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(locals)(environment)(operand) with
                | TraitReferenceRewriting { expression = operandExpr, error = error } -> TraitReferenceRewriting(expression = ExprLogicalNot(operandExpr), error = error)
        | ExprGreaterThan(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprGreaterThan(leftExpr)(rightExpr), error = error)
        | ExprLessThan(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprLessThan(leftExpr)(rightExpr), error = error)
        | ExprGreaterOrEqual(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprGreaterOrEqual(leftExpr)(rightExpr), error = error)
        | ExprLessOrEqual(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprLessOrEqual(leftExpr)(rightExpr), error = error)
        | ExprEqual(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprEqual(leftExpr)(rightExpr), error = error)
        | ExprNotEqual(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprNotEqual(leftExpr)(rightExpr), error = error)
        | ExprResultPipe(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprResultPipe(leftExpr)(rightExpr), error = error)
        | ExprResultMapErrorPipe(left, right) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(left)(right) with
                | (leftExpr, rightExpr, error) -> TraitReferenceRewriting(expression = ExprResultMapErrorPipe(leftExpr)(rightExpr), error = error)
        | ExprLet(name, value, body, parameters, annotation, requirements) ->
            match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(appendList(parameters)(locals))(environment)(value) with
                | TraitReferenceRewriting { expression = valueExpr, error = valueError } ->
                    match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(name :: locals)(environment)(body) with
                        | TraitReferenceRewriting { expression = bodyExpr, error = bodyError } ->
                            TraitReferenceRewriting(
                                expression = ExprLet(name)(valueExpr)(bodyExpr)(parameters)(annotation)(requirements),
                                error = firstTraitEvidenceError(valueError)(bodyError)
                            )
        | ExprLetResult(name, value, body) ->
            match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(locals)(environment)(value) with
                | TraitReferenceRewriting { expression = valueExpr, error = valueError } ->
                    match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(name :: locals)(environment)(body) with
                        | TraitReferenceRewriting { expression = bodyExpr, error = bodyError } ->
                            TraitReferenceRewriting(
                                expression = ExprLetResult(name)(valueExpr)(bodyExpr),
                                error = firstTraitEvidenceError(valueError)(bodyError)
                            )
        | ExprLetRecursive(name, value, body, parameters, annotation, requirements) ->
            match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(name :: appendList(parameters)(locals))(environment)(value) with
                | TraitReferenceRewriting { expression = valueExpr, error = valueError } ->
                    match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(name :: locals)(environment)(body) with
                        | TraitReferenceRewriting { expression = bodyExpr, error = bodyError } ->
                            TraitReferenceRewriting(
                                expression = ExprLetRecursive(name)(valueExpr)(bodyExpr)(parameters)(annotation)(requirements),
                                error = firstTraitEvidenceError(valueError)(bodyError)
                            )
        | ExprIf(condition, thenBranch, elseBranch) ->
            match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(locals)(environment)(condition) with
                | TraitReferenceRewriting { expression = conditionExpr, error = conditionError } ->
                    match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(thenBranch)(elseBranch) with
                        | (thenExpr, elseExpr, branchError) ->
                            TraitReferenceRewriting(
                                expression = ExprIf(conditionExpr)(thenExpr)(elseExpr),
                                error = firstTraitEvidenceError(conditionError)(branchError)
                            )
        | ExprLambda(name, body, annotation) ->
            match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(name :: locals)(environment)(body) with
                | TraitReferenceRewriting { expression = bodyExpr, error = error } -> TraitReferenceRewriting(expression = ExprLambda(name)(bodyExpr)(annotation), error = error)
        | ExprCall(function, argument, whitespace, layout) ->
            match stripTraitCallSiteAt(function) with
                | ExprVar(calleeName) ->
                    if containsMethodName(calleeName)(locals)
                    then
                        match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(function)(argument) with
                            | (functionExpr, argumentExpr, error) -> TraitReferenceRewriting(expression = ExprCall(functionExpr)(argumentExpr)(whitespace)(layout), error = error)
                    else
                        match rewriteTraitCallSiteForwardedCall(activeConstraints)(ownParameters)(locals)(environment)(calleeName)(function)(argument)(whitespace)(layout) with
                            | Some(forwarded) -> forwarded
                            | None ->
                                match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(function)(argument) with
                                    | (functionExpr, argumentExpr, error) -> TraitReferenceRewriting(expression = ExprCall(functionExpr)(argumentExpr)(whitespace)(layout), error = error)
                | _ ->
                    match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(function)(argument) with
                        | (functionExpr, argumentExpr, error) -> TraitReferenceRewriting(expression = ExprCall(functionExpr)(argumentExpr)(whitespace)(layout), error = error)
        | ExprTuple(elements) ->
            match rewriteTraitCallSiteReferenceList(activeConstraints)(ownParameters)(locals)(environment)(elements) with
                | TraitReferenceRewritingList { expressions = rewrittenElements, error = error } -> TraitReferenceRewriting(expression = ExprTuple(rewrittenElements), error = error)
        | ExprList(elements, isMultiline) ->
            match rewriteTraitCallSiteReferenceList(activeConstraints)(ownParameters)(locals)(environment)(elements) with
                | TraitReferenceRewritingList { expressions = rewrittenElements, error = error } -> TraitReferenceRewriting(expression = ExprList(rewrittenElements)(isMultiline), error = error)
        | ExprCons(head, tail) ->
            match rewriteTraitCallSiteBinaryOperand(activeConstraints)(ownParameters)(locals)(environment)(head)(tail) with
                | (headExpr, tailExpr, error) -> TraitReferenceRewriting(expression = ExprCons(headExpr)(tailExpr), error = error)
        | ExprMatch(value, cases, offset) ->
            match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(locals)(environment)(value) with
                | TraitReferenceRewriting { expression = valueExpr, error = valueError } ->
                    match rewriteTraitCallSiteReferenceCases(activeConstraints)(ownParameters)(locals)(environment)(cases) with
                        | TraitReferenceRewritingCases { cases = rewrittenCases, error = casesError } ->
                            TraitReferenceRewriting(
                                expression = ExprMatch(valueExpr)(rewrittenCases)(offset),
                                error = firstTraitEvidenceError(valueError)(casesError)
                            )
        | ExprAwait(task) ->
            match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(locals)(environment)(task) with
                | TraitReferenceRewriting { expression = taskExpr, error = error } -> TraitReferenceRewriting(expression = ExprAwait(taskExpr), error = error)
        | ExprRecord(name, fields, isMultiline) ->
            match rewriteTraitCallSiteReferenceFields(activeConstraints)(ownParameters)(locals)(environment)(fields) with
                | TraitReferenceRewritingFields { fields = rewrittenFields, error = error } -> TraitReferenceRewriting(expression = ExprRecord(name)(rewrittenFields)(isMultiline), error = error)
        | ExprRecordUpdate(value, fields) ->
            match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(locals)(environment)(value) with
                | TraitReferenceRewriting { expression = valueExpr, error = valueError } ->
                    match rewriteTraitCallSiteReferenceFields(activeConstraints)(ownParameters)(locals)(environment)(fields) with
                        | TraitReferenceRewritingFields { fields = rewrittenFields, error = fieldsError } ->
                            TraitReferenceRewriting(
                                expression = ExprRecordUpdate(valueExpr)(rewrittenFields),
                                error = firstTraitEvidenceError(valueError)(fieldsError)
                            )
        | ExprPerform(operation) ->
            match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(locals)(environment)(operation) with
                | TraitReferenceRewriting { expression = operationExpr, error = error } -> TraitReferenceRewriting(expression = ExprPerform(operationExpr), error = error)
        | ExprHandle(body, arms) ->
            match rewriteTraitCallSiteReferences(activeConstraints)(ownParameters)(locals)(environment)(body) with
                | TraitReferenceRewriting { expression = bodyExpr, error = bodyError } ->
                    match rewriteTraitCallSiteReferenceArms(activeConstraints)(ownParameters)(locals)(environment)(arms) with
                        | TraitReferenceRewritingArms { arms = rewrittenArms, error = armsError } ->
                            TraitReferenceRewriting(
                                expression = ExprHandle(bodyExpr)(rewrittenArms),
                                error = firstTraitEvidenceError(bodyError)(armsError)
                            )
        | _ -> TraitReferenceRewriting(expression = expression, error = None)

let recursive anyBindingHasTraitConstraints bindings =
    match bindings with
        | [] -> false
        | (_name, TypeScheme { constraints = [] }) :: rest -> anyBindingHasTraitConstraints(rest)
        | (_name, TypeScheme { constraints = _nonEmpty }) :: _rest -> true

// Whole-environment gate so a program with no trait constraints anywhere pays only one flat scan
// per top-level binding, never the call-site walker's full body traversal.
let environmentHasTraitConstraints environment =
    match environment with
        | TypeEnvironment { bindings = bindings } -> anyBindingHasTraitConstraints(bindings)

type TraitConstrainedTopLevelValueRewriting =
    | value: Expr
    | error: Maybe(TraitEvidenceForwardingError)

// Elaborates a top-level binding's own trait constraints (rewriteTraitConstrainedValue) and then
// forwards evidence at every call site inside it that reaches another constrained top-level binding
// (rewriteTraitCallSiteReferences), using this binding's own constraints as the active evidence
// available to forward from. Replaces the environment-less passthrough and the value-only rewrite
// CoreLowering.ash previously did inline as traitRewrittenTopLevelValue.
let rewriteTraitConstrainedTopLevelValue name value environment =
    match environment with
        | None -> TraitConstrainedTopLevelValueRewriting(value = value, error = None)
        | Some(env) ->
            let activeConstraints = bindingTraitConstraints(name)(env)
            in
                let dictionaryElaborated =
                    match activeConstraints with
                        | [] -> value
                        | constraints -> rewriteTraitConstrainedValue(value)(constraints)(env)
                in
                    if environmentHasTraitConstraints(env)
                    then
                        match rewriteTraitCallSiteReferences(activeConstraints)(outermostLambdaParameterNames(value))([])(env)(dictionaryElaborated) with
                            | TraitReferenceRewriting { expression = rewritten, error = error } -> TraitConstrainedTopLevelValueRewriting(value = rewritten, error = error)
                    else TraitConstrainedTopLevelValueRewriting(value = dictionaryElaborated, error = None)
