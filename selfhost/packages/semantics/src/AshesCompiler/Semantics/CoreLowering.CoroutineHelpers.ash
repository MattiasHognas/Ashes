// Transparent tasks for named helpers whose awaits belong to an enclosing coroutine.
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.RecursiveTraitEvidence.replaceFreeReference
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Constructors.collectCallSpine
import AshesCompiler.Semantics.TcoAnalysis.collectLambdaParamNames
import AshesCompiler.Semantics.TcoAnalysis.collectInnermostBody
import AshesCompiler.Semantics.ExprMentions.helperBodyContainsAwait
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.Types
import Ashes.Collection.List
export (
    value helperRewrite,
    value directHelperCall,
    value enterHelperLoop,
    value restartHelperLoop,
)

let recursive applyArguments fn args =
    match args with
        | [] -> fn
        | arg :: rest ->
            applyArguments(ExprCall(fn)(arg)(false)(callArgumentsInline))(rest)

let internal name args =
    applyArguments(ExprQualifiedVar("$coroutine")(name))(args)

let recursive bridgeLambda names body =
    match names with
        | [] -> body
        | name :: rest ->
            ExprLambda(name)(bridgeLambda(rest)(body))(None)

let recursive replaceInner value body =
    match value with
        | ExprAt(span, inner) ->
            body
            |> replaceInner(inner)
            |> ExprAt(span)
        | ExprNode(_id, inner) -> replaceInner(inner)(body)
        | ExprLambda(name, inner, annotation) ->
            ExprLambda(name)(replaceInner(inner)(body))(annotation)
        | _ -> body

// Only this compiler-generated lambda returns the raw result of a task. Direct calls
// suspend in the caller. A first-class use retains a callable source-level adapter.
let directHelperCall expression =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = ExprLambda(_name, _body, _ann) as bridge, arguments = args } ->
            match bridge
            |> collectInnermostBody
            |> collectCallSpine with
                | CoreCallSpine { root = ExprQualifiedVar("$coroutine", "await"), arguments = task :: [] } ->
                    match collectCallSpine(task) with
                        | CoreCallSpine { root = ExprVar(factory), arguments = parameters } ->
                            if Ashes.Collection.List.length(args) == Ashes.Collection.List.length(parameters)
                            then Some((factory, args))
                            else None
                        | _ -> None
                | _ -> None
        | _ -> None

let recursive restartTail factory restart params expression =
    match expression with
        | ExprAt(span, inner) ->
            inner
            |> restartTail(factory)(restart)(params)
            |> ExprAt(span)
        | ExprNode(id, inner) ->
            inner
            |> restartTail(factory)(restart)(params)
            |> ExprNode(id)
        | ExprIf(cond, yes, no) ->
            no
            |> restartTail(factory)(restart)(params)
            |> ExprIf(cond)(restartTail(factory)(restart)(params)(yes))
        | ExprLet(name, value, body, ps, ann, req) ->
            ExprLet(name)(value)(restartTail(factory)(restart)(params)(body))(ps)(ann)(req)
        | ExprLetRecursive(name, value, body, ps, ann, req) ->
            ExprLetRecursive(name)(value)(restartTail(factory)(restart)(params)(body))(ps)(ann)(req)
        | ExprLetResult(name, value, body) ->
            body
            |> restartTail(factory)(restart)(params)
            |> ExprLetResult(name)(value)
        | ExprMatch(value, cases, pos) ->
            ExprMatch(value)(restartCases(factory)(restart)(params)(cases))(pos)
        | _ ->
            match directHelperCall(expression) with
                | Some((callee, args)) ->
                    if factory == callee
                    then internal("restart")([ExprString(restart), ExprTuple(params), ExprTuple(args)])
                    else expression
                | None -> expression
and restartCases factory restart params cases =
    match cases with
        | [] -> []
        | (pattern, body, guard) :: rest -> (pattern, restartTail(factory)(restart)(params)(body), guard) :: restartCases(factory)(restart)(params)(rest)

