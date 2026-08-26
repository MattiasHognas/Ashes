// Renders parsed syntax in the canonical Ashes source form.
//
// Invariants:
// - Source spans never affect rendered text.
// - Parentheses preserve precedence and associativity; explicitly multiline call arguments retain that layout.
// - Top-level declarations use canonical spacing and the result has one terminal newline.

import AshesCompiler.Frontend.Syntax
import Ashes.Collection.List.sortBy
import Ashes.Collection.List.map as listMap
export (
    value formatProgram,
    value formatExpression,
    value formatTypeExpression,
    value formatPattern,
    type FormattingOptions(..),
    value formattingOptionsDefault,
    value formattingOptionsNormalize,
    value formatProgramWithOptions,
    value formatExpressionWithOptions,
)

let recursive formatterIndent : Int -> Str =
    given (count) ->
        if count <= 0
        then ""
        else " " + formatterIndent(count - 1)

let formatterWrap : Int -> Int -> Str -> Str =
    given (parent) ->
        given (own) ->
            given (text) ->
                if parent > own
                then "(" + text + ")"
                else text

let recursive formatterEscape : Str -> Str =
    given (text) ->
        match Ashes.Text.uncons(text) with
            | None -> ""
            | Some((head, tail)) ->
                let escaped =
                    if head == '\\'
                    then "\\\\"
                    else
                        if head == '"'
                        then "\\\""
                        else
                            if head == '\n'
                            then "\\n"
                            else
                                if head == '\r'
                                then "\\r"
                                else
                                    if head == '\t'
                                    then "\\t"
                                    else Ashes.Rune.toText(head)
                in escaped + formatterEscape(tail)

let formatterRune : Int -> Str =
    given (value) ->
        match Ashes.Rune.fromInt(value) with
            | Some(rune) -> Ashes.Rune.toText(rune)
            | None -> "�"

let recursive formatterJoin : Str -> (a -> Str) -> List(a) -> Str =
    given (separator) ->
        given (render) ->
            given (values) ->
                match values with
                    | [] -> ""
                    | value :: tail ->
                        let recursive rest : List(a) -> Str =
                            given (remaining) ->
                                match remaining with
                                    | [] -> ""
                                    | next :: nextTail -> separator + render(next) + rest(nextTail)
                        in render(value) + rest(tail)

let recursive formatterUnspanType : TypeExpr -> TypeExpr =
    given (typeExpression) ->
        match typeExpression with
            | TypeAt(_span, inner) -> formatterUnspanType(inner)
            | _ -> typeExpression

let recursive formatterType : TypeExpr -> Str =
    given (typeExpression) ->
        match formatterUnspanType(typeExpression) with
            | TypeNamed(name) -> name
            | TypeApplied(name, arguments) -> name + "(" + formatterJoin(", ")(formatterType)(arguments) + ")"
            | TypeTuple(elements) -> "(" + formatterJoin(", ")(formatterType)(elements) + ")"
            | TypeUnit -> "()"
            | TypeArrow(source, destination, capabilities, tailVariable) ->
                let sourceText =
                    match formatterUnspanType(source) with
                        | TypeArrow(_, _, _, _) -> "(" + formatterType(source) + ")"
                        | _ -> formatterType(source)
                in
                    let destinationText =
                        match (capabilities, tailVariable, formatterUnspanType(destination)) with
                            | ([], None, _) -> formatterType(destination)
                            | (_, _, TypeArrow(_, _, _, _)) -> "(" + formatterType(destination) + ")"
                            | _ -> formatterType(destination)
                    in sourceText + " -> " + destinationText + formatterNeeds(capabilities)(tailVariable)
            | TypeAt(_span, inner) -> formatterType(inner)
and formatterNeeds : List((Str, List(TypeExpr))) -> Maybe(Str) -> Str =
    given (capabilities) ->
        given (tailVariable) ->
            match (capabilities, tailVariable) with
                | ([], None) -> ""
                | ([], Some(tail)) -> " needs " + tail
                | _ ->
                    let renderCapability : (Str, List(TypeExpr)) -> Str =
                        given (item) ->
                            match item with
                                | (name, []) -> name
                                | (name, arguments) -> name + "(" + formatterJoin(", ")(formatterType)(arguments) + ")"
                    in
                        let capabilitiesText = formatterJoin(", ")(renderCapability)(capabilities)
                        in
                            match tailVariable with
                                | None -> " needs {" + capabilitiesText + "}"
                                | Some(tail) -> " needs {" + capabilitiesText + " | " + tail + "}"

let recursive formatterPatternAt : Pattern -> Int -> Str =
    given (pattern) ->
        given (parent) ->
            match pattern with
                | PatternEmptyList -> "[]"
                | PatternVar(name) -> name
                | PatternWildcard -> "_"
                | PatternInt(value) -> Ashes.Text.fromInt(value)
                | PatternString(value) -> "\"" + formatterEscape(value) + "\""
                | PatternRune(value) ->
                    "'" + formatterEscape(formatterRune(value)) + "'"
                | PatternBool(value) ->
                    if value
                    then "true"
                    else "false"
                | PatternTuple(elements) ->
                    "(" + formatterJoin(", ")(given (item) -> formatterPatternAt(item)(0))(elements) + ")"
                | PatternConstructor(name, arguments) ->
                    if arguments == []
                    then name
                    else
                        name + "(" + formatterJoin(", ")(given (item) -> formatterPatternAt(item)(0))(arguments) + ")"
                | PatternRecord(name, fields) ->
                    let renderField : (Str, Pattern) -> Str =
                        given (field) ->
                            match field with
                                | (fieldName, fieldPattern) -> fieldName + " = " + formatterPatternAt(fieldPattern)(0)
                    in name + " { " + formatterJoin(", ")(renderField)(fields) + " }"
                | PatternCons(head, tail) ->
                    let renderedHead = formatterPatternAt(head)(4)
                    in
                        let renderedTail = formatterPatternAt(tail)(3)
                        in formatterWrap(parent)(3)(renderedHead + " :: " + renderedTail)
                | PatternAs(inner, name) ->
                    let renderedInner = formatterPatternAt(inner)(2)
                    in formatterWrap(parent)(2)(renderedInner + " as " + name)
                | PatternOr(alternatives) ->
                    alternatives
                    |> formatterJoin(" | ")(given (item) -> formatterPatternAt(item)(0))
                    |> formatterWrap(parent)(1)
                | PatternAt(_span, inner) -> formatterPatternAt(inner)(parent)

let recursive formatterUnspanExpr : Expr -> Expr =
    given (expression) ->
        match expression with
            | ExprAt(_span, inner) -> formatterUnspanExpr(inner)
            | _ -> expression

