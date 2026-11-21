// The first source application stage at which each inferred dictionary is used. A negative
// stage denotes a requirement that has no operation at this use site.
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import Ashes.Collection.Map.MapTree
export (
    value evidenceFlowForValue,
    value zipEvidenceStages,
    value functionArgumentCount,
)

let recursive functionArgumentCount semanticType =
    match semanticType with
        | SemFunction(_argument, result, _row) -> 1 + functionArgumentCount(result)
        | _ -> 0

let recursive zipEvidenceStages (requirements: List(TraitConstraint)) stages fallback =
    match requirements with
        | [] -> []
        | requirement :: rest ->
            match stages with
                | first :: tail -> (requirement, first) :: zipEvidenceStages(rest)(tail)(fallback)
                | [] -> (requirement, fallback) :: zipEvidenceStages(rest)([])(fallback)

type DemandSites =
    | requirements: MapTree(Int, List((TraitConstraint, Int)))
    | flows: MapTree(Int, EvidenceCallFlow)

type EvidenceWalk =
    | requirements: List((TraitConstraint, Int))
    | uses: List(EvidenceParameterUse)

let emptyWalk = EvidenceWalk(requirements = [], uses = [])

// Negative application count means an unknown consumer can invoke any argument stage.
let moreArguments count extra =
    if count < 0 || extra < 0
    then -1
    else count + extra

let recursive expressionId value =
    match value with
        | ExprAt(_span, inner) -> expressionId(inner)
        | ExprNode(id, _inner) -> id
        | _ -> -1

let recursive bare value =
    match value with
        | ExprAt(_span, inner) -> bare(inner)
        | ExprNode(_node, inner) -> bare(inner)
        | other -> other

let recursive calleeAndOrdinal value ordinal =
    match bare(value) with
        | ExprCall(callee, _arg, _space, _layout) -> calleeAndOrdinal(callee)(ordinal + 1)
        | _ -> (value, ordinal)

let siteFlow (sites: DemandSites) value =
    match Ashes.Collection.Map.getInt(expressionId(value))(sites.flows) with
        | Some(flow) -> flow
        | None -> UnknownEvidenceFlow

let recursive containsUse use existing =
    match existing with
        | [] -> false
        | candidate :: rest -> candidate == use || containsUse(use)(rest)

let addUse (use: EvidenceParameterUse) (found: EvidenceWalk) =
    if containsUse(use)(found.uses)
    then found
    else found with uses = use :: found.uses

let recursive projectCapturedUses uses applied depth escapes (found: EvidenceWalk) =
    match uses with
        | [] -> found
        | (EvidenceParameterUse { applied = passed, stage = stage, returned = returned } as use) :: rest ->
            if applied >= 0 && applied < stage && escapes == false
            then projectCapturedUses(rest)(applied)(depth)(escapes)(found)
            else
                let delayed =
                    if applied >= 0 && stage > applied
                    then stage - applied
                    else 0
                in
                    let extra =
                        if returned && (applied < 0 || applied > stage)
                        then
                            if applied < 0
                            then -1
                            else applied - stage
                        else 0
                    in
                        let updated = use with applied = moreArguments(passed)(extra), stage = depth + delayed, returned = returned && escapes
                        in
                            found
                            |> addUse(updated)
                            |> projectCapturedUses(rest)(applied)(depth)(escapes)

let addFormalUse flow applied depth escapes (found: EvidenceWalk) =
    match flow with
        | Some(SourceEvidenceFlow(_parameters, captures)) -> projectCapturedUses(captures)(applied)(depth)(escapes)(found)
        | Some(FormalEvidenceParameter(id)) ->
            if applied != 0 || escapes
            then addUse(EvidenceParameterUse(parameter = id, applied = applied, stage = depth, returned = escapes))(found)
            else found
        | _ -> found

let recursive projectSites sites applied depth escapes (found: EvidenceWalk) =
    match sites with
        | [] -> found
        | (requirement, stage) :: rest ->
            if stage < 0 || applied >= 0 && stage > applied && escapes == false
            then projectSites(rest)(applied)(depth)(escapes)(found)
            else
                let needed =
                    if applied >= 0 && stage > applied
                    then depth + stage - applied
                    else depth
                in projectSites(rest)(applied)(depth)(escapes)((found with requirements = (requirement, needed) :: found.requirements))