let helperRewrite name value continuation id =
    (let params = collectLambdaParamNames([])(value)
    in
        let inner = collectInnermostBody(value)
        in
            if params == [] || !helperBodyContainsAwait(inner)
            then None
            else
                let factory = "$async_helper_" + Ashes.Text.fromInt(id)
                in
                    let restart = "__async_loop_" + Ashes.Text.fromInt(id)
                    in
                        let adapterParams =
                            Ashes.Collection.List.map(given (param) -> factory + "_" + param)(params)
                        in
                            let adapter =
                                [adapterParams
                                |> Ashes.Collection.List.map(ExprVar)
                                |> applyArguments(ExprVar(factory))]
                                |> internal("await")
                                |> bridgeLambda(adapterParams)
                            in
                                match (replaceFreeReference(name)(adapter)(value), replaceFreeReference(name)(adapter)(continuation)) with
                                    | (Some(nextValue), Some(nextBody)) ->
                                        let names = Ashes.Collection.List.map(ExprVar)(params)
                                        in
                                            let body =
                                                nextValue
                                                |> collectInnermostBody
                                                |> restartTail(factory)(restart)(names)
                                            in
                                                let step = internal("begin")([ExprString(restart), ExprTuple(names), body])
                                                in
                                                    []
                                                    |> ExprLetRecursive(factory)([step]
                                                    |> internal("task")
                                                    |> replaceInner(nextValue))(nextBody)([])(None)
                                                    |> Some
                                    | _ -> None)

let destination restart name = "$slot" + restart + name

let recursive bindArguments restart params lower (state: CoreLoweringState) =
    match params with
        | [] -> (state, None)
        | ExprVar(name) :: rest ->
            match state
            |> clearConsumerRequest
            |> lower(ExprVar(name)) with
                | LoweredCoreValue { state = failed, error = Some(error) } -> (failed, Some(error))
                | LoweredCoreValue { state = read, temp = temp, semanticType = ty, error = None } ->
                    match freshLocal(read) with
                        | FreshLocal { state = allocated, local = slot } ->
                            let binding = CoreBinding(name = name, scheme = emptyScheme(ty), location = CoreLocal(slot), ownedRead = false, patternFact = None, closedScheme = false)
                            in
                                let alias = binding with name = destination(restart)(name)
                                in
                                    (allocated with bindings = binding :: alias :: allocated.bindings)
                                    |> emit(StoreLocal(slot)(temp))
                                    |> bindArguments(restart)(rest)(lower)
        | _ -> (state, Some(UnsupportedCoreLoweringExpression("coroutine loop parameter")))

let enterHelperLoop restart params body lower (state: CoreLoweringState) =
    match bindArguments(restart)(params)(lower)(state) with
        | (entered, None) ->
            entered
            |> emit(Label(restart))
            |> lower(body)
        | (failed, Some(error)) -> failure(failed)(error)

let recursive successorArguments restart params args lower (state: CoreLoweringState) values =
    match (params, args) with
        | ([], []) -> (state, values, None)
        | (ExprVar(name) :: ps, arg :: rest) ->
            match lookupBinding(destination(restart)(name))(state.bindings) with
                | Some(CoreBinding { location = CoreLocal(slot), scheme = scheme }) ->
                    match state
                    |> clearConsumerRequest
                    |> lower(arg) with
                        | LoweredCoreValue { state = failed, error = Some(error) } -> (failed, [], Some(error))
                        | LoweredCoreValue { state = read, temp = temp, semanticType = ty, error = None } ->
                            match bindType(scheme.body)(ty)(read) with
                                | (typed, None) -> successorArguments(restart)(ps)(rest)(lower)(typed)((slot, temp) :: values)
                                | (failed, Some(error)) -> (failed, [], Some(error))
                | _ -> (state, [], Some(UnsupportedCoreLoweringExpression("coroutine loop slot")))
        | _ -> (state, [], Some(UnsupportedCoreLoweringExpression("coroutine loop arity")))

let recursive storeSuccessors values state =
    match values with
        | [] -> state
        | (slot, temp) :: rest ->
            state
            |> emit(StoreLocal(slot)(temp))
            |> storeSuccessors(rest)

let restartHelperLoop restart params args lower state =
    match successorArguments(restart)(params)(args)(lower)(state)([]) with
        | (entered, values, None) ->
            match entered
            |> storeSuccessors(values)
            |> emit(Jump(restart))
            |> freshTemp with
                | FreshTemp { state = jumped, temp = dummy } ->
                    match freshType(jumped) with
                        | FreshType { state = typed, semanticType = ty } ->
                            typed
                            |> emit(LoadConstInt(dummy)(0))
                            |> success(dummy)(ty)
        | (failed, _, Some(error)) -> failure(failed)(error)
