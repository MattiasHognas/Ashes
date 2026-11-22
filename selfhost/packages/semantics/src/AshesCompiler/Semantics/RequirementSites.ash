// Where a trait requirement that a binding or the trailing expression leaves unsatisfied was
// raised, as stage 0 reports it: at the operator, the trait method reference, or the argument of
// `Ashes.IO.print` that introduced it.
//
// The search is syntactic: the first such site in source order. A `let` whose value is a function
// is searched in its body only, since the function's own requirements enter its scheme and reach
// the enclosing expression through uses of the name, which stage 0 does not report at the function.

import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Frontend.Token.TextSpan
export (
    value requirementSiteSpan,
    value requirementSiteWithContext,
)

let recursive unspanned (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> unspanned(inner)
        | ExprNode(_id, inner) -> unspanned(inner)
        | other -> other

let recursive spanOf (expression: Expr) =
    match expression with
        | ExprAt(span, _inner) -> Some(span)
        | ExprNode(_id, inner) -> spanOf(inner)
        | _ -> None

let isFunctionValue (expression: Expr) =
    match unspanned(expression) with
        | ExprLambda(_parameter, _body, _annotation) -> true
        | _ -> false

let operatorTrait (expression: Expr) =
    match expression with
        | ExprAdd(_left, _right) -> Some("Add")
        | ExprSubtract(_left, _right) -> Some("Subtract")
        | ExprMultiply(_left, _right) -> Some("Multiply")
        | ExprDivide(_left, _right) -> Some("Divide")
        | ExprModulo(_left, _right) -> Some("Remainder")
        | ExprBitwiseAnd(_left, _right) -> Some("BitAnd")
        | ExprBitwiseOr(_left, _right) -> Some("BitOr")
        | ExprBitwiseXor(_left, _right) -> Some("BitXor")
        | ExprShiftLeft(_left, _right) -> Some("ShiftLeft")
        | ExprShiftRight(_left, _right) -> Some("ShiftRight")
        | ExprLogicalNot(_operand) -> Some("Not")
        | ExprEqual(_left, _right) -> Some("Eq")
        | ExprNotEqual(_left, _right) -> Some("Eq")
        | ExprGreaterThan(_left, _right) -> Some("Ord")
        | ExprLessThan(_left, _right) -> Some("Ord")
        | ExprGreaterOrEqual(_left, _right) -> Some("Ord")
        | ExprLessOrEqual(_left, _right) -> Some("Ord")
        | _ -> None

// Whether `name` ends with `suffix`.
let endsWithText (suffix: Str) (name: Str) =
    (let length = Ashes.Text.length(name)
    in
        let suffixLength = Ashes.Text.length(suffix)
        in length > suffixLength && Ashes.Text.substring(name)(length - suffixLength)(suffixLength) == suffix)

// Whether `moduleName` names the trait `traitName`: written bare, qualified, or as the compiler name
// stitching gives a shipped trait (`Ashes_Trait_Default`).
let namesTrait (traitName: Str) (moduleName: Str) = moduleName == traitName || endsWithText("." + traitName)(moduleName) || endsWithText("_" + traitName)(moduleName)

let isPrintCallee (function: Expr) =
    match unspanned(function) with
        | ExprQualifiedVar("Ashes.IO", "print") -> true
        | ExprVar("Ashes.IO.print") -> true
        | _ -> false

// The site of `expression` itself, whose innermost enclosing span is `enclosing`, when it raises a
// requirement of `traitName`.
let siteHere (traitName: Str) (expression: Expr) (enclosing: Maybe(TextSpan)) =
    if traitName == "$PrintArgument"
    then
        match expression with
            | ExprCall(function, argument, _whitespace, _layout) ->
                if isPrintCallee(function)
                then spanOf(argument)
                else None
            | _ -> None
    else
        match expression with
            | ExprQualifiedVar(moduleName, _member) ->
                if namesTrait(traitName)(moduleName)
                then enclosing
                else None
            | ExprVar(name) ->
                if Ashes.Text.startsWith(name)(traitName + ".") || Ashes.Text.contains(name)("." + traitName + ".")
                then enclosing
                else None
            | other ->
                match operatorTrait(other) with
                    | Some(operator) ->
                        if operator == traitName
                        then enclosing
                        else None
                    | None -> None

// Where the search stands: the innermost enclosing span, and the diagnostic contexts stage 0 has
// pushed around the expression there (an `if` else branch, a match arm), innermost first.
type SiteScope =
    | span: Maybe(TextSpan)
    | contexts: List(Str)

let withSpan (span: TextSpan) (scope: SiteScope) = SiteScope(span = Some(span), contexts = scope.contexts)

let withContext (context: Str) (scope: SiteScope) = SiteScope(span = scope.span, contexts = context :: scope.contexts)

let recursive requirementSite (traitName: Str) (expression: Expr) (enclosing: SiteScope) =
    match expression with
        | ExprAt(span, inner) ->
            enclosing
            |> withSpan(span)
            |> requirementSite(traitName)(inner)
        | ExprNode(_id, inner) -> requirementSite(traitName)(inner)(enclosing)
        | other ->
            match siteHere(traitName)(other)(enclosing.span) with
                | Some(site) -> Some((site, enclosing.contexts))
                | None -> childSite(traitName)(enclosing)(other)
and firstSite (traitName: Str) (enclosing: SiteScope) (expressions: List(Expr)) =
    match expressions with
        | [] -> None
        | expression :: rest ->
            match requirementSite(traitName)(expression)(enclosing) with
                | Some(site) -> Some(site)
                | None -> firstSite(traitName)(enclosing)(rest)
and fieldSites (traitName: Str) (enclosing: SiteScope) (fields: List((Str, Expr))) =
    match fields with
        | [] -> None
        | (_name, value) :: rest ->
            match requirementSite(traitName)(value)(enclosing) with
                | Some(site) -> Some(site)
                | None -> fieldSites(traitName)(enclosing)(rest)
and caseSites (traitName: Str) (enclosing: SiteScope) (index: Int) (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> None
        | (_pattern, body, guard) :: rest ->
            let armScope = withContext("in match arm " + Ashes.Text.fromInt(index))(enclosing)
            in
                let guarded =
                    match guard with
                        | Some(condition) -> requirementSite(traitName)(condition)(armScope)
                        | None -> None
                in
                    match guarded with
                        | Some(site) -> Some(site)
                        | None ->
                            match requirementSite(traitName)(body)(armScope) with
                                | Some(site) -> Some(site)
                                | None -> caseSites(traitName)(enclosing)(index + 1)(rest)
and armSites (traitName: Str) (enclosing: SiteScope) (arms: List((Maybe(Str), Str, List(Pattern), Expr))) =
    match arms with
        | [] -> None
        | (_capability, _operation, _patterns, body) :: rest ->
            match requirementSite(traitName)(body)(enclosing) with
                | Some(site) -> Some(site)
                | None -> armSites(traitName)(enclosing)(rest)
and letSite (traitName: Str) (enclosing: SiteScope) (value: Expr) (body: Expr) =
    if isFunctionValue(value)
    then requirementSite(traitName)(body)(enclosing)
    else firstSite(traitName)(enclosing)([value, body])
and childSite (traitName: Str) (enclosing: SiteScope) (expression: Expr) =
    match expression with
        | ExprAdd(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprSubtract(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprMultiply(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprDivide(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprModulo(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprBitwiseAnd(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprBitwiseOr(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprBitwiseXor(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprShiftLeft(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprShiftRight(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprBitwiseNot(operand) -> requirementSite(traitName)(operand)(enclosing)
        | ExprLogicalNot(operand) -> requirementSite(traitName)(operand)(enclosing)
        | ExprLogicalAnd(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprLogicalOr(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprGreaterThan(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprLessThan(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprGreaterOrEqual(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprLessOrEqual(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprEqual(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprNotEqual(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprResultPipe(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprResultMapErrorPipe(left, right) -> firstSite(traitName)(enclosing)([left, right])
        | ExprLet(_name, value, body, _parameters, _annotation, _requirements) -> letSite(traitName)(enclosing)(value)(body)
        | ExprLetResult(_name, value, body) -> firstSite(traitName)(enclosing)([value, body])
        | ExprLetRecursive(_name, value, body, _parameters, _annotation, _requirements) -> letSite(traitName)(enclosing)(value)(body)
        | ExprIf(condition, thenBranch, elseBranch) ->
            match firstSite(traitName)(enclosing)([condition, thenBranch]) with
                | Some(site) -> Some(site)
                | None ->
                    enclosing
                    |> withContext("in if branches")
                    |> requirementSite(traitName)(elseBranch)
        | ExprLambda(_parameter, body, _annotation) -> requirementSite(traitName)(body)(enclosing)
        | ExprCall(function, argument, _whitespace, _layout) -> firstSite(traitName)(enclosing)([function, argument])
        | ExprTuple(elements) -> firstSite(traitName)(enclosing)(elements)
        | ExprList(elements, _spread) -> firstSite(traitName)(enclosing)(elements)
        | ExprCons(head, tail) -> firstSite(traitName)(enclosing)([head, tail])
        | ExprMatch(scrutinee, cases, _hint) ->
            match requirementSite(traitName)(scrutinee)(enclosing) with
                | Some(site) -> Some(site)
                | None -> caseSites(traitName)(enclosing)(1)(cases)
        | ExprAwait(operand) -> requirementSite(traitName)(operand)(enclosing)
        | ExprRecord(_name, fields, _spread) -> fieldSites(traitName)(enclosing)(fields)
        | ExprRecordUpdate(base, fields) ->
            match requirementSite(traitName)(base)(enclosing) with
                | Some(site) -> Some(site)
                | None -> fieldSites(traitName)(enclosing)(fields)
        | ExprPerform(operation) -> requirementSite(traitName)(operation)(enclosing)
        | ExprHandle(body, arms) ->
            match requirementSite(traitName)(body)(enclosing) with
                | Some(site) -> Some(site)
                | None -> armSites(traitName)(enclosing)(arms)
        | _ -> None

let emptyScope = SiteScope(span = None, contexts = [])

// The span stage 0 reports a requirement of `traitName` raised in `expression` at, if any.
let requirementSiteSpan (traitName: Str) (expression: Expr) =
    match requirementSite(traitName)(expression)(emptyScope) with
        | Some((span, _contexts)) -> Some(span)
        | None -> None

// The span of that site and stage 0's diagnostic context there, outermost first and joined as stage
// 0 joins it, or None for a site in no context.
let requirementSiteWithContext (traitName: Str) (expression: Expr) =
    match requirementSite(traitName)(expression)(emptyScope) with
        | Some((span, [])) -> Some((span, None))
        | Some((span, contexts)) ->
            Some((span, contexts
            |> Ashes.Collection.List.reverse
            |> Ashes.Text.join(" -> ")
            |> Some))
        | None -> None