// A callee consumes an argument stage. Evaluating the argument, condition or discarded
// binding only demands its immediate operations. A branch can return a still-latent value.
let recursive sourceParameterIds value ordinal found =
    match bare(value) with
        | ExprLambda(_parameter, body, _annotation) -> sourceParameterIds(body)(ordinal + 1)((expressionId(value), ordinal) :: found)
        | ExprLet(_name, _rhs, body, _params, _anno, _reqs) -> sourceParameterIds(body)(ordinal)(found)
        | ExprLetRecursive(_name, _rhs, body, _params, _anno, _reqs) -> sourceParameterIds(body)(ordinal)(found)
        | ExprIf(_condition, yes, no) ->
            found
            |> sourceParameterIds(no)(ordinal)
            |> sourceParameterIds(yes)(ordinal)
        | ExprMatch(_value, cases, _pos) -> caseParameterIds(cases)(ordinal)(found)
        | _ -> found
and caseParameterIds cases ordinal found =
    match cases with
        | [] -> found
        | (_pattern, body, _guard) :: rest ->
            found
            |> sourceParameterIds(body)(ordinal)
            |> caseParameterIds(rest)(ordinal)

let recursive sourceOrdinal parameter ids =
    match ids with
        | [] -> None
        | (id, ordinal) :: rest ->
            if parameter == id
            then Some(ordinal)
            else sourceOrdinal(parameter)(rest)

let recursive publishUses ids uses =
    match uses with
        | [] -> []
        | (EvidenceParameterUse { parameter = parameter } as use) :: rest ->
            let tail = publishUses(ids)(rest)
            in
                match sourceOrdinal(parameter)(ids) with
                    | Some(ordinal) -> (use with parameter = ordinal) :: tail
                    | None -> tail

