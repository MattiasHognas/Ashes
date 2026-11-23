// Lexical companions for a source stage that computes before returning its next lambda.
// The prefix returns its live locals. The residual consumes them in the declaration's scope.
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.CoreLowering.Owners.stripExprAt
import AshesCompiler.Semantics.CoreLowering.Owners.collectFree
import AshesCompiler.Semantics.ModuleReferenceRewriting.patternNames
import AshesCompiler.Semantics.ModuleReferenceRewriting.patternListNames
import AshesCompiler.Semantics.ExprMentions.exprReadsName
import AshesCompiler.Semantics.NodeIdentity.foldItemNodes
import AshesCompiler.Semantics.NodeTypeTable.NodeTypeTable
import AshesCompiler.Semantics.NodeTypeTable.nodeEvidenceStagesAt
import AshesCompiler.Semantics.NodeIdentity.nodeIdentityOf
import AshesCompiler.Semantics.NodeIdentity.synthesizedNodeIdentity
import AshesCompiler.Semantics.NodeIdentity.partialPrefixRole
import AshesCompiler.Semantics.NodeIdentity.partialResumeRole
import Ashes.Collection.List.append
import Ashes.Collection.List.filter
import Ashes.Collection.List.foldLeft
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
export (
    value scheduleStrictProgram,
    value scheduleStrictLocal,
)

type StrictStage =
    | nodeTypes: Maybe(NodeTypeTable)
    | prefix: Str
    | resume: Str
    | arity: Int
    | prefixBody: Expr
    | resumeBody: Expr

let recursive contains names sought =
    match names with
        | [] -> false
        | name :: rest -> name == sought || contains(rest)(sought)

let hide names name =
    filter(given (other) -> other != name)(names)

let recursive sourceStart expression =
    match expression with
        | ExprNode(id, _inner) -> id
        | ExprAt(TextSpan(start, _end), _inner) -> start
        | _ -> -1

let syntheticBinding name value = LetBindingSyntax(name = name, value = value, sugarParameters = [], typeAnnotation = None, requirements = [], declarationStart = -1)

let nest name value body = ExprLet(name)(value)(body)([])(None)([])

let apply function argument = ExprCall(function)(argument)(false)(callArgumentsInline)

let recursive describePrefix value parameters locals bindings arity suffix =
    match stripExprAt(value) with
        | ExprLambda(param, body, annotation) ->
            match bindings with
                | [] -> describePrefix(body)((param, annotation) :: parameters)(param :: locals)([])(arity + 1)(suffix)
                | _prefix ->
                    let reads = collectFree(value)([])([])
                    in
                        let live =
                            foldLeft(given (kept) ->
                                given (name) ->
                                    if contains(reads)(name) && !contains(kept)(name)
                                    then name :: kept
                                    else kept)([])(locals)
                        in
                            let packed =
                                match live with
                                    | [] -> ExprVar("Unit")
                                    | single :: [] -> ExprVar(single)
                                    | _ ->
                                        live
                                        |> map(ExprVar)
                                        |> ExprTuple
                            in
                                let residual =
                                    match live with
                                        | single :: [] -> ExprLambda(single)(value)(None)
                                        | [] -> ExprLambda("$partial_unused")(value)(None)
                                        | _ ->
                                            ExprLambda("$partial_environment")(ExprMatch(ExprVar("$partial_environment"))([(live
                                            |> map(PatternVar)
                                            |> PatternTuple, value, None)])(None))(None)
                                in
                                    let prefix =
                                        foldLeft(given (inner) ->
                                            given (entry) ->
                                                match entry with
                                                    | (name, rhs, sugar, ty, reqs) -> ExprLet(name)(rhs)(inner)(sugar)(ty)(reqs))(packed)(bindings)
                                    in
                                        Some(StrictStage(nodeTypes = None, prefix = "$partial_prefix_" + suffix, resume = "$partial_resume_" + suffix, arity = arity, prefixBody = foldLeft(given (inner) ->
                                            given (entry) ->
                                                match entry with
                                                    | (name, ty) -> ExprLambda(name)(inner)(ty))(prefix)(parameters), resumeBody = residual))
        | ExprLet(name, rhs, body, sugar, annotation, requirements) ->
            if arity == 0
            then None
            else describePrefix(body)(parameters)(name :: locals)((name, rhs, sugar, annotation, requirements) :: bindings)(arity)(suffix)
        | _ -> None

