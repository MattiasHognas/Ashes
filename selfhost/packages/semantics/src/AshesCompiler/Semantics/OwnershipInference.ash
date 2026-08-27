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
    value analyzeExprReach,
    value computeCaptures,
    value inferFunctionOwnership,
    value inferProgramOwnership,
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
let computeCaptures (body: Expr) (params: List(Str)) = reverse(collectFreeVars(body)(params)([]))

let recursive isParamUsedOnlyAsBorrowRead (expr: Expr) (param: Str) =
    match expr with
        | ExprAt(_span, inner) -> isParamUsedOnlyAsBorrowRead(inner)(param)
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
            if isParamUsedOnlyAsBorrowRead(cond)(param)
            then
                if isParamUsedOnlyAsBorrowRead(thenE)(param)
                then isParamUsedOnlyAsBorrowRead(elseE)(param)
                else false
            else false
        | ExprLet(name, val, body, _params, _ann, _traits) ->
            if name == param
            then false
            else
                if isParamUsedOnlyAsBorrowRead(val)(param)
                then isParamUsedOnlyAsBorrowRead(body)(param)
                else false
        | ExprLetResult(name, val, body) ->
            if name == param
            then false
            else
                if isParamUsedOnlyAsBorrowRead(val)(param)
                then isParamUsedOnlyAsBorrowRead(body)(param)
                else false
        | ExprLetRecursive(name, val, body, _params, _ann, _traits) ->
            if name == param
            then false
            else
                if isParamUsedOnlyAsBorrowRead(val)(param)
                then isParamUsedOnlyAsBorrowRead(body)(param)
                else false
        | ExprLambda(p, body, _ann) ->
            if p == param
            then true
            else notBool(mentionsVar(body)(param))
        | ExprCall(_, _, _, _) -> checkCallUsesParamOnlyAsBorrowRead(expr)(param)
        | ExprMatch(scrutinee, arms, _defaultArm) ->
            if isParamUsedOnlyAsBorrowRead(scrutinee)(param)
            then checkMatchArmsBorrow(arms)(param)
            else false
        | ExprTuple(elements) -> checkExprListBorrow(elements)(param)
        | ExprList(elements, _isMultiline) -> checkExprListBorrow(elements)(param)
        | ExprCons(head, tail) ->
            if isParamUsedOnlyAsBorrowRead(head)(param)
            then isParamUsedOnlyAsBorrowRead(tail)(param)
            else false
        | ExprRecord(_, fields, _isMultiline) -> checkRecordFieldsBorrow(fields)(param)
        | ExprRecordUpdate(record, fields) ->
            if isParamUsedOnlyAsBorrowRead(record)(param)
            then checkRecordFieldsBorrow(fields)(param)
            else false
        | ExprAdd(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprSubtract(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprMultiply(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprDivide(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprModulo(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprBitwiseAnd(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprBitwiseOr(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprBitwiseXor(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprShiftLeft(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprShiftRight(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprBitwiseNot(operand) -> isParamUsedOnlyAsBorrowRead(operand)(param)
        | ExprLogicalNot(operand) -> isParamUsedOnlyAsBorrowRead(operand)(param)
        | ExprEqual(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprNotEqual(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprLessThan(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprLessOrEqual(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprGreaterThan(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprGreaterOrEqual(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprResultPipe(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprResultMapErrorPipe(left, right) ->
            if isParamUsedOnlyAsBorrowRead(left)(param)
            then isParamUsedOnlyAsBorrowRead(right)(param)
            else false
        | ExprAwait(e) -> isParamUsedOnlyAsBorrowRead(e)(param)
        | ExprPerform(e) -> isParamUsedOnlyAsBorrowRead(e)(param)
        | _ -> notBool(mentionsVar(expr)(param))
and checkCallUsesParamOnlyAsBorrowRead (call: Expr) (param: Str) =
    match collectCallArgsAndRoot(call)([]) with
        | (root, args) ->
            match root with
                | ExprQualifiedVar(mod, name) ->
                    if isBorrowReadResourceOp(mod)(name)
                    then
                        match args with
                            | firstArg :: otherArgs ->
                                match firstArg with
                                    | ExprVar(vName) ->
                                        if vName == param
                                        then checkExprListBorrow(otherArgs)(param)
                                        else
                                            if isParamUsedOnlyAsBorrowRead(root)(param)
                                            then checkExprListBorrow(args)(param)
                                            else false
                                    | _ ->
                                        if isParamUsedOnlyAsBorrowRead(root)(param)
                                        then checkExprListBorrow(args)(param)
                                        else false
                            | [] -> isParamUsedOnlyAsBorrowRead(root)(param)
                    else
                        if isParamUsedOnlyAsBorrowRead(root)(param)
                        then checkExprListBorrow(args)(param)
                        else false
                | _ ->
                    if isParamUsedOnlyAsBorrowRead(root)(param)
                    then checkExprListBorrow(args)(param)
                    else false
and checkExprListBorrow (list: List(Expr)) (param: Str) =
    match list with
        | [] -> true
        | head :: tail ->
            if isParamUsedOnlyAsBorrowRead(head)(param)
            then checkExprListBorrow(tail)(param)
            else false
and checkRecordFieldsBorrow (fields: List((Str, Expr))) (param: Str) =
    match fields with
        | [] -> true
        | field :: tail ->
            match field with
                | (_, expr) ->
                    if isParamUsedOnlyAsBorrowRead(expr)(param)
                    then checkRecordFieldsBorrow(tail)(param)
                    else false
and checkMatchArmsBorrow (arms: List((Pattern, Expr, Maybe(Expr)))) (param: Str) =
    match arms with
        | [] -> true
        | arm :: tail ->
            match arm with
                | (_pat, body, guard) ->
                    let guardOk =
                        match guard with
                            | None -> true
                            | Some(g) -> isParamUsedOnlyAsBorrowRead(g)(param)
                    in
                        let bodyOk = isParamUsedOnlyAsBorrowRead(body)(param)
                        in
                            if guardOk
                            then
                                if bodyOk
                                then checkMatchArmsBorrow(tail)(param)
                                else false
                            else false

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

let inferFunctionOwnership (sig: FunctionSignature) (provMap: List((Str, FunctionResultProvenance))) =
    match sig with
        | FunctionSignature { name = fName, origin = origin, parameters = params, body = body } ->
            let initialEnv = buildInitialEnv(params)([])
            in
                let reachState = analyzeExprReach(body)(initialEnv)
                in
                    let reachFacts =
                        match reachState with
                            | ResultReachState { counts = counts, causes = causes, isPoisoned = poisoned } -> FunctionResultReachFacts(parameterReach = counts, causes = causes, isPoisoned = poisoned)
                    in
                        let paramOwnership = classifyParameterOwnership(params)(body)([])
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

let recursive inferProgramOwnershipAux (funcs: List(FunctionSignature)) (provMap: List((Str, FunctionResultProvenance))) (programNames: List(Str)) (acc: List(FunctionOwnershipSummary)) =
    match funcs with
        | [] -> reverse(acc)
        | func :: rest ->
            let rawSummary = inferFunctionOwnership(func)(provMap)
            in
                match rawSummary with
                    | FunctionOwnershipSummary { capturedValues = caps } ->
                        let summary = rawSummary with capturedValues = filterOutNames(caps)(programNames)
                        in inferProgramOwnershipAux(rest)(provMap)(programNames)(summary :: acc)

// Every top-level function name is excluded from `capturedValues`: an ordinary call to another
// whole-program function is not a closure capture, only a free reference into an enclosing scope is.
let inferProgramOwnership (funcs: List(FunctionSignature)) (provNodes: List(ProvenanceFunctionNode)) =
    (let provMap = resolveResultProvenances(provNodes)
    in
        let programNames = collectFunctionNames(funcs)([])
        in inferProgramOwnershipAux(funcs)(provMap)(programNames)([]))