// Whether a record update occurs anywhere inside an expression. `with` takes every following
// `name = value` pair as one of its own fields and binds looser than application, so an operand
// that contains one (a call argument, a tuple or list element, a record-literal field value, a
// match scrutinee) is rendered at the precedence just above `with`, which parenthesizes the
// update, or the let/if/lambda/match around it, exactly as stage 0 does.
let recursive formatterContainsRecordUpdate : Expr -> Bool =
    given (expression) ->
        match expression with
            | ExprAt(_span, inner) -> formatterContainsRecordUpdate(inner)
            | ExprRecordUpdate(_target, _fields) -> true
            | ExprAdd(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprSubtract(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprMultiply(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprDivide(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprModulo(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprBitwiseAnd(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprBitwiseOr(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprBitwiseXor(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprShiftLeft(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprShiftRight(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprBitwiseNot(operand) -> formatterContainsRecordUpdate(operand)
            | ExprLogicalNot(operand) -> formatterContainsRecordUpdate(operand)
            | ExprGreaterThan(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprLessThan(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprGreaterOrEqual(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprLessOrEqual(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprEqual(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprNotEqual(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprResultPipe(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprResultMapErrorPipe(left, right) -> formatterContainsRecordUpdateIn(left)(right)
            | ExprLet(_name, value, body, _parameters, _annotation, _requirements) -> formatterContainsRecordUpdateIn(value)(body)
            | ExprLetRecursive(_name, value, body, _parameters, _annotation, _requirements) -> formatterContainsRecordUpdateIn(value)(body)
            | ExprLetResult(_name, value, body) -> formatterContainsRecordUpdateIn(value)(body)
            | ExprLambda(_name, body, _annotation) -> formatterContainsRecordUpdate(body)
            | ExprIf(condition, thenBranch, elseBranch) ->
                if formatterContainsRecordUpdateIn(condition)(thenBranch)
                then true
                else formatterContainsRecordUpdate(elseBranch)
            | ExprCall(function, argument, _whitespace, _layout) -> formatterContainsRecordUpdateIn(function)(argument)
            | ExprTuple(elements) -> formatterAnyContainsRecordUpdate(elements)
            | ExprList(elements, _isMultiline) -> formatterAnyContainsRecordUpdate(elements)
            | ExprCons(head, tail) -> formatterContainsRecordUpdateIn(head)(tail)
            | ExprMatch(value, cases, _position) ->
                if formatterContainsRecordUpdate(value)
                then true
                else formatterAnyCaseContainsRecordUpdate(cases)
            | ExprAwait(task) -> formatterContainsRecordUpdate(task)
            | ExprRecord(_name, fields, _isMultiline) -> formatterAnyFieldContainsRecordUpdate(fields)
            | ExprPerform(operation) -> formatterContainsRecordUpdate(operation)
            | ExprHandle(body, arms) ->
                if formatterContainsRecordUpdate(body)
                then true
                else formatterAnyArmContainsRecordUpdate(arms)
            | _ -> false
and formatterContainsRecordUpdateIn : Expr -> Expr -> Bool =
    given (first) ->
        given (second) ->
            if formatterContainsRecordUpdate(first)
            then true
            else formatterContainsRecordUpdate(second)
and formatterAnyContainsRecordUpdate : List(Expr) -> Bool =
    given (expressions) ->
        match expressions with
            | [] -> false
            | expression :: tail ->
                if formatterContainsRecordUpdate(expression)
                then true
                else formatterAnyContainsRecordUpdate(tail)
and formatterAnyFieldContainsRecordUpdate : List((Str, Expr)) -> Bool =
    given (fields) ->
        match fields with
            | [] -> false
            | (_name, value) :: tail ->
                if formatterContainsRecordUpdate(value)
                then true
                else formatterAnyFieldContainsRecordUpdate(tail)
and formatterAnyCaseContainsRecordUpdate : List((Pattern, Expr, Maybe(Expr))) -> Bool =
    given (cases) ->
        match cases with
            | [] -> false
            | (_pattern, body, guard) :: tail ->
                if formatterContainsRecordUpdate(body)
                then true
                else
                    match guard with
                        | Some(guardExpression) ->
                            if formatterContainsRecordUpdate(guardExpression)
                            then true
                            else formatterAnyCaseContainsRecordUpdate(tail)
                        | None -> formatterAnyCaseContainsRecordUpdate(tail)
and formatterAnyArmContainsRecordUpdate : List((Maybe(Str), Str, List(Pattern), Expr)) -> Bool =
    given (arms) ->
        match arms with
            | [] -> false
            | (_capability, _operation, _patterns, body) :: tail ->
                if formatterContainsRecordUpdate(body)
                then true
                else formatterAnyArmContainsRecordUpdate(tail)

// The parent precedence for an operand position that must keep a contained `with` parenthesized:
// just above `with` when the operand contains an update, otherwise unconstrained.
let formatterOperandPrecedence : Expr -> Int =
    given (expression) ->
        if formatterContainsRecordUpdate(expression)
        then 3
        else 0

// Whether a record update sits at the unparenthesized right edge of an expression: the update
// itself, or the trailing body of a let, lambda, conditional, match, or handler. Such an
// expression must be parenthesized wherever a comma-separated `name = value` (a record-literal
// field) or another comma-separated operand follows it, or the update absorbs what follows. A
// binary operator, call, or bracket already closes its right edge, so those keep their form.
let recursive formatterEndsWithRecordUpdate : Expr -> Bool =
    given (expression) ->
        match expression with
            | ExprAt(_span, inner) -> formatterEndsWithRecordUpdate(inner)
            | ExprRecordUpdate(_target, _fields) -> true
            | ExprLet(_name, _value, body, _parameters, _annotation, _requirements) -> formatterEndsWithRecordUpdate(body)
            | ExprLetRecursive(_name, _value, body, _parameters, _annotation, _requirements) -> formatterEndsWithRecordUpdate(body)
            | ExprLetResult(_name, _value, body) -> formatterEndsWithRecordUpdate(body)
            | ExprLambda(_name, body, _annotation) -> formatterEndsWithRecordUpdate(body)
            | ExprIf(_condition, _thenBranch, elseBranch) -> formatterEndsWithRecordUpdate(elseBranch)
            | ExprMatch(_value, cases, _position) -> formatterLastCaseEndsWithRecordUpdate(cases)
            | ExprHandle(_body, arms) -> formatterLastArmEndsWithRecordUpdate(arms)
            | _ -> false
and formatterLastCaseEndsWithRecordUpdate : List((Pattern, Expr, Maybe(Expr))) -> Bool =
    given (cases) ->
        match cases with
            | [] -> false
            | (_pattern, body, _guard) :: [] -> formatterEndsWithRecordUpdate(body)
            | _ :: tail -> formatterLastCaseEndsWithRecordUpdate(tail)
and formatterLastArmEndsWithRecordUpdate : List((Maybe(Str), Str, List(Pattern), Expr)) -> Bool =
    given (arms) ->
        match arms with
            | [] -> false
            | (_capability, _operation, _patterns, body) :: [] -> formatterEndsWithRecordUpdate(body)
            | _ :: tail -> formatterLastArmEndsWithRecordUpdate(tail)

// The parent precedence for a comma-separated field value or multiline operand.
let formatterFieldPrecedence : Expr -> Int =
    given (expression) ->
        if formatterEndsWithRecordUpdate(expression)
        then 3
        else 0

// Giving the right operand a tighter parent precedence preserves left associativity.
let recursive formatterBinary : Int -> Int -> Str -> Expr -> Expr -> Bool -> Str =
    given (parent) ->
        given (precedence) ->
            given (operator) ->
                given (left) ->
                    given (right) ->
                        given (preferPipelines) ->
                            formatterWrap(
                                parent,
                                precedence,
                                formatterExpr(left)(precedence)(0)(preferPipelines) + " " + operator + " " + formatterExpr(
                                    right,
                                    precedence + 1,
                                    0,
                                    preferPipelines
                                )
                            )
// A call/`|?>`/`|!>` chain at a top-level expression position (`parent == 0`) renders as a
// multiline `|>` pipeline instead of nested calls when the file prefers that layout — mirroring
// how the whole document already only reaches parent == 0 at exactly the syntactic positions
// (let-bound values/bodies, if/match/lambda/handle bodies, case and arm bodies, the trailing
// program expression) where stage 0's own WriteExprInline/WritePipeOperatorInline attempt the
// same rewrite. Anywhere else (a call argument, a binary operand, ...) parent is never 0, so this
// is never attempted mid-expression, exactly matching stage 0's own gating.
and formatterTryPipelineRendering : Expr -> Int -> Int -> Bool -> Maybe(Str) =
    given (expression) ->
        given (parent) ->
            given (indent) ->
                given (preferPipelines) ->
                    if preferPipelines
                    then
                        if parent == 0
                        then formatterTryWritePipeline(expression)(indent)(preferPipelines)
                        else None
                    else None
and formatterTryWritePipeline : Expr -> Int -> Bool -> Maybe(Str) =
    given (expression) ->
        given (indent) ->
            given (preferPipelines) ->
                match formatterTryCollectPipeline(expression) with
                    | None -> None
                    | Some((value, stages)) ->
                        Some(
                            formatterExpr(value)(4)(indent)(preferPipelines) + formatterPipelineStagesText(
                                stages,
                                indent,
                                preferPipelines
                            )
                        )
and formatterPipelineStagesText : List((Str, Expr)) -> Int -> Bool -> Str =
    given (stages) ->
        given (indent) ->
            given (preferPipelines) ->
                match stages with
                    | [] -> ""
                    | (operatorText, func) :: tail ->
                        "\n" + formatterIndent(indent) + operatorText + " " + formatterExpr(
                            func,
                            4,
                            indent,
                            preferPipelines
                        ) + formatterPipelineStagesText(tail)(indent)(preferPipelines)
// Collects a call/`|?>`/`|!>` chain into (base value, ordered stages), or None when the chain is
// not eligible for pipeline rendering at all — collection is all-or-nothing: hitting one
// non-pipeline-eligible function anywhere along the chain (a let/if/match/handle) rejects the
// whole expression rather than converting only part of it, and at least two stages are required
// (a single call is not worth rewriting).
and formatterTryCollectPipeline : Expr -> Maybe((Expr, List((Str, Expr)))) =
    given (expression) ->
        match formatterCollectPipelineStages(expression)([]) with
            | None -> None
            | Some((value, stages)) ->
                match stages with
                    | [] -> None
                    | _ :: [] -> None
                    | _ -> Some((value, stages))
// stagesSoFar is built by consing each newly discovered outer stage onto its front while walking
// from the outermost call inward, so by the time collection reaches the innermost value the list
// already reads innermost-stage-first — the exact order the pipeline renders in. No reversal step
// is needed (or correct): reversing here would put the outermost stage first instead of last.
and formatterCollectPipelineStages : Expr -> List((Str, Expr)) -> Maybe((Expr, List((Str, Expr)))) =
    given (current) ->
        given (stagesSoFar) ->
            match formatterUnspanExpr(current) with
                | ExprCall(function, argument, _whitespace, _layout) ->
                    if formatterHasPipelineStages(stagesSoFar)
                    then
                        if formatterIsCapitalizedVarFunction(function)
                        then Some((current, stagesSoFar))
                        else
                            if formatterCanBePipelineFunction(function)
                            then formatterCollectPipelineStages(argument)(("|>", function) :: stagesSoFar)
                            else None
                    else
                        if formatterCanBePipelineFunction(function)
                        then formatterCollectPipelineStages(argument)(("|>", function) :: stagesSoFar)
                        else None
                | ExprResultPipe(left, right) ->
                    if formatterCanBePipelineFunction(right)
                    then formatterCollectPipelineStages(left)(("|?>", right) :: stagesSoFar)
                    else None
                | ExprResultMapErrorPipe(left, right) ->
                    if formatterCanBePipelineFunction(right)
                    then formatterCollectPipelineStages(left)(("|!>", right) :: stagesSoFar)
                    else None
                | _ -> Some((current, stagesSoFar))
and formatterHasPipelineStages : List((Str, Expr)) -> Bool =
    given (stages) ->
        match stages with
            | [] -> false
            | _ -> true
// Stops pipeline collection at a constructor call (e.g. `Some(x)`) once at least one outer stage
// is already collected, so `g(f(Some(x)))` becomes `Some(x) |> f |> g` rather than decomposing the
// constructor's own argument into a further stage.
and formatterIsCapitalizedVarFunction : Expr -> Bool =
    given (expression) ->
        match formatterUnspanExpr(expression) with
            | ExprVar(name) ->
                match Ashes.Text.uncons(name) with
                    | None -> false
                    | Some((head, _tail)) ->
                        let code = Ashes.Rune.toInt(head)
                        in
                            if code >= 65
                            then code <= 90
                            else false
            | _ -> false
and formatterCanBePipelineFunction : Expr -> Bool =
    given (expression) ->
        match formatterUnspanExpr(expression) with
            | ExprLet(_, _, _, _, _, _) -> false
            | ExprLetRecursive(_, _, _, _, _, _) -> false
            | ExprLetResult(_, _, _) -> false
            | ExprIf(_, _, _) -> false
            | ExprMatch(_, _, _) -> false
            | ExprHandle(_, _) -> false
            | _ -> true
and formatterExpr : Expr -> Int -> Int -> Bool -> Str =
    given (expression) ->
        given (parent) ->
            given (indent) ->
                given (preferPipelines) ->
                    match formatterTryPipelineRendering(expression)(parent)(indent)(preferPipelines) with
                        | Some(rendered) -> rendered
                        | None ->
                            match formatterUnspanExpr(expression) with
                                | ExprInt(value) -> Ashes.Text.fromInt(value)
                                | ExprBigInt(value) -> value + "N"
                                | ExprUInt(_value, _bits, text) -> text
                                | ExprFloat(_value, text) -> text
                                | ExprString(value) -> "\"" + formatterEscape(value) + "\""
                                | ExprRune(value) ->
                                    "'" + formatterEscape(formatterRune(value)) + "'"
                                | ExprBool(value) ->
                                    if value
                                    then "true"
                                    else "false"
                                | ExprVar(name) -> name
                                | ExprQualifiedVar(moduleName, name) -> moduleName + "." + name
                                | ExprAdd(left, right) -> formatterBinary(parent)(10)("+")(left)(right)(preferPipelines)
                                | ExprSubtract(left, right) -> formatterBinary(parent)(10)("-")(left)(right)(preferPipelines)
                                | ExprMultiply(left, right) -> formatterBinary(parent)(11)("*")(left)(right)(preferPipelines)
                                | ExprDivide(left, right) -> formatterBinary(parent)(11)("/")(left)(right)(preferPipelines)
                                | ExprModulo(left, right) -> formatterBinary(parent)(11)("%")(left)(right)(preferPipelines)
                                | ExprBitwiseAnd(left, right) -> formatterBinary(parent)(7)("&")(left)(right)(preferPipelines)
                                | ExprBitwiseOr(left, right) -> formatterBinary(parent)(5)("|")(left)(right)(preferPipelines)
                                | ExprBitwiseXor(left, right) -> formatterBinary(parent)(6)("^")(left)(right)(preferPipelines)
                                | ExprShiftLeft(left, right) -> formatterBinary(parent)(9)("<<")(left)(right)(preferPipelines)
                                | ExprShiftRight(left, right) -> formatterBinary(parent)(9)(">>")(left)(right)(preferPipelines)
                                | ExprGreaterThan(left, right) -> formatterBinary(parent)(4)(">")(left)(right)(preferPipelines)
                                | ExprLessThan(left, right) -> formatterBinary(parent)(4)("<")(left)(right)(preferPipelines)
                                | ExprGreaterOrEqual(left, right) -> formatterBinary(parent)(4)(">=")(left)(right)(preferPipelines)
                                | ExprLessOrEqual(left, right) -> formatterBinary(parent)(4)("<=")(left)(right)(preferPipelines)
                                | ExprEqual(left, right) -> formatterBinary(parent)(4)("==")(left)(right)(preferPipelines)
                                | ExprNotEqual(left, right) -> formatterBinary(parent)(4)("!=")(left)(right)(preferPipelines)
                                | ExprResultPipe(left, right) -> formatterBinary(parent)(3)("|?>")(left)(right)(preferPipelines)
                                | ExprResultMapErrorPipe(left, right) -> formatterBinary(parent)(3)("|!>")(left)(right)(preferPipelines)
                                | ExprCons(head, tail) ->
                                    formatterWrap(
                                        parent,
                                        8,
                                        formatterExpr(head)(9)(indent)(preferPipelines) + " :: " + formatterExpr(tail)(8)(indent)(preferPipelines)
                                    )
                                | ExprBitwiseNot(operand) -> formatterWrap(parent)(12)("~" + formatterExpr(operand)(12)(indent)(preferPipelines))
                                | ExprLogicalNot(operand) -> formatterWrap(parent)(12)("!" + formatterExpr(operand)(12)(indent)(preferPipelines))
                                | ExprCall(function, argument, whitespace, layout) ->
                                    let rendered =
                                        if whitespace
                                        then formatterExpr(function)(13)(indent)(preferPipelines) + " " + formatterExpr(argument)(14)(indent)(preferPipelines)
                                        else formatterParenthesizedCall(expression)(function)(argument)(layout)(indent)(preferPipelines)
                                    in formatterWrap(parent)(13)(rendered)
                                | ExprTuple(elements) ->
                                    "(" + formatterJoin(", ")(given (item) -> formatterOperandInline(item)(preferPipelines))(elements) + ")"
                                | ExprList(elements, isMultiline) ->
                                    if isMultiline
                                    then formatterMultilineList(elements)(indent)(preferPipelines)
                                    else
                                        "[" + formatterJoin(", ")(given (item) -> formatterOperandInline(item)(preferPipelines))(elements) + "]"
                                | ExprRecord(name, fields, isMultiline) ->
                                    if isMultiline
                                    then formatterMultilineRecord(name)(fields)(indent)(preferPipelines)
                                    else name + "(" + formatterInlineFields(fields)(preferPipelines) + ")"
                                | ExprRecordUpdate(target, fields) ->
                                    formatterWrap(
                                        parent,
                                        2,
                                        formatterExpr(
                                            target,
                                            3,
                                            indent,
                                            preferPipelines
                                        ) + " with " + formatterJoin(", ")(given (field) -> formatterUpdateField(field)(preferPipelines))(fields)
                                    )
                                | ExprAwait(task) -> formatterWrap(parent)(12)("await " + formatterExpr(task)(12)(indent)(preferPipelines))
                                | ExprPerform(operation) ->
                                    formatterWrap(
                                        parent,
                                        12,
                                        "perform " + formatterExpr(operation)(12)(indent)(preferPipelines)
                                    )
                                | ExprIf(condition, thenBranch, elseBranch) ->
                                    formatterWrap(
                                        parent,
                                        1,
                                        "if " + formatterExpr(
                                            condition,
                                            0,
                                            indent,
                                            preferPipelines
                                        ) + "\n" + formatterIndent(indent) + "then" + formatterBranchSuffix(
                                            thenBranch,
                                            indent,
                                            preferPipelines
                                        ) + "\n" + formatterIndent(indent) + "else" + formatterBranchSuffix(
                                            elseBranch,
                                            indent,
                                            preferPipelines
                                        )
                                    )
                                | ExprLambda(name, body, annotation) ->
                                    let parameter =
                                        match annotation with
                                            | None -> name
                                            | Some(typeExpression) -> name + ": " + formatterType(typeExpression)
                                    in
                                        if formatterExpressionIsMultilineOrPipeline(body)(preferPipelines)
                                        then
                                            formatterWrap(
                                                parent,
                                                1,
                                                "given (" + parameter + ") ->\n" + formatterIndent(indent + 4) + formatterExpr(
                                                    body,
                                                    0,
                                                    indent + 4,
                                                    preferPipelines
                                                )
                                            )
                                        else
                                            formatterWrap(
                                                parent,
                                                1,
                                                "given (" + parameter + ") -> " + formatterExpr(body)(0)(indent)(preferPipelines)
                                            )
                                | ExprLet(name, value, body, parameters, annotation, requirements) ->
                                    formatterLet(
                                        parent,
                                        indent,
                                        "let ",
                                        name,
                                        value,
                                        body,
                                        parameters,
                                        annotation,
                                        requirements,
                                        preferPipelines
                                    )
                                | ExprLetRecursive(name, value, body, parameters, annotation, requirements) ->
                                    formatterLet(
                                        parent,
                                        indent,
                                        "let recursive ",
                                        name,
                                        value,
                                        body,
                                        parameters,
                                        annotation,
                                        requirements,
                                        preferPipelines
                                    )
                                | ExprLetResult(name, value, body) ->
                                    formatterWrap(
                                        parent,
                                        1,
                                        "let? " + name + " = " + formatterExpr(
                                            value,
                                            0,
                                            indent,
                                            preferPipelines
                                        ) + "\nin " + formatterExpr(body)(0)(indent)(preferPipelines)
                                    )
                                | ExprMatch(value, cases, _position) ->
                                    formatterWrap(
                                        parent,
                                        1,
                                        "match " + formatterExpr(value)(formatterOperandPrecedence(value))(indent)(preferPipelines) + " with\n" + formatterCases(cases)(indent + 4)(preferPipelines)
                                    )
                                | ExprHandle(body, arms) ->
                                    formatterWrap(
                                        parent,
                                        1,
                                        "handle " + formatterExpr(body)(0)(indent)(preferPipelines) + " with\n" + formatterArms(arms)(indent + 4)(preferPipelines)
                                    )
                                | ExprAt(_span, inner) -> formatterExpr(inner)(parent)(indent)(preferPipelines)
and formatterExpressionInline : Expr -> Str =
    given (expression) -> formatterExpr(expression)(0)(0)(false)
and formatterOperandInline : Expr -> Bool -> Str =
    given (expression) ->
        given (preferPipelines) ->
            formatterExpr(expression)(formatterOperandPrecedence(expression))(0)(preferPipelines)
and formatterParenthesizedCall : Expr -> Expr -> Expr -> CallArgumentListLayout -> Int -> Bool -> Str =
    given (expression) ->
        given (function) ->
            given (argument) ->
                given (layout) ->
                    given (indent) ->
                        given (preferPipelines) ->
                            if layout == callArgumentsInline
                            then
                                formatterExpr(function)(13)(indent)(preferPipelines) + "(" + formatterExpr(argument)(formatterOperandPrecedence(argument))(indent)(preferPipelines) + ")"
                            else formatterMultilineCall(expression)(indent)(preferPipelines)
// Inline record-literal fields, comma-separated. Only a field with a following sibling needs
// protection against an unparenthesized `with` absorbing the next `name = value` pair — the last
// field's own closing `)` already ends the update unambiguously.
and formatterInlineFields : List((Str, Expr)) -> Bool -> Str =
    given (fields) ->
        given (preferPipelines) ->
            match fields with
                | [] -> ""
                | (name, value) :: [] -> name + " = " + formatterExpr(value)(0)(0)(preferPipelines)
                | (name, value) :: tail ->
                    name + " = " + formatterExpr(value)(formatterFieldPrecedence(value))(0)(preferPipelines) + ", " + formatterInlineFields(tail)(preferPipelines)
and formatterUpdateField : (Str, Expr) -> Bool -> Str =
    given (field) ->
        given (preferPipelines) ->
            match field with
                | (name, value) -> name + " = " + formatterExpr(value)(3)(0)(preferPipelines)
and formatterMultilineRecord : Str -> List((Str, Expr)) -> Int -> Bool -> Str =
    given (name) ->
        given (fields) ->
            given (indent) ->
                given (preferPipelines) ->
                    name + "(\n" + formatterMultilineFields(
                        fields,
                        indent + 4,
                        preferPipelines
                    ) + "\n" + formatterIndent(indent) + ")"
and formatterMultilineFields : List((Str, Expr)) -> Int -> Bool -> Str =
    given (fields) ->
        given (indent) ->
            given (preferPipelines) ->
                match fields with
                    | [] -> ""
                    | (name, value) :: [] -> formatterIndent(indent) + name + " = " + formatterExpr(value)(0)(indent)(preferPipelines)
                    | (name, value) :: tail ->
                        formatterIndent(indent) + name + " = " + formatterExpr(
                            value,
                            formatterFieldPrecedence(value),
                            indent,
                            preferPipelines
                        ) + ",\n" + formatterMultilineFields(tail)(indent)(preferPipelines)
and formatterMultilineCall : Expr -> Int -> Bool -> Str =
    given (expression) ->
        given (indent) ->
            given (preferPipelines) ->
                match formatterCollectMultilineCall(expression)([]) with
                    | (function, arguments) ->
                        formatterExpr(function)(13)(indent)(preferPipelines) + "(\n" + formatterMultilineExpressions(
                            arguments,
                            indent + 4,
                            preferPipelines
                        ) + "\n" + formatterIndent(indent) + ")"
and formatterCollectMultilineCall : Expr -> List(Expr) -> (Expr, List(Expr)) =
    given (expression) ->
        given (laterArguments) ->
            match formatterUnspanExpr(expression) with
                | ExprCall(function, argument, _whitespace, layout) ->
                    match (layout == callArgumentsMultilineContinuation, layout == callArgumentsMultilineStart) with
                        | (true, _) -> formatterCollectMultilineCall(function)(argument :: laterArguments)
                        | (false, true) -> (function, argument :: laterArguments)
                        | _ -> (expression, laterArguments)
                | _ -> (expression, laterArguments)
and formatterMultilineList : List(Expr) -> Int -> Bool -> Str =
    given (elements) ->
        given (indent) ->
            given (preferPipelines) ->
                let renderedElements = formatterMultilineExpressions(elements)(indent + 4)(preferPipelines)
                in "[\n" + renderedElements + "\n" + formatterIndent(indent) + "]"
and formatterMultilineExpressions : List(Expr) -> Int -> Bool -> Str =
    given (expressions) ->
        given (indent) ->
            given (preferPipelines) ->
                match expressions with
                    | [] -> ""
                    | expression :: [] -> formatterIndent(indent) + formatterExpr(expression)(0)(indent)(preferPipelines)
                    | expression :: tail ->
                        formatterIndent(indent) + formatterExpr(
                            expression,
                            formatterFieldPrecedence(expression),
                            indent,
                            preferPipelines
                        ) + ",\n" + formatterMultilineExpressions(tail)(indent)(preferPipelines)
and formatterLet : Int -> Int -> Str -> Str -> Expr -> Expr -> List(Str) -> Maybe(TypeExpr) -> List(TraitConstraintSyntax) -> Bool -> Str =
    given (parent) ->
        given (indent) ->
            given (prefix) ->
                given (name) ->
                    given (value) ->
                        given (body) ->
                            given (parameters) ->
                                given (annotation) ->
                                    given (requirements) ->
                                        given (preferPipelines) ->
                                            let annotationText =
                                                match annotation with
                                                    | None -> ""
                                                    | Some(typeExpression) ->
                                                        " : " + formatterType(
                                                            typeExpression
                                                        ) + formatterRequirements(requirements)
                                            in
                                                match formatterSugarParameters(parameters)(value)("") with
                                                    | (parameterText, formattedValue) ->
                                                        formatterWrap(
                                                            parent,
                                                            1,
                                                            prefix + name + parameterText + annotationText + " =" + formatterBranchSuffix(
                                                                formattedValue,
                                                                indent,
                                                                preferPipelines
                                                            ) + "\nin " + formatterExpr(body)(0)(indent)(preferPipelines)
                                                        )
and formatterSugarParameters : List(Str) -> Expr -> Str -> (Str, Expr) =
    given (parameters) ->
        given (value) ->
            given (rendered) ->
                match parameters with
                    | [] -> (rendered, value)
                    | parameter :: tail ->
                        match formatterUnspanExpr(value) with
                            | ExprLambda(_name, lambdaBody, _annotation) ->
                                formatterSugarParameters(
                                    tail,
                                    lambdaBody,
                                    rendered + " " + parameter
                                )
                            | _ -> (rendered, value)
and formatterRequirements : List(TraitConstraintSyntax) -> Str =
    given (requirements) ->
        let sorted =
            sortBy(given (left) ->
                given (right) -> formatterRequirement(left) <= formatterRequirement(right))(requirements)
        in
            match requirements with
                | [] -> ""
                | _ -> " requires {" + formatterJoin(", ")(formatterRequirement)(sorted) + "}"
and formatterRequirement : TraitConstraintSyntax -> Str =
    given (requirement) ->
        match requirement with
            | TraitConstraintSyntax { traitName = name, typeArguments = arguments } ->
                name + "(" + formatterJoin(
                    ", ",
                    formatterType,
                    arguments
                ) + ")"
and formatterCases : List((Pattern, Expr, Maybe(Expr))) -> Int -> Bool -> Str =
    given (cases) ->
        given (indent) ->
            given (preferPipelines) ->
                match cases with
                    | [] -> ""
                    | (pattern, body, guard) :: tail ->
                        let guardText =
                            match guard with
                                | None -> ""
                                | Some(condition) -> " when " + formatterExpressionInline(condition)
                        in
                            formatterIndent(indent) + "| " + formatterPatternAt(
                                pattern,
                                0
                            ) + guardText + " ->" + formatterBranchSuffix(
                                body,
                                indent,
                                preferPipelines
                            ) + formatterFollowingCases(tail)(indent)(preferPipelines)
and formatterFollowingCases : List((Pattern, Expr, Maybe(Expr))) -> Int -> Bool -> Str =
    given (cases) ->
        given (indent) ->
            given (preferPipelines) ->
                match cases with
                    | [] -> ""
                    | _ -> "\n" + formatterCases(cases)(indent)(preferPipelines)
and formatterArms : List((Maybe(Str), Str, List(Pattern), Expr)) -> Int -> Bool -> Str =
    given (arms) ->
        given (indent) ->
            given (preferPipelines) ->
                match arms with
                    | [] -> ""
                    | (moduleName, operationName, parameters, body) :: tail ->
                        let prefix =
                            match moduleName with
                                | None -> operationName
                                | Some(moduleValue) -> moduleValue + "." + operationName
                        in
                            formatterIndent(indent) + "| " + prefix + "(" + formatterJoin(
                                ", ",
                                given (item) -> formatterPatternAt(item)(0),
                                parameters
                            ) + ") ->" + formatterBranchSuffix(body)(indent)(preferPipelines) + formatterFollowingArms(tail)(indent)(preferPipelines)
and formatterFollowingArms : List((Maybe(Str), Str, List(Pattern), Expr)) -> Int -> Bool -> Str =
    given (arms) ->
        given (indent) ->
            given (preferPipelines) ->
                match arms with
                    | [] -> ""
                    | _ -> "\n" + formatterArms(arms)(indent)(preferPipelines)
and formatterExpressionIsMultiline : Expr -> Bool =
    given (expression) ->
        match formatterUnspanExpr(expression) with
            | ExprLambda(_, _, _) -> true
            | ExprLet(_, _, _, _, _, _) -> true
            | ExprLetRecursive(_, _, _, _, _, _) -> true
            | ExprLetResult(_, _, _) -> true
            | ExprIf(_, _, _) -> true
            | ExprMatch(_, _, _) -> true
            | ExprHandle(_, _) -> true
            | ExprCall(_, _, _, layout) -> layout != callArgumentsInline
            | ExprList(_, isMultiline) -> isMultiline
            | ExprRecord(_, _, isMultiline) -> isMultiline
            | _ -> false
// Like formatterExpressionIsMultiline, but also true when preferPipelines would rewrite this
// expression into a multiline `|>` pipeline -- a fact formatterExpressionIsMultiline's own
// static AST-shape check cannot see, since pipeline eligibility depends on preferPipelines and a
// walk of the call chain, not a fixed flag on the expression node.
and formatterExpressionIsMultilineOrPipeline : Expr -> Bool -> Bool =
    given (expression) ->
        given (preferPipelines) ->
            if formatterExpressionIsMultiline(expression)
            then true
            else
                if preferPipelines
                then
                    match formatterTryCollectPipeline(expression) with
                        | Some(_) -> true
                        | None -> false
                else false
// The single-line-or-multiline rendering shared by an `if` branch, a `let` value, a match-case
// body, and a handler-arm body: a single-line value follows its introducer (`then`, `=`, `->`) on
// the same line with a leading space; a multiline value starts on its own line, one indent level
// deeper, with no trailing space left on the introducer's own line (the introducer never has a
// dangling space that would need a separate trailing-whitespace trim to clean up).
and formatterBranchSuffix : Expr -> Int -> Bool -> Str =
    given (expression) ->
        given (indent) ->
            given (preferPipelines) ->
                if formatterExpressionIsMultilineOrPipeline(expression)(preferPipelines)
                then "\n" + formatterIndent(indent + 4) + formatterExpr(expression)(0)(indent + 4)(preferPipelines)
                else " " + formatterExpr(expression)(0)(indent)(preferPipelines)

let formatExpression expression = formatterExpr(expression)(0)(0)(false) + "\n"

let formatTypeExpression typeExpression = formatterType(typeExpression)

let formatPattern pattern = formatterPatternAt(pattern)(0)

let formatterTypeParameter parameter =
    match parameter with
        | TypeParameter { name = name } -> name

let formatterTypeParameters parameters =
    match parameters with
        | [] -> ""
        | _ -> "(" + formatterJoin(", ")(formatterTypeParameter)(parameters) + ")"

let formatterDeriving traits =
    match traits with
        | [] -> ""
        | _ ->
            "    deriving {" + formatterJoin(", ")(given (name) -> name)(traits) + "}\n"

let formatterConstructor constructor =
    match constructor with
        | TypeConstructor { name = name, parameters = [], fieldNames = _fields } -> "    | " + name + "\n"
        | TypeConstructor { name = name, parameters = parameters, fieldNames = _fields } ->
            "    | " + name + "(" + formatterJoin(
                ", ",
                formatterType,
                parameters
            ) + ")\n"

let recursive formatterConstructors constructors =
    match constructors with
        | [] -> ""
        | constructor :: tail -> formatterConstructor(constructor) + formatterConstructors(tail)

let recursive formatterRecordFields names parameters =
    match (names, parameters) with
        | (name :: nameTail, parameter :: parameterTail) ->
            "    | " + name + ": " + formatterType(
                parameter
            ) + "\n" + formatterRecordFields(
                nameTail,
                parameterTail
            )
        | _ -> ""

let formatterTypeDeclaration declaration =
    match declaration with
        | TypeDecl { name = name, typeParameters = parameters, constructors = constructors, isRecord = isRecord, derivingTraits = derivingTraits } ->
            let body =
                if isRecord
                then
                    match constructors with
                        | TypeConstructor { name = _constructorName, parameters = fieldTypes, fieldNames = fieldNames } :: _ ->
                            formatterRecordFields(
                                fieldNames,
                                fieldTypes
                            )
                        | [] -> ""
                else formatterConstructors(constructors)
            in "type " + name + formatterTypeParameters(parameters) + " =\n" + body + formatterDeriving(derivingTraits)

let formatterTypeAliasDeclaration declaration =
    match declaration with
        | TypeAliasDecl { name = name, typeParameters = parameters, target = target } ->
            "type alias " + name + formatterTypeParameters(
                parameters
            ) + " = " + formatterType(target) + "\n"

let formatterZeroCostTypeDeclaration declaration =
    match declaration with
        | ZeroCostTypeDecl { name = name, typeParameters = parameters, constructor = TypeConstructor { name = constructorName, parameters = constructorType :: _tail, fieldNames = _fields }, derivingTraits = derivingTraits } ->
            "type " + name + formatterTypeParameters(
                parameters
            ) + " = " + constructorName + "(" + formatterType(
                constructorType
            ) + ")\n" + formatterDeriving(derivingTraits)
        | ZeroCostTypeDecl { name = name, typeParameters = parameters, constructor = TypeConstructor { name = constructorName, parameters = [], fieldNames = _fields }, derivingTraits = derivingTraits } ->
            "type " + name + formatterTypeParameters(
                parameters
            ) + " = " + constructorName + "()\n" + formatterDeriving(
                derivingTraits
            )

let formatterExportConstructors constructors =
    match constructors with
        | ExportConstructorsHidden -> ""
        | ExportConstructorsAll -> "(..)"
        | ExportConstructorsSelected(names) ->
            "(" + formatterJoin(", ")(given (name) -> name)(names) + ")"

let formatterExportItem item =
    match item with
        | ExportValue(name) -> "    value " + name + ",\n"
        | ExportType(name, constructors) -> "    type " + name + formatterExportConstructors(constructors) + ",\n"
        | ExportModule(name) -> "    module " + name + ",\n"

let recursive formatterExportItems items =
    match items with
        | [] -> ""
        | item :: tail -> formatterExportItem(item) + formatterExportItems(tail)

let formatterExportDeclaration declaration =
    match declaration with
        | ExportDecl { items = items } -> "export (\n" + formatterExportItems(items) + ")\n"

let recursive formatterParsedType parsedType =
    match parsedType with
        | ParsedNamed(name) -> name
        | ParsedPointer(inner) -> "*" + formatterParsedType(inner)
        | ParsedBuffer(inner) -> "FfiBuffer(" + formatterParsedType(inner) + ")"
        | ParsedOut(inner) -> "out " + formatterParsedType(inner)
        | ParsedNativeString(nullable, ownership, destructor) ->
            let nullableText =
                if nullable
                then "nullable "
                else ""
            in
                let ownershipText =
                    match (ownership, destructor) with
                        | (FfiStringBorrowed, _) -> "borrowed"
                        | (FfiStringOwned, Some(name)) -> "owned " + name
                        | (FfiStringOwned, None) -> "owned"
                in "FfiStr(" + nullableText + ownershipText + ")"

let formatterExternalOwnership ownership =
    match ownership with
        | ExternalOwnershipUnspecified -> ""
        | ExternalOwnershipBorrow -> "borrow "
        | ExternalOwnershipConsume -> "consume "

let recursive formatterExternalParameters parameterTypes ownerships =
    match parameterTypes with
        | [] -> ""
        | parameter :: tail ->
            let ownership =
                match ownerships with
                    | head :: _ -> head
                    | [] -> ExternalOwnershipUnspecified
            in
                let remainingOwnerships =
                    match ownerships with
                        | _head :: rest -> rest
                        | [] -> []
                in
                    let suffix =
                        match tail with
                            | [] -> ""
                            | _ -> ", " + formatterExternalParameters(tail)(remainingOwnerships)
                    in formatterExternalOwnership(ownership) + formatterParsedType(parameter) + suffix

let formatterCapabilityRef capabilityReference =
    match capabilityReference with
        | CapabilityRefSyntax { name = name, args = [] } -> name
        | CapabilityRefSyntax { name = name, args = arguments } ->
            name + "(" + formatterJoin(
                ", ",
                formatterType,
                arguments
            ) + ")"

let formatterNeedsRow row =
    match row with
        | NeedsRowSyntax { capabilities = [], tailVariable = Some(tail) } -> " needs " + tail
        | NeedsRowSyntax { capabilities = capabilities, tailVariable = tailVariable } ->
            let capabilitiesText = formatterJoin(", ")(formatterCapabilityRef)(capabilities)
            in
                match tailVariable with
                    | None -> " needs {" + capabilitiesText + "}"
                    | Some(tail) -> " needs {" + capabilitiesText + " | " + tail + "}"

let formatterExternalDeclaration declaration =
    match declaration with
        | ExternalOpaqueType(name, None) -> "external type " + name + "\n"
        | ExternalOpaqueType(name, Some(destructor)) -> "external type " + name + " resource destructor " + destructor + "\n"
        | ExternalFunction(name, parameterTypes, returnType, symbol, ownerships, needsRow) ->
            let needsText =
                match needsRow with
                    | None -> ""
                    | Some(row) -> formatterNeedsRow(row)
            in
                let symbolText =
                    match symbol with
                        | None -> ""
                        | Some(value) -> " = \"" + formatterEscape(value) + "\""
                in
                    "external " + name + "(" + formatterExternalParameters(
                        parameterTypes,
                        ownerships
                    ) + ") -> " + formatterParsedType(returnType) + needsText + symbolText + "\n"

let formatterCapabilityOperation operation =
    match operation with
        | CapabilityOperation { name = name, signature = None } -> "    | " + name + "\n"
        | CapabilityOperation { name = name, signature = Some(signature) } ->
            "    | " + name + " : " + formatterType(
                signature
            ) + "\n"

let recursive formatterCapabilityOperations operations =
    match operations with
        | [] -> ""
        | operation :: tail -> formatterCapabilityOperation(operation) + formatterCapabilityOperations(tail)

let formatterCapabilityDeclaration declaration =
    match declaration with
        | CapabilityDecl { name = name, typeParameters = parameters, operations = operations } ->
            "capability " + name + formatterTypeParameters(
                parameters
            ) + " =\n" + formatterCapabilityOperations(
                operations
            )

let formatterIsMultiline expression = formatterExpressionIsMultiline(expression)

let formatterDeclarationValue expression indent preferPipelines =
    if formatterIsMultiline(expression)
    then "\n" + formatterIndent(indent) + formatterExpr(expression)(0)(indent)(preferPipelines) + "\n"
    else " " + formatterExpr(expression)(0)(0)(preferPipelines) + "\n"

let formatterProvideBinding binding preferPipelines =
    match binding with
        | ProvideBinding { operationName = name, implementation = implementation } ->
            "    | " + name + " =" + formatterDeclarationValue(
                implementation,
                8,
                preferPipelines
            )

let recursive formatterProvideBindings bindings preferPipelines =
    match bindings with
        | [] -> ""
        | binding :: tail -> formatterProvideBinding(binding)(preferPipelines) + formatterProvideBindings(tail)(preferPipelines)

let formatterProvideDeclaration declaration preferPipelines =
    match declaration with
        | ProvideDecl { capabilityName = name, typeArguments = arguments, bindings = bindings } ->
            let argumentsText =
                match arguments with
                    | [] -> ""
                    | _ -> "(" + formatterJoin(", ")(formatterType)(arguments) + ")"
            in "provide " + name + argumentsText + " =\n" + formatterProvideBindings(bindings)(preferPipelines)

let formatterTraitMethod method preferPipelines =
    match method with
        | TraitMethodDecl { name = name, signature = signature, defaultImplementation = None } ->
            "    | " + name + " : " + formatterType(
                signature
            ) + "\n"
        | TraitMethodDecl { name = name, signature = signature, defaultImplementation = Some(implementation) } ->
            "    | " + name + " : " + formatterType(
                signature
            ) + " =" + formatterDeclarationValue(
                implementation,
                8,
                preferPipelines
            )

let recursive formatterTraitMethods methods preferPipelines =
    match methods with
        | [] -> ""
        | method :: tail -> formatterTraitMethod(method)(preferPipelines) + formatterTraitMethods(tail)(preferPipelines)

let formatterTraitDeclaration declaration preferPipelines =
    match declaration with
        | TraitDecl { name = name, typeParameters = parameters, supertraits = supertraits, methods = methods } ->
            "trait " + name + formatterTypeParameters(
                parameters
            ) + formatterRequirements(
                supertraits
            ) + " =\n" + formatterTraitMethods(methods)(preferPipelines)

let formatterImplementationBinding binding preferPipelines =
    match binding with
        | TraitImplementationMethodBinding { methodName = name, implementation = implementation } ->
            "    | " + name + " =" + formatterDeclarationValue(
                implementation,
                8,
                preferPipelines
            )

let recursive formatterImplementationBindings bindings preferPipelines =
    match bindings with
        | [] -> ""
        | binding :: tail -> formatterImplementationBinding(binding)(preferPipelines) + formatterImplementationBindings(tail)(preferPipelines)

let formatterImplementationDeclaration declaration preferPipelines =
    match declaration with
        | TraitImplementationDecl { traitName = name, typeArguments = arguments, requirements = requirements, bindings = bindings } ->
            "implement " + name + "(" + formatterJoin(
                ", ",
                formatterType,
                arguments
            ) + ")" + formatterRequirements(requirements) + " =\n" + formatterImplementationBindings(bindings)(preferPipelines)

let formatterLetBinding prefix binding preferPipelines =
    match binding with
        | LetBindingSyntax { name = name, value = value, sugarParameters = parameters, typeAnnotation = annotation, requirements = requirements } ->
            let annotationText =
                match annotation with
                    | None -> ""
                    | Some(typeExpression) ->
                        " : " + formatterType(
                            typeExpression
                        ) + formatterRequirements(requirements)
            in
                match formatterSugarParameters(parameters)(value)("") with
                    | (parameterText, formattedValue) ->
                        prefix + name + parameterText + annotationText + " =" + formatterDeclarationValue(
                            formattedValue,
                            4,
                            preferPipelines
                        )

let recursive formatterRecursiveBindings bindings first preferPipelines =
    match bindings with
        | [] -> ""
        | binding :: tail ->
            let prefix =
                if first
                then "let recursive "
                else "and "
            in formatterLetBinding(prefix)(binding)(preferPipelines) + formatterRecursiveBindings(tail)(false)(preferPipelines)

let recursive formatterUnspanTopLevel item =
    match item with
        | TopLevelAt(_span, inner) -> formatterUnspanTopLevel(inner)
        | _ -> item

let formatterIsExternal item =
    match formatterUnspanTopLevel(item) with
        | TopLevelExternal(_) -> true
        | _ -> false

let recursive formatterTopLevelItem item preferPipelines =
    match formatterUnspanTopLevel(item) with
        | TopLevelExport(declaration) -> formatterExportDeclaration(declaration)
        | TopLevelType(declaration) -> formatterTypeDeclaration(declaration)
        | TopLevelTypeAlias(declaration) -> formatterTypeAliasDeclaration(declaration)
        | TopLevelZeroCostType(declaration) -> formatterZeroCostTypeDeclaration(declaration)
        | TopLevelExternal(declaration) -> formatterExternalDeclaration(declaration)
        | TopLevelCapability(declaration) -> formatterCapabilityDeclaration(declaration)
        | TopLevelProvide(declaration) -> formatterProvideDeclaration(declaration)(preferPipelines)
        | TopLevelTrait(declaration) -> formatterTraitDeclaration(declaration)(preferPipelines)
        | TopLevelImplementation(declaration) -> formatterImplementationDeclaration(declaration)(preferPipelines)
        | TopLevelLet(binding, isRecursive) ->
            if isRecursive
            then formatterLetBinding("let recursive ")(binding)(preferPipelines)
            else formatterLetBinding("let ")(binding)(preferPipelines)
        | TopLevelRecursiveGroup(bindings) -> formatterRecursiveBindings(bindings)(true)(preferPipelines)
        | TopLevelAt(_span, inner) -> formatterTopLevelItem(inner)(preferPipelines)

// Consecutive externals form one declaration block; every other top-level boundary gets a blank line.
let recursive formatterProgramItems items first previousExternal preferPipelines =
    match items with
        | [] -> ""
        | item :: tail ->
            let currentExternal = formatterIsExternal(item)
            in
                let separator =
                    if first
                    then ""
                    else
                        if previousExternal
                        then
                            if currentExternal
                            then ""
                            else "\n"
                        else "\n"
                in separator + formatterTopLevelItem(item)(preferPipelines) + formatterProgramItems(tail)(false)(currentExternal)(preferPipelines)

let formatterProgramWith program preferPipelines =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            let declarations = formatterProgramItems(items)(true)(false)(preferPipelines)
            in
                match (items, body) with
                    | ([], None) -> "\n"
                    | ([], Some(expression)) -> formatterExpr(expression)(0)(0)(preferPipelines) + "\n"
                    | (_, None) -> declarations
                    | (_, Some(expression)) -> declarations + "\n" + formatterExpr(expression)(0)(0)(preferPipelines) + "\n"

let formatProgram program = formatterProgramWith(program)(false)

// The whitespace conventions the formatter emits with: indent width, tabs versus spaces, and line
// ending. Every writer above builds its output assuming 4-space indentation and "\n" line endings
// (an invariant checked by the self-hosted formatter's own parity tests: every leading-whitespace
// run in the canonical output is an exact multiple of 4, since the only ways `indent` is threaded
// are "start at 0/4/8" and "+4 per nesting level"); formatterApplyOptions rescales that fixed
// baseline to the requested options in one post-processing pass, mirroring how stage 0's
// `FinishOutput` trims trailing whitespace and applies the configured newline as a final step
// rather than threading them through every writer.
type FormattingOptions =
    | indentSize: Int
    | useTabs: Bool
    | newLine: Str

let formattingOptionsDefault = FormattingOptions(indentSize = 4, useTabs = false, newLine = "\n")

let formattingOptionsNormalize options =
    match options with
        | FormattingOptions { indentSize = indentSize, useTabs = useTabs, newLine = newLine } ->
            let normalizedIndentSize =
                if indentSize > 0
                then indentSize
                else 4
            in
                let normalizedNewLine =
                    if newLine == "\n"
                    then "\n"
                    else
                        if newLine == "\r\n"
                        then "\r\n"
                        else "\n"
                in FormattingOptions(indentSize = normalizedIndentSize, useTabs = useTabs, newLine = normalizedNewLine)

let recursive formatterRepeatUnit unit count =
    if count <= 0
    then ""
    else unit + formatterRepeatUnit(unit)(count - 1)

// The target indentation for a nesting depth (0, 1, 2, ...), not a raw column count: `formatterIndent`
// above only ever produced spaces in multiples of 4, so a leading run's depth is that count divided
// by 4, and re-rendering by depth (rather than by scaled column count) makes the tab case exact
// rather than needing stage 0's separate tab/remainder-space split.
let formatterIndentForDepth depth options =
    match options with
        | FormattingOptions { indentSize = indentSize, useTabs = useTabs } ->
            if useTabs
            then formatterRepeatUnit("\t")(depth)
            else formatterRepeatUnit(" ")(depth * indentSize)

let byteSpace = 32

let recursive formatterLeadingSpaceByteCount bytes index total =
    if index >= total
    then index
    else
        if Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(index)) == byteSpace
        then formatterLeadingSpaceByteCount(bytes)(index + 1)(total)
        else index

let formatterRescaleLine options line =
    (let bytes = Ashes.Byte.fromText(line)
    in
        let total = Ashes.Byte.length(bytes)
        in
            let leadingSpaces = formatterLeadingSpaceByteCount(bytes)(0)(total)
            in
                let rest = Ashes.Byte.subText(bytes)(leadingSpaces)(total - leadingSpaces)
                in
                    let restTrimmed = Ashes.Text.trimEnd(rest)
                    in
                        if restTrimmed == ""
                        then ""
                        else formatterIndentForDepth(leadingSpaces / 4)(options) + restTrimmed)

// Applies `options` to already-formatted (fixed 4-space, "\n") text: rescales every line's leading
// indentation by nesting depth, trims trailing whitespace (mirroring stage 0's FinishOutput), and
// joins with the configured line ending.
let formatterApplyOptions options text =
    (let normalized = formattingOptionsNormalize(options)
    in
        "\n"
        |> Ashes.Text.split(text)
        |> listMap(formatterRescaleLine(normalized))
        |> Ashes.Text.join(normalized.newLine))

let formatProgramWithOptions program preferPipelines options =
    preferPipelines
    |> formatterProgramWith(program)
    |> formatterApplyOptions(options)

let formatExpressionWithOptions expression preferPipelines options = formatterApplyOptions(options)(formatterExpr(expression)(0)(0)(preferPipelines) + "\n")