let strictStage nodeTypes name value =
    match describePrefix(value)([])([])([])(0)(name + "_" + Ashes.Text.fromInt(sourceStart(value))) with
        | Some(stage) -> Some((stage with nodeTypes = nodeTypes))
        | None -> None

let recursive spine expression arguments =
    match stripExprAt(expression) with
        | ExprCall(function, argument, _sugar, _layout) -> spine(function)(argument :: arguments)
        | root -> (root, arguments)

let recursive hasLatentStage stages =
    match stages with
        | [] -> false
        | arity :: rest -> arity > 0 || hasLatentStage(rest)

let residualNeedsDictionary (stage: StrictStage) expression =
    match stage.nodeTypes with
        | Some(table) ->
            match nodeEvidenceStagesAt(nodeIdentityOf(expression))(table) with
                | Some(stages) -> hasLatentStage(stages)
                | None -> true
        | None -> true

// The identity of the reference a call spine is rooted at: the innermost numbered node around its
// root, -1 for an unnumbered tree.
let recursive rootReferenceNode expression enclosing =
    match expression with
        | ExprAt(_span, inner) -> rootReferenceNode(inner)(enclosing)
        | ExprNode(id, inner) -> rootReferenceNode(inner)(id)
        | ExprCall(function, _argument, _sugar, _layout) -> rootReferenceNode(function)(-1)
        | _ -> enclosing

// A reference to the prefix or resume function standing for the source call's callee: under typed
// lowering it carries the synthesized identity of that callee reference in its role, so a reader of
// the recorded instantiation finds the source reference's record; without a node table it is the
// plain reference it always was.
let stageReference (stage: StrictStage) (name: Str) (role: Int) (source: Expr) =
    match stage.nodeTypes with
        | Some(_table) ->
            let identity =
                synthesizedNodeIdentity(rootReferenceNode(source)(-1))(role)
            in
                if identity == -1
                then ExprVar(name)
                else ExprNode(identity)(ExprVar(name))
        | None -> ExprVar(name)

let strictPartial (stage: StrictStage) names expression =
    match spine(expression)([]) with
        | (ExprVar(name), args) ->
            if contains(names)(name) && length(args) == stage.arity && residualNeedsDictionary(stage)(expression)
            then
                args
                |> foldLeft(apply)(stageReference(stage)(stage.prefix)(partialPrefixRole)(expression))
                |> apply(stageReference(stage)(stage.resume)(partialResumeRole)(expression))
            else expression
        | _ -> expression

// Make the wrapper's evaluation boundary explicit so its own binding can receive a
// declaration-site prefix and resume pair. The introduced names are compiler-private.
let forwardedBody (stage: StrictStage) names expression =
    match spine(expression)([]) with
        | (ExprVar(name), args) ->
            if contains(names)(name) && length(args) == stage.arity
            then
                let packed = "$partial_forwarded_environment"
                in
                    let argument = "$partial_forwarded_argument"
                    in
                        None
                        |> ExprLambda(argument)(apply(apply(stageReference(stage)(stage.resume)(partialResumeRole)(expression))(ExprVar(packed)))(ExprVar(argument)))
                        |> nest(packed)(foldLeft(apply)(stageReference(stage)(stage.prefix)(partialPrefixRole)(expression))(args))
            else expression
        | _ -> expression

let nextAliases names name rhs =
    match stripExprAt(rhs) with
        | ExprVar(reference) ->
            if contains(names)(reference)
            then name :: names
            else hide(names)(name)
        | _ -> hide(names)(name)

