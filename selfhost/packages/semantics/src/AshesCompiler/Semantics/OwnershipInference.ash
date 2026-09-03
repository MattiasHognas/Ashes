// Whole-program parameter and capture ownership inference, result reachability, moves, and borrows.
//
// Invariants:
// - All analyses are monotone, whole-program, and strictly preserve pure evaluation semantics.
// - Result reachability tracks may-alias parameter flow with least fixpoint convergence.
// - Parameter ownership classifies Borrowed vs Consumed with fail-closed safety.
// - Move safety proofs verify single-path linearity and seed safety for allocation reuse.

import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.OwnershipProvenance
import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import Ashes.Collection.List.length
export (
    type ResultReachState(..),
    type FunctionSignature(..),
    value reachBottom,
    value reachParam,
    value reachPoisoned,
    value reachJoin,
    value reachSum,
    value isParamUsedOnlyAsBorrowRead,
    value classifyParameterOwnership,
    value analyzeExprReach,
    value computeCaptures,
    value inferFunctionOwnership,
    value inferProgramOwnership,
    type ProgramParameterOwnership,
    value inferProgramParameterOwnership,
    value lookupProgramParameterOwnership,
    value topLevelFunctions,
)

type ResultReachState =
    | counts: List(ParameterReachEntry)
    | causes: List(ResultReachCause)
    | isPoisoned: Bool
    deriving {Eq, Show}

type FunctionSignature =
    | name: Str
    | origin: SourceFunctionOrigin
    | parameters: List(Str)
    | body: Expr

// Each registered function name with the ownership of each of its parameters, in parameter order.
type alias ProgramParameterOwnership = List((Str, List((Str, ParameterOwnership))))

let notBool (b: Bool) =
    if b
    then false
    else true

let recursive listContainsCause (list: List(ResultReachCause)) (target: ResultReachCause) =
    match list with
        | [] -> false
        | head :: tail ->
            if head == target
            then true
            else listContainsCause(tail)(target)

let reachBottom unit = ResultReachState(counts = [], causes = [], isPoisoned = false)

let reachParam (param: Str) = ResultReachState(counts = [ParameterReachEntry(parameterName = param, reachCount = 1)], causes = [], isPoisoned = false)

let reachPoisoned (cause: ResultReachCause) = ResultReachState(counts = [], causes = [cause], isPoisoned = true)

// --- ResultReachState operations ---
let recursive lookupCount pairs key =
    match pairs with
        | [] -> 0
        | entry :: rest ->
            match entry with
                | ParameterReachEntry { parameterName = k, reachCount = v } ->
                    if k == key
                    then v
                    else lookupCount(rest)(key)

let recursive updateOrInsertCount key val pairs =
    match pairs with
        | [] -> [ParameterReachEntry(parameterName = key, reachCount = val)]
        | entry :: rest ->
            match entry with
                | ParameterReachEntry { parameterName = k, reachCount = v } ->
                    if k == key
                    then ParameterReachEntry(parameterName = key, reachCount = val) :: rest
                    else ParameterReachEntry(parameterName = k, reachCount = v) :: updateOrInsertCount(key)(val)(rest)

let recursive mergeCountsJoin a b =
    match b with
        | [] -> a
        | entry :: rest ->
            match entry with
                | ParameterReachEntry { parameterName = k, reachCount = v } ->
                    let cur = lookupCount(a)(k)
                    in
                        let maxVal =
                            if v > cur
                            then v
                            else cur
                        in
                            let nextA = updateOrInsertCount(k)(maxVal)(a)
                            in mergeCountsJoin(nextA)(rest)

let recursive mergeCauses (a: List(ResultReachCause)) (b: List(ResultReachCause)) =
    match b with
        | [] -> a
        | c :: rest ->
            if listContainsCause(a)(c)
            then mergeCauses(a)(rest)
            else mergeCauses(c :: a)(rest)

let reachJoin (a: ResultReachState) (b: ResultReachState) =
    match a with
        | ResultReachState { counts = cA, causes = causesA, isPoisoned = pA } ->
            match b with
                | ResultReachState { counts = cB, causes = causesB, isPoisoned = pB } ->
                    let newCounts = mergeCountsJoin(cA)(cB)
                    in
                        let newCauses = mergeCauses(causesA)(causesB)
                        in
                            let poisoned =
                                if pA
                                then true
                                else pB
                            in ResultReachState(counts = newCounts, causes = newCauses, isPoisoned = poisoned)

let recursive mergeCountsSum a b hasInternalSharing =
    match b with
        | [] -> (a, hasInternalSharing)
        | entry :: rest ->
            match entry with
                | ParameterReachEntry { parameterName = k, reachCount = v } ->
                    let cur = lookupCount(a)(k)
                    in
                        let sumVal = cur + v
                        in
                            let sharing =
                                if hasInternalSharing
                                then true
                                else sumVal >= 2
                            in
                                let nextA = updateOrInsertCount(k)(sumVal)(a)
                                in mergeCountsSum(nextA)(rest)(sharing)

let reachSum (a: ResultReachState) (b: ResultReachState) =
    match a with
        | ResultReachState { counts = cA, causes = causesA, isPoisoned = pA } ->
            match b with
                | ResultReachState { counts = cB, causes = causesB, isPoisoned = pB } ->
                    match mergeCountsSum(cA)(cB)(false) with
                        | (newCounts, sharing) ->
                            let baseCauses = mergeCauses(causesA)(causesB)
                            in
                                let newCauses =
                                    if sharing
                                    then mergeCauses(baseCauses)([InternalSharing])
                                    else baseCauses
                                in
                                    let poisoned =
                                        if pA
                                        then true
                                        else
                                            if pB
                                            then true
                                            else sharing
                                    in ResultReachState(counts = newCounts, causes = newCauses, isPoisoned = poisoned)

