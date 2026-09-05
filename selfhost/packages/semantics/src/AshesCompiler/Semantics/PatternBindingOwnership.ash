// Stage 0's pre-lowering pattern-binding ownership classification (`ComputePatternBindingOwnership`
// in `Lowering.PatternBindingOwnership.cs`): for one self-recursive loop function, every name a
// `match` pattern extracts out of one of the function's own parameters (directly, or out of a
// binding that was itself extracted from one) is classified by how the arm uses it, before any
// slot is assigned. A binding that only feeds a scrutinee, a condition, an operator, or an
// ordinary call borrows the parameter's reference; a binding forwarded to the same parameter of
// an exact tail self-call transfers it; a binding stored into an aggregate, forwarded to a
// different parameter, captured by a closure, or used through a construct the walk does not
// classify keeps a reference of its own past the arm, so the lowering gives it a protective
// owner (`patternFactRequiresProtection`).
//
// A binder is identified by the start offset of its own source span (the nearest enclosing
// `PatternAt`), which the lowering's pattern preparation reads back off the same syntax tree.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax
export (
    type PatternBindingUse(..),
    type PatternBindingOwnershipKind(..),
    type PatternBindingFact(..),
    value patternBindingFacts,
    value patternFactRequiresProtection,
    value lookupPatternBindingFact,
    value patternBinderKey,
)

// One way an arm uses a pattern binding, stage 0's `PatternBindingOwnershipUse` flags.
type PatternBindingUse =
    | UseStructuralInspection
    | UseOrdinaryCallBorrow
    | UseSameParameterTransfer
    | UseEmbeddedInOwner
    | UseIndependentEscape
    | UseCapturedByClosure
    | UseConservativeUnknown
    deriving {Eq, Show}

// The ownership a binding's uses add up to, stage 0's `PatternBindingOwnershipKind`.
type PatternBindingOwnershipKind =
    | PatternBorrowedOnly
    | PatternTransferredToSameParameter
    | PatternEmbeddedInOwner
    | PatternEscapesIndependently
    | PatternConservativeUnknown
    deriving {Eq, Show}

// The classified fact of one binder: its span key, name, the parameter it was extracted from,
// how many extraction levels below that parameter it sits, the uses the walk recorded in
// encounter order, and the ownership they add up to.
type PatternBindingFact =
    | binder: Int
    | name: Str
    | rootParameterOrdinal: Int
    | rootParameterName: Str
    | extractionDepth: Int
    | uses: List(PatternBindingUse)
    | ownership: PatternBindingOwnershipKind
    deriving {Eq, Show}

// The position an expression is walked in, stage 0's `PatternBindingUseContext`.
type PatternUseContext =
    | ContextStructuralInspection
    | ContextEmbeddedInOwner
    | ContextIndependentEscape
    | ContextCapturedByClosure
    | ContextConservativeUnknown
    deriving {Eq, Show}

// A binder still being classified: the uses the walk has recorded so far.
type PatternBinder =
    | key: Int
    | binderName: Str
    | rootOrdinal: Int
    | rootName: Str
    | depth: Int
    | uses: List(PatternBindingUse)

// The parameter lineage a name carries: the root parameter, the extraction depth, and the
// binder key when the name is itself a pattern binding (a parameter has none).
type PatternLineage =
    | lineageRoot: Int
    | lineageRootName: Str
    | lineageDepth: Int
    | lineageBinder: Maybe(Int)

// The walk over one loop function: its self name and parameters, the program's constructor
// names (nullary ones separately, since a bare constructor name in a pattern binds nothing),
// and the binders classified so far, most recent first.
type PatternWalk =
    | selfName: Str
    | parameters: List(Str)
    | constructors: List(Str)
    | nullaryConstructors: List(Str)
    | binders: List(PatternBinder)

