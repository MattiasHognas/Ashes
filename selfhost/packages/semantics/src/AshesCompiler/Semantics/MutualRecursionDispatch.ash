// Merges a mutually recursive top-level group into one self-recursive dispatch function, so the
// members' tail calls to one another become back edges of a single loop and stop growing the
// native stack. The dispatch function takes the called member's tag and one parameter slot per
// distinct parameter of the group; each member becomes a wrapper that calls it.
//
// A group is merged when every member is a function of at least one parameter with no declared
// type or trait requirement, every parameter is annotated, every mention of a member inside a
// member body is a saturated call in tail position, and those calls connect all the members.
// Parameters at the same position share a slot when their annotations agree; a slot some member
// lacks needs an annotation whose type has a default value, which a call to a member that does
// not own the slot passes.
//
// The merge is syntactic, so it does not see types. Members joined by tail calls have one result
// type, and annotated parameters give every slot its type, so the merged program type-checks
// whenever the group did; should it not, the caller lowers the program as written instead.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.ExprMentions.exprMentionsName
import AshesCompiler.Semantics.ModuleReferenceRewriting.patternNames
export (
    value mergeMutualRecursionGroups,
)

type DispatchMember =
    | name: Str
    | parameters: List((Str, Maybe(TypeExpr)))
    | body: Expr

// A dispatch parameter: its annotation, the value a call passes when the called member has no
// parameter in it (none for a slot every member owns), and the members' parameters it carries
// as (member index, parameter position).
type DispatchSlot =
    | annotation: Maybe(TypeExpr)
    | defaultValue: Maybe(Expr)
    | owners: List((Int, Int))

let dispatchTagName = "__recgroup_tag"

let dispatchArgName (slot: Int) = "__recgroup_arg" + Ashes.Text.fromInt(slot)

let wrapperArgName (index: Int) = "__recgroup_w" + Ashes.Text.fromInt(index)

let recursive containsName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest -> candidate == name || containsName(name)(rest)

let recursive mentionsAny (names: List(Str)) (expression: Expr) =
    match names with
        | [] -> false
        | name :: rest -> exprMentionsName(name)(expression) || mentionsAny(rest)(expression)

let recursive anyMentionsAny (names: List(Str)) (expressions: List(Expr)) =
    match expressions with
        | [] -> false
        | expression :: rest -> mentionsAny(names)(expression) || anyMentionsAny(names)(rest)

let recursive anyNameIn (candidates: List(Str)) (names: List(Str)) =
    match candidates with
        | [] -> false
        | candidate :: rest -> containsName(candidate)(names) || anyNameIn(rest)(names)