let recursive walk (expression: Expr) (applied: Int) (depth: Int) (escapes: Bool) (sites: DemandSites) (found: EvidenceWalk) =
    match expression with
        | ExprAt(_span, inner) -> walk(inner)(applied)(depth)(escapes)(sites)(found)
        | ExprNode(id, inner) ->
            let withDirect =
                match Ashes.Collection.Map.getInt(id)(sites.requirements) with
                    | Some(demands) -> projectSites(demands)(applied)(depth)(escapes)(found)
                    | None -> found
            in
                withDirect
                |> addFormalUse(Ashes.Collection.Map.getInt(id)(sites.flows))(applied)(depth)(escapes)
                |> walk(inner)(applied)(depth)(escapes)(sites)
        | ExprLambda(_name, body, _annotation) ->
            if applied != 0
            then
                let remaining =
                    if applied < 0
                    then -1
                    else applied - 1
                in walk(body)(remaining)(depth)(escapes)(sites)(found)
            else
                if escapes
                then walk(body)(0)(depth + 1)(true)(sites)(found)
                else found
        | ExprCall(callee, arg, _whitespace, _layout) ->
            found
            |> walkCallArgument(callee)(arg)(applied)(depth)(escapes)(sites)
            |> walk(callee)(moreArguments(applied)(1))(depth)(escapes)(sites)
        | ExprLet(_name, value, body, _parameters, _annotation, _requirements) ->
            found
            |> walk(value)(0)(depth)(false)(sites)
            |> walk(body)(applied)(depth)(escapes)(sites)
        | ExprLetRecursive(_name, value, body, _parameters, _annotation, _requirements) ->
            found
            |> walk(value)(0)(depth)(false)(sites)
            |> walk(body)(applied)(depth)(escapes)(sites)
        | ExprLetResult(_name, value, body) ->
            found
            |> walk(value)(0)(depth)(false)(sites)
            |> walk(body)(applied)(depth)(escapes)(sites)
        | ExprIf(condition, yes, no) ->
            found
            |> walk(condition)(0)(depth)(false)(sites)
            |> walk(yes)(applied)(depth)(escapes)(sites)
            |> walk(no)(applied)(depth)(escapes)(sites)
        | ExprMatch(value, arms, _position) ->
            found
            |> walk(value)(0)(depth)(false)(sites)
            |> walkArms(arms)(applied)(depth)(escapes)(sites)
        | ExprHandle(value, arms) ->
            found
            |> walk(value)(applied)(depth)(escapes)(sites)
            |> walkHandlers(arms)(applied)(depth)(escapes)(sites)
        | ExprPerform(value) -> walk(value)(0)(depth)(false)(sites)(found)
        | ExprAwait(value) -> walk(value)(0)(depth)(false)(sites)(found)
        | ExprResultPipe(value, function) ->
            found
            |> walk(value)(0)(depth)(false)(sites)
            |> walk(function)(moreArguments(applied)(1))(depth)(escapes)(sites)
        | ExprResultMapErrorPipe(value, function) ->
            found
            |> walk(value)(0)(depth)(false)(sites)
            |> walk(function)(moreArguments(applied)(1))(depth)(escapes)(sites)
        | ExprTuple(values) -> walkValues(values)(depth)(sites)(found)
        | ExprList(values, _layout) -> walkValues(values)(depth)(sites)(found)
        | ExprRecord(_name, fields, _layout) -> walkFields(fields)(depth)(sites)(found)
        | ExprRecordUpdate(value, fields) ->
            found
            |> walk(value)(0)(depth)(false)(sites)
            |> walkFields(fields)(depth)(sites)
        | ExprAdd(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprSubtract(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprMultiply(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprDivide(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprModulo(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprBitwiseAnd(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprBitwiseOr(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprBitwiseXor(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprShiftLeft(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprShiftRight(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprBitwiseNot(operand) -> walk(operand)(0)(depth)(false)(sites)(found)
        | ExprLogicalNot(operand) -> walk(operand)(0)(depth)(false)(sites)(found)
        | ExprLogicalAnd(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprLogicalOr(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprGreaterThan(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprLessThan(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprGreaterOrEqual(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprLessOrEqual(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprEqual(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprNotEqual(left, right) -> walkValues([left, right])(depth)(sites)(found)
        | ExprCons(head, tail) -> walkValues([head, tail])(depth)(sites)(found)
        | _ -> found
and walkCallArgument callee arg applied depth escapes sites found =
    match calleeAndOrdinal(callee)(0) with
        | (root, ordinal) ->
            let total = moreArguments(applied)(ordinal + 1)
            in
                match siteFlow(sites)(root) with
                    | SourceEvidenceFlow(uses, _captures) ->
                        found
                        |> walk(arg)(0)(depth)(false)(sites)
                        |> walkScheduledUses(uses)(ordinal)(arg)(total)(depth)(escapes)(sites)
                    | _ ->
                        match bare(root) with
                            | ExprLambda(_parameter, _body, _annotation) ->
                                let inline = walk(root)(0)(0)(true)(sites)(emptyWalk)
                                in
                                    found
                                    |> walk(arg)(0)(depth)(false)(sites)
                                    |> walkScheduledUses(publishUses(sourceParameterIds(root)(0)([]))(inline.uses))(ordinal)(arg)(total)(depth)(escapes)(sites)
                            | _ -> walk(arg)(-1)(depth)(false)(sites)(found)
and walkScheduledUses uses ordinal arg total depth escapes sites found =
    match uses with
        | [] -> found
        | EvidenceParameterUse { parameter = parameter, applied = passed, stage = stage, returned = returned } :: rest ->
            if parameter != ordinal || total >= 0 && total < stage && escapes == false
            then walkScheduledUses(rest)(ordinal)(arg)(total)(depth)(escapes)(sites)(found)
            else
                let delayed =
                    if total >= 0 && stage > total
                    then stage - total
                    else 0
                in
                    let extra =
                        if returned && (total < 0 || total > stage)
                        then
                            if total < 0
                            then -1
                            else total - stage
                        else 0
                    in
                        found
                        |> walk(arg)(moreArguments(passed)(extra))(depth + delayed)(returned && escapes)(sites)
                        |> walkScheduledUses(rest)(ordinal)(arg)(total)(depth)(escapes)(sites)
and walkValues values depth sites found =
    match values with
        | [] -> found
        | head :: rest ->
            found
            |> walk(head)(0)(depth)(false)(sites)
            |> walkValues(rest)(depth)(sites)
and walkFields fields depth sites found =
    match fields with
        | [] -> found
        | (_name, head) :: rest ->
            found
            |> walk(head)(0)(depth)(false)(sites)
            |> walkFields(rest)(depth)(sites)
and walkArms arms applied depth escapes sites found =
    match arms with
        | [] -> found
        | (_pattern, body, guard) :: rest ->
            let guarded =
                match guard with
                    | Some(value) -> walk(value)(0)(depth)(false)(sites)(found)
                    | None -> found
            in
                guarded
                |> walk(body)(applied)(depth)(escapes)(sites)
                |> walkArms(rest)(applied)(depth)(escapes)(sites)
and walkHandlers arms applied depth escapes sites found =
    match arms with
        | [] -> found
        | (_capability, _name, _patterns, body) :: rest ->
            found
            |> walk(body)(applied)(depth)(escapes)(sites)
            |> walkHandlers(rest)(applied)(depth)(escapes)(sites)

let recursive indexSites root sites indexed =
    match sites with
        | [] -> indexed
        | (node, demands) :: rest ->
            if node < root
            then indexed
            else
                match Ashes.Collection.Map.getInt(node)(indexed) with
                    | Some(_younger) -> indexSites(root)(rest)(indexed)
                    | None ->
                        indexed
                        |> Ashes.Collection.Map.setInt(node)(demands)
                        |> indexSites(root)(rest)

let recursive captureUses ids uses =
    match uses with
        | [] -> []
        | (EvidenceParameterUse { parameter = parameter } as use) :: rest ->
            match sourceOrdinal(parameter)(ids) with
                | Some(_local) -> captureUses(ids)(rest)
                | None -> use :: captureUses(ids)(rest)

let recursive residualUses consumed uses =
    match uses with
        | [] -> []
        | (EvidenceParameterUse { parameter = parameter, stage = stage } as use) :: rest ->
            if parameter < consumed
            then residualUses(consumed)(rest)
            else
                let remaining =
                    if stage > consumed
                    then stage - consumed
                    else 0
                in (use with parameter = parameter - consumed, stage = remaining) :: residualUses(consumed)(rest)

// A direct alias keeps both ignored and invoked callback positions. A partial alias removes
// already supplied source slots; its remaining demands start from the residual arity.
let recursive inheritedFlow value sites =
    match bare(value) with
        | ExprVar(_name) -> siteFlow(sites)(value)
        | ExprQualifiedVar(_owner, _name) -> siteFlow(sites)(value)
        | ExprCall(callee, _arg, _space, _layout) ->
            match calleeAndOrdinal(callee)(1) with
                | (head, consumed) ->
                    match siteFlow(sites)(head) with
                        | SourceEvidenceFlow(uses, _captured) ->
                            SourceEvidenceFlow(residualUses(consumed)(uses))([])
                        | _ -> UnknownEvidenceFlow
        | ExprLet(_name, _rhs, body, _params, _anno, _reqs) -> inheritedFlow(body)(sites)
        | _ -> UnknownEvidenceFlow

let evidenceFlowForValue root value requirements flows =
    (let sites = DemandSites(requirements = indexSites(root)(requirements)(Ashes.Collection.Map.empty), flows = indexSites(root)(flows)(Ashes.Collection.Map.empty))
    in
        let found = walk(value)(0)(0)(true)(sites)(emptyWalk)
        in
            let ids = sourceParameterIds(value)(0)([])
            in
                let captures = captureUses(ids)(found.uses)
                in
                    let flow =
                        match ids with
                            | [] ->
                                match inheritedFlow(value)(sites) with
                                    | SourceEvidenceFlow(uses, _captured) -> SourceEvidenceFlow(uses)(captures)
                                    | other -> other
                            | _ ->
                                // An opaque higher-order callee may invoke its callback during
                                // this application. A source-proven ignored argument has no use.
                                SourceEvidenceFlow(publishUses(ids)(found.uses))(captures)
                    in (found.requirements, flow))