let recursive rewrite stage names expression =
    if names == []
    then expression
    else
        let child = rewrite(stage)(names)
        in
            match expression with
                | ExprAt(span, inner) ->
                    inner
                    |> child
                    |> ExprAt(span)
                | ExprNode(id, inner) ->
                    inner
                    |> child
                    |> ExprNode(id)
                | ExprLet(name, rhs, body, sugar, annotation, reqs) ->
                    ExprLet(name)(rhs
                    |> child
                    |> strictPartial(stage)(names))(rewrite(stage)(nextAliases(names)(name)(rhs))(body))(sugar)(annotation)(reqs)
                | ExprLetResult(name, rhs, body) ->
                    body
                    |> rewrite(stage)(hide(names)(name))
                    |> ExprLetResult(name)(child(rhs))
                | ExprLetRecursive(name, rhs, body, sugar, annotation, reqs) ->
                    let inner =
                        name
                        |> hide(names)
                        |> rewrite(stage)
                    in
                        ExprLetRecursive(name)(inner(rhs))(inner(body))(sugar)(annotation)(reqs)
                | ExprLambda(name, body, annotation) ->
                    let visible = hide(names)(name)
                    in
                        ExprLambda(name)(body
                        |> rewrite(stage)(visible)
                        |> forwardedBody(stage)(visible))(annotation)
                | ExprIf(condition, yes, no) ->
                    no
                    |> child
                    |> ExprIf(child(condition))(child(yes))
                | ExprCall(function, argument, sugar, layout) ->
                    ExprCall(child(function))(child(argument))(sugar)(layout)
                | ExprList(elements, layout) ->
                    ExprList(map(child)(elements))(layout)
                | ExprTuple(elements) ->
                    elements
                    |> map(child)
                    |> ExprTuple
                | ExprMatch(target, arms, position) ->
                    ExprMatch(child(target))(map(given (arm) ->
                        match arm with
                            | (pat, body, guard) ->
                                let inner =
                                    pat
                                    |> patternNames
                                    |> foldLeft(hide)(names)
                                    |> rewrite(stage)
                                in
                                    (pat, inner(body), match guard with
                                        | Some(value) ->
                                            value
                                            |> inner
                                            |> Some
                                        | None -> None))(arms))(position)
                | ExprHandle(body, arms) ->
                    arms
                    |> map(given (arm) ->
                        match arm with
                            | (qualifier, operation, patterns, armBody) ->
                                (qualifier, operation, patterns, rewrite(stage)(patterns
                                |> patternListNames
                                |> foldLeft(hide)(names))(armBody)))
                    |> ExprHandle(child(body))
                | ExprRecord(name, fields, layout) ->
                    ExprRecord(name)(rewriteFields(child)(fields))(layout)
                | ExprRecordUpdate(target, fields) ->
                    fields
                    |> rewriteFields(child)
                    |> ExprRecordUpdate(child(target))
                | ExprAdd(left, right) ->
                    right
                    |> child
                    |> ExprAdd(child(left))
                | ExprSubtract(left, right) ->
                    right
                    |> child
                    |> ExprSubtract(child(left))
                | ExprMultiply(left, right) ->
                    right
                    |> child
                    |> ExprMultiply(child(left))
                | ExprDivide(left, right) ->
                    right
                    |> child
                    |> ExprDivide(child(left))
                | ExprModulo(left, right) ->
                    right
                    |> child
                    |> ExprModulo(child(left))
                | ExprBitwiseAnd(left, right) ->
                    right
                    |> child
                    |> ExprBitwiseAnd(child(left))
                | ExprBitwiseOr(left, right) ->
                    right
                    |> child
                    |> ExprBitwiseOr(child(left))
                | ExprBitwiseXor(left, right) ->
                    right
                    |> child
                    |> ExprBitwiseXor(child(left))
                | ExprShiftLeft(left, right) ->
                    right
                    |> child
                    |> ExprShiftLeft(child(left))
                | ExprShiftRight(left, right) ->
                    right
                    |> child
                    |> ExprShiftRight(child(left))
                | ExprLogicalAnd(left, right) ->
                    right
                    |> child
                    |> ExprLogicalAnd(child(left))
                | ExprLogicalOr(left, right) ->
                    right
                    |> child
                    |> ExprLogicalOr(child(left))
                | ExprGreaterThan(left, right) ->
                    right
                    |> child
                    |> ExprGreaterThan(child(left))
                | ExprLessThan(left, right) ->
                    right
                    |> child
                    |> ExprLessThan(child(left))
                | ExprGreaterOrEqual(left, right) ->
                    right
                    |> child
                    |> ExprGreaterOrEqual(child(left))
                | ExprLessOrEqual(left, right) ->
                    right
                    |> child
                    |> ExprLessOrEqual(child(left))
                | ExprEqual(left, right) ->
                    right
                    |> child
                    |> ExprEqual(child(left))
                | ExprNotEqual(left, right) ->
                    right
                    |> child
                    |> ExprNotEqual(child(left))
                | ExprResultPipe(left, right) ->
                    right
                    |> child
                    |> ExprResultPipe(child(left))
                | ExprResultMapErrorPipe(left, right) ->
                    right
                    |> child
                    |> ExprResultMapErrorPipe(child(left))
                | ExprCons(left, right) ->
                    right
                    |> child
                    |> ExprCons(child(left))
                | ExprAwait(operand) ->
                    operand
                    |> child
                    |> ExprAwait
                | ExprPerform(operand) ->
                    operand
                    |> child
                    |> ExprPerform
                | ExprLogicalNot(operand) ->
                    operand
                    |> child
                    |> ExprLogicalNot
                | ExprBitwiseNot(operand) ->
                    operand
                    |> child
                    |> ExprBitwiseNot
                | _ -> expression
