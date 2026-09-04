// The pre-lowering classification of a let-bound function's result, stage 0's
// `BuildProvenanceFunctionNode`: each terminal arm of the innermost body is a direct
// reference-counted construction, a saturated forwarding call to another let-bound function, or a
// rejection, and the program's forwarding graph is solved by `OwnershipProvenance`'s
// strongly-connected-component fixpoint into one RC-eligibility verdict per function.
//
// Invariants:
// - A terminal bare variable is read through the plain `let` alias captured at its binding site,
//   never through a `let recursive` binding or a name a pattern rebinds.
// - An exact self-recursive arm is neutral: it neither grounds nor rejects the node.
// - A construction is direct only when every argument is itself independently fresh; a bare
//   variable of any origin is never fresh.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.Pattern
import AshesCompiler.Semantics.OwnershipProvenance
import AshesCompiler.Semantics.OwnershipSummary
export (
    type ProvenanceFunction(..),
    type ProvenanceConstructors(..),
    value resultProvenanceNodes,
    value resolvedRcEligibility,
    value lookupRcEligible,
)

// A let-bound function the classification knows: its name, its parameters, and its innermost
// body.
type ProvenanceFunction =
    | name: Str
    | parameters: List(Str)
    | body: Expr

// The constructors in scope: every constructor's arity by name, and the nullary constructors that
// are the only nullary constructor of their type (a fresh tag cell rather than a shared
// singleton).
type ProvenanceConstructors =
    | arities: List((Str, Int))
    | soleNullary: List(Str)

// A plain `let` alias with the aliases and hidden function names in force at its binding site.
type ProvenanceAlias =
    | aliasName: Str
    | aliasValue: Expr
    | aliasScope: List(ProvenanceAlias)
    | aliasHidden: List(Str)

// One terminal arm with the aliases and hidden function names in force where it was found.
type ProvenanceArm =
    | expression: Expr
    | aliases: List(ProvenanceAlias)
    | hidden: List(Str)

let recursive containsName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest -> candidate == name || containsName(name)(rest)

