// Where an `Eq`, `Ord` or `Not` requirement on an inferred binding scheme can come from.
//
// Inference adds a trait requirement in three places only: a trait-mapped operator, the
// instantiation of a referenced name whose scheme carries requirements, and a `requires` written on
// a `let`. Every other form forwards what its parts produced. Constructor, capability operation and
// external function schemes are built without requirements, and patterns produce none.
//
// `mayOriginateOperatorConstraint` answers for one expression. It is shadow-blind: a name bound
// inside the expression is asked about as if it were the outer one, which can only turn a "no" into
// a "yes". A requirement an inner binding generalizes was itself produced inside the expression.

import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
export (
    value mayOriginateOperatorConstraint,
)

let recursive isLiteralOperand (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> isLiteralOperand(inner)
        | ExprNode(_node, inner) -> isLiteralOperand(inner)
        | ExprInt(_) -> true
        | ExprBigInt(_) -> true
        | ExprUInt(_value, _bits, _text) -> true
        | ExprFloat(_value, _text) -> true
        | ExprString(_) -> true
        | ExprRune(_) -> true
        | ExprBool(_) -> true
        | _ -> false

// `nameCarries` reports whether a name, as the enclosing scope resolves it, has a scheme with one
// of the three requirements. A comparison with a literal operand is a requirement at the literal's
// own type, which no binding generalizes.
let recursive mayOriginateOperatorConstraint (nameCarries: Str -> Bool) (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> mayOriginateOperatorConstraint(nameCarries)(inner)
        | ExprNode(_node, inner) -> mayOriginateOperatorConstraint(nameCarries)(inner)
        | ExprNode(_id, inner) -> mayOriginateOperatorConstraint(nameCarries)(inner)
        | ExprInt(_) -> false
        | ExprBigInt(_) -> false
        | ExprUInt(_value, _bits, _text) -> false
        | ExprFloat(_value, _text) -> false
        | ExprString(_) -> false
        | ExprRune(_) -> false
        | ExprBool(_) -> false
        | ExprVar(name) -> nameCarries(name)
        | ExprQualifiedVar(qualifier, member) -> nameCarries(qualifier) || nameCarries(qualifier + "." + member)
        | ExprAdd(left, right) -> eitherMayOriginate(nameCarries)(left)(right)
        | ExprSubtract(left, right) -> eitherMayOriginate(nameCarries)(left)(right)
        | ExprMultiply(left, right) -> eitherMayOriginate(nameCarries)(left)(right)
        | ExprDivide(left, right) -> eitherMayOriginate(nameCarries)(left)(right)
        | ExprModulo(left, right) -> eitherMayOriginate(nameCarries)(left)(right)
        | ExprBitwiseAnd(left, right) -> eitherMayOriginate(nameCarries)(left)(right)
        | ExprBitwiseOr(left, right) -> eitherMayOriginate(nameCarries)(left)(right)
        | ExprBitwiseXor(left, right) -> eitherMayOriginate(nameCarries)(left)(right)
        | ExprShiftLeft(left, right) -> eitherMayOriginate(nameCarries)(left)(right)
        | ExprShiftRight(left, right) -> eitherMayOriginate(nameCarries)(left)(right)
        | ExprBitwiseNot(operand) -> mayOriginateOperatorConstraint(nameCarries)(operand)
        | ExprLogicalNot(_operand) -> true
        | ExprLogicalAnd(left, right) -> eitherMayOriginate(nameCarries)(left)(right)
        | ExprLogicalOr(left, right) -> eitherMayOriginate(nameCarries)(left)(right)
        | ExprGreaterThan(left, right) -> comparisonMayOriginate(nameCarries)(left)(right)
        | ExprLessThan(left, right) -> comparisonMayOriginate(nameCarries)(left)(right)
        | ExprGreaterOrEqual(left, right) -> comparisonMayOriginate(nameCarries)(left)(right)
        | ExprLessOrEqual(left, right) -> comparisonMayOriginate(nameCarries)(left)(right)
        | ExprEqual(left, right) -> comparisonMayOriginate(nameCarries)(left)(right)
        | ExprNotEqual(left, right) -> comparisonMayOriginate(nameCarries)(left)(right)
        | ExprResultPipe(left, right) -> eitherMayOriginate(nameCarries)(left)(right)
        | ExprResultMapErrorPipe(left, right) -> eitherMayOriginate(nameCarries)(left)(right)
        | ExprCons(left, right) -> eitherMayOriginate(nameCarries)(left)(right)
        | ExprLet(_name, value, body, _parameters, _annotation, []) -> eitherMayOriginate(nameCarries)(value)(body)
        | ExprLet(_name, _value, _body, _parameters, _annotation, _requirements) -> true
        | ExprLetResult(_name, value, body) -> eitherMayOriginate(nameCarries)(value)(body)
        | ExprLetRecursive(_name, value, body, _parameters, _annotation, []) -> eitherMayOriginate(nameCarries)(value)(body)
        | ExprLetRecursive(_name, _value, _body, _parameters, _annotation, _requirements) -> true
        | ExprIf(condition, thenBranch, elseBranch) -> mayOriginateOperatorConstraint(nameCarries)(condition) || eitherMayOriginate(nameCarries)(thenBranch)(elseBranch)
        | ExprLambda(_parameter, body, _annotation) -> mayOriginateOperatorConstraint(nameCarries)(body)
        | ExprCall(function, argument, _isSugar, _layout) -> eitherMayOriginate(nameCarries)(function)(argument)
        | ExprTuple(elements) -> anyMayOriginate(nameCarries)(elements)
        | ExprList(elements, _isMultiline) -> anyMayOriginate(nameCarries)(elements)
        | ExprMatch(value, cases, _position) -> mayOriginateOperatorConstraint(nameCarries)(value) || casesMayOriginate(nameCarries)(cases)
        | ExprAwait(operand) -> mayOriginateOperatorConstraint(nameCarries)(operand)
        | ExprRecord(_constructorName, fields, _isMultiline) -> fieldsMayOriginate(nameCarries)(fields)
        | ExprRecordUpdate(target, fields) -> mayOriginateOperatorConstraint(nameCarries)(target) || fieldsMayOriginate(nameCarries)(fields)
        | ExprPerform(operand) -> mayOriginateOperatorConstraint(nameCarries)(operand)
        | ExprHandle(operand, arms) -> mayOriginateOperatorConstraint(nameCarries)(operand) || handleArmsMayOriginate(nameCarries)(arms)
and eitherMayOriginate (nameCarries: Str -> Bool) (left: Expr) (right: Expr) = mayOriginateOperatorConstraint(nameCarries)(left) || mayOriginateOperatorConstraint(nameCarries)(right)
and comparisonMayOriginate (nameCarries: Str -> Bool) (left: Expr) (right: Expr) =
    if isLiteralOperand(left) || isLiteralOperand(right)
    then eitherMayOriginate(nameCarries)(left)(right)
    else true
and anyMayOriginate (nameCarries: Str -> Bool) (expressions: List(Expr)) =
    match expressions with
        | [] -> false
        | head :: rest -> mayOriginateOperatorConstraint(nameCarries)(head) || anyMayOriginate(nameCarries)(rest)
and fieldsMayOriginate (nameCarries: Str -> Bool) (fields: List((Str, Expr))) =
    match fields with
        | [] -> false
        | (_fieldName, expression) :: rest -> mayOriginateOperatorConstraint(nameCarries)(expression) || fieldsMayOriginate(nameCarries)(rest)
and casesMayOriginate (nameCarries: Str -> Bool) (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> false
        | (_pattern, body, guard) :: rest -> mayOriginateOperatorConstraint(nameCarries)(body) || guardMayOriginate(nameCarries)(guard) || casesMayOriginate(nameCarries)(rest)
and guardMayOriginate (nameCarries: Str -> Bool) (guard: Maybe(Expr)) =
    match guard with
        | Some(expression) -> mayOriginateOperatorConstraint(nameCarries)(expression)
        | None -> false
and handleArmsMayOriginate (nameCarries: Str -> Bool) (arms: List((Maybe(Str), Str, List(Pattern), Expr))) =
    match arms with
        | [] -> false
        | (_binder, _operation, _patterns, body) :: rest -> mayOriginateOperatorConstraint(nameCarries)(body) || handleArmsMayOriginate(nameCarries)(rest)
