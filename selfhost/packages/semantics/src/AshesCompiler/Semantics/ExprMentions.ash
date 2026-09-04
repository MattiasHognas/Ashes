// Shadow-blind name-mention queries over module syntax.
//
// `exprMentionsName` reports whether an expression mentions a variable name anywhere inside it,
// counting a `let`, lambda, or `match` binder that reuses the name as a mention too, so a caller
// asking "is this name still live" or "does this module use this name" is never told no when the
// answer is yes. `programMentionsVariable` asks the same of a whole module: every flat top-level
// `let` and recursive-group value plus the trailing expression, the same surface stage 0's
// `CollectReferencedNames` walks when it decides which imported names a module actually uses.

import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Frontend.Syntax.ProgramSyntax
import AshesCompiler.Frontend.Syntax.TopLevelItem
import AshesCompiler.Frontend.Syntax.LetBindingSyntax
export (
    value exprMentionsName,
    value programMentionsVariable,
)

let recursive exprMentionsName (name: Str) (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> exprMentionsName(name)(inner)
        | ExprVar(candidate) -> candidate == name
        | ExprQualifiedVar(_module, _member) -> false
        | ExprAdd(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprSubtract(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprMultiply(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprDivide(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprModulo(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprBitwiseAnd(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprBitwiseOr(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprBitwiseXor(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprShiftLeft(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprShiftRight(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprBitwiseNot(operand) -> exprMentionsName(name)(operand)
        | ExprLogicalNot(operand) -> exprMentionsName(name)(operand)
        | ExprLogicalAnd(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprLogicalOr(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprGreaterThan(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprLessThan(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprGreaterOrEqual(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprLessOrEqual(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprEqual(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprNotEqual(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprResultPipe(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprResultMapErrorPipe(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprCons(left, right) -> exprMentionsEither(name)(left)(right)
        | ExprLet(_name, value, body, _params, _annotation, _requirements) -> exprMentionsEither(name)(value)(body)
        | ExprLetResult(_name, value, body) -> exprMentionsEither(name)(value)(body)
        | ExprLetRecursive(_name, value, body, _params, _annotation, _requirements) -> exprMentionsEither(name)(value)(body)
        | ExprIf(condition, thenBranch, elseBranch) -> exprMentionsName(name)(condition) || exprMentionsEither(name)(thenBranch)(elseBranch)
        | ExprLambda(_parameter, body, _annotation) -> exprMentionsName(name)(body)
        | ExprCall(function, argument, _isSugar, _layout) -> exprMentionsEither(name)(function)(argument)
        | ExprTuple(elements) -> exprMentionsAny(name)(elements)
        | ExprList(elements, _isMultiline) -> exprMentionsAny(name)(elements)
        | ExprMatch(value, cases, _position) -> exprMentionsName(name)(value) || exprMentionsMatchCases(name)(cases)
        | ExprAwait(operand) -> exprMentionsName(name)(operand)
        | ExprRecord(_ctorName, fields, _isMultiline) -> exprMentionsFields(name)(fields)
        | ExprRecordUpdate(target, fields) -> exprMentionsName(name)(target) || exprMentionsFields(name)(fields)
        | ExprPerform(operand) -> exprMentionsName(name)(operand)
        | ExprHandle(operand, arms) -> exprMentionsName(name)(operand) || exprMentionsHandleArms(name)(arms)
        | _ -> false
and exprMentionsEither (name: Str) (left: Expr) (right: Expr) = exprMentionsName(name)(left) || exprMentionsName(name)(right)
and exprMentionsAny (name: Str) (expressions: List(Expr)) =
    match expressions with
        | [] -> false
        | head :: rest -> exprMentionsName(name)(head) || exprMentionsAny(name)(rest)
and exprMentionsFields (name: Str) (fields: List((Str, Expr))) =
    match fields with
        | [] -> false
        | (_fieldName, expression) :: rest -> exprMentionsName(name)(expression) || exprMentionsFields(name)(rest)
and exprMentionsMatchCases (name: Str) (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> false
        | (_pattern, body, guard) :: rest -> exprMentionsName(name)(body) || exprMentionsGuard(name)(guard) || exprMentionsMatchCases(name)(rest)
and exprMentionsGuard (name: Str) (guard: Maybe(Expr)) =
    match guard with
        | Some(expression) -> exprMentionsName(name)(expression)
        | None -> false
and exprMentionsHandleArms (name: Str) (arms: List((Maybe(Str), Str, List(Pattern), Expr))) =
    match arms with
        | [] -> false
        | (_binder, _operation, _patterns, body) :: rest -> exprMentionsName(name)(body) || exprMentionsHandleArms(name)(rest)

let recursive bindingsMentionName (name: Str) (bindings: List(LetBindingSyntax)) =
    match bindings with
        | [] -> false
        | binding :: rest -> exprMentionsName(name)(binding.value) || bindingsMentionName(name)(rest)

let recursive itemsMentionName (name: Str) (items: List(TopLevelItem)) =
    match items with
        | [] -> false
        | TopLevelAt(_span, inner) :: rest -> itemsMentionName(name)(inner :: rest)
        | TopLevelLet(binding, _isRecursive) :: rest -> exprMentionsName(name)(binding.value) || itemsMentionName(name)(rest)
        | TopLevelRecursiveGroup(bindings) :: rest -> bindingsMentionName(name)(bindings) || itemsMentionName(name)(rest)
        | _item :: rest -> itemsMentionName(name)(rest)

let programMentionsVariable (name: Str) (program: ProgramSyntax) =
    match program with
        | ProgramSyntax { items = items, body = body } -> itemsMentionName(name)(items) || exprMentionsGuard(name)(body)
