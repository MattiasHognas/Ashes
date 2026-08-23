// Tail-call optimization (TCO) analysis, stack-safety rules, and mutual recursion dispatch.
//
// Invariants:
// - Tail calls in self-recursive and mutually-recursive functions are detected deterministically.
// - Arguments are strictly evaluated left-to-right into temporary registers before updating
//   parameter registers/locals, preventing clobbering when arguments read previous parameters.
// - Mutually-recursive groups sharing identical arity and parameter types are unified into
//   a single self-recursive dispatch loop with lightweight entry wrappers.

import Ashes.Collection.List.append
import Ashes.Collection.List.filter
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
export (
    type TailPosition(..),
    type MutualMemberInfo(..),
    type MutualTcoPlan(..),
    type TcoTailCall(..),
    value isTailPosition,
    value countLambdaArity,
    value collectLambdaParamNames,
    value hasTailSelfCalls,
    value collectGroupTailCalls,
    value tryPlanMutualRecursionTco,
    value buildMutualDispatchParamNames,
    value formatMutualDispatchLabel,
    value formatMutualWrapperLabel,
)

type TailPosition =
    | InTailPos
    | NotInTailPos
    deriving {Eq, Show}

type MutualMemberInfo =
    | name: Str
    | paramNames: List(Str)
    | paramTypes: List(SemanticType)
    | returnType: SemanticType
    | arity: Int
    deriving {Eq, Show}

type MutualTcoPlan =
    | groupName: Str
    | dispatchName: Str
    | dispatchLabel: Str
    | arity: Int
    | members: List(MutualMemberInfo)
    | sharedParamNames: List(Str)
    | sharedParamTypes: List(SemanticType)
    | returnType: SemanticType
    deriving {Eq, Show}

type TcoTailCall =
    | targetName: Str
    | argumentCount: Int
    | isCrossMember: Bool
    deriving {Eq, Show}

let notBool b =
    if b
    then false
    else true

let isTailPosition pos =
    match pos with
        | InTailPos -> true
        | NotInTailPos -> false

let recursive countLambdaArity depth expr =
    match expr with
        | ExprAt(_span, inner) -> countLambdaArity(depth)(inner)
        | ExprLambda(_param, inner, _ann) -> countLambdaArity(depth + 1)(inner)
        | _ -> depth

let recursive collectLambdaParamNames acc expr =
    match expr with
        | ExprAt(_span, inner) -> collectLambdaParamNames(acc)(inner)
        | ExprLambda(name, inner, _ann) -> collectLambdaParamNames(name :: acc)(inner)
        | _ -> reverse(acc)

let recursive collectInnermostBody expr =
    match expr with
        | ExprAt(_span, inner) -> collectInnermostBody(inner)
        | ExprLambda(_param, inner, _ann) -> collectInnermostBody(inner)
        | _ -> expr

let recursive containsName name list =
    match list with
        | [] -> false
        | x :: rest ->
            if x == name
            then true
            else containsName(name)(rest)

let recursive findCalledTarget targetNames arity count expr =
    match expr with
        | ExprAt(_span, inner) -> findCalledTarget(targetNames)(arity)(count)(inner)
        | ExprCall(f, _arg, _sugar, _layout) -> findCalledTarget(targetNames)(arity)(count + 1)(f)
        | ExprVar(v) ->
            if count == arity
            then
                if containsName(v)(targetNames)
                then Some(v)
                else None
            else None
        | _ -> None

let checkTailApp targetNames arity expr acc =
    match findCalledTarget(targetNames)(arity)(0)(expr) with
        | Some(target) ->
            let call =
                TcoTailCall(
                    targetName = target,
                    argumentCount = arity,
                    isCrossMember = true
                )
            in call :: acc
        | None -> acc

let recursive collectTailCallsInExpr targetNames arity inTail expr acc =
    if notBool(isTailPosition(inTail))
    then acc
    else
        match expr with
            | ExprAt(_span, inner) -> collectTailCallsInExpr(targetNames)(arity)(inTail)(inner)(acc)
            | ExprIf(_cond, t, e) ->
                let accThen = collectTailCallsInExpr(targetNames)(arity)(InTailPos)(t)(acc)
                in collectTailCallsInExpr(targetNames)(arity)(InTailPos)(e)(accThen)
            | ExprMatch(_scrutinee, armList, _defaultArm) -> collectTailCallsInMatchArms(targetNames)(arity)(armList)(acc)
            | ExprLet(_name, _value, b, _params, _ann, _traits) -> collectTailCallsInExpr(targetNames)(arity)(InTailPos)(b)(acc)
            | ExprLetRecursive(_name, _value, b, _params, _ann, _traits) -> collectTailCallsInExpr(targetNames)(arity)(InTailPos)(b)(acc)
            | ExprCall(_, _, _, _) -> checkTailApp(targetNames)(arity)(expr)(acc)
            | _ -> acc
