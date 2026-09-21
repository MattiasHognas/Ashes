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
    value exprReadsName,
    value programMentionsVariable,
)

let recursive exprMentionsWith (qualifiers: Bool) (name: Str) (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> exprMentionsWith(qualifiers)(name)(inner)
        | ExprVar(candidate) -> candidate == name
        | ExprQualifiedVar(qualifier, _member) -> qualifiers && qualifier == name
        | ExprAdd(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprSubtract(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprMultiply(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprDivide(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprModulo(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprBitwiseAnd(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprBitwiseOr(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprBitwiseXor(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprShiftLeft(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprShiftRight(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprBitwiseNot(operand) -> exprMentionsWith(qualifiers)(name)(operand)
        | ExprLogicalNot(operand) -> exprMentionsWith(qualifiers)(name)(operand)
        | ExprLogicalAnd(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprLogicalOr(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprGreaterThan(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprLessThan(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprGreaterOrEqual(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprLessOrEqual(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprEqual(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprNotEqual(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprResultPipe(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprResultMapErrorPipe(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprCons(left, right) -> exprMentionsEither(qualifiers)(name)(left)(right)
        | ExprLet(_name, value, body, _params, _annotation, _requirements) -> exprMentionsEither(qualifiers)(name)(value)(body)
        | ExprLetResult(_name, value, body) -> exprMentionsEither(qualifiers)(name)(value)(body)
        | ExprLetRecursive(_name, value, body, _params, _annotation, _requirements) -> exprMentionsEither(qualifiers)(name)(value)(body)
        | ExprIf(condition, thenBranch, elseBranch) -> exprMentionsWith(qualifiers)(name)(condition) || exprMentionsEither(qualifiers)(name)(thenBranch)(elseBranch)
        | ExprLambda(_parameter, body, _annotation) -> exprMentionsWith(qualifiers)(name)(body)
        | ExprCall(function, argument, _isSugar, _layout) -> exprMentionsEither(qualifiers)(name)(function)(argument)
        | ExprTuple(elements) -> exprMentionsAny(qualifiers)(name)(elements)
        | ExprList(elements, _isMultiline) -> exprMentionsAny(qualifiers)(name)(elements)
        | ExprMatch(value, cases, _position) -> exprMentionsWith(qualifiers)(name)(value) || exprMentionsMatchCases(qualifiers)(name)(cases)
        | ExprAwait(operand) -> exprMentionsWith(qualifiers)(name)(operand)
        | ExprRecord(_ctorName, fields, _isMultiline) -> exprMentionsFields(qualifiers)(name)(fields)
        | ExprRecordUpdate(target, fields) -> exprMentionsWith(qualifiers)(name)(target) || exprMentionsFields(qualifiers)(name)(fields)
        | ExprPerform(operand) -> exprMentionsWith(qualifiers)(name)(operand)
        | ExprHandle(operand, arms) -> exprMentionsWith(qualifiers)(name)(operand) || exprMentionsHandleArms(qualifiers)(name)(arms)
        | _ -> false
and exprMentionsEither (qualifiers: Bool) (name: Str) (left: Expr) (right: Expr) = exprMentionsWith(qualifiers)(name)(left) || exprMentionsWith(qualifiers)(name)(right)
and exprMentionsAny (qualifiers: Bool) (name: Str) (expressions: List(Expr)) =
    match expressions with
        | [] -> false
        | head :: rest -> exprMentionsWith(qualifiers)(name)(head) || exprMentionsAny(qualifiers)(name)(rest)
and exprMentionsFields (qualifiers: Bool) (name: Str) (fields: List((Str, Expr))) =
    match fields with
        | [] -> false
        | (_fieldName, expression) :: rest -> exprMentionsWith(qualifiers)(name)(expression) || exprMentionsFields(qualifiers)(name)(rest)
and exprMentionsMatchCases (qualifiers: Bool) (name: Str) (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> false
        | (_pattern, body, guard) :: rest -> exprMentionsWith(qualifiers)(name)(body) || exprMentionsGuard(qualifiers)(name)(guard) || exprMentionsMatchCases(qualifiers)(name)(rest)
and exprMentionsGuard (qualifiers: Bool) (name: Str) (guard: Maybe(Expr)) =
    match guard with
        | Some(expression) -> exprMentionsWith(qualifiers)(name)(expression)
        | None -> false
and exprMentionsHandleArms (qualifiers: Bool) (name: Str) (arms: List((Maybe(Str), Str, List(Pattern), Expr))) =
    match arms with
        | [] -> false
        | (_binder, _operation, _patterns, body) :: rest -> exprMentionsWith(qualifiers)(name)(body) || exprMentionsHandleArms(qualifiers)(name)(rest)

let exprMentionsName (name: Str) (expression: Expr) = exprMentionsWith(false)(name)(expression)

// A mention that also counts a member read through the name, `name.member`.
let exprReadsName (name: Str) (expression: Expr) = exprMentionsWith(true)(name)(expression)

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
        | ProgramSyntax { items = items, body = body } -> itemsMentionName(name)(items) || exprMentionsGuard(false)(name)(body)