and rewriteFields child fields =
    map(given (field) ->
        match field with
            | (name, value) -> (name, child(value)))(fields)

let scheduleStrictLocal nodeTypes name value body annotation reqs =
    match strictStage(nodeTypes)(name)(value) with
        | None -> None
        | Some(stage) ->
            let changed = rewrite(stage)([name])(body)
            in
                if !exprReadsName(stage.prefix)(body) && exprReadsName(stage.prefix)(changed)
                then
                    reqs
                    |> ExprLet(name)(value)(changed)([])(annotation)
                    |> nest(stage.resume)(stage.resumeBody)
                    |> nest(stage.prefix)(stage.prefixBody)
                    |> Some
                else None

let recursive rewriteItem stage names item =
    match item with
        | TopLevelAt(span, inner) ->
            match rewriteItem(stage)(names)(inner) with
                | (after, changed) -> (after, TopLevelAt(span)(changed))
        | TopLevelLet(binding, false) ->
            (nextAliases(names)(binding.name)(binding.value), TopLevelLet((binding with value = strictPartial(stage)(names)(rewrite(stage)(names)(binding.value))))(false))
        | TopLevelLet(binding, true) ->
            let after = hide(names)(binding.name)
            in (after, TopLevelLet((binding with value = rewrite(stage)(after)(binding.value)))(true))
        | TopLevelRecursiveGroup(group) ->
            let after =
                foldLeft(given (active) ->
                    given (binding: LetBindingSyntax) -> hide(active)(binding.name))(names)(group)
            in
                (after, TopLevelRecursiveGroup(map((given (binding: LetBindingSyntax) -> binding with value = rewrite(stage)(after)(binding.value)))(group)))
        | _ -> (names, item)

let recursive rewriteItems stage names items reversed trailing =
    match items with
        | [] -> (reverse(reversed), rewrite(stage)(names)(trailing))
        | item :: rest ->
            match rewriteItem(stage)(names)(item) with
                | (after, changed) -> rewriteItems(stage)(after)(rest)(changed :: reversed)(trailing)

let recursive declaresStage nodeTypes item =
    match item with
        | TopLevelAt(_span, inner) -> declaresStage(nodeTypes)(inner)
        | TopLevelLet(binding, false) ->
            match strictStage(nodeTypes)(binding.name)(binding.value) with
                | Some(stage) -> Some((binding.name, stage))
                | None -> None
        | _ -> None

let recursive usesPrefix name items trailing =
    match items with
        | [] -> exprReadsName(name)(trailing)
        | item :: rest ->
            foldItemNodes(given (used) ->
                given (_id) ->
                    given (_span) ->
                        given (expr) ->
                            match expr with
                                | ExprVar(read) -> used || read == name
                                | _ -> used)(false)(item) || usesPrefix(name)(rest)(trailing)

let recursive scheduleItems nodeTypes items trailing reversed =
    match items with
        | [] -> (reverse(reversed), trailing)
        | item :: rest ->
            match declaresStage(nodeTypes)(item) with
                | None -> scheduleItems(nodeTypes)(rest)(trailing)(item :: reversed)
                | Some((name, stage)) ->
                    match rewriteItems(stage)([name])(rest)([])(trailing) with
                        | (remaining, body) ->
                            if usesPrefix(stage.prefix)(remaining)(body)
                            then
                                scheduleItems(nodeTypes)(remaining)(body)(item :: TopLevelLet(syntheticBinding(stage.resume)(stage.resumeBody))(false) :: TopLevelLet(syntheticBinding(stage.prefix)(stage.prefixBody))(false) :: reversed)
                            else scheduleItems(nodeTypes)(rest)(trailing)(item :: reversed)

let scheduleStrictProgram nodeTypes items trailing = scheduleItems(nodeTypes)(items)(trailing)([])