and collectTailCallsInMatchArms targetNames arity arms acc =
    match arms with
        | [] -> acc
        | (_pat, b, _guard) :: rest ->
            let accArm = collectTailCallsInExpr(targetNames)(arity)(InTailPos)(b)(acc)
            in collectTailCallsInMatchArms(targetNames)(arity)(rest)(accArm)

let hasTailSelfCalls expr funcName arity =
    (let calls = collectTailCallsInExpr(funcName :: [])(arity)(InTailPos)(expr)([])
    in
        match calls with
            | [] -> false
            | _ -> true)

let collectGroupTailCalls expr groupNames arity = collectTailCallsInExpr(groupNames)(arity)(InTailPos)(expr)([])

let recursive checkAllEqualTypes t0 list =
    match list with
        | [] -> true
        | t :: rest ->
            if t0 == t
            then checkAllEqualTypes(t0)(rest)
            else false

let recursive checkAllParamsMatch arity paramLists =
    if arity <= 0
    then true
    else
        let heads =
            map(given (l) ->
                match l with
                    | h :: _ -> h
                    | [] -> SemTuple([]))(paramLists)
        in
            match heads with
                | [] -> true
                | first :: rest ->
                    if checkAllEqualTypes(first)(rest)
                    then
                        let tails =
                            map(given (l) ->
                                match l with
                                    | _ :: t -> t
                                    | [] -> [])(paramLists)
                        in checkAllParamsMatch(arity - 1)(tails)
                    else false

let recursive hasCrossMemberCalls selfName calls =
    match calls with
        | [] -> false
        | call :: rest ->
            match call with
                | TcoTailCall { targetName = targetName } ->
                    if targetName == selfName
                    then hasCrossMemberCalls(selfName)(rest)
                    else true

let getMemberName member =
    match member with
        | MutualMemberInfo { name = n } -> n

let getMemberArity member =
    match member with
        | MutualMemberInfo { arity = a } -> a

let getMemberParamTypes member =
    match member with
        | MutualMemberInfo { paramTypes = pts } -> pts

let getMemberReturnType member =
    match member with
        | MutualMemberInfo { returnType = rt } -> rt

let getMemberParamNames member =
    match member with
        | MutualMemberInfo { paramNames = pns } -> pns

let recursive checkGroupHasCrossMemberTailCalls members groupNames arity =
    match members with
        | [] -> false
        | m :: rest ->
            let name = getMemberName(m)
            in
                let calls = collectTailCallsInExpr(groupNames)(arity)(InTailPos)(ExprVar(name))([])
                in
                    if hasCrossMemberCalls(name)(calls)
                    then true
                    else checkGroupHasCrossMemberTailCalls(rest)(groupNames)(arity)

let recursive checkAllBooleans list =
    match list with
        | [] -> true
        | b :: rest ->
            if b
            then checkAllBooleans(rest)
            else false

let tryPlanMutualRecursionTco groupName members =
    if length(members) < 2
    then None
    else
        match members with
            | [] -> None
            | first :: restMembers ->
                let firstArity = getMemberArity(first)
                in
                    if firstArity < 1
                    then None
                    else
                        let allSameArity =
                            map(given (m) -> getMemberArity(m) == firstArity)(members)
                        in
                            if notBool(checkAllBooleans(allSameArity))
                            then None
                            else
                                let allParamTypes =
                                    map(given (m) -> getMemberParamTypes(m))(members)
                                in
                                    if notBool(checkAllParamsMatch(firstArity)(allParamTypes))
                                    then None
                                    else
                                        let firstReturnType = getMemberReturnType(first)
                                        in
                                            let allReturnTypes =
                                                map(given (m) -> getMemberReturnType(m))(members)
                                            in
                                                if notBool(checkAllEqualTypes(firstReturnType)(allReturnTypes))
                                                then None
                                                else
                                                    let dispatchName = "__recgroup_dispatch_" + groupName
                                                    in
                                                        let dispatchLabel = "_recgroup_dispatch_" + groupName
                                                        in
                                                            Some(MutualTcoPlan(
                                                                groupName = groupName,
                                                                dispatchName = dispatchName,
                                                                dispatchLabel = dispatchLabel,
                                                                arity = firstArity,
                                                                members = members,
                                                                sharedParamNames = getMemberParamNames(first),
                                                                sharedParamTypes = getMemberParamTypes(first),
                                                                returnType = firstReturnType
                                                            ))

let buildMutualDispatchParamNames arity sharedNames = "__recgroup_tag" :: sharedNames

let formatMutualDispatchLabel groupName = "_recgroup_dispatch_" + groupName

let formatMutualWrapperLabel memberName = "_recgroup_wrapper_" + memberName