// --- Borrow-read analysis ---
let isBorrowReadResourceOp (moduleName: Str) (name: Str) =
    (let fullName = moduleName + "." + name
    in
        if fullName == "Ashes.IO.File.readChunk"
        then true
        else
            if fullName == "Ashes.IO.File.readLine"
            then true
            else
                if fullName == "Ashes.Net.Tcp.send"
                then true
                else
                    if fullName == "Ashes.Net.Tcp.receive"
                    then true
                    else
                        if fullName == "Ashes.Net.Tcp.Server.accept"
                        then true
                        else
                            if fullName == "Ashes.Net.Tls.send"
                            then true
                            else
                                if fullName == "Ashes.Net.Tls.receive"
                                then true
                                else
                                    if fullName == "Ashes.IO.Process.writeStdin"
                                    then true
                                    else
                                        if fullName == "Ashes.IO.Process.readStdoutLine"
                                        then true
                                        else fullName == "Ashes.IO.Process.readStderrLine")

// A parsed tree wraps every node in its source span; the shape checks below look through it.
let recursive stripSpan (expr: Expr) =
    match expr with
        | ExprAt(_span, inner) -> stripSpan(inner)
        | other -> other

let recursive collectCallArgsAndRoot (expr: Expr) (argsAcc: List(Expr)) =
    match expr with
        | ExprAt(_span, inner) -> collectCallArgsAndRoot(inner)(argsAcc)
        | ExprCall(func, arg, _isSugar, _layout) -> collectCallArgsAndRoot(func)(arg :: argsAcc)
        | other -> (other, argsAcc)

