// Gives every expression node of a program a unique identity.
//
// Invariants:
// - Identities are consecutive integers in pre-order, starting from the id the caller supplies.
// - Every expression node is wrapped in exactly one `ExprNode`; a located node keeps its `ExprAt`
//   outside the identity, so `ExprAt(span, ExprNode(id, node))`.
// - Numbering depends only on the shape of the program, so the same program numbers the same way.
// - Patterns and type expressions carry no identity.

import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token.TextSpan
import Ashes.Collection.List.reverse
export (
    value identifyExpression,
    value identifyItems,
    value identifyProgram,
    value foldExpressionNodes,
    value foldItemNodes,
    value foldProgramNodes,
    value expressionKind,
    value nodeIdentityOf,
    value withoutNodeIdentity,
)

let recursive identifyExpression (next: Int) (expression: Expr) =
    match expression with
        | ExprAt(span, inner) ->
            match identifyExpression(next)(inner) with
                | (identified, after) -> (ExprAt(span)(identified), after)
        | ExprNode(_id, inner) -> identifyExpression(next)(inner)
        | other ->
            match identifyChildren(next + 1)(other) with
                | (rebuilt, after) -> (ExprNode(next)(rebuilt), after)
and identifyPair (next: Int) (left: Expr) (right: Expr) =
    match identifyExpression(next)(left) with
        | (identifiedLeft, afterLeft) ->
            match identifyExpression(afterLeft)(right) with
                | (identifiedRight, afterRight) -> (identifiedLeft, identifiedRight, afterRight)
and identifyList (next: Int) (expressions: List(Expr)) (reversed: List(Expr)) =
    match expressions with
        | [] -> (reverse(reversed), next)
        | head :: tail ->
            match identifyExpression(next)(head) with
                | (identified, after) -> identifyList(after)(tail)(identified :: reversed)
and identifyFields (next: Int) (fields: List((Str, Expr))) (reversed: List((Str, Expr))) =
    match fields with
        | [] -> (reverse(reversed), next)
        | (name, value) :: tail ->
            match identifyExpression(next)(value) with
                | (identified, after) -> identifyFields(after)(tail)((name, identified) :: reversed)