let recursive stripSpans (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> stripSpans(inner)
        | other -> other

let recursive lookupAlias (name: Str) (aliases: List(ProvenanceAlias)) =
    match aliases with
        | [] -> None
        | (ProvenanceAlias { aliasName = candidate } as alias) :: rest ->
            if candidate == name
            then Some(alias)
            else lookupAlias(name)(rest)

let recursive removeAliases (names: List(Str)) (aliases: List(ProvenanceAlias)) =
    match aliases with
        | [] -> []
        | (ProvenanceAlias { aliasName = name } as alias) :: rest ->
            if containsName(name)(names)
            then removeAliases(names)(rest)
            else alias :: removeAliases(names)(rest)

let recursive removeNames (names: List(Str)) (from: List(Str)) =
    match from with
        | [] -> []
        | name :: rest ->
            if containsName(name)(names)
            then removeNames(names)(rest)
            else name :: removeNames(names)(rest)

let recursive isLambda (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> isLambda(inner)
        | ExprLambda(_parameter, _body, _annotation) -> true
        | _ -> false

// Stage 0's `ExtendFuncScope`: a `let` whose value is a lambda names a function again, any other
// `let` hides a function of the same name.
let extendHidden (name: Str) (value: Expr) (hidden: List(Str)) =
    if isLambda(value)
    then removeNames([name])(hidden)
    else name :: removeNames([name])(hidden)

let recursive patternBinders (pattern: Pattern) (names: List(Str)) =
    match pattern with
        | PatternAt(_span, inner) -> patternBinders(inner)(names)
        | PatternVar(name) -> name :: names
        | PatternCons(head, tail) ->
            names
            |> patternBinders(head)
            |> patternBinders(tail)
        | PatternTuple(elements) -> patternListBinders(elements)(names)
        | PatternConstructor(_name, arguments) -> patternListBinders(arguments)(names)
        | PatternRecord(_name, fields) -> patternFieldBinders(fields)(names)
        | PatternAs(inner, name) -> patternBinders(inner)(name :: names)
        | PatternOr(alternatives) -> patternListBinders(alternatives)(names)
        | _ -> names
and patternListBinders (patterns: List(Pattern)) (names: List(Str)) =
    match patterns with
        | [] -> names
        | pattern :: rest ->
            names
            |> patternBinders(pattern)
            |> patternListBinders(rest)
and patternFieldBinders (fields: List((Str, Pattern))) (names: List(Str)) =
    match fields with
        | [] -> names
        | (_field, pattern) :: rest ->
            names
            |> patternBinders(pattern)
            |> patternFieldBinders(rest)

// Stage 0's `CollectResultProvenanceTerminalArms`: the arms of `body` in source order, in front
// of `arms`.
let recursive collectArms (body: Expr) (aliases: List(ProvenanceAlias)) (hidden: List(Str)) (arms: List(ProvenanceArm)) =
    match body with
        | ExprAt(_span, inner) -> collectArms(inner)(aliases)(hidden)(arms)
        | ExprIf(_condition, thenBranch, elseBranch) ->
            arms
            |> collectArms(thenBranch)(aliases)(hidden)
            |> collectArms(elseBranch)(aliases)(hidden)
        | ExprMatch(_scrutinee, cases, _fallback) -> collectCaseArms(cases)(aliases)(hidden)(arms)
        | ExprLet(name, value, letBody, _parameters, _annotation, _requirements) ->
            collectArms(letBody)(ProvenanceAlias(aliasName = name, aliasValue = value, aliasScope = aliases, aliasHidden = hidden) :: removeAliases([name])(aliases))(extendHidden(name)(value)(hidden))(arms)
        | ExprLetResult(name, value, letBody) ->
            collectArms(letBody)(ProvenanceAlias(aliasName = name, aliasValue = value, aliasScope = aliases, aliasHidden = hidden) :: removeAliases([name])(aliases))(extendHidden(name)(value)(hidden))(arms)
        | ExprLetRecursive(name, value, letBody, _parameters, _annotation, _requirements) ->
            collectArms(letBody)(removeAliases([name])(aliases))(extendHidden(name)(value)(hidden))(arms)
        | ExprVar(name) ->
            match lookupAlias(name)(aliases) with
                | Some(ProvenanceAlias { aliasValue = value, aliasScope = scope, aliasHidden = aliasHidden }) -> collectArms(value)(scope)(aliasHidden)(arms)
                | None -> ProvenanceArm(expression = body, aliases = aliases, hidden = hidden) :: arms
        | _ -> ProvenanceArm(expression = body, aliases = aliases, hidden = hidden) :: arms
and collectCaseArms (cases: List((Pattern, Expr, Maybe(Expr)))) (aliases: List(ProvenanceAlias)) (hidden: List(Str)) (arms: List(ProvenanceArm)) =
    match cases with
        | [] -> arms
        | (pattern, caseBody, _guard) :: rest ->
            match patternBinders(pattern)([]) with
                | binders ->
                    arms
                    |> collectArms(caseBody)(removeAliases(binders)(aliases))(append(binders)(hidden))
                    |> collectCaseArms(rest)(aliases)(hidden)

// The head name and argument count of a call spine, `None` for a head that is not a bare name.
let recursive callHead (expression: Expr) (argumentCount: Int) =
    match expression with
        | ExprAt(_span, inner) -> callHead(inner)(argumentCount)
        | ExprCall(function, _argument, _isSugar, _layout) -> callHead(function)(argumentCount + 1)
        | ExprVar(name) -> Some((name, argumentCount))
        | _ -> None

let recursive lookupArity (name: Str) (arities: List((Str, Int))) =
    match arities with
        | [] -> None
        | (candidate, arity) :: rest ->
            if candidate == name
            then Some(arity)
            else lookupArity(name)(rest)

let recursive lookupFunction (name: Str) (functions: List(ProvenanceFunction)) =
    match functions with
        | [] -> None
        | (ProvenanceFunction { name = candidate } as function) :: rest ->
            if candidate == name
            then Some(function)
            else lookupFunction(name)(rest)

let isLiteral (expression: Expr) =
    match expression with
        | ExprInt(_value) -> true
        | ExprUInt(_value, _bits, _raw) -> true
        | ExprFloat(_value, _raw) -> true
        | ExprBool(_value) -> true
        | ExprBigInt(_digits) -> true
        | ExprString(_value) -> true
        | ExprRune(_value) -> true
        | _ -> false

// Stage 0's `IsFreshListConstructionExpression`: a list literal, or a cons chain whose tail
// bottoms out at one.
let recursive isFreshListConstruction (expression: Expr) =
    match stripSpans(expression) with
        | ExprList(_elements, _trailing) -> true
        | ExprCons(_head, tail) -> isFreshListConstruction(tail)
        | _ -> false

// Stage 0's `IsDirectRcConstruction`: a fresh list, a tuple or record whose every part is fresh, a
// sole nullary constructor, or a saturated constructor application over fresh arguments; stage
// 0's `IsFreshConstructionArgument` accepts a literal, a string addition, a nested direct
// construction, or a fresh-result builtin call as a fresh part.
let recursive isDirectRcConstruction (constructors: ProvenanceConstructors) (isFreshBuiltin: Expr -> Bool) (expression: Expr) =
    match stripSpans(expression) with
        | ExprList(_elements, _trailing) -> true
        | ExprCons(_head, _tail) as list -> isFreshListConstruction(list)
        | ExprTuple(elements) -> allFreshArguments(constructors)(isFreshBuiltin)(elements)
        | ExprRecord(_name, fields, _trailing) -> allFreshFields(constructors)(isFreshBuiltin)(fields)
        | ExprVar(name) ->
            match lookupArity(name)(constructors.arities) with
                | Some(0) -> containsName(name)(constructors.soleNullary)
                | _ -> false
        | ExprCall(_function, _argument, _isSugar, _layout) as call ->
            match callHead(call)(0) with
                | Some((name, argumentCount)) ->
                    match lookupArity(name)(constructors.arities) with
                        | Some(arity) ->
                            arity == argumentCount && allFreshArguments(constructors)(isFreshBuiltin)(callArguments(call)([]))
                        | None -> false
                | None -> false
        | _ -> false
and allFreshArguments (constructors: ProvenanceConstructors) (isFreshBuiltin: Expr -> Bool) (arguments: List(Expr)) =
    match arguments with
        | [] -> true
        | argument :: rest -> isFreshConstructionArgument(constructors)(isFreshBuiltin)(argument) && allFreshArguments(constructors)(isFreshBuiltin)(rest)
and allFreshFields (constructors: ProvenanceConstructors) (isFreshBuiltin: Expr -> Bool) (fields: List((Str, Expr))) =
    match fields with
        | [] -> true
        | (_field, value) :: rest -> isFreshConstructionArgument(constructors)(isFreshBuiltin)(value) && allFreshFields(constructors)(isFreshBuiltin)(rest)
and isFreshConstructionArgument (constructors: ProvenanceConstructors) (isFreshBuiltin: Expr -> Bool) (argument: Expr) =
    match stripSpans(argument) with
        | ExprAdd(_left, _right) -> true
        | stripped -> isLiteral(stripped) || isDirectRcConstruction(constructors)(isFreshBuiltin)(stripped) || isFreshBuiltin(stripped)
and callArguments (expression: Expr) (arguments: List(Expr)) =
    match expression with
        | ExprAt(_span, inner) -> callArguments(inner)(arguments)
        | ExprCall(function, argument, _isSugar, _layout) -> callArguments(function)(argument :: arguments)
        | _ -> arguments

// Stage 0's `TryResolveForwardTarget`: a saturated call to a visible let-bound function that is
// not a constructor.
let forwardTarget (functions: List(ProvenanceFunction)) (constructors: ProvenanceConstructors) (arm: ProvenanceArm) =
    match callHead(arm.expression)(0) with
        | Some((name, argumentCount)) ->
            match (lookupArity(name)(constructors.arities), containsName(name)(arm.hidden), lookupFunction(name)(functions)) with
                | (None, false, Some(ProvenanceFunction { parameters = parameters })) ->
                    if length(parameters) == argumentCount
                    then Some(name)
                    else None
                | _ -> None
        | None -> None

let recursive addTarget (target: Str) (targets: List(Str)) =
    if containsName(target)(targets)
    then targets
    else append(targets)([target])

// The node facts accumulated over a function's arms: direct, rejected, considered, targets.
type ArmFacts =
    | direct: Bool
    | rejected: Bool
    | considered: Int
    | targets: List(Str)

let recursive classifyArms (function: ProvenanceFunction) (functions: List(ProvenanceFunction)) (constructors: ProvenanceConstructors) (isFreshBuiltin: Expr -> Bool) (arms: List(ProvenanceArm)) (facts: ArmFacts) =
    match arms with
        | [] -> facts
        | arm :: rest ->
            match forwardTarget(functions)(constructors)(arm) with
                | Some(target) ->
                    if target == function.name
                    then classifyArms(function)(functions)(constructors)(isFreshBuiltin)(rest)(facts)
                    else classifyArms(function)(functions)(constructors)(isFreshBuiltin)(rest)((facts with considered = facts.considered + 1, targets = addTarget(target)(facts.targets)))
                | None ->
                    if isDirectArm(constructors)(isFreshBuiltin)(arm.expression)
                    then classifyArms(function)(functions)(constructors)(isFreshBuiltin)(rest)((facts with considered = facts.considered + 1, direct = true))
                    else classifyArms(function)(functions)(constructors)(isFreshBuiltin)(rest)((facts with considered = facts.considered + 1, rejected = true))
and isDirectArm (constructors: ProvenanceConstructors) (isFreshBuiltin: Expr -> Bool) (expression: Expr) =
    match stripSpans(expression) with
        | ExprAdd(_left, _right) -> true
        | stripped -> isDirectRcConstruction(constructors)(isFreshBuiltin)(stripped) || isFreshBuiltin(stripped)

let functionBodyArms (function: ProvenanceFunction) =
    match stripSpans(function.body) with
        | ExprString(_value) -> None
        | body ->
            []
            |> collectArms(body)([])([])
            |> reverse
            |> Some

// The provenance node of one function: a directly returned string literal is a fresh string by
// itself; any other body is classified arm by arm.
let provenanceNodeOf (functions: List(ProvenanceFunction)) (constructors: ProvenanceConstructors) (isFreshBuiltin: Expr -> Bool) (function: ProvenanceFunction) =
    match functionBodyArms(function) with
        | None -> buildProvenanceNode(function.name)(true)(false)(1)([])(None)([])(true)
        | Some(arms) ->
            match classifyArms(function)(functions)(constructors)(isFreshBuiltin)(arms)(ArmFacts(direct = false, rejected = false, considered = 0, targets = [])) with
                | ArmFacts { direct = direct, rejected = rejected, considered = considered, targets = targets } ->
                    match targets with
                        | target :: [] -> buildProvenanceNode(function.name)(direct)(rejected)(considered)(targets)(Some(target))([])(true)
                        | _ -> buildProvenanceNode(function.name)(direct)(rejected)(considered)(targets)(None)([])(true)

let recursive dedupeFunctions (functions: List(ProvenanceFunction)) (seen: List(Str)) =
    match functions with
        | [] -> []
        | (ProvenanceFunction { name = name } as function) :: rest ->
            if containsName(name)(seen)
            then dedupeFunctions(rest)(seen)
            else function :: dedupeFunctions(rest)(name :: seen)

let recursive nodesOf (functions: List(ProvenanceFunction)) (constructors: ProvenanceConstructors) (isFreshBuiltin: Expr -> Bool) (remaining: List(ProvenanceFunction)) =
    match remaining with
        | [] -> []
        | function :: rest -> provenanceNodeOf(functions)(constructors)(isFreshBuiltin)(function) :: nodesOf(functions)(constructors)(isFreshBuiltin)(rest)

// The provenance nodes of every function, the earliest entry of a repeated name standing for it.
let resultProvenanceNodes (functions: List(ProvenanceFunction)) (constructors: ProvenanceConstructors) (isFreshBuiltin: Expr -> Bool) =
    match dedupeFunctions(functions)([]) with
        | unique -> nodesOf(unique)(constructors)(isFreshBuiltin)(unique)

let recursive eligibilityOf (provenances: List((Str, FunctionResultProvenance))) =
    match provenances with
        | [] -> []
        | (name, FunctionResultProvenance { rcEligible = eligible }) :: rest -> (name, eligible) :: eligibilityOf(rest)

// Every function's RC-eligibility verdict after the forwarding fixpoint.
let resolvedRcEligibility (nodes: List(ProvenanceFunctionNode)) =
    nodes
    |> resolveResultProvenances
    |> eligibilityOf

let recursive lookupRcEligible (name: Str) (eligibility: List((Str, Bool))) =
    match eligibility with
        | [] -> false
        | (candidate, eligible) :: rest ->
            if candidate == name
            then eligible
            else lookupRcEligible(name)(rest)