let recursive lambdaOf (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> lambdaOf(inner)
        | ExprNode(_node, inner) -> lambdaOf(inner)
        | ExprLambda(parameter, body, annotation) -> Some((parameter, body, annotation))
        | _ -> None

// The parameters of a curried lambda chain, outermost first, and the body under the last one.
let recursive lambdaChainOf (value: Expr) (reversed: List((Str, Maybe(TypeExpr)))) =
    match lambdaOf(value) with
        | Some((parameter, body, annotation)) -> lambdaChainOf(body)((parameter, annotation) :: reversed)
        | None -> (reverse(reversed), value)

// An annotation without its source positions, so two annotations of one type compare equal.
let recursive plainType (typeExpr: TypeExpr) =
    match typeExpr with
        | TypeAt(_span, inner) -> plainType(inner)
        | TypeApplied(name, arguments) ->
            arguments
            |> plainTypes
            |> TypeApplied(name)
        | TypeArrow(parameter, result, row, tail) ->
            TypeArrow(plainType(parameter))(plainType(result))(row)(tail)
        | TypeTuple(elements) ->
            elements
            |> plainTypes
            |> TypeTuple
        | other -> other
and plainTypes (typeExprs: List(TypeExpr)) =
    match typeExprs with
        | [] -> []
        | typeExpr :: rest -> plainType(typeExpr) :: plainTypes(rest)

let plainAnnotation (annotation: Maybe(TypeExpr)) =
    match annotation with
        | Some(typeExpr) ->
            typeExpr
            |> plainType
            |> Some
        | None -> None

let recursive dispatchMembersOf (bindings: List(LetBindingSyntax)) (reversed: List(DispatchMember)) =
    match bindings with
        | [] ->
            reversed
            |> reverse
            |> Some
        | LetBindingSyntax { name = name, value = value, typeAnnotation = None, requirements = [] } :: rest ->
            match lambdaChainOf(value)([]) with
                | ([], _body) -> None
                | (parameters, body) -> dispatchMembersOf(rest)(DispatchMember(name = name, parameters = parameters, body = body) :: reversed)
        | _ -> None

let recursive memberNamesOf (members: List(DispatchMember)) =
    match members with
        | [] -> []
        | member :: rest -> member.name :: memberNamesOf(rest)

// The index and parameter count of the member called `name`.
let recursive memberNamed (name: Str) (members: List(DispatchMember)) (index: Int) =
    match members with
        | [] -> None
        | member :: rest ->
            if member.name == name
            then Some((index, length(member.parameters)))
            else memberNamed(name)(rest)(index + 1)

// The name a call spine is rooted at and its arguments in application order.
let recursive callSpineOf (expression: Expr) (arguments: List(Expr)) =
    match expression with
        | ExprAt(_span, inner) -> callSpineOf(inner)(arguments)
        | ExprNode(_node, inner) -> callSpineOf(inner)(arguments)
        | ExprCall(function, argument, _sugar, _layout) -> callSpineOf(function)(argument :: arguments)
        | ExprVar(name) -> Some((name, arguments))
        | _ -> None

// The members an expression tail-calls, added to `found`, when every mention of a member in it is
// a saturated call in tail position whose arguments mention none; `None` when a member is
// mentioned anywhere else, or a binder of the expression takes a member's name.
let recursive tailCalleesOf (members: List(DispatchMember)) (names: List(Str)) (expression: Expr) (found: List(Str)) =
    match expression with
        | ExprAt(_span, inner) -> tailCalleesOf(members)(names)(inner)(found)
        | ExprNode(_node, inner) -> tailCalleesOf(members)(names)(inner)(found)
        | ExprIf(condition, thenBranch, elseBranch) ->
            if mentionsAny(names)(condition)
            then None
            else
                match tailCalleesOf(members)(names)(thenBranch)(found) with
                    | Some(afterThen) -> tailCalleesOf(members)(names)(elseBranch)(afterThen)
                    | None -> None
        | ExprMatch(scrutinee, cases, _position) ->
            if mentionsAny(names)(scrutinee)
            then None
            else tailCalleesOfCases(members)(names)(cases)(found)
        | ExprLet(bound, value, body, _parameters, _annotation, _requirements) -> tailCalleesUnderBinding(members)(names)(bound)(value)(body)(found)
        | ExprLetResult(bound, value, body) -> tailCalleesUnderBinding(members)(names)(bound)(value)(body)(found)
        | ExprLetRecursive(bound, value, body, _parameters, _annotation, _requirements) -> tailCalleesUnderBinding(members)(names)(bound)(value)(body)(found)
        | other ->
            match callSpineOf(other)([]) with
                | Some((callee, arguments)) ->
                    match memberNamed(callee)(members)(0) with
                        | Some((_index, arity)) ->
                            if arity == length(arguments) && anyMentionsAny(names)(arguments) == false
                            then Some(callee :: found)
                            else None
                        | None ->
                            if mentionsAny(names)(other)
                            then None
                            else Some(found)
                | None ->
                    if mentionsAny(names)(other)
                    then None
                    else Some(found)
and tailCalleesUnderBinding (members: List(DispatchMember)) (names: List(Str)) (bound: Str) (value: Expr) (body: Expr) (found: List(Str)) =
    if containsName(bound)(names) || mentionsAny(names)(value)
    then None
    else tailCalleesOf(members)(names)(body)(found)
and tailCalleesOfCases (members: List(DispatchMember)) (names: List(Str)) (cases: List((Pattern, Expr, Maybe(Expr)))) (found: List(Str)) =
    match cases with
        | [] -> Some(found)
        | (pattern, body, guard) :: rest ->
            let guardMentions =
                match guard with
                    | Some(condition) -> mentionsAny(names)(condition)
                    | None -> false
            in
                if guardMentions || anyNameIn(patternNames(pattern))(names)
                then None
                else
                    match tailCalleesOf(members)(names)(body)(found) with
                        | Some(afterArm) -> tailCalleesOfCases(members)(names)(rest)(afterArm)
                        | None -> None

let recursive parameterNamesOf (parameters: List((Str, Maybe(TypeExpr)))) =
    match parameters with
        | [] -> []
        | (name, _annotation) :: rest -> name :: parameterNamesOf(rest)

let recursive edgesFrom (caller: Str) (callees: List(Str)) (edges: List((Str, Str))) =
    match callees with
        | [] -> edges
        | callee :: rest -> edgesFrom(caller)(rest)((caller, callee) :: edges)

// The group's tail calls as (caller, callee) pairs, when every member body mentions the group
// only through saturated tail calls and no member parameter takes a member's name.
let recursive groupTailCalls (members: List(DispatchMember)) (names: List(Str)) (remaining: List(DispatchMember)) (edges: List((Str, Str))) =
    match remaining with
        | [] -> Some(edges)
        | member :: rest ->
            if anyNameIn(parameterNamesOf(member.parameters))(names)
            then None
            else
                match tailCalleesOf(members)(names)(member.body)([]) with
                    | Some(callees) ->
                        edges
                        |> edgesFrom(member.name)(callees)
                        |> groupTailCalls(members)(names)(rest)
                    | None -> None

// The members joined to those already reached by a tail call in either direction, to a fixed point.
let recursive reachedThrough (edges: List((Str, Str))) (reached: List(Str)) (remaining: List((Str, Str))) (grew: Bool) =
    match remaining with
        | [] ->
            if grew
            then reachedThrough(edges)(reached)(edges)(false)
            else reached
        | (caller, callee) :: rest ->
            match (containsName(caller)(reached), containsName(callee)(reached)) with
                | (true, false) -> reachedThrough(edges)(callee :: reached)(rest)(true)
                | (false, true) -> reachedThrough(edges)(caller :: reached)(rest)(true)
                | _ -> reachedThrough(edges)(reached)(rest)(grew)

// Whether tail calls connect every member of the group, which gives the members one result type.
let groupJoinedByTailCalls (members: List(DispatchMember)) (names: List(Str)) =
    match (groupTailCalls(members)(names)(members)([]), names) with
        | (Some(edges), first :: _rest) ->
            length(reachedThrough(edges)([first])(edges)(false)) == length(names)
        | _ -> false

// The value a call passes in a slot the called member does not own: the zero of a scalar type,
// the empty string, or the empty list. Any other type has none.
let defaultValueOf (typeExpr: TypeExpr) =
    match typeExpr with
        | TypeNamed("Int") -> Some(ExprInt(0))
        | TypeNamed("Bool") -> Some(ExprBool(false))
        | TypeNamed("Str") -> Some(ExprString(""))
        | TypeApplied("List", _element :: []) ->
            false
            |> ExprList([])
            |> Some
        | _ -> None

// The parameters the members have at `position`, as (member index, annotation).
let recursive parametersAt (position: Int) (members: List(DispatchMember)) (index: Int) =
    match members with
        | [] -> []
        | member :: rest ->
            match nthParameter(position)(member.parameters) with
                | Some((_name, annotation)) -> (index, plainAnnotation(annotation)) :: parametersAt(position)(rest)(index + 1)
                | None -> parametersAt(position)(rest)(index + 1)
and nthParameter (position: Int) (parameters: List((Str, Maybe(TypeExpr)))) =
    match parameters with
        | [] -> None
        | parameter :: rest ->
            if position == 0
            then Some(parameter)
            else nthParameter(position - 1)(rest)

let recursive allAnnotated (entries: List((Int, Maybe(TypeExpr)))) =
    match entries with
        | [] -> true
        | (_index, Some(_annotation)) :: rest -> allAnnotated(rest)
        | _ -> false

let recursive ownersAt (position: Int) (entries: List((Int, Maybe(TypeExpr)))) =
    match entries with
        | [] -> []
        | (index, _annotation) :: rest -> (index, position) :: ownersAt(position)(rest)

// The entries annotated with `annotation`, and the rest.
let recursive splitByAnnotation (annotation: Maybe(TypeExpr)) (entries: List((Int, Maybe(TypeExpr)))) (matching: List((Int, Maybe(TypeExpr)))) (others: List((Int, Maybe(TypeExpr)))) =
    match entries with
        | [] -> (reverse(matching), reverse(others))
        | (index, candidate) :: rest ->
            if candidate == annotation
            then splitByAnnotation(annotation)(rest)((index, candidate) :: matching)(others)
            else splitByAnnotation(annotation)(rest)(matching)((index, candidate) :: others)

// One slot per distinct annotation among the entries of a position, in order of first
// appearance. A slot not every member owns needs a default value.
let recursive annotatedSlotsAt (position: Int) (memberCount: Int) (entries: List((Int, Maybe(TypeExpr)))) (reversed: List(DispatchSlot)) =
    match entries with
        | [] ->
            reversed
            |> reverse
            |> Some
        | (_index, annotation) :: _rest ->
            match splitByAnnotation(annotation)(entries)([])([]) with
                | (matching, others) ->
                    if length(matching) == memberCount
                    then annotatedSlotsAt(position)(memberCount)(others)(DispatchSlot(annotation = annotation, defaultValue = None, owners = ownersAt(position)(matching)) :: reversed)
                    else
                        match annotation with
                            | Some(typeExpr) ->
                                match defaultValueOf(typeExpr) with
                                    | Some(value) -> annotatedSlotsAt(position)(memberCount)(others)(DispatchSlot(annotation = annotation, defaultValue = Some(value), owners = ownersAt(position)(matching)) :: reversed)
                                    | None -> None
                            | None -> None

let slotsAt (position: Int) (members: List(DispatchMember)) =
    (let entries = parametersAt(position)(members)(0)
    in
        if allAnnotated(entries)
        then
            annotatedSlotsAt(position)(length(members))(entries)([])
        else None)

let recursive maxArityOf (members: List(DispatchMember)) (largest: Int) =
    match members with
        | [] -> largest
        | member :: rest ->
            if length(member.parameters) > largest
            then
                member.parameters
                |> length
                |> maxArityOf(rest)
            else maxArityOf(rest)(largest)

// The dispatch slots, position by position.
let recursive slotLayoutOf (members: List(DispatchMember)) (position: Int) (positions: Int) (slots: List(DispatchSlot)) =
    if position >= positions
    then Some(slots)
    else
        match slotsAt(position)(members) with
            | Some(added) ->
                added
                |> append(slots)
                |> slotLayoutOf(members)(position + 1)(positions)
            | None -> None

let recursive ownedPosition (member: Int) (owners: List((Int, Int))) =
    match owners with
        | [] -> None
        | (owner, position) :: rest ->
            if owner == member
            then Some(position)
            else ownedPosition(member)(rest)

let recursive nthExpression (index: Int) (expressions: List(Expr)) =
    match expressions with
        | [] -> ExprInt(0)
        | expression :: rest ->
            if index == 0
            then expression
            else nthExpression(index - 1)(rest)

let applyTo (function: Expr) (argument: Expr) = ExprCall(function)(argument)(false)(callArgumentsInline)

let recursive applySlots (call: Expr) (member: Int) (arguments: List(Expr)) (slots: List(DispatchSlot)) =
    match slots with
        | [] -> call
        | DispatchSlot { defaultValue = defaultValue, owners = owners } :: rest ->
            let value =
                match (ownedPosition(member)(owners), defaultValue) with
                    | (Some(position), _) -> nthExpression(position)(arguments)
                    | (None, Some(defaulted)) -> defaulted
                    | (None, None) -> ExprInt(0)
            in
                applySlots(applyTo(call)(value))(member)(arguments)(rest)

// `dispatch(tag)(slot 0) ... (slot n)` for a call of the member `member` with `arguments`.
let dispatchCall (dispatchName: Str) (member: Int) (arguments: List(Expr)) (slots: List(DispatchSlot)) =
    applySlots(applyTo(ExprVar(dispatchName))(ExprInt(member)))(member)(arguments)(slots)

// The member body with its saturated tail calls into the group redirected to the dispatch.
let recursive redirectTailCalls (dispatchName: Str) (members: List(DispatchMember)) (slots: List(DispatchSlot)) (expression: Expr) =
    match expression with
        | ExprAt(span, inner) ->
            inner
            |> redirectTailCalls(dispatchName)(members)(slots)
            |> ExprAt(span)
        | ExprNode(node, inner) ->
            inner
            |> redirectTailCalls(dispatchName)(members)(slots)
            |> ExprNode(node)
        | ExprIf(condition, thenBranch, elseBranch) ->
            elseBranch
            |> redirectTailCalls(dispatchName)(members)(slots)
            |> ExprIf(condition)(redirectTailCalls(dispatchName)(members)(slots)(thenBranch))
        | ExprMatch(scrutinee, cases, position) ->
            ExprMatch(scrutinee)(redirectCases(dispatchName)(members)(slots)(cases))(position)
        | ExprLet(bound, value, body, parameters, annotation, requirements) ->
            ExprLet(bound)(value)(redirectTailCalls(dispatchName)(members)(slots)(body))(parameters)(annotation)(requirements)
        | ExprLetResult(bound, value, body) ->
            body
            |> redirectTailCalls(dispatchName)(members)(slots)
            |> ExprLetResult(bound)(value)
        | ExprLetRecursive(bound, value, body, parameters, annotation, requirements) ->
            ExprLetRecursive(bound)(value)(redirectTailCalls(dispatchName)(members)(slots)(body))(parameters)(annotation)(requirements)
        | other ->
            match callSpineOf(other)([]) with
                | Some((callee, arguments)) ->
                    match memberNamed(callee)(members)(0) with
                        | Some((index, arity)) ->
                            if arity == length(arguments)
                            then dispatchCall(dispatchName)(index)(arguments)(slots)
                            else other
                        | None -> other
                | None -> other
and redirectCases (dispatchName: Str) (members: List(DispatchMember)) (slots: List(DispatchSlot)) (cases: List((Pattern, Expr, Maybe(Expr)))) =
    match cases with
        | [] -> []
        | (pattern, body, guard) :: rest -> (pattern, redirectTailCalls(dispatchName)(members)(slots)(body), guard) :: redirectCases(dispatchName)(members)(slots)(rest)

// The slot index that carries parameter `position` of member `member`.
let recursive slotOfParameter (member: Int) (position: Int) (slots: List(DispatchSlot)) (index: Int) =
    match slots with
        | [] -> index
        | DispatchSlot { owners = owners } :: rest ->
            match ownedPosition(member)(owners) with
                | Some(owned) ->
                    if owned == position
                    then index
                    else slotOfParameter(member)(position)(rest)(index + 1)
                | None -> slotOfParameter(member)(position)(rest)(index + 1)

let recursive nthMember (index: Int) (members: List(DispatchMember)) =
    match members with
        | [] -> None
        | member :: rest ->
            if index == 0
            then Some(member)
            else nthMember(index - 1)(rest)

// The name the member `member` gives its parameter at `position`.
let parameterNameAt (member: Int) (position: Int) (members: List(DispatchMember)) =
    match nthMember(member)(members) with
        | Some(found) ->
            match nthParameter(position)(found.parameters) with
                | Some((name, _annotation)) -> Some(name)
                | None -> None
        | None -> None

let recursive ownersAllName (name: Str) (members: List(DispatchMember)) (owners: List((Int, Int))) =
    match owners with
        | [] -> true
        | (member, position) :: rest -> parameterNameAt(member)(position)(members) == Some(name) && ownersAllName(name)(members)(rest)

// Whether a member that has no parameter in the slot mentions `name`, which a slot of that name
// would capture.
let recursive strangerMentions (name: Str) (owners: List((Int, Int))) (members: List(DispatchMember)) (index: Int) =
    match members with
        | [] -> false
        | member :: rest -> ownedPosition(index)(owners) == None && exprMentionsName(name)(member.body) || strangerMentions(name)(owners)(rest)(index + 1)

// The name of each slot. A slot takes the name its owners all give their parameter, so the member
// bodies read the dispatch parameter itself, as they read their own parameter before the merge;
// a slot whose owners disagree, whose name an earlier slot took or another member's body
// mentions, or whose owners leave it unnamed, takes a generated name its owners bind theirs to.
let recursive slotNamesOf (members: List(DispatchMember)) (slots: List(DispatchSlot)) (index: Int) (taken: List(Str)) =
    match slots with
        | [] -> []
        | DispatchSlot { owners = owners } :: rest ->
            let agreed =
                match owners with
                    | (member, position) :: others ->
                        match parameterNameAt(member)(position)(members) with
                            | Some(name) ->
                                if ownersAllName(name)(members)(others) && Ashes.Text.startsWith(name)("_") == false && containsName(name)(taken) == false && strangerMentions(name)(owners)(members)(0) == false
                                then Some(name)
                                else None
                            | None -> None
                    | [] -> None
            in
                match agreed with
                    | Some(name) -> name :: slotNamesOf(members)(rest)(index + 1)(name :: taken)
                    | None -> dispatchArgName(index) :: slotNamesOf(members)(rest)(index + 1)(taken)

let recursive nthName (index: Int) (names: List(Str)) =
    match names with
        | [] -> dispatchArgName(index)
        | name :: rest ->
            if index == 0
            then name
            else nthName(index - 1)(rest)

// The member's parameters bound to its slots over `body`, the first parameter outermost; a
// parameter whose slot carries its own name needs no binding.
let recursive bindParameters (member: Int) (position: Int) (parameters: List((Str, Maybe(TypeExpr)))) (slots: List(DispatchSlot)) (slotNames: List(Str)) (body: Expr) =
    match parameters with
        | [] -> body
        | (name, _annotation) :: rest ->
            let slotName =
                nthName(slotOfParameter(member)(position)(slots)(0))(slotNames)
            in
                if slotName == name
                then bindParameters(member)(position + 1)(rest)(slots)(slotNames)(body)
                else
                    ExprLet(name)(ExprVar(slotName))(bindParameters(member)(position + 1)(rest)(slots)(slotNames)(body))([])(None)([])

// One arm per member, the last behind a wildcard so the match on the tag is exhaustive.
let recursive dispatchArms (dispatchName: Str) (members: List(DispatchMember)) (slots: List(DispatchSlot)) (slotNames: List(Str)) (remaining: List(DispatchMember)) (index: Int) =
    match remaining with
        | [] -> []
        | member :: rest ->
            let arm =
                member.body
                |> redirectTailCalls(dispatchName)(members)(slots)
                |> bindParameters(index)(0)(member.parameters)(slots)(slotNames)
            in
                let pattern =
                    match rest with
                        | [] -> PatternWildcard
                        | _ -> PatternInt(index)
                in (pattern, arm, None) :: dispatchArms(dispatchName)(members)(slots)(slotNames)(rest)(index + 1)

let recursive slotLambdas (slots: List(DispatchSlot)) (slotNames: List(Str)) (body: Expr) =
    match (slots, slotNames) with
        | (DispatchSlot { annotation = annotation } :: rest, name :: restNames) ->
            ExprLambda(name)(slotLambdas(rest)(restNames)(body))(annotation)
        | _ -> body

let dispatchLambda (dispatchName: Str) (members: List(DispatchMember)) (slots: List(DispatchSlot)) =
    (let slotNames = slotNamesOf(members)(slots)(0)([dispatchTagName, dispatchName])
    in
        ExprLambda(dispatchTagName)(None
        |> ExprMatch(ExprVar(dispatchTagName))(dispatchArms(dispatchName)(members)(slots)(slotNames)(members)(0))
        |> slotLambdas(slots)(slotNames))(Some(TypeNamed("Int"))))

let recursive wrapperArguments (count: Int) (index: Int) =
    if index >= count
    then []
    else
        ExprVar(wrapperArgName(index)) :: wrapperArguments(count)(index + 1)

let recursive wrapperLambdas (parameters: List((Str, Maybe(TypeExpr)))) (index: Int) (body: Expr) =
    match parameters with
        | [] -> body
        | (_name, annotation) :: rest ->
            ExprLambda(wrapperArgName(index))(wrapperLambdas(rest)(index + 1)(body))(annotation)

// The member as its callers see it: a function of the member's parameters that enters the
// dispatch with the member's tag.
let memberWrapper (dispatchName: Str) (member: DispatchMember) (index: Int) (slots: List(DispatchSlot)) =
    slots
    |> dispatchCall(dispatchName)(index)(wrapperArguments(length(member.parameters))(0))
    |> wrapperLambdas(member.parameters)(0)

let plainBinding (name: Str) (value: Expr) =
    LetBindingSyntax(
        name = name,
        value = value,
        sugarParameters = [],
        typeAnnotation = None,
        requirements = [],
        declarationStart = -1
    )

let recursive wrapperItems (dispatchName: Str) (members: List(DispatchMember)) (slots: List(DispatchSlot)) (index: Int) =
    match members with
        | [] -> []
        | member :: rest ->
            TopLevelLet(slots
            |> memberWrapper(dispatchName)(member)(index)
            |> plainBinding(member.name))(false) :: wrapperItems(dispatchName)(rest)(slots)(index + 1)

// The items a group merges into: the dispatch function, then each member's wrapper.
let mergedGroupItems (bindings: List(LetBindingSyntax)) =
    match bindings with
        | _first :: _second :: _rest ->
            match dispatchMembersOf(bindings)([]) with
                | Some(first :: _others as members) ->
                    let names = memberNamesOf(members)
                    in
                        if groupJoinedByTailCalls(members)(names)
                        then
                            match slotLayoutOf(members)(0)(maxArityOf(members)(0))([]) with
                                | Some(slots) ->
                                    let dispatchName = "__recgroup_dispatch_" + first.name
                                    in
                                        Some(TopLevelLet(slots
                                        |> dispatchLambda(dispatchName)(members)
                                        |> plainBinding(dispatchName))(true) :: wrapperItems(dispatchName)(members)(slots)(0))
                                | None -> None
                        else None
                | _ -> None
        | _ -> None

let recursive locateItems (span: TextSpan) (items: List(TopLevelItem)) =
    match items with
        | [] -> []
        | item :: rest -> TopLevelAt(span)(item) :: locateItems(span)(rest)

// The group an item declares, with the span the declaration carries.
let recursive recursiveGroupOf (item: TopLevelItem) (span: Maybe(TextSpan)) =
    match item with
        | TopLevelAt(at, inner) -> recursiveGroupOf(inner)(Some(at))
        | TopLevelRecursiveGroup(bindings) -> Some((bindings, span))
        | _ -> None

let recursive mergeGroupsIn (items: List(TopLevelItem)) (reversed: List(TopLevelItem)) (merged: Bool) =
    match items with
        | [] ->
            if merged
            then
                reversed
                |> reverse
                |> Some
            else None
        | item :: rest ->
            match recursiveGroupOf(item)(None) with
                | Some((bindings, span)) ->
                    match mergedGroupItems(bindings) with
                        | Some(replacement) ->
                            let located =
                                match span with
                                    | Some(at) -> locateItems(at)(replacement)
                                    | None -> replacement
                            in
                                mergeGroupsIn(rest)(prependReversed(located)(reversed))(true)
                        | None -> mergeGroupsIn(rest)(item :: reversed)(merged)
                | None -> mergeGroupsIn(rest)(item :: reversed)(merged)
and prependReversed (items: List(TopLevelItem)) (reversed: List(TopLevelItem)) =
    match items with
        | [] -> reversed
        | item :: rest -> prependReversed(rest)(item :: reversed)

// The program with every mergeable mutually recursive group merged, or `None` when it has none.
let mergeMutualRecursionGroups (program: ProgramSyntax) =
    match mergeGroupsIn(program.items)([])(false) with
        | Some(items) -> Some((program with items = items))
        | None -> None