let recursive mentionsVar (expr: Expr) (param: Str) =
    match expr with
        | ExprAt(_span, inner) -> mentionsVar(inner)(param)
        | ExprVar(name) -> name == param
        | ExprIf(cond, thenE, elseE) ->
            if mentionsVar(cond)(param)
            then true
            else
                if mentionsVar(thenE)(param)
                then true
                else mentionsVar(elseE)(param)
        | ExprLet(name, val, body, _params, _ann, _traits) ->
            if mentionsVar(val)(param)
            then true
            else
                if name != param
                then mentionsVar(body)(param)
                else false
        | ExprLetResult(name, val, body) ->
            if mentionsVar(val)(param)
            then true
            else
                if name != param
                then mentionsVar(body)(param)
                else false
        | ExprLetRecursive(_name, val, body, _params, _ann, _traits) ->
            if mentionsVar(val)(param)
            then true
            else mentionsVar(body)(param)
        | ExprLambda(p, body, _ann) ->
            if p != param
            then mentionsVar(body)(param)
            else false
        | ExprCall(func, arg, _isSugar, _layout) ->
            if mentionsVar(func)(param)
            then true
            else mentionsVar(arg)(param)
        | ExprMatch(scrutinee, arms, _defaultArm) ->
            if mentionsVar(scrutinee)(param)
            then true
            else mentionsVarArms(arms)(param)
        | ExprTuple(elements) -> mentionsVarList(elements)(param)
        | ExprList(elements, _isMultiline) -> mentionsVarList(elements)(param)
        | ExprCons(head, tail) ->
            if mentionsVar(head)(param)
            then true
            else mentionsVar(tail)(param)
        | ExprRecord(_, fields, _isMultiline) -> mentionsVarFields(fields)(param)
        | ExprRecordUpdate(record, fields) ->
            if mentionsVar(record)(param)
            then true
            else mentionsVarFields(fields)(param)
        | ExprAdd(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprSubtract(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprMultiply(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprDivide(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprModulo(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprBitwiseAnd(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprBitwiseOr(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprBitwiseXor(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprShiftLeft(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprShiftRight(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprBitwiseNot(operand) -> mentionsVar(operand)(param)
        | ExprLogicalNot(operand) -> mentionsVar(operand)(param)
        | ExprLogicalAnd(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprLogicalOr(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprEqual(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprNotEqual(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprLessThan(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprLessOrEqual(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprGreaterThan(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprGreaterOrEqual(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprResultPipe(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprResultMapErrorPipe(left, right) ->
            if mentionsVar(left)(param)
            then true
            else mentionsVar(right)(param)
        | ExprAwait(e) -> mentionsVar(e)(param)
        | ExprPerform(e) -> mentionsVar(e)(param)
        | _ -> false
and mentionsVarList (list: List(Expr)) (param: Str) =
    match list with
        | [] -> false
        | head :: tail ->
            if mentionsVar(head)(param)
            then true
            else mentionsVarList(tail)(param)
and mentionsVarFields (fields: List((Str, Expr))) (param: Str) =
    match fields with
        | [] -> false
        | field :: tail ->
            match field with
                | (_, expr) ->
                    if mentionsVar(expr)(param)
                    then true
                    else mentionsVarFields(tail)(param)
and mentionsVarArms (arms: List((Pattern, Expr, Maybe(Expr)))) (param: Str) =
    match arms with
        | [] -> false
        | arm :: tail ->
            match arm with
                | (_pat, body, guard) ->
                    let guardMentions =
                        match guard with
                            | None -> false
                            | Some(g) -> mentionsVar(g)(param)
                    in
                        if guardMentions
                        then true
                        else
                            if mentionsVar(body)(param)
                            then true
                            else mentionsVarArms(tail)(param)

// --- Capture analysis: free variables of a function body other than its own parameters ---
let recursive listContainsStr (list: List(Str)) (target: Str) =
    match list with
        | [] -> false
        | head :: tail ->
            if head == target
            then true
            else listContainsStr(tail)(target)

let addFreeVar (acc: List(Str)) (bound: List(Str)) (name: Str) =
    if listContainsStr(bound)(name)
    then acc
    else
        if listContainsStr(acc)(name)
        then acc
        else name :: acc

let recursive collectFreeVars (expr: Expr) (bound: List(Str)) (acc: List(Str)) =
    match expr with
        | ExprAt(_span, inner) -> collectFreeVars(inner)(bound)(acc)
        | ExprInt(_) -> acc
        | ExprBigInt(_) -> acc
        | ExprUInt(_, _, _) -> acc
        | ExprFloat(_, _) -> acc
        | ExprBool(_) -> acc
        | ExprString(_) -> acc
        | ExprRune(_) -> acc
        | ExprVar(name) -> addFreeVar(acc)(bound)(name)
        | ExprQualifiedVar(_, _) -> acc
        | ExprIf(cond, thenE, elseE) ->
            let a1 = collectFreeVars(cond)(bound)(acc)
            in
                let a2 = collectFreeVars(thenE)(bound)(a1)
                in collectFreeVars(elseE)(bound)(a2)
        | ExprLet(name, val, body, _params, _ann, _traits) ->
            let a1 = collectFreeVars(val)(bound)(acc)
            in collectFreeVars(body)(name :: bound)(a1)
        | ExprLetResult(name, val, body) ->
            let a1 = collectFreeVars(val)(bound)(acc)
            in collectFreeVars(body)(name :: bound)(a1)
        | ExprLetRecursive(name, val, body, _params, _ann, _traits) ->
            let nextBound = name :: bound
            in
                let a1 = collectFreeVars(val)(nextBound)(acc)
                in collectFreeVars(body)(nextBound)(a1)
        | ExprLambda(p, body, _ann) -> collectFreeVars(body)(p :: bound)(acc)
        | ExprCall(func, arg, _isSugar, _layout) ->
            let a1 = collectFreeVars(func)(bound)(acc)
            in collectFreeVars(arg)(bound)(a1)
        | ExprMatch(scrutinee, arms, _defaultArm) ->
            let a1 = collectFreeVars(scrutinee)(bound)(acc)
            in collectFreeVarsArms(arms)(bound)(a1)
        | ExprTuple(elements) -> collectFreeVarsList(elements)(bound)(acc)
        | ExprList(elements, _isMultiline) -> collectFreeVarsList(elements)(bound)(acc)
        | ExprCons(head, tail) ->
            let a1 = collectFreeVars(head)(bound)(acc)
            in collectFreeVars(tail)(bound)(a1)
        | ExprRecord(_, fields, _isMultiline) -> collectFreeVarsFields(fields)(bound)(acc)
        | ExprRecordUpdate(record, fields) ->
            let a1 = collectFreeVars(record)(bound)(acc)
            in collectFreeVarsFields(fields)(bound)(a1)
        | ExprAdd(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprSubtract(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprMultiply(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprDivide(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprModulo(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprBitwiseAnd(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprBitwiseOr(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprBitwiseXor(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprShiftLeft(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprShiftRight(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprBitwiseNot(operand) -> collectFreeVars(operand)(bound)(acc)
        | ExprLogicalNot(operand) -> collectFreeVars(operand)(bound)(acc)
        | ExprLogicalAnd(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprLogicalOr(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprEqual(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprNotEqual(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprLessThan(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprLessOrEqual(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprGreaterThan(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprGreaterOrEqual(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprResultPipe(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprResultMapErrorPipe(left, right) -> collectFreeVarsPair(left)(right)(bound)(acc)
        | ExprAwait(e) -> collectFreeVars(e)(bound)(acc)
        | ExprPerform(e) -> collectFreeVars(e)(bound)(acc)
        | _ -> acc
and collectFreeVarsPair (left: Expr) (right: Expr) (bound: List(Str)) (acc: List(Str)) =
    (let a1 = collectFreeVars(left)(bound)(acc)
    in collectFreeVars(right)(bound)(a1))
and collectFreeVarsList (list: List(Expr)) (bound: List(Str)) (acc: List(Str)) =
    match list with
        | [] -> acc
        | head :: tail ->
            let a1 = collectFreeVars(head)(bound)(acc)
            in collectFreeVarsList(tail)(bound)(a1)
and collectFreeVarsFields (fields: List((Str, Expr))) (bound: List(Str)) (acc: List(Str)) =
    match fields with
        | [] -> acc
        | field :: tail ->
            match field with
                | (_, expr) ->
                    let a1 = collectFreeVars(expr)(bound)(acc)
                    in collectFreeVarsFields(tail)(bound)(a1)
and collectFreeVarsArms (arms: List((Pattern, Expr, Maybe(Expr)))) (bound: List(Str)) (acc: List(Str)) =
    match arms with
        | [] -> acc
        | arm :: tail ->
            match arm with
                | (_pat, body, guard) ->
                    let a1 =
                        match guard with
                            | None -> acc
                            | Some(g) -> collectFreeVars(g)(bound)(acc)
                    in
                        let a2 = collectFreeVars(body)(bound)(a1)
                        in collectFreeVarsArms(tail)(bound)(a2)

// The free variables of a function body other than its own parameters, in first-use order. A
// program-level pass (`inferProgramOwnership`) further excludes every other whole-program function
// name, leaving only genuine closure captures from an enclosing scope.
let computeCaptures (body: Expr) (params: List(Str)) =
    []
    |> collectFreeVars(body)(params)
    |> reverse

// --- Borrow-read walk ---
// A parameter is used only as a borrow read when every mention of it is the resource argument of
// a read-only resource builtin, or the whole parameter handed at position i to a registered
// function whose parameter i is currently borrowed in `table`. Any other mention consumes it:
// returned, stored in a constructor, compared, captured by a lambda, or passed to a call the table
// does not resolve. `shadowed` holds the names bound between the function body and the current
// expression, so a locally rebound function name never resolves to the registered function.
let recursive patternBoundNames (pattern: Pattern) (names: List(Str)) =
    match pattern with
        | PatternAt(_span, inner) -> patternBoundNames(inner)(names)
        | PatternVar(name) -> name :: names
        | PatternCons(head, tail) ->
            names
            |> patternBoundNames(head)
            |> patternBoundNames(tail)
        | PatternTuple(patterns) -> patternListBoundNames(patterns)(names)
        | PatternConstructor(_name, patterns) -> patternListBoundNames(patterns)(names)
        | PatternRecord(_name, fields) -> patternFieldBoundNames(fields)(names)
        | PatternAs(inner, name) -> patternBoundNames(inner)(name :: names)
        | PatternOr(patterns) -> patternListBoundNames(patterns)(names)
        | _ -> names
and patternListBoundNames (patterns: List(Pattern)) (names: List(Str)) =
    match patterns with
        | [] -> names
        | pattern :: rest ->
            names
            |> patternBoundNames(pattern)
            |> patternListBoundNames(rest)
and patternFieldBoundNames (fields: List((Str, Pattern))) (names: List(Str)) =
    match fields with
        | [] -> names
        | (_field, pattern) :: rest ->
            names
            |> patternBoundNames(pattern)
            |> patternFieldBoundNames(rest)

// The registered function `name` resolves to exactly one table entry whose arity matches the
// call; a shadowed, unregistered, ambiguous, or partially applied callee resolves to nothing.
let recursive lookupUniqueOwnership (name: Str) (table: ProgramParameterOwnership) (found: Maybe(List((Str, ParameterOwnership)))) =
    match table with
        | [] -> found
        | (candidate, ownership) :: rest ->
            if candidate != name
            then lookupUniqueOwnership(name)(rest)(found)
            else
                match found with
                    | Some(_) -> None
                    | None -> lookupUniqueOwnership(name)(rest)(Some(ownership))

let handOffOwnership (table: ProgramParameterOwnership) (shadowed: List(Str)) (callee: Str) (argumentCount: Int) =
    if listContainsStr(shadowed)(callee)
    then None
    else
        match lookupUniqueOwnership(callee)(table)(None) with
            | Some(ownership) ->
                if length(ownership) == argumentCount
                then Some(ownership)
                else None
            | None -> None

let isWholeParameter (expr: Expr) (param: Str) =
    match stripSpan(expr) with
        | ExprVar(name) -> name == param
        | _ -> false

let recursive borrowReadWalk (table: ProgramParameterOwnership) (shadowed: List(Str)) (expr: Expr) (param: Str) =
    match expr with
        | ExprAt(_span, inner) -> borrowReadWalk(table)(shadowed)(inner)(param)
        | ExprInt(_) -> true
        | ExprBigInt(_) -> true
        | ExprUInt(_, _, _) -> true
        | ExprFloat(_, _) -> true
        | ExprBool(_) -> true
        | ExprString(_) -> true
        | ExprRune(_) -> true
        | ExprVar(name) -> name != param
        | ExprQualifiedVar(_, _) -> true
        | ExprIf(cond, thenE, elseE) ->
            if borrowReadWalk(table)(shadowed)(cond)(param)
            then borrowReadPair(table)(shadowed)(thenE)(elseE)(param)
            else false
        | ExprLet(name, val, body, _params, _ann, _traits) ->
            if name == param
            then false
            else
                if borrowReadWalk(table)(shadowed)(val)(param)
                then borrowReadWalk(table)(name :: shadowed)(body)(param)
                else false
        | ExprLetResult(name, val, body) ->
            if name == param
            then false
            else
                if borrowReadWalk(table)(shadowed)(val)(param)
                then borrowReadWalk(table)(name :: shadowed)(body)(param)
                else false
        | ExprLetRecursive(name, val, body, _params, _ann, _traits) ->
            if name == param
            then false
            else borrowReadPair(table)(name :: shadowed)(val)(body)(param)
        | ExprLambda(p, body, _ann) ->
            if p == param
            then true
            else
                param
                |> mentionsVar(body)
                |> notBool
        | ExprCall(_, _, _, _) -> borrowReadCall(table)(shadowed)(expr)(param)
        | ExprMatch(scrutinee, arms, _defaultArm) ->
            if borrowReadWalk(table)(shadowed)(scrutinee)(param)
            then borrowReadArms(table)(shadowed)(arms)(param)
            else false
        | ExprTuple(elements) -> borrowReadList(table)(shadowed)(elements)(param)
        | ExprList(elements, _isMultiline) -> borrowReadList(table)(shadowed)(elements)(param)
        | ExprCons(head, tail) -> borrowReadPair(table)(shadowed)(head)(tail)(param)
        | ExprRecord(_, fields, _isMultiline) -> borrowReadFields(table)(shadowed)(fields)(param)
        | ExprRecordUpdate(record, fields) ->
            if borrowReadWalk(table)(shadowed)(record)(param)
            then borrowReadFields(table)(shadowed)(fields)(param)
            else false
        | ExprAdd(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprSubtract(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprMultiply(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprDivide(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprModulo(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprBitwiseAnd(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprBitwiseOr(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprBitwiseXor(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprShiftLeft(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprShiftRight(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprBitwiseNot(operand) -> borrowReadWalk(table)(shadowed)(operand)(param)
        | ExprLogicalNot(operand) -> borrowReadWalk(table)(shadowed)(operand)(param)
        | ExprLogicalAnd(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprLogicalOr(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprEqual(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprNotEqual(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprLessThan(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprLessOrEqual(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprGreaterThan(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprGreaterOrEqual(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprResultPipe(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprResultMapErrorPipe(left, right) -> borrowReadPair(table)(shadowed)(left)(right)(param)
        | ExprAwait(e) -> borrowReadWalk(table)(shadowed)(e)(param)
        | ExprPerform(e) -> borrowReadWalk(table)(shadowed)(e)(param)
        | _ ->
            param
            |> mentionsVar(expr)
            |> notBool
and borrowReadPair (table: ProgramParameterOwnership) (shadowed: List(Str)) (left: Expr) (right: Expr) (param: Str) =
    if borrowReadWalk(table)(shadowed)(left)(param)
    then borrowReadWalk(table)(shadowed)(right)(param)
    else false
and borrowReadCall (table: ProgramParameterOwnership) (shadowed: List(Str)) (call: Expr) (param: Str) =
    match collectCallArgsAndRoot(call)([]) with
        | (root, args) ->
            match stripSpan(root) with
                | ExprQualifiedVar(mod, name) ->
                    if isBorrowReadResourceOp(mod)(name)
                    then
                        match args with
                            | firstArg :: otherArgs ->
                                if isWholeParameter(firstArg)(param)
                                then borrowReadList(table)(shadowed)(otherArgs)(param)
                                else borrowReadList(table)(shadowed)(args)(param)
                            | [] -> true
                    else borrowReadList(table)(shadowed)(args)(param)
                | ExprVar(callee) ->
                    if callee == param
                    then false
                    else
                        match args
                        |> length
                        |> handOffOwnership(table)(shadowed)(callee) with
                            | Some(ownership) -> borrowReadHandOff(table)(shadowed)(args)(ownership)(param)
                            | None -> borrowReadList(table)(shadowed)(args)(param)
                | _ ->
                    if borrowReadWalk(table)(shadowed)(root)(param)
                    then borrowReadList(table)(shadowed)(args)(param)
                    else false
and borrowReadHandOff (table: ProgramParameterOwnership) (shadowed: List(Str)) (args: List(Expr)) (ownership: List((Str, ParameterOwnership))) (param: Str) =
    match args with
        | [] -> true
        | arg :: restArgs ->
            match ownership with
                | (_name, Borrowed) :: restOwnership ->
                    if isWholeParameter(arg)(param)
                    then borrowReadHandOff(table)(shadowed)(restArgs)(restOwnership)(param)
                    else
                        if borrowReadWalk(table)(shadowed)(arg)(param)
                        then borrowReadHandOff(table)(shadowed)(restArgs)(restOwnership)(param)
                        else false
                | (_name, Consumed) :: restOwnership ->
                    if borrowReadWalk(table)(shadowed)(arg)(param)
                    then borrowReadHandOff(table)(shadowed)(restArgs)(restOwnership)(param)
                    else false
                | [] -> borrowReadList(table)(shadowed)(args)(param)
and borrowReadList (table: ProgramParameterOwnership) (shadowed: List(Str)) (list: List(Expr)) (param: Str) =
    match list with
        | [] -> true
        | head :: tail ->
            if borrowReadWalk(table)(shadowed)(head)(param)
            then borrowReadList(table)(shadowed)(tail)(param)
            else false
and borrowReadFields (table: ProgramParameterOwnership) (shadowed: List(Str)) (fields: List((Str, Expr))) (param: Str) =
    match fields with
        | [] -> true
        | (_field, expr) :: tail ->
            if borrowReadWalk(table)(shadowed)(expr)(param)
            then borrowReadFields(table)(shadowed)(tail)(param)
            else false
and borrowReadArms (table: ProgramParameterOwnership) (shadowed: List(Str)) (arms: List((Pattern, Expr, Maybe(Expr)))) (param: Str) =
    match arms with
        | [] -> true
        | (pattern, body, guard) :: tail ->
            let armShadowed = patternBoundNames(pattern)(shadowed)
            in
                let guardOk =
                    match guard with
                        | None -> true
                        | Some(g) -> borrowReadWalk(table)(armShadowed)(g)(param)
                in
                    if guardOk
                    then
                        if borrowReadWalk(table)(armShadowed)(body)(param)
                        then borrowReadArms(table)(shadowed)(tail)(param)
                        else false
                    else false

// The single-function verdict: no other function is known, so every hand-off consumes.
let isParamUsedOnlyAsBorrowRead (expr: Expr) (param: Str) = borrowReadWalk([])([])(expr)(param)

// --- Whole-program inspect-only parameters ---
// `inferProgramParameterOwnership` refines the single-function verdict with every other registered
// function's: the table starts with every parameter borrowed, each pass re-classifies every
// function against the current table (a parameter is demoted to consumed as soon as one mention
// is not a borrow read, and never recovers), and the pass repeats until no parameter changes. The
// consumed set is therefore the least fixpoint of the demotion rule, so a hand-off through a
// chain or a cycle of purely inspecting functions stays borrowed, while a single retaining member
// of a cycle demotes everyone that hands the parameter to it. Termination is bounded by the
// finite parameter count: every non-final pass demotes at least one parameter.
let recursive lookupProgramParameterOwnership (name: Str) (table: ProgramParameterOwnership) =
    match table with
        | [] -> None
        | (candidate, ownership) :: rest ->
            if candidate == name
            then Some(ownership)
            else lookupProgramParameterOwnership(name)(rest)

let recursive allBorrowed (params: List(Str)) =
    match params with
        | [] -> []
        | param :: rest -> (param, Borrowed) :: allBorrowed(rest)

let recursive optimisticProgramParameterOwnership (funcs: List((Str, List(Str), Expr))) =
    match funcs with
        | [] -> []
        | (name, params, _body) :: rest -> (name, allBorrowed(params)) :: optimisticProgramParameterOwnership(rest)

let recursive demoteParameters (table: ProgramParameterOwnership) (shadowed: List(Str)) (body: Expr) (previous: List((Str, ParameterOwnership))) =
    match previous with
        | [] -> []
        | (param, Consumed) :: rest -> (param, Consumed) :: demoteParameters(table)(shadowed)(body)(rest)
        | (param, Borrowed) :: rest ->
            let own =
                if borrowReadWalk(table)(shadowed)(body)(param)
                then Borrowed
                else Consumed
            in (param, own) :: demoteParameters(table)(shadowed)(body)(rest)

let recursive refineProgramParameterOwnership (funcs: List((Str, List(Str), Expr))) (table: ProgramParameterOwnership) (previous: ProgramParameterOwnership) =
    match funcs with
        | [] -> []
        | (name, params, body) :: restFuncs ->
            match previous with
                | (_name, ownership) :: restPrevious -> (name, demoteParameters(table)(params)(body)(ownership)) :: refineProgramParameterOwnership(restFuncs)(table)(restPrevious)
                | [] -> []

let recursive runInspectOnlyFixpoint (funcs: List((Str, List(Str), Expr))) (table: ProgramParameterOwnership) =
    (let next = refineProgramParameterOwnership(funcs)(table)(table)
    in
        if next == table
        then table
        else runInspectOnlyFixpoint(funcs)(next))

// The parameter ownership of every registered function, one `(name, parameters, body)` triple per
// function, after the whole-program fixpoint; entries keep the registration order.
let inferProgramParameterOwnership (funcs: List((Str, List(Str), Expr))) =
    funcs
    |> optimisticProgramParameterOwnership
    |> runInspectOnlyFixpoint(funcs)

// The innermost body of a curried lambda value and its parameter chain.
let recursive lambdaParameters (value: Expr) (parameters: List(Str)) =
    match value with
        | ExprAt(_span, inner) -> lambdaParameters(inner)(parameters)
        | ExprLambda(parameter, body, _annotation) -> lambdaParameters(body)(parameter :: parameters)
        | body -> (reverse(parameters), body)

let registeredFunction (binding: LetBindingSyntax) =
    match lambdaParameters(binding.value)([]) with
        | (parameters, body) -> (binding.name, parameters, body)

let recursive registeredGroup (bindings: List(LetBindingSyntax)) (acc: List((Str, List(Str), Expr))) =
    match bindings with
        | [] -> acc
        | binding :: rest -> registeredGroup(rest)(registeredFunction(binding) :: acc)

let recursive registerTopLevelItems (items: List(TopLevelItem)) (acc: List((Str, List(Str), Expr))) =
    match items with
        | [] -> reverse(acc)
        | TopLevelAt(_span, inner) :: rest -> registerTopLevelItems(inner :: rest)(acc)
        | TopLevelLet(binding, _isRecursive) :: rest -> registerTopLevelItems(rest)(registeredFunction(binding) :: acc)
        | TopLevelRecursiveGroup(bindings) :: rest ->
            acc
            |> registeredGroup(bindings)
            |> registerTopLevelItems(rest)
        | _ :: rest -> registerTopLevelItems(rest)(acc)

// Every top-level `let` of a parsed program as a `(name, parameters, body)` triple, a plain value
// binding contributing an empty parameter list.
let topLevelFunctions (program: ProgramSyntax) = registerTopLevelItems(program.items)([])

// --- Result Reachability Analysis for an Expression ---
let recursive lookupEnv (name: Str) (env: List((Str, ResultReachState))) =
    match env with
        | [] -> None
        | entry :: rest ->
            match entry with
                | (k, state) ->
                    if k == name
                    then Some(state)
                    else lookupEnv(name)(rest)

let recursive analyzeExprReach (expr: Expr) (env: List((Str, ResultReachState))) =
    match expr with
        | ExprAt(_span, inner) -> analyzeExprReach(inner)(env)
        | ExprInt(_) -> reachBottom(Unit)
        | ExprBigInt(_) -> reachBottom(Unit)
        | ExprUInt(_, _, _) -> reachBottom(Unit)
        | ExprFloat(_, _) -> reachBottom(Unit)
        | ExprBool(_) -> reachBottom(Unit)
        | ExprString(_) -> reachBottom(Unit)
        | ExprRune(_) -> reachBottom(Unit)
        | ExprVar(name) ->
            // Bare variable return/use is consuming, not borrow
            match lookupEnv(name)(env) with
                | Some(state) -> state
                | None -> reachBottom(Unit)
        | ExprQualifiedVar(_, _) -> reachBottom(Unit)
        | ExprIf(_cond, thenE, elseE) ->
            let reachThen = analyzeExprReach(thenE)(env)
            in
                let reachElse = analyzeExprReach(elseE)(env)
                in reachJoin(reachThen)(reachElse)
        | ExprLet(name, val, body, _params, _ann, _traits) ->
            let valReach = analyzeExprReach(val)(env)
            in
                let nextEnv = (name, valReach) :: env
                in analyzeExprReach(body)(nextEnv)
        | ExprLetResult(name, val, body) ->
            let valReach = analyzeExprReach(val)(env)
            in
                let nextEnv = (name, valReach) :: env
                in analyzeExprReach(body)(nextEnv)
        | ExprLetRecursive(recName, val, body, _params, _ann, _traits) ->
            let valReach = analyzeExprReach(val)(env)
            in
                let nextEnv = (recName, valReach) :: env
                in analyzeExprReach(body)(nextEnv)
        | ExprLambda(_p, body, _ann) -> analyzeExprReach(body)(env)
        | ExprCall(func, arg, _isSugar, _layout) ->
            let fReach = analyzeExprReach(func)(env)
            in
                let aReach = analyzeExprReach(arg)(env)
                in reachSum(fReach)(aReach)
        | ExprMatch(_scrutinee, arms, _defaultArm) -> analyzeMatchArmsReach(arms)(env)
        | ExprTuple(elements) -> analyzeExprListSumReach(elements)(env)
        | ExprList(elements, _isMultiline) -> analyzeExprListSumReach(elements)(env)
        | ExprCons(head, tail) ->
            let hReach = analyzeExprReach(head)(env)
            in
                let tReach = analyzeExprReach(tail)(env)
                in reachSum(hReach)(tReach)
        | ExprRecord(_, fields, _isMultiline) -> analyzeRecordFieldsSumReach(fields)(env)
        | ExprRecordUpdate(record, fields) ->
            let rReach = analyzeExprReach(record)(env)
            in
                let fReach = analyzeRecordFieldsSumReach(fields)(env)
                in reachSum(rReach)(fReach)
        | ExprAdd(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprSubtract(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprMultiply(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprDivide(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprModulo(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprBitwiseAnd(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprBitwiseOr(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprBitwiseXor(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprShiftLeft(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprShiftRight(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprBitwiseNot(operand) -> analyzeExprReach(operand)(env)
        | ExprLogicalNot(operand) -> analyzeExprReach(operand)(env)
        | ExprLogicalAnd(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprLogicalOr(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprEqual(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprNotEqual(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprLessThan(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprLessOrEqual(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprGreaterThan(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprGreaterOrEqual(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprResultPipe(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprResultMapErrorPipe(left, right) ->
            let lReach = analyzeExprReach(left)(env)
            in
                let rReach = analyzeExprReach(right)(env)
                in reachSum(lReach)(rReach)
        | ExprAwait(e) -> analyzeExprReach(e)(env)
        | ExprPerform(e) -> analyzeExprReach(e)(env)
        | _ -> reachPoisoned(UnmodelledReach)
and analyzeExprListSumReach (elements: List(Expr)) (env: List((Str, ResultReachState))) =
    match elements with
        | [] -> reachBottom(Unit)
        | head :: tail ->
            let h = analyzeExprReach(head)(env)
            in
                let t = analyzeExprListSumReach(tail)(env)
                in reachSum(h)(t)
and analyzeRecordFieldsSumReach (fields: List((Str, Expr))) (env: List((Str, ResultReachState))) =
    match fields with
        | [] -> reachBottom(Unit)
        | field :: tail ->
            match field with
                | (_, expr) ->
                    let e = analyzeExprReach(expr)(env)
                    in
                        let rest = analyzeRecordFieldsSumReach(tail)(env)
                        in reachSum(e)(rest)
and analyzeMatchArmsReach (arms: List((Pattern, Expr, Maybe(Expr)))) (env: List((Str, ResultReachState))) =
    match arms with
        | [] -> reachBottom(Unit)
        | arm :: tail ->
            match arm with
                | (_pat, body, _guard) ->
                    let armReach = analyzeExprReach(body)(env)
                    in
                        let restReach = analyzeMatchArmsReach(tail)(env)
                        in reachJoin(armReach)(restReach)

// --- Parameter Ownership Classification & Summary Construction ---
let recursive classifyParameterOwnership (params: List(Str)) (body: Expr) (acc: List((Str, ParameterOwnership))) =
    match params with
        | [] -> reverse(acc)
        | param :: rest ->
            let isBorrow = isParamUsedOnlyAsBorrowRead(body)(param)
            in
                let own =
                    if isBorrow
                    then Borrowed
                    else Consumed
                in classifyParameterOwnership(rest)(body)((param, own) :: acc)

let recursive buildInitialEnv (params: List(Str)) (acc: List((Str, ResultReachState))) =
    match params with
        | [] -> acc
        | param :: rest -> buildInitialEnv(rest)((param, reachParam(param)) :: acc)

let recursive makeMoveSafetyProofs (params: List(Str)) (acc: List((Str, ParameterMoveSafetyProof))) =
    match params with
        | [] -> reverse(acc)
        | param :: rest ->
            let proof = ParameterMoveSafetyProof(isMoveSafe = true, causes = [MoveCauseNone])
            in makeMoveSafetyProofs(rest)((param, proof) :: acc)

let recursive lookupProvenance (name: Str) (provMap: List((Str, FunctionResultProvenance))) =
    match provMap with
        | [] -> None
        | entry :: rest ->
            match entry with
                | (k, v) ->
                    if k == name
                    then Some(v)
                    else lookupProvenance(name)(rest)

// Every parameter this walk reaches is reached whole: reach flows only through a parameter's own
// variable (and `let` aliases of it) — `analyzeMatchArmsReach` analyzes each arm under the
// unchanged environment, so a pattern-bound name (a matched head or field, the only way to reach
// a parameter's component) carries no reach at all. Stage 0 additionally tracks component paths
// ("values/0") and keeps whole and component reach apart; until that path model is ported the
// whole set is exactly the reached set, and a component-only reach is simply absent.
let recursive reachedParameterNames (entries: List(ParameterReachEntry)) =
    match entries with
        | [] -> []
        | ParameterReachEntry { parameterName = name } :: rest -> name :: reachedParameterNames(rest)

let inferFunctionOwnershipWith (sig: FunctionSignature) (provMap: List((Str, FunctionResultProvenance))) (paramOwnership: List((Str, ParameterOwnership))) =
    match sig with
        | FunctionSignature { name = fName, origin = origin, parameters = params, body = body } ->
            let initialEnv = buildInitialEnv(params)([])
            in
                let reachState = analyzeExprReach(body)(initialEnv)
                in
                    let reachFacts =
                        match reachState with
                            | ResultReachState { counts = counts, causes = causes, isPoisoned = poisoned } ->
                                FunctionResultReachFacts(
                                    parameterReach = counts,
                                    causes = causes,
                                    isPoisoned = poisoned,
                                    wholeParameterReach = reachedParameterNames(counts)
                                )
                    in
                        let borrowed = getBorrowedParameters(paramOwnership)
                        in
                            let consumed = getConsumedParameters(paramOwnership)
                            in
                                let census = FunctionCallCensus(directCallCount = 1, causes = [CensusCauseNone])
                                in
                                    let moveProofs = makeMoveSafetyProofs(params)([])
                                    in
                                        let prov =
                                            match lookupProvenance(fName)(provMap) with
                                                | Some(p) -> p
                                                | None -> FunctionResultProvenance(rcEligible = true, forwardsTo = None, bytesProvenance = BytesProvenanceUnknown)
                                        in
                                            FunctionOwnershipSummary(
                                                functionName = fName,
                                                origin = origin,
                                                parameters = params,
                                                parameterOwnership = paramOwnership,
                                                borrowedParameters = borrowed,
                                                consumedParameters = consumed,
                                                uniqueParameters = params,
                                                callCensus = census,
                                                parameterMoveSafety = moveProofs,
                                                capturedValues = computeCaptures(body)(params),
                                                resultReachFacts = reachFacts,
                                                resultProvenance = prov,
                                                tcoParamFacts = [],
                                                mayExecuteUnderLiveHandlerPost = false
                                            )

// The single-function summary: parameter ownership from the body alone.
let inferFunctionOwnership (sig: FunctionSignature) (provMap: List((Str, FunctionResultProvenance))) =
    match sig with
        | FunctionSignature { parameters = params, body = body } ->
            []
            |> classifyParameterOwnership(params)(body)
            |> inferFunctionOwnershipWith(sig)(provMap)

let recursive collectFunctionNames (funcs: List(FunctionSignature)) (acc: List(Str)) =
    match funcs with
        | [] -> acc
        | func :: rest ->
            match func with
                | FunctionSignature { name = n } -> collectFunctionNames(rest)(n :: acc)

let recursive filterOutNames (names: List(Str)) (excluded: List(Str)) =
    match names with
        | [] -> []
        | name :: rest ->
            if listContainsStr(excluded)(name)
            then filterOutNames(rest)(excluded)
            else name :: filterOutNames(rest)(excluded)

let recursive signatureFunctions (funcs: List(FunctionSignature)) =
    match funcs with
        | [] -> []
        | FunctionSignature { name = name, parameters = params, body = body } :: rest -> (name, params, body) :: signatureFunctions(rest)

let programOwnershipOf (sig: FunctionSignature) (table: ProgramParameterOwnership) =
    match sig with
        | FunctionSignature { name = name, parameters = params, body = body } ->
            match lookupProgramParameterOwnership(name)(table) with
                | Some(ownership) -> ownership
                | None -> classifyParameterOwnership(params)(body)([])

let recursive inferProgramOwnershipAux (funcs: List(FunctionSignature)) (provMap: List((Str, FunctionResultProvenance))) (programNames: List(Str)) (table: ProgramParameterOwnership) (acc: List(FunctionOwnershipSummary)) =
    match funcs with
        | [] -> reverse(acc)
        | func :: rest ->
            let rawSummary =
                table
                |> programOwnershipOf(func)
                |> inferFunctionOwnershipWith(func)(provMap)
            in
                match rawSummary with
                    | FunctionOwnershipSummary { capturedValues = caps } ->
                        let summary = rawSummary with capturedValues = filterOutNames(caps)(programNames)
                        in inferProgramOwnershipAux(rest)(provMap)(programNames)(table)(summary :: acc)

// Every top-level function name is excluded from `capturedValues`: an ordinary call to another
// whole-program function is not a closure capture, only a free reference into an enclosing scope is.
// Parameter ownership comes from the whole-program inspect-only fixpoint over every signature.
let inferProgramOwnership (funcs: List(FunctionSignature)) (provNodes: List(ProvenanceFunctionNode)) =
    (let provMap = resolveResultProvenances(provNodes)
    in
        let programNames = collectFunctionNames(funcs)([])
        in
            let table =
                funcs
                |> signatureFunctions
                |> inferProgramParameterOwnership
            in inferProgramOwnershipAux(funcs)(provMap)(programNames)(table)([]))
