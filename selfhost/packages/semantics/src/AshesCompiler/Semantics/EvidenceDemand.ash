// The first source application stage at which each inferred dictionary is used. A negative
// stage denotes a requirement that has no operation at this use site.
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import Ashes.Collection.Map.MapTree
import Ashes.Collection.List.append
export (
    value indexEvidenceUse,
    value evidenceFlowForValue,
    value projectEvidenceField,
    value evidenceFlowOfPattern,
    value evidenceFlowForScrutinee,
    value renameEvidenceFlow,
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
    | indexedUses: MapTree(EvidenceParameterUse, Unit)

let emptyWalk = EvidenceWalk(requirements = [], uses = [], indexedUses = Ashes.Collection.Map.empty)

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

let recursive memberNamed name fields =
    match fields with
        | [] -> UnknownEvidenceFlow
        | (candidate, flow) :: rest ->
            if candidate == name
            then flow
            else memberNamed(name)(rest)

let recursive projectEvidenceField field flow =
    match flow with
        | FormalEvidenceParameter(id, path) ->
            [field]
            |> append(path)
            |> FormalEvidenceParameter(id)
        | AggregateEvidenceFlow(fields) -> memberNamed(field)(fields)
        | AlternativeEvidenceFlow(flows) ->
            flows
            |> Ashes.Collection.List.map(projectEvidenceField(field))
            |> AlternativeEvidenceFlow
        | MemberEvidenceFlow(_demands, inner) -> projectEvidenceField(field)(inner)
        | _ -> UnknownEvidenceFlow

let recursive callableFlow flow =
    match flow with
        | MemberEvidenceFlow(_demands, inner) -> callableFlow(inner)
        | other -> other

let recursive renameEvidenceFlow arguments flow =
    match flow with
        | AggregateEvidenceFlow(fields) ->
            fields
            |> renameMembers(arguments)
            |> AggregateEvidenceFlow
        | AlternativeEvidenceFlow(flows) ->
            flows
            |> Ashes.Collection.List.map(renameEvidenceFlow(arguments))
            |> AlternativeEvidenceFlow
        | MemberEvidenceFlow(demands, inner) ->
            inner
            |> renameEvidenceFlow(arguments)
            |> MemberEvidenceFlow(renameDemands(arguments)(demands))
        | other -> other
and renameMembers arguments fields =
    match fields with
        | [] -> []
        | (name, flow) :: rest -> (name, renameEvidenceFlow(arguments)(flow)) :: renameMembers(arguments)(rest)
and renameDemands arguments demands =
    match demands with
        | [] -> []
        | (TraitConstraint { traitName = name, typeArguments = types }, stage) :: rest ->
            (TraitConstraint(traitName = name, typeArguments = Ashes.Collection.List.map(applySubstitution(arguments))(types)), stage) :: renameDemands(arguments)(rest)

let recursive evidenceFlowOfPattern pattern flow bound =
    match pattern with
        | PatternAt(_span, inner) -> evidenceFlowOfPattern(inner)(flow)(bound)
        | PatternVar(name) -> (name, flow) :: bound
        | PatternAs(inner, name) -> evidenceFlowOfPattern(inner)(flow)((name, flow) :: bound)
        | PatternTuple(patterns) -> numberedPatternFlows(patterns)(0)(flow)(bound)
        | PatternConstructor(_name, patterns) -> numberedPatternFlows(patterns)(0)(flow)(bound)
        | PatternRecord(_name, fields) -> namedPatternFlows(fields)(flow)(bound)
        | PatternCons(head, tail) -> numberedPatternFlows([head, tail])(0)(flow)(bound)
        | PatternOr(alternatives) -> alternativePatternFlows(alternatives)(flow)(bound)
        | _ -> bound
and numberedPatternFlows patterns index flow bound =
    match patterns with
        | [] -> bound
        | pattern :: rest ->
            bound
            |> evidenceFlowOfPattern(pattern)(projectEvidenceField(Ashes.Text.fromInt(index))(flow))
            |> numberedPatternFlows(rest)(index + 1)(flow)
and namedPatternFlows fields flow bound =
    match fields with
        | [] -> bound
        | (name, pattern) :: rest ->
            bound
            |> evidenceFlowOfPattern(pattern)(projectEvidenceField(name)(flow))
            |> namedPatternFlows(rest)(flow)
and alternativePatternFlows alternatives flow bound =
    match alternatives with
        | [] -> bound
        | first :: rest ->
            let firstBindings = evidenceFlowOfPattern(first)(flow)([])
            in
                let others =
                    Ashes.Collection.List.map(given (arm) -> evidenceFlowOfPattern(arm)(flow)([]))(rest)
                in
                    Ashes.Collection.List.foldLeft(given (acc) ->
                        given (entry) ->
                            match entry with
                                | (name, firstFlow) ->
                                    (name, AlternativeEvidenceFlow(firstFlow :: Ashes.Collection.List.map(memberNamed(name))(others))) :: acc)(bound)(firstBindings)

// Index membership separately from the encounter-ordered use list. A different application
// count, demand stage, return flag or projection is a different observation of the same formal.
let recursive compareUsePaths (left: List(Str)) (right: List(Str)) =
    match (left, right) with
        | ([], []) -> 0
        | ([], _head :: _rest) -> -1
        | (_head :: _rest, []) -> 1
        | (first :: rest, other :: tail) ->
            let order = Ashes.Text.compare(first)(other)
            in
                if order == 0
                then compareUsePaths(rest)(tail)
                else order

let compareEvidenceUses (left: EvidenceParameterUse) (right: EvidenceParameterUse) =
    if left.parameter != right.parameter
    then
        if left.parameter < right.parameter
        then -1
        else 1
    else
        if left.applied != right.applied
        then
            if left.applied < right.applied
            then -1
            else 1
        else
            if left.stage != right.stage
            then
                if left.stage < right.stage
                then -1
                else 1
            else
                if left.returned != right.returned
                then
                    if left.returned
                    then 1
                    else -1
                else compareUsePaths(left.path)(right.path)

let indexEvidenceUse (use: EvidenceParameterUse) (indexed: MapTree(EvidenceParameterUse, Unit)) =
    if Ashes.Collection.Map.containsWith(compareEvidenceUses)(use)(indexed)
    then None
    else
        indexed
        |> Ashes.Collection.Map.setWith(compareEvidenceUses)(use)(Unit)
        |> Some

let addUse (use: EvidenceParameterUse) (found: EvidenceWalk) =
    match indexEvidenceUse(use)(found.indexedUses) with
        | None -> found
        | Some(indexed) -> found with uses = use :: found.uses, indexedUses = indexed

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
        | Some(FormalEvidenceParameter(id, path)) ->
            if applied != 0 || escapes
            then addUse(EvidenceParameterUse(parameter = id, path = path, applied = applied, stage = depth, returned = escapes))(found)
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
let recursive useFlow flow applied depth escapes found =
    match flow with
        | MemberEvidenceFlow(demands, inner) ->
            found
            |> projectSites(demands)(applied)(depth)(escapes)
            |> useFlow(inner)(applied)(depth)(escapes)
        | AlternativeEvidenceFlow(flows) ->
            Ashes.Collection.List.foldLeft(given (acc) ->
                given (item) -> useFlow(item)(applied)(depth)(escapes)(acc))(found)(flows)
        | other -> addFormalUse(Some(other))(applied)(depth)(escapes)(found)

let recursive sourceArguments expression arguments =
    match bare(expression) with
        | ExprCall(callee, argument, _space, _layout) -> sourceArguments(callee)(argument :: arguments)
        | _ -> (expression, arguments)

let recursive fieldsByIndex values index =
    match values with
        | [] -> []
        | value :: rest -> (Ashes.Text.fromInt(index), value) :: fieldsByIndex(rest)(index + 1)

let recursive expressionField key fields =
    match fields with
        | [] -> None
        | (name, value) :: rest ->
            if key == name
            then Some(value)
            else expressionField(key)(rest)

let aggregateFields value sites =
    match bare(value) with
        | ExprTuple(values) ->
            0
            |> fieldsByIndex(values)
            |> Some
        | ExprRecord(_name, fields, _multiline) -> Some(fields)
        | ExprCons(head, tail) -> Some([("0", head), ("1", tail)])
        | ExprList(first :: rest, layout) -> Some([("0", first), ("1", ExprList(rest)(layout))])
        | ExprCall(_callee, _arg, _space, _layout) ->
            match sourceArguments(value)([]) with
                | (root, arguments) ->
                    match root
                    |> siteFlow(sites)
                    |> callableFlow with
                        | ConstructorEvidenceFlow(arity) ->
                            if Ashes.Collection.List.length(arguments) == arity
                            then
                                0
                                |> fieldsByIndex(arguments)
                                |> Some
                            else None
                        | _ -> None
        | _ -> None

let recursive useNestedFlow flow applied depth escapes found =
    match flow with
        | AggregateEvidenceFlow(fields) ->
            Ashes.Collection.List.foldLeft(given (acc) ->
                given (entry) ->
                    match entry with
                        | (_name, child) -> useNestedFlow(child)(applied)(depth)(escapes)(acc))(found)(fields)
        | MemberEvidenceFlow(demands, inner) ->
            found
            |> projectSites(demands)(applied)(depth)(escapes)
            |> useNestedFlow(inner)(applied)(depth)(escapes)
        | AlternativeEvidenceFlow(flows) ->
            Ashes.Collection.List.foldLeft(given (acc) ->
                given (child) -> useNestedFlow(child)(applied)(depth)(escapes)(acc))(found)(flows)
        | FormalEvidenceParameter(id, path) ->
            addFormalUse(["*"]
            |> append(path)
            |> FormalEvidenceParameter(id)
            |> Some)(applied)(depth)(escapes)(found)
        | other -> useFlow(other)(applied)(depth)(escapes)(found)

// An unknown consumer can inspect the selected value's descendants, including through an
// alias to a stored aggregate. Sibling fields belong to separate source paths.
let recursive useProjectedFlow path flow applied depth escapes found =
    match path with
        | [] -> useFlow(flow)(applied)(depth)(escapes)(found)
        | "*" :: _rest -> useNestedFlow(flow)(applied)(depth)(escapes)(found)
        | field :: rest ->
            useProjectedFlow(rest)(projectEvidenceField(field)(flow))(applied)(depth)(escapes)(found)

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
                |> useFlow(siteFlow(sites)(expression))(applied)(depth)(escapes)
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
                match root
                |> siteFlow(sites)
                |> callableFlow with
                    | ConstructorEvidenceFlow(_arity) -> walk(arg)(0)(depth)(false)(sites)(found)
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
                            | _ -> walkNestedProjection(arg)(-1)(depth)(false)(sites)(found)
and walkScheduledUses uses ordinal arg total depth escapes sites found =
    match uses with
        | [] -> found
        | EvidenceParameterUse { parameter = parameter, path = path, applied = passed, stage = stage, returned = returned } :: rest ->
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
                        |> walkProjection(path)(arg)(moreArguments(passed)(extra))(depth + delayed)(returned && escapes)(sites)
                        |> walkScheduledUses(rest)(ordinal)(arg)(total)(depth)(escapes)(sites)
and walkNestedProjection value applied depth escapes sites found =
    match aggregateFields(value)(sites) with
        | Some(fields) ->
            Ashes.Collection.List.foldLeft(given (acc) ->
                given (entry) ->
                    match entry with
                        | (_field, child) -> walkNestedProjection(child)(applied)(depth)(escapes)(sites)(acc))(found)(fields)
        | None ->
            let visited = walk(value)(applied)(depth)(escapes)(sites)(found)
            in
                useNestedFlow(siteFlow(sites)(value))(applied)(depth)(escapes)(visited)
and walkProjection path value applied depth escapes sites found =
    match path with
        | [] -> walk(value)(applied)(depth)(escapes)(sites)(found)
        | "*" :: _rest -> walkNestedProjection(value)(applied)(depth)(escapes)(sites)(found)
        | field :: rest ->
            match bare(value) with
                | ExprIf(_condition, yes, no) ->
                    found
                    |> walkProjection(path)(yes)(applied)(depth)(escapes)(sites)
                    |> walkProjection(path)(no)(applied)(depth)(escapes)(sites)
                | ExprRecordUpdate(base, fields) ->
                    match expressionField(field)(fields) with
                        | Some(member) -> walkProjection(rest)(member)(applied)(depth)(escapes)(sites)(found)
                        | None -> walkProjection(path)(base)(applied)(depth)(escapes)(sites)(found)
                | _ ->
                    match aggregateFields(value)(sites) with
                        | Some(fields) ->
                            match expressionField(field)(fields) with
                                | Some(member) -> walkProjection(rest)(member)(applied)(depth)(escapes)(sites)(found)
                                | None -> found
                        | None ->
                            useProjectedFlow(path)(siteFlow(sites)(value))(applied)(depth)(escapes)(found)
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

let recursive latentDemands demands =
    match demands with
        | [] -> []
        | (constraint, stage) :: rest ->
            if stage > 0
            then (constraint, stage) :: latentDemands(rest)
            else latentDemands(rest)

let recursive valueEvidence value sites =
    (let found = walk(value)(0)(0)(true)(sites)(emptyWalk)
    in
        match aggregateFields(value)(sites) with
            | Some(fields) ->
                match fieldEvidence(fields)(sites) with
                    | [] -> (found.requirements, UnknownEvidenceFlow)
                    | members -> (found.requirements, AggregateEvidenceFlow(members))
            | None ->
                match bare(value) with
                    | ExprRecordUpdate(base, fields) ->
                        match valueEvidence(base)(sites) with
                            | (_requirements, AggregateEvidenceFlow(original)) ->
                                (found.requirements, original
                                |> append(fieldEvidence(fields)(sites))
                                |> AggregateEvidenceFlow)
                            | _ ->
                                (found.requirements, sites
                                |> fieldEvidence(fields)
                                |> AggregateEvidenceFlow)
                    | _ ->
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
                                            SourceEvidenceFlow(publishUses(ids)(found.uses))(captures)
                                in (found.requirements, flow))
and fieldEvidence fields sites =
    match fields with
        | [] -> []
        | (name, value) :: rest ->
            match valueEvidence(value)(sites) with
                | (demands, flow) ->
                    let stored =
                        match latentDemands(demands) with
                            | [] -> flow
                            | latent -> MemberEvidenceFlow(latent)(flow)
                    in
                        match stored with
                            | UnknownEvidenceFlow -> fieldEvidence(rest)(sites)
                            | _ -> (name, stored) :: fieldEvidence(rest)(sites)

let evidenceFlowForValue root value requirements flows = valueEvidence(value)(DemandSites(requirements = indexSites(root)(requirements)(Ashes.Collection.Map.empty), flows = indexSites(root)(flows)(Ashes.Collection.Map.empty)))

let recursive notedFlow node flows =
    match flows with
        | [] -> UnknownEvidenceFlow
        | (id, flow) :: rest ->
            if id == node
            then flow
            else
                if id < node
                then UnknownEvidenceFlow
                else notedFlow(node)(rest)

let evidenceFlowForScrutinee value (supply: TypeVariableSupply) =
    (let node = expressionId(value)
    in
        if node < 0
        then UnknownEvidenceFlow
        else
            match bare(value) with
                | ExprVar(_name) -> notedFlow(node)(supply.evidenceFlowSites)
                | ExprQualifiedVar(_owner, _name) -> notedFlow(node)(supply.evidenceFlowSites)
                | _ ->
                    match (supply.evidenceSites, supply.evidenceFlowSites) with
                        | ((id, _) :: _, _) when id >= node ->
                            match evidenceFlowForValue(node)(value)(supply.evidenceSites)(supply.evidenceFlowSites) with
                                | (_requirements, flow) -> flow
                        | (_, (id, _) :: _) when id >= node ->
                            match evidenceFlowForValue(node)(value)(supply.evidenceSites)(supply.evidenceFlowSites) with
                                | (_requirements, flow) -> flow
                        | _ -> UnknownEvidenceFlow)