let recursive containsText (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest -> candidate == name || containsText(name)(rest)

let recursive containsUse (use: PatternBindingUse) (uses: List(PatternBindingUse)) =
    match uses with
        | [] -> false
        | candidate :: rest -> candidate == use || containsUse(use)(rest)

let recursive unspan (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> unspan(inner)
        | other -> other

let recursive unspanPattern (pattern: Pattern) =
    match pattern with
        | PatternAt(_span, inner) -> unspanPattern(inner)
        | other -> other

// The key of a binder under the span `span`: the span's start, or -1 for a binder the syntax
// tree carries no span for, which then matches no fact.
let patternBinderKey (span: Maybe(Int)) =
    match span with
        | Some(start) -> start
        | None -> -1

let recursive lookupPatternBindingFact (key: Int) (facts: List(PatternBindingFact)) =
    match facts with
        | [] -> None
        | (PatternBindingFact { binder = candidate } as fact) :: rest ->
            if candidate == key
            then Some(fact)
            else lookupPatternBindingFact(key)(rest)

// Whether the lowering must give the binding a protective reference of its own.
let patternFactRequiresProtection (fact: PatternBindingFact) =
    match fact.ownership with
        | PatternEmbeddedInOwner -> true
        | PatternEscapesIndependently -> true
        | PatternConservativeUnknown -> true
        | _ -> false

// Stage 0's `ClassifyPatternBindingOwnership`.
let classifyUses (uses: List(PatternBindingUse)) =
    if containsUse(UseConservativeUnknown)(uses)
    then PatternConservativeUnknown
    else
        if containsUse(UseIndependentEscape)(uses) || containsUse(UseCapturedByClosure)(uses)
        then PatternEscapesIndependently
        else
            if containsUse(UseEmbeddedInOwner)(uses)
            then PatternEmbeddedInOwner
            else
                if containsUse(UseSameParameterTransfer)(uses)
                then PatternTransferredToSameParameter
                else PatternBorrowedOnly

let contextUse (context: PatternUseContext) =
    match context with
        | ContextStructuralInspection -> UseStructuralInspection
        | ContextEmbeddedInOwner -> UseEmbeddedInOwner
        | ContextIndependentEscape -> UseIndependentEscape
        | ContextCapturedByClosure -> UseCapturedByClosure
        | ContextConservativeUnknown -> UseConservativeUnknown

// Whether a pattern is a plain binder (a variable or an alias) once its spans are stripped, so
// its own binders sit at its parent's extraction depth rather than one level deeper.
let isBinderPattern (pattern: Pattern) =
    match unspanPattern(pattern) with
        | PatternVar(_name) -> true
        | PatternAs(_inner, _alias) -> true
        | _ -> false

// Every binder a pattern introduces with its span key and extraction depth, stage 0's
// `EnumeratePatternOwnershipBinders`: an alias names the whole value at its position and sits at
// that position's depth, one level above the binders its inner pattern extracts; an `or` pattern
// contributes its first alternative's binders.
let recursive enumerateBinders (pattern: Pattern) (depth: Int) (span: Maybe(Int)) =
    match pattern with
        | PatternAt(TextSpan { start = start }, inner) -> enumerateBinders(inner)(depth)(Some(start))
        | PatternVar(name) -> [(patternBinderKey(span), name, depth)]
        | PatternAs(inner, alias) -> (patternBinderKey(span), alias, depth) :: enumerateChild(inner)(depth)
        | PatternOr(first :: _alternatives) -> enumerateBinders(first)(depth)(span)
        | PatternCons(head, tail) ->
            depth
            |> enumerateChild(tail)
            |> append(enumerateChild(head)(depth))
        | PatternTuple(elements) -> enumerateChildren(elements)(depth)
        | PatternConstructor(_constructorName, elements) -> enumerateChildren(elements)(depth)
        | PatternRecord(_constructorName, fields) -> enumerateFieldChildren(fields)(depth)
        | _ -> []
and enumerateChild (pattern: Pattern) (parentDepth: Int) =
    enumerateBinders(pattern)(if isBinderPattern(pattern)
    then parentDepth
    else parentDepth + 1)(None)
and enumerateChildren (patterns: List(Pattern)) (depth: Int) =
    match patterns with
        | [] -> []
        | pattern :: rest ->
            depth
            |> enumerateChildren(rest)
            |> append(enumerateChild(pattern)(depth))
and enumerateFieldChildren (fields: List((Str, Pattern))) (depth: Int) =
    match fields with
        | [] -> []
        | (_fieldName, pattern) :: rest ->
            depth
            |> enumerateFieldChildren(rest)
            |> append(enumerateChild(pattern)(depth))

let recursive binderNames (binders: List((Int, Str, Int))) =
    match binders with
        | [] -> []
        | (_key, name, _depth) :: rest -> name :: binderNames(rest)

let recursive removeNames (removed: List(Str)) (names: List(Str)) =
    match names with
        | [] -> []
        | name :: rest ->
            if containsText(name)(removed)
            then removeNames(removed)(rest)
            else name :: removeNames(removed)(rest)

// The names a pattern binds, minus the nullary constructors it merely tests.
let patternBinderNames (pattern: Pattern) (walk: PatternWalk) =
    None
    |> enumerateBinders(pattern)(1)
    |> binderNames
    |> removeNames(walk.nullaryConstructors)

let recursive lookupLineage (name: Str) (lineages: List((Str, PatternLineage))) =
    match lineages with
        | [] -> None
        | (candidate, lineage) :: rest ->
            if candidate == name
            then Some(lineage)
            else lookupLineage(name)(rest)

let recursive removeLineages (names: List(Str)) (lineages: List((Str, PatternLineage))) =
    match lineages with
        | [] -> []
        | (candidate, lineage) :: rest ->
            if containsText(candidate)(names)
            then removeLineages(names)(rest)
            else (candidate, lineage) :: removeLineages(names)(rest)

let setLineage (name: Str) (lineage: PatternLineage) (lineages: List((Str, PatternLineage))) = (name, lineage) :: removeLineages([name])(lineages)

let lineageIsBinding (lineage: PatternLineage) =
    match lineage.lineageBinder with
        | Some(_key) -> true
        | None -> false

let recursive addBinderUse (key: Int) (use: PatternBindingUse) (binders: List(PatternBinder)) =
    match binders with
        | [] -> []
        | (PatternBinder { key = candidate, uses = uses } as binder) :: rest ->
            if candidate == key
            then (binder with uses = use :: uses) :: rest
            else binder :: addBinderUse(key)(use)(rest)

// Records one use of the binding behind `name`, when the name is a pattern binding.
let recordUse (name: Str) (lineages: List((Str, PatternLineage))) (use: PatternBindingUse) (walk: PatternWalk) =
    match lookupLineage(name)(lineages) with
        | Some(PatternLineage { lineageBinder = Some(key) }) -> walk with binders = addBinderUse(key)(use)(walk.binders)
        | _ -> walk

let recursive recordUses (names: List(Str)) (lineages: List((Str, PatternLineage))) (use: PatternBindingUse) (walk: PatternWalk) =
    match names with
        | [] -> walk
        | name :: rest ->
            walk
            |> recordUse(name)(lineages)(use)
            |> recordUses(rest)(lineages)(use)

let recursive patternNames (pattern: Pattern) (names: List(Str)) =
    match pattern with
        | PatternAt(_span, inner) -> patternNames(inner)(names)
        | PatternVar(name) -> name :: names
        | PatternCons(head, tail) ->
            names
            |> patternNames(head)
            |> patternNames(tail)
        | PatternTuple(patterns) -> patternListNames(patterns)(names)
        | PatternConstructor(_constructorName, patterns) -> patternListNames(patterns)(names)
        | PatternRecord(_constructorName, fields) -> patternFieldNames(fields)(names)
        | PatternAs(inner, alias) -> alias :: patternNames(inner)(names)
        | PatternOr(patterns) -> patternListNames(patterns)(names)
        | _ -> names
and patternListNames (patterns: List(Pattern)) (names: List(Str)) =
    match patterns with
        | [] -> names
        | pattern :: rest ->
            names
            |> patternNames(pattern)
            |> patternListNames(rest)
and patternFieldNames (fields: List((Str, Pattern))) (names: List(Str)) =
    match fields with
        | [] -> names
        | (_fieldName, pattern) :: rest ->
            names
            |> patternNames(pattern)
            |> patternFieldNames(rest)

// The variable names free in an expression, stage 0's `FreeVars`: every name read that no
// enclosing binder of the expression itself introduces.
let recursive freeNames (expression: Expr) (bound: List(Str)) (names: List(Str)) =
    match expression with
        | ExprAt(_span, inner) -> freeNames(inner)(bound)(names)
        | ExprVar(name) ->
            if containsText(name)(bound)
            then names
            else name :: names
        | ExprAdd(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprSubtract(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprMultiply(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprDivide(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprModulo(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprBitwiseAnd(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprBitwiseOr(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprBitwiseXor(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprShiftLeft(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprShiftRight(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprBitwiseNot(operand) -> freeNames(operand)(bound)(names)
        | ExprLogicalNot(operand) -> freeNames(operand)(bound)(names)
        | ExprLogicalAnd(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprLogicalOr(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprGreaterThan(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprLessThan(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprGreaterOrEqual(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprLessOrEqual(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprEqual(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprNotEqual(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprResultPipe(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprResultMapErrorPipe(left, right) -> freeNamesBoth(left)(right)(bound)(names)
        | ExprLet(name, value, body, parameters, _annotation, _requirements) ->
            names
            |> freeNames(value)(append(parameters)(bound))
            |> freeNames(body)(name :: bound)
        | ExprLetResult(name, value, body) ->
            names
            |> freeNames(value)(bound)
            |> freeNames(body)(name :: bound)
        | ExprLetRecursive(name, value, body, parameters, _annotation, _requirements) ->
            names
            |> freeNames(value)(name :: append(parameters)(bound))
            |> freeNames(body)(name :: bound)
        | ExprIf(condition, thenBranch, elseBranch) ->
            names
            |> freeNames(condition)(bound)
            |> freeNames(thenBranch)(bound)
            |> freeNames(elseBranch)(bound)
        | ExprLambda(parameter, body, _annotation) -> freeNames(body)(parameter :: bound)(names)
        | ExprCall(function, argument, _sugar, _layout) -> freeNamesBoth(function)(argument)(bound)(names)
        | ExprTuple(elements) -> freeNamesAll(elements)(bound)(names)
        | ExprList(elements, _isMultiline) -> freeNamesAll(elements)(bound)(names)
        | ExprCons(head, tail) -> freeNamesBoth(head)(tail)(bound)(names)
        | ExprMatch(value, cases, _position) ->
            names
            |> freeNames(value)(bound)
            |> freeNamesCases(cases)(bound)
        | ExprAwait(inner) -> freeNames(inner)(bound)(names)
        | ExprRecord(_constructorName, fields, _isMultiline) -> freeNamesFields(fields)(bound)(names)
        | ExprRecordUpdate(target, fields) ->
            names
            |> freeNames(target)(bound)
            |> freeNamesFields(fields)(bound)
        | ExprPerform(inner) -> freeNames(inner)(bound)(names)
        | ExprHandle(body, handlers) ->
            names
            |> freeNames(body)(bound)
            |> freeNamesHandlers(handlers)(bound)
        | _ -> names
and freeNamesBoth (left: Expr) (right: Expr) (bound: List(Str)) (names: List(Str)) =
    names
    |> freeNames(left)(bound)
    |> freeNames(right)(bound)
and freeNamesAll (expressions: List(Expr)) (bound: List(Str)) (names: List(Str)) =
    match expressions with
        | [] -> names
        | expression :: rest ->
            names
            |> freeNames(expression)(bound)
            |> freeNamesAll(rest)(bound)
and freeNamesFields (fields: List((Str, Expr))) (bound: List(Str)) (names: List(Str)) =
    match fields with
        | [] -> names
        | (_fieldName, expression) :: rest ->
            names
            |> freeNames(expression)(bound)
            |> freeNamesFields(rest)(bound)
and freeNamesCases (cases: List((Pattern, Expr, Maybe(Expr)))) (bound: List(Str)) (names: List(Str)) =
    match cases with
        | [] -> names
        | (pattern, body, guard) :: rest ->
            names
            |> freeNamesGuard(guard)(append(patternNames(pattern)([]))(bound))
            |> freeNames(body)(append(patternNames(pattern)([]))(bound))
            |> freeNamesCases(rest)(bound)
and freeNamesGuard (guard: Maybe(Expr)) (bound: List(Str)) (names: List(Str)) =
    match guard with
        | Some(expression) -> freeNames(expression)(bound)(names)
        | None -> names
and freeNamesHandlers (handlers: List((Maybe(Str), Str, List(Pattern), Expr))) (bound: List(Str)) (names: List(Str)) =
    match handlers with
        | [] -> names
        | (resumeName, _operation, patterns, body) :: rest ->
            names
            |> freeNames(body)(bound
            |> patternListNames(patterns)
            |> resumeBound(resumeName))
            |> freeNamesHandlers(rest)(bound)
and resumeBound (resumeName: Maybe(Str)) (bound: List(Str)) =
    match resumeName with
        | Some(name) -> name :: bound
        | None -> bound

// Stage 0's `RecordPatternBindingFreeUses`: every pattern binding the expression mentions freely
// takes the context's use.
let recordFreeUses (expression: Expr) (bound: List(Str)) (lineages: List((Str, PatternLineage))) (context: PatternUseContext) (walk: PatternWalk) =
    recordUses(freeNames(expression)(bound)([]))(lineages)(contextUse(context))(walk)

let recursive callSpine (expression: Expr) (arguments: List(Expr)) =
    match expression with
        | ExprAt(_span, inner) -> callSpine(inner)(arguments)
        | ExprCall(function, argument, _sugar, _layout) -> callSpine(function)(argument :: arguments)
        | root -> (root, arguments)

let rootConstructorName (root: Expr) =
    match root with
        | ExprVar(name) -> Some(name)
        | ExprQualifiedVar(_moduleName, member) -> Some(member)
        | _ -> None

// The ownership use of a pattern binding passed by name as a call argument, stage 0's
// `WalkPatternBindingOwnershipCall`: the same parameter of an exact tail self-call transfers it,
// any other exact-self-call position lets it escape, a constructor embeds it, an ordinary call
// borrows a binding extracted directly off the scrutinee but lets a deeper extraction escape
// (`ClassifyOrdinaryCallArgumentUse`), and anything else is unclassified.
let argumentUse (exactSelfCall: Bool) (constructorCall: Bool) (ordinaryCall: Bool) (index: Int) (lineage: PatternLineage) =
    if exactSelfCall
    then
        if lineage.lineageRoot == index
        then UseSameParameterTransfer
        else UseIndependentEscape
    else
        if constructorCall
        then UseEmbeddedInOwner
        else
            if ordinaryCall
            then
                if lineage.lineageDepth >= 2
                then UseIndependentEscape
                else UseOrdinaryCallBorrow
            else UseConservativeUnknown

let argumentContext (exactSelfCall: Bool) (constructorCall: Bool) (ordinaryCall: Bool) =
    if exactSelfCall
    then ContextIndependentEscape
    else
        if constructorCall
        then ContextEmbeddedInOwner
        else
            if ordinaryCall
            then ContextStructuralInspection
            else ContextConservativeUnknown

// The lineages an arm's pattern establishes: the names it binds shadow every outer lineage, and
// when the scrutinee itself carries a lineage, each binder becomes a new pattern binding of that
// lineage's root, one extraction level deeper per structural level (stage 0's
// `BindPatternOwnershipLineages`).
let recursive bindBinders (binders: List((Int, Str, Int))) (source: PatternLineage) (lineages: List((Str, PatternLineage))) (walk: PatternWalk) =
    match binders with
        | [] -> (lineages, walk)
        | (key, name, relativeDepth) :: rest ->
            if containsText(name)(walk.nullaryConstructors)
            then bindBinders(rest)(source)(lineages)(walk)
            else
                bindBinders(rest)(source)(setLineage(name)(PatternLineage(
                    lineageRoot = source.lineageRoot,
                    lineageRootName = source.lineageRootName,
                    lineageDepth = source.lineageDepth + relativeDepth,
                    lineageBinder = Some(key)
                ))(lineages))((walk with binders = PatternBinder(
                    key = key,
                    binderName = name,
                    rootOrdinal = source.lineageRoot,
                    rootName = source.lineageRootName,
                    depth = source.lineageDepth + relativeDepth,
                    uses = []
                ) :: walk.binders))

let bindArmLineages (pattern: Pattern) (source: Maybe(PatternLineage)) (lineages: List((Str, PatternLineage))) (walk: PatternWalk) =
    match source with
        | None ->
            (removeLineages(patternBinderNames(pattern)(walk))(lineages), walk)
        | Some(sourceLineage) ->
            bindBinders(enumerateBinders(pattern)(1)(None))(sourceLineage)(removeLineages(patternBinderNames(pattern)(walk))(lineages))(walk)

let scrutineeLineage (value: Expr) (lineages: List((Str, PatternLineage))) =
    match unspan(value) with
        | ExprVar(name) -> lookupLineage(name)(lineages)
        | _ -> None

// Stage 0's `WalkPatternBindingOwnership`: `shadowedSelf` says whether a binder between the loop
// function and this expression reuses its name, so a call of that name is no self-call.
let recursive walkExpression (expression: Expr) (lineages: List((Str, PatternLineage))) (shadowedSelf: Bool) (context: PatternUseContext) (walk: PatternWalk) =
    match expression with
        | ExprAt(_span, inner) -> walkExpression(inner)(lineages)(shadowedSelf)(context)(walk)
        | ExprCall(_function, _argument, _sugar, _layout) -> walkCall(expression)(lineages)(shadowedSelf)(walk)
        | ExprVar(name) ->
            recordUse(name)(lineages)(contextUse(context))(walk)
        | ExprQualifiedVar(_moduleName, _member) -> walk
        | ExprInt(_value) -> walk
        | ExprBigInt(_value) -> walk
        | ExprUInt(_value, _width, _text) -> walk
        | ExprFloat(_value, _text) -> walk
        | ExprString(_value) -> walk
        | ExprRune(_value) -> walk
        | ExprBool(_value) -> walk
        | ExprIf(condition, thenBranch, elseBranch) ->
            walk
            |> walkExpression(condition)(lineages)(shadowedSelf)(ContextStructuralInspection)
            |> walkExpression(thenBranch)(lineages)(shadowedSelf)(context)
            |> walkExpression(elseBranch)(lineages)(shadowedSelf)(context)
        | ExprLogicalAnd(left, right) ->
            walk
            |> walkExpression(left)(lineages)(shadowedSelf)(ContextStructuralInspection)
            |> walkExpression(right)(lineages)(shadowedSelf)(context)
        | ExprLogicalOr(left, right) ->
            walk
            |> walkExpression(left)(lineages)(shadowedSelf)(ContextStructuralInspection)
            |> walkExpression(right)(lineages)(shadowedSelf)(context)
        | ExprLet(name, value, body, _parameters, _annotation, _requirements) -> walkLet(name)(value)(body)(lineages)(shadowedSelf)(context)(walk)
        | ExprLetResult(name, value, body) ->
            walk
            |> walkExpression(value)(lineages)(shadowedSelf)(ContextConservativeUnknown)
            |> walkExpression(body)(removeLineages([name])(lineages))(shadowedSelf || name == walk.selfName)(context)
        | ExprLetRecursive(name, value, body, _parameters, _annotation, _requirements) ->
            walk
            |> walkExpression(value)(removeLineages([name])(lineages))(shadowedSelf || name == walk.selfName)(ContextConservativeUnknown)
            |> walkExpression(body)(removeLineages([name])(lineages))(shadowedSelf || name == walk.selfName)(context)
        | ExprLambda(parameter, body, _annotation) ->
            recordFreeUses(body)([parameter])(removeLineages([parameter])(lineages))(ContextCapturedByClosure)(walk)
        | ExprMatch(value, cases, _position) -> walkMatch(value)(cases)(lineages)(shadowedSelf)(context)(walk)
        | ExprTuple(elements) -> walkAll(elements)(lineages)(shadowedSelf)(ContextEmbeddedInOwner)(walk)
        | ExprList(elements, _isMultiline) -> walkAll(elements)(lineages)(shadowedSelf)(ContextEmbeddedInOwner)(walk)
        | ExprCons(head, tail) -> walkAll([head, tail])(lineages)(shadowedSelf)(ContextEmbeddedInOwner)(walk)
        | ExprRecord(_constructorName, fields, _isMultiline) ->
            walkAll(fieldValues(fields))(lineages)(shadowedSelf)(ContextEmbeddedInOwner)(walk)
        | ExprRecordUpdate(target, fields) -> walkAll(target :: fieldValues(fields))(lineages)(shadowedSelf)(ContextEmbeddedInOwner)(walk)
        | ExprAdd(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprSubtract(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprMultiply(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprDivide(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprModulo(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprBitwiseAnd(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprBitwiseOr(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprBitwiseXor(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprShiftLeft(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprShiftRight(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprBitwiseNot(operand) -> walkExpression(operand)(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprLogicalNot(operand) -> walkExpression(operand)(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprGreaterThan(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprLessThan(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprGreaterOrEqual(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprLessOrEqual(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprEqual(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprNotEqual(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | ExprResultPipe(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextConservativeUnknown)(walk)
        | ExprResultMapErrorPipe(left, right) -> walkAll([left, right])(lineages)(shadowedSelf)(ContextConservativeUnknown)(walk)
        | ExprAwait(inner) -> walkExpression(inner)(lineages)(shadowedSelf)(ContextConservativeUnknown)(walk)
        | other -> recordFreeUses(other)([])(lineages)(ContextConservativeUnknown)(walk)
and fieldValues (fields: List((Str, Expr))) =
    match fields with
        | [] -> []
        | (_fieldName, value) :: rest -> value :: fieldValues(rest)
and walkAll (expressions: List(Expr)) (lineages: List((Str, PatternLineage))) (shadowedSelf: Bool) (context: PatternUseContext) (walk: PatternWalk) =
    match expressions with
        | [] -> walk
        | expression :: rest ->
            walk
            |> walkExpression(expression)(lineages)(shadowedSelf)(context)
            |> walkAll(rest)(lineages)(shadowedSelf)(context)
and walkLet (name: Str) (value: Expr) (body: Expr) (lineages: List((Str, PatternLineage))) (shadowedSelf: Bool) (context: PatternUseContext) (walk: PatternWalk) =
    match aliasLineage(value)(lineages) with
        | Some(lineage) ->
            walkExpression(body)(setLineage(name)(lineage)(lineages))(shadowedSelf || name == walk.selfName)(context)(walk)
        | None ->
            walk
            |> walkExpression(value)(lineages)(shadowedSelf)(ContextIndependentEscape)
            |> walkExpression(body)(removeLineages([name])(lineages))(shadowedSelf || name == walk.selfName)(context)
and aliasLineage (value: Expr) (lineages: List((Str, PatternLineage))) =
    match unspan(value) with
        | ExprVar(alias) -> lookupLineage(alias)(lineages)
        | _ -> None
and walkMatch (value: Expr) (cases: List((Pattern, Expr, Maybe(Expr)))) (lineages: List((Str, PatternLineage))) (shadowedSelf: Bool) (context: PatternUseContext) (walk: PatternWalk) =
    match scrutineeLineage(value)(lineages) with
        | Some(source) ->
            walkCases(cases)(Some(source))(lineages)(shadowedSelf)(context)((match source with
                | PatternLineage { lineageBinder = Some(key) } -> walk with binders = addBinderUse(key)(UseStructuralInspection)(walk.binders)
                | _ -> walk))
        | None ->
            walk
            |> walkExpression(value)(lineages)(shadowedSelf)(ContextStructuralInspection)
            |> walkCases(cases)(None)(lineages)(shadowedSelf)(context)
and walkCases (cases: List((Pattern, Expr, Maybe(Expr)))) (source: Maybe(PatternLineage)) (lineages: List((Str, PatternLineage))) (shadowedSelf: Bool) (context: PatternUseContext) (walk: PatternWalk) =
    match cases with
        | [] -> walk
        | (pattern, body, guard) :: rest ->
            match bindArmLineages(pattern)(source)(lineages)(walk) with
                | (armLineages, bound) ->
                    bound
                    |> walkGuard(guard)(armLineages)(shadowedSelf || containsText(walk.selfName)(patternBinderNames(pattern)(walk)))
                    |> walkExpression(body)(armLineages)(shadowedSelf || containsText(walk.selfName)(patternBinderNames(pattern)(walk)))(context)
                    |> walkCases(rest)(source)(lineages)(shadowedSelf)(context)
and walkGuard (guard: Maybe(Expr)) (lineages: List((Str, PatternLineage))) (shadowedSelf: Bool) (walk: PatternWalk) =
    match guard with
        | Some(expression) -> walkExpression(expression)(lineages)(shadowedSelf)(ContextStructuralInspection)(walk)
        | None -> walk
and walkCall (expression: Expr) (lineages: List((Str, PatternLineage))) (shadowedSelf: Bool) (walk: PatternWalk) =
    match callSpine(expression)([]) with
        | (root, arguments) ->
            walkCallWith(root)(arguments)(isExactSelfCall(root)(arguments)(shadowedSelf)(walk))(isConstructorRoot(root)(walk))(lineages)(shadowedSelf)(walk)
and isExactSelfCall (root: Expr) (arguments: List(Expr)) (shadowedSelf: Bool) (walk: PatternWalk) =
    match root with
        | ExprVar(callee) -> callee == walk.selfName && !shadowedSelf && length(arguments) == length(walk.parameters)
        | _ -> false
and isConstructorRoot (root: Expr) (walk: PatternWalk) =
    match rootConstructorName(root) with
        | Some(name) -> containsText(name)(walk.constructors)
        | None -> false
and isOrdinaryRoot (root: Expr) (constructorCall: Bool) =
    match root with
        | ExprVar(_name) -> !constructorCall
        | ExprQualifiedVar(_moduleName, _member) -> !constructorCall
        | _ -> false
and walkCallWith (root: Expr) (arguments: List(Expr)) (exactSelfCall: Bool) (constructorCall: Bool) (lineages: List((Str, PatternLineage))) (shadowedSelf: Bool) (walk: PatternWalk) =
    walk
    |> walkArguments(arguments)(0)(exactSelfCall)(constructorCall)(isOrdinaryRoot(root)(constructorCall))(lineages)(shadowedSelf)
    |> walkExpression(root)(lineages)(shadowedSelf)(ContextConservativeUnknown)
and walkArguments (arguments: List(Expr)) (index: Int) (exactSelfCall: Bool) (constructorCall: Bool) (ordinaryCall: Bool) (lineages: List((Str, PatternLineage))) (shadowedSelf: Bool) (walk: PatternWalk) =
    match arguments with
        | [] -> walk
        | argument :: rest ->
            walk
            |> walkArgument(argument)(index)(exactSelfCall)(constructorCall)(ordinaryCall)(lineages)(shadowedSelf)
            |> walkArguments(rest)(index + 1)(exactSelfCall)(constructorCall)(ordinaryCall)(lineages)(shadowedSelf)
and walkArgument (argument: Expr) (index: Int) (exactSelfCall: Bool) (constructorCall: Bool) (ordinaryCall: Bool) (lineages: List((Str, PatternLineage))) (shadowedSelf: Bool) (walk: PatternWalk) =
    match argumentLineage(argument)(lineages) with
        | Some((name, lineage)) ->
            recordUse(name)(lineages)(argumentUse(exactSelfCall)(constructorCall)(ordinaryCall)(index)(lineage))(walk)
        | None ->
            walkExpression(argument)(lineages)(shadowedSelf)(argumentContext(exactSelfCall)(constructorCall)(ordinaryCall))(walk)
and argumentLineage (argument: Expr) (lineages: List((Str, PatternLineage))) =
    match unspan(argument) with
        | ExprVar(name) ->
            match lookupLineage(name)(lineages) with
                | Some(lineage) ->
                    if lineageIsBinding(lineage)
                    then Some((name, lineage))
                    else None
                | None -> None
        | _ -> None

let recursive parameterLineages (parameters: List(Str)) (ordinal: Int) =
    match parameters with
        | [] -> []
        | parameter :: rest -> (parameter, PatternLineage(lineageRoot = ordinal, lineageRootName = parameter, lineageDepth = 0, lineageBinder = None)) :: parameterLineages(rest)(ordinal + 1)

let recursive buildFacts (binders: List(PatternBinder)) =
    match binders with
        | [] -> []
        | PatternBinder { key = key, binderName = name, rootOrdinal = rootOrdinal, rootName = rootName, depth = depth, uses = uses } :: rest ->
            PatternBindingFact(
                binder = key,
                name = name,
                rootParameterOrdinal = rootOrdinal,
                rootParameterName = rootName,
                extractionDepth = depth,
                uses = reverse(uses),
                ownership = classifyUses(uses)
            ) :: buildFacts(rest)

// The classified facts of every pattern binding extracted from a parameter of the loop function
// `self` (over `parameters`, the whole curried chain in order) inside its innermost body `body`,
// in binder order. `constructors` are the program's constructor names and `nullaryConstructors`
// the subset a bare pattern name tests rather than binds.
let patternBindingFacts (self: Str) (parameters: List(Str)) (constructors: List(Str)) (nullaryConstructors: List(Str)) (body: Expr) =
    PatternWalk(selfName = self, parameters = parameters, constructors = constructors, nullaryConstructors = nullaryConstructors, binders = [])
    |> walkExpression(body)(parameterLineages(parameters)(0))(false)(ContextIndependentEscape)
    |> (given (walk: PatternWalk) ->
        walk.binders
        |> reverse
        |> buildFacts)