and identifyCases (next: Int) (cases: List((Pattern, Expr, Maybe(Expr)))) (reversed: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> (reverse(reversed), next)
        | (pattern, body, None) :: tail ->
            match identifyExpression(next)(body) with
                | (identifiedBody, after) -> identifyCases(after)(tail)((pattern, identifiedBody, None) :: reversed)
        | (pattern, body, Some(guard)) :: tail ->
            match identifyPair(next)(guard)(body) with
                | (identifiedGuard, identifiedBody, after) -> identifyCases(after)(tail)((pattern, identifiedBody, Some(identifiedGuard)) :: reversed)
and identifyHandlerArms (next: Int) (arms: List((Maybe(Str), Str, List(Pattern), Expr))) (reversed: List((Maybe(Str), Str, List(Pattern), Expr))) =
    match arms with
        | [] -> (reverse(reversed), next)
        | (resumeName, operation, patterns, body) :: tail ->
            match identifyExpression(next)(body) with
                | (identified, after) -> identifyHandlerArms(after)(tail)((resumeName, operation, patterns, identified) :: reversed)
and identifyChildren (next: Int) (expression: Expr) =
    match expression with
        | ExprAdd(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprAdd(l)(r), after)
        | ExprSubtract(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprSubtract(l)(r), after)
        | ExprMultiply(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprMultiply(l)(r), after)
        | ExprDivide(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprDivide(l)(r), after)
        | ExprModulo(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprModulo(l)(r), after)
        | ExprBitwiseAnd(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprBitwiseAnd(l)(r), after)
        | ExprBitwiseOr(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprBitwiseOr(l)(r), after)
        | ExprBitwiseXor(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprBitwiseXor(l)(r), after)
        | ExprShiftLeft(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprShiftLeft(l)(r), after)
        | ExprShiftRight(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprShiftRight(l)(r), after)
        | ExprLogicalAnd(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprLogicalAnd(l)(r), after)
        | ExprLogicalOr(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprLogicalOr(l)(r), after)
        | ExprGreaterThan(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprGreaterThan(l)(r), after)
        | ExprLessThan(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprLessThan(l)(r), after)
        | ExprGreaterOrEqual(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprGreaterOrEqual(l)(r), after)
        | ExprLessOrEqual(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprLessOrEqual(l)(r), after)
        | ExprEqual(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprEqual(l)(r), after)
        | ExprNotEqual(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprNotEqual(l)(r), after)
        | ExprResultPipe(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprResultPipe(l)(r), after)
        | ExprResultMapErrorPipe(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprResultMapErrorPipe(l)(r), after)
        | ExprCons(left, right) ->
            match identifyPair(next)(left)(right) with
                | (l, r, after) -> (ExprCons(l)(r), after)
        | ExprBitwiseNot(operand) ->
            match identifyExpression(next)(operand) with
                | (identified, after) -> (ExprBitwiseNot(identified), after)
        | ExprLogicalNot(operand) ->
            match identifyExpression(next)(operand) with
                | (identified, after) -> (ExprLogicalNot(identified), after)
        | ExprAwait(operand) ->
            match identifyExpression(next)(operand) with
                | (identified, after) -> (ExprAwait(identified), after)
        | ExprPerform(operand) ->
            match identifyExpression(next)(operand) with
                | (identified, after) -> (ExprPerform(identified), after)
        | ExprLet(name, value, body, parameters, annotation, requirements) ->
            match identifyPair(next)(value)(body) with
                | (v, b, after) -> (ExprLet(name)(v)(b)(parameters)(annotation)(requirements), after)
        | ExprLetRecursive(name, value, body, parameters, annotation, requirements) ->
            match identifyPair(next)(value)(body) with
                | (v, b, after) -> (ExprLetRecursive(name)(v)(b)(parameters)(annotation)(requirements), after)
        | ExprLetResult(name, value, body) ->
            match identifyPair(next)(value)(body) with
                | (v, b, after) -> (ExprLetResult(name)(v)(b), after)
        | ExprIf(condition, thenBranch, elseBranch) ->
            match identifyExpression(next)(condition) with
                | (c, afterCondition) ->
                    match identifyPair(afterCondition)(thenBranch)(elseBranch) with
                        | (t, e, after) -> (ExprIf(c)(t)(e), after)
        | ExprLambda(name, body, annotation) ->
            match identifyExpression(next)(body) with
                | (identified, after) -> (ExprLambda(name)(identified)(annotation), after)
        | ExprCall(function, argument, whitespace, layout) ->
            match identifyPair(next)(function)(argument) with
                | (f, a, after) -> (ExprCall(f)(a)(whitespace)(layout), after)
        | ExprTuple(elements) ->
            match identifyList(next)(elements)([]) with
                | (identified, after) -> (ExprTuple(identified), after)
        | ExprList(elements, isMultiline) ->
            match identifyList(next)(elements)([]) with
                | (identified, after) -> (ExprList(identified)(isMultiline), after)
        | ExprMatch(scrutinee, cases, position) ->
            match identifyExpression(next)(scrutinee) with
                | (s, afterScrutinee) ->
                    match identifyCases(afterScrutinee)(cases)([]) with
                        | (identified, after) -> (ExprMatch(s)(identified)(position), after)
        | ExprRecord(name, fields, isMultiline) ->
            match identifyFields(next)(fields)([]) with
                | (identified, after) -> (ExprRecord(name)(identified)(isMultiline), after)
        | ExprRecordUpdate(target, fields) ->
            match identifyExpression(next)(target) with
                | (t, afterTarget) ->
                    match identifyFields(afterTarget)(fields)([]) with
                        | (identified, after) -> (ExprRecordUpdate(t)(identified), after)
        | ExprHandle(body, arms) ->
            match identifyExpression(next)(body) with
                | (b, afterBody) ->
                    match identifyHandlerArms(afterBody)(arms)([]) with
                        | (identified, after) -> (ExprHandle(b)(identified), after)
        | leaf -> (leaf, next)

let identifyBinding (next: Int) (binding: LetBindingSyntax) =
    match identifyExpression(next)(binding.value) with
        | (identified, after) ->
            let updated = binding with value = identified
            in (updated, after)

let recursive identifyBindings (next: Int) (bindings: List(LetBindingSyntax)) (reversed: List(LetBindingSyntax)) =
    match bindings with
        | [] -> (reverse(reversed), next)
        | head :: tail ->
            match identifyBinding(next)(head) with
                | (identified, after) -> identifyBindings(after)(tail)(identified :: reversed)

let recursive identifyProvideBindings (next: Int) (bindings: List(ProvideBinding)) (reversed: List(ProvideBinding)) =
    match bindings with
        | [] -> (reverse(reversed), next)
        | head :: tail ->
            match identifyExpression(next)(head.implementation) with
                | (identified, after) ->
                    let updated = head with implementation = identified
                    in identifyProvideBindings(after)(tail)(updated :: reversed)

let recursive identifyTraitMethods (next: Int) (methods: List(TraitMethodDecl)) (reversed: List(TraitMethodDecl)) =
    match methods with
        | [] -> (reverse(reversed), next)
        | head :: tail ->
            match head.defaultImplementation with
                | None -> identifyTraitMethods(next)(tail)(head :: reversed)
                | Some(implementation) ->
                    match identifyExpression(next)(implementation) with
                        | (identified, after) ->
                            let updated = head with defaultImplementation = Some(identified)
                            in identifyTraitMethods(after)(tail)(updated :: reversed)

let recursive identifyImplementationBindings (next: Int) (bindings: List(TraitImplementationMethodBinding)) (reversed: List(TraitImplementationMethodBinding)) =
    match bindings with
        | [] -> (reverse(reversed), next)
        | head :: tail ->
            match identifyExpression(next)(head.implementation) with
                | (identified, after) ->
                    let updated = head with implementation = identified
                    in identifyImplementationBindings(after)(tail)(updated :: reversed)

let recursive identifyItem (next: Int) (item: TopLevelItem) =
    match item with
        | TopLevelAt(span, inner) ->
            match identifyItem(next)(inner) with
                | (identified, after) -> (TopLevelAt(span)(identified), after)
        | TopLevelLet(binding, isRecursive) ->
            match identifyBinding(next)(binding) with
                | (identified, after) -> (TopLevelLet(identified)(isRecursive), after)
        | TopLevelRecursiveGroup(bindings) ->
            match identifyBindings(next)(bindings)([]) with
                | (identified, after) -> (TopLevelRecursiveGroup(identified), after)
        | TopLevelProvide(declaration) ->
            match identifyProvideBindings(next)(declaration.bindings)([]) with
                | (identified, after) ->
                    let updated = declaration with bindings = identified
                    in (TopLevelProvide(updated), after)
        | TopLevelTrait(declaration) ->
            match identifyTraitMethods(next)(declaration.methods)([]) with
                | (identified, after) ->
                    let updated = declaration with methods = identified
                    in (TopLevelTrait(updated), after)
        | TopLevelImplementation(declaration) ->
            match identifyImplementationBindings(next)(declaration.bindings)([]) with
                | (identified, after) ->
                    let updated = declaration with bindings = identified
                    in (TopLevelImplementation(updated), after)
        | other -> (other, next)

// Numbers the expressions of `items` from `next`, returning the numbered items and the first id
// not used.
let recursive identifyItems (next: Int) (items: List(TopLevelItem)) (reversed: List(TopLevelItem)) =
    match items with
        | [] -> (reverse(reversed), next)
        | head :: tail ->
            match identifyItem(next)(head) with
                | (identified, after) -> identifyItems(after)(tail)(identified :: reversed)

let identifyProgram (next: Int) (program: ProgramSyntax) =
    match identifyItems(next)(program.items)([]) with
        | (items, afterItems) ->
            match program.body with
                | None -> (ProgramSyntax(items = items, body = None), afterItems)
                | Some(body) ->
                    match identifyExpression(afterItems)(body) with
                        | (identified, after) -> (ProgramSyntax(items = items, body = Some(identified)), after)

// The identity directly around `expression`, looking through its location, or -1.
let recursive nodeIdentityOf (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> nodeIdentityOf(inner)
        | ExprNode(id, _inner) -> id
        | _ -> -1

// The node without the identity wrappers at its top, under its location if it has one, so a shape
// written for an unnumbered tree matches it.
let recursive withoutNodeIdentity (expression: Expr) =
    match expression with
        | ExprNode(_id, inner) -> withoutNodeIdentity(inner)
        | ExprAt(span, ExprNode(_id, inner)) ->
            inner
            |> withoutNodeIdentity
            |> ExprAt(span)
        | other -> other

let expressionKind (expression: Expr) =
    match expression with
        | ExprAt(_, _) -> "At"
        | ExprNode(_, _) -> "Node"
        | ExprInt(_) -> "Int"
        | ExprBigInt(_) -> "BigInt"
        | ExprUInt(_, _, _) -> "UInt"
        | ExprFloat(_, _) -> "Float"
        | ExprString(_) -> "String"
        | ExprRune(_) -> "Rune"
        | ExprBool(_) -> "Bool"
        | ExprVar(_) -> "Var"
        | ExprQualifiedVar(_, _) -> "QualifiedVar"
        | ExprAdd(_, _) -> "Add"
        | ExprSubtract(_, _) -> "Subtract"
        | ExprMultiply(_, _) -> "Multiply"
        | ExprDivide(_, _) -> "Divide"
        | ExprModulo(_, _) -> "Modulo"
        | ExprBitwiseAnd(_, _) -> "BitwiseAnd"
        | ExprBitwiseOr(_, _) -> "BitwiseOr"
        | ExprBitwiseXor(_, _) -> "BitwiseXor"
        | ExprShiftLeft(_, _) -> "ShiftLeft"
        | ExprShiftRight(_, _) -> "ShiftRight"
        | ExprBitwiseNot(_) -> "BitwiseNot"
        | ExprLogicalNot(_) -> "LogicalNot"
        | ExprLogicalAnd(_, _) -> "LogicalAnd"
        | ExprLogicalOr(_, _) -> "LogicalOr"
        | ExprGreaterThan(_, _) -> "GreaterThan"
        | ExprLessThan(_, _) -> "LessThan"
        | ExprGreaterOrEqual(_, _) -> "GreaterOrEqual"
        | ExprLessOrEqual(_, _) -> "LessOrEqual"
        | ExprEqual(_, _) -> "Equal"
        | ExprNotEqual(_, _) -> "NotEqual"
        | ExprResultPipe(_, _) -> "ResultPipe"
        | ExprResultMapErrorPipe(_, _) -> "ResultMapErrorPipe"
        | ExprLet(_, _, _, _, _, _) -> "Let"
        | ExprLetResult(_, _, _) -> "LetResult"
        | ExprLetRecursive(_, _, _, _, _, _) -> "LetRecursive"
        | ExprIf(_, _, _) -> "If"
        | ExprLambda(_, _, _) -> "Lambda"
        | ExprCall(_, _, _, _) -> "Call"
        | ExprTuple(_) -> "Tuple"
        | ExprList(_, _) -> "List"
        | ExprCons(_, _) -> "Cons"
        | ExprMatch(_, _, _) -> "Match"
        | ExprAwait(_) -> "Await"
        | ExprRecord(_, _, _) -> "Record"
        | ExprRecordUpdate(_, _) -> "RecordUpdate"
        | ExprPerform(_) -> "Perform"
        | ExprHandle(_, _) -> "Handle"

// Visits every expression node under `expression` in pre-order. `visit` receives the state, the
// node's identity (-1 when it has none), the span of the location directly around it, and the node
// itself without its wrappers.
let recursive foldExpressionNodes visit state (expression: Expr) = foldWrapped(visit)(state)(-1)(None)(expression)
and foldWrapped visit state (id: Int) (span: Maybe(TextSpan)) (expression: Expr) =
    match expression with
        | ExprAt(located, inner) -> foldWrapped(visit)(state)(id)(Some(located))(inner)
        | ExprNode(identity, inner) -> foldWrapped(visit)(state)(identity)(span)(inner)
        | node ->
            foldChildren(visit)(visit(state)(id)(span)(node))(node)
and foldPair visit state (left: Expr) (right: Expr) =
    foldExpressionNodes(visit)(foldExpressionNodes(visit)(state)(left))(right)
and foldList visit state (expressions: List(Expr)) =
    match expressions with
        | [] -> state
        | head :: tail ->
            foldList(visit)(foldExpressionNodes(visit)(state)(head))(tail)
and foldFields visit state (fields: List((Str, Expr))) =
    match fields with
        | [] -> state
        | (_name, value) :: tail ->
            foldFields(visit)(foldExpressionNodes(visit)(state)(value))(tail)
and foldCases visit state (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> state
        | (_pattern, body, None) :: tail ->
            foldCases(visit)(foldExpressionNodes(visit)(state)(body))(tail)
        | (_pattern, body, Some(guard)) :: tail ->
            foldCases(visit)(foldPair(visit)(state)(guard)(body))(tail)
and foldHandlerArms visit state (arms: List((Maybe(Str), Str, List(Pattern), Expr))) =
    match arms with
        | [] -> state
        | (_resumeName, _operation, _patterns, body) :: tail ->
            foldHandlerArms(visit)(foldExpressionNodes(visit)(state)(body))(tail)
and foldChildren visit state (expression: Expr) =
    match expression with
        | ExprAdd(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprSubtract(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprMultiply(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprDivide(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprModulo(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprBitwiseAnd(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprBitwiseOr(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprBitwiseXor(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprShiftLeft(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprShiftRight(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprLogicalAnd(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprLogicalOr(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprGreaterThan(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprLessThan(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprGreaterOrEqual(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprLessOrEqual(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprEqual(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprNotEqual(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprResultPipe(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprResultMapErrorPipe(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprCons(left, right) -> foldPair(visit)(state)(left)(right)
        | ExprBitwiseNot(operand) -> foldExpressionNodes(visit)(state)(operand)
        | ExprLogicalNot(operand) -> foldExpressionNodes(visit)(state)(operand)
        | ExprAwait(operand) -> foldExpressionNodes(visit)(state)(operand)
        | ExprPerform(operand) -> foldExpressionNodes(visit)(state)(operand)
        | ExprLet(_name, value, body, _parameters, _annotation, _requirements) -> foldPair(visit)(state)(value)(body)
        | ExprLetRecursive(_name, value, body, _parameters, _annotation, _requirements) -> foldPair(visit)(state)(value)(body)
        | ExprLetResult(_name, value, body) -> foldPair(visit)(state)(value)(body)
        | ExprIf(condition, thenBranch, elseBranch) ->
            foldPair(visit)(foldExpressionNodes(visit)(state)(condition))(thenBranch)(elseBranch)
        | ExprLambda(_name, body, _annotation) -> foldExpressionNodes(visit)(state)(body)
        | ExprCall(function, argument, _whitespace, _layout) -> foldPair(visit)(state)(function)(argument)
        | ExprTuple(elements) -> foldList(visit)(state)(elements)
        | ExprList(elements, _isMultiline) -> foldList(visit)(state)(elements)
        | ExprMatch(scrutinee, cases, _position) ->
            foldCases(visit)(foldExpressionNodes(visit)(state)(scrutinee))(cases)
        | ExprRecord(_name, fields, _isMultiline) -> foldFields(visit)(state)(fields)
        | ExprRecordUpdate(target, fields) ->
            foldFields(visit)(foldExpressionNodes(visit)(state)(target))(fields)
        | ExprHandle(body, arms) ->
            foldHandlerArms(visit)(foldExpressionNodes(visit)(state)(body))(arms)
        | _ -> state

let recursive foldBindings visit state (bindings: List(LetBindingSyntax)) =
    match bindings with
        | [] -> state
        | head :: tail ->
            foldBindings(visit)(foldExpressionNodes(visit)(state)(head.value))(tail)

let recursive foldProvideBindings visit state (bindings: List(ProvideBinding)) =
    match bindings with
        | [] -> state
        | head :: tail ->
            foldProvideBindings(visit)(foldExpressionNodes(visit)(state)(head.implementation))(tail)

let recursive foldTraitMethods visit state (methods: List(TraitMethodDecl)) =
    match methods with
        | [] -> state
        | head :: tail ->
            match head.defaultImplementation with
                | None -> foldTraitMethods(visit)(state)(tail)
                | Some(implementation) ->
                    foldTraitMethods(visit)(foldExpressionNodes(visit)(state)(implementation))(tail)

let recursive foldImplementationBindings visit state (bindings: List(TraitImplementationMethodBinding)) =
    match bindings with
        | [] -> state
        | head :: tail ->
            foldImplementationBindings(visit)(foldExpressionNodes(visit)(state)(head.implementation))(tail)

// Visits the expression nodes of one top-level item, as `foldExpressionNodes` does.
let recursive foldItemNodes visit state (item: TopLevelItem) =
    match item with
        | TopLevelAt(_span, inner) -> foldItemNodes(visit)(state)(inner)
        | TopLevelLet(binding, _isRecursive) -> foldExpressionNodes(visit)(state)(binding.value)
        | TopLevelRecursiveGroup(bindings) -> foldBindings(visit)(state)(bindings)
        | TopLevelProvide(declaration) -> foldProvideBindings(visit)(state)(declaration.bindings)
        | TopLevelTrait(declaration) -> foldTraitMethods(visit)(state)(declaration.methods)
        | TopLevelImplementation(declaration) -> foldImplementationBindings(visit)(state)(declaration.bindings)
        | _ -> state

let recursive foldItems visit state (items: List(TopLevelItem)) =
    match items with
        | [] -> state
        | head :: tail ->
            foldItems(visit)(foldItemNodes(visit)(state)(head))(tail)

let foldProgramNodes visit state (program: ProgramSyntax) =
    match program.body with
        | None -> foldItems(visit)(state)(program.items)
        | Some(body) ->
            foldExpressionNodes(visit)(foldItems(visit)(state)(program.items))(body)
