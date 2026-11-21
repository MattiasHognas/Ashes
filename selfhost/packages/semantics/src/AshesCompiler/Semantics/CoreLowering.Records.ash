// Constructor and builtin references as functions, external calls, record literals, field reads
// and record updates. A part of `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.callArgumentsInline
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.CoreExternalLowering
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.Owners
import AshesCompiler.Semantics.CoreLowering.TcoReset
import AshesCompiler.Semantics.CoreLowering.CallArguments
import AshesCompiler.Semantics.CoreLowering.Matches
import AshesCompiler.Semantics.CoreLowering.Aggregates
import AshesCompiler.Semantics.CoreLowering.Constructors
export (
    value constructorLambda,
    value builtinLambda,
    value tryLowerConstructorCall,
    value tryLowerBuiltinCall,
    value finishCoreExternalReference,
    value emitInstructions,
    value tryLowerExternalCall,
    value lowerRecord,
    value lowerRecordFieldAccess,
    value lowerRecordUpdate,
    value failedCoreBinary,
    value lowerCoreBinaryOperands,
    value numericDefault,
    value finishCoreBinaryBinding,
    value bindCoreBinaryLeft,
)

let recursive constructorParameterNames name count index =
    if index >= count
    then []
    else
        "__core_ctor_" + name + "_" + Ashes.Text.fromInt(index) :: constructorParameterNames(
            name,
            count,
            index + 1
        )

let recursive applyConstructorParameters expression parameters =
    match parameters with
        | [] -> expression
        | parameter :: rest ->
            applyConstructorParameters(
                ExprCall(
                    expression,
                    ExprVar(parameter),
                    false,
                    callArgumentsInline
                ),
                rest
            )

let recursive wrapConstructorParameters parameters body =
    match parameters with
        | [] -> body
        | parameter :: rest ->
            ExprLambda(
                parameter,
                wrapConstructorParameters(rest)(body),
                None
            )

let constructorLambda layout =
    match layout with
        | CoreConstructorLayout { name = name } ->
            let parameters =
                constructorParameterNames(name)(constructorArity(layout))(0)
            in
                parameters
                |> applyConstructorParameters(ExprVar(name))
                |> wrapConstructorParameters(parameters)

let builtinLambda layout =
    match layout with
        | CoreBuiltinLayout { moduleName = moduleName, memberName = memberName } ->
            0
            |> constructorParameterNames("builtin_" + memberName)(builtinArity(layout))
            |> (given (parameters) ->
                parameters
                |> applyConstructorParameters(ExprQualifiedVar(moduleName)(memberName))
                |> wrapConstructorParameters(parameters))

let tryLowerConstructorCall expression (request: ConsumerRequest) lower state =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = ExprVar(name), arguments = arguments } ->
            match constructorLayout(name)(state) with
                | None -> None
                | Some(layout) ->
                    if coreListLength(arguments) == constructorArity(layout)
                    then
                        state
                        |> lowerConstructor(layout)(arguments)(request)(lower)
                        |> Some
                    else None
        | _ -> None

let tryLowerBuiltinCall expression lower state =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = ExprQualifiedVar(moduleName, memberName), arguments = arguments } ->
            match builtinLayout(moduleName)(memberName)(state) with
                | None -> None
                | Some(layout) ->
                    if coreListLength(arguments) == builtinArity(layout)
                    then
                        state
                        |> lowerBuiltin(layout)(arguments)(lower)
                        |> Some
                    else None
        | _ -> None

let externalLayout name state =
    match state with
        | CoreLoweringState { programState = CoreProgramState { externalLayouts = layouts } } -> tryFindExternalLayout(name)(layouts)

let recursive buildExternalParameterNameList count index =
    if index >= count
    then []
    else "__ext_param_" + Ashes.Text.fromInt(index) :: buildExternalParameterNameList(count)(index + 1)

let recursive applyExternalParameters expression parameters =
    match parameters with
        | [] -> expression
        | parameter :: rest ->
            applyExternalParameters(
                ExprCall(
                    expression,
                    ExprVar(parameter),
                    false,
                    callArgumentsInline
                )
            )(rest)

let recursive wrapExternalParameters parameters body =
    match parameters with
        | [] -> body
        | parameter :: rest ->
            ExprLambda(
                parameter,
                wrapExternalParameters(rest)(body),
                None
            )

let externalWrapperLambda layout =
    match layout with
        | CoreExternalFunctionLayout { name = name, abi = abi } ->
            match abi with
                | ExternalFunctionAbi { parameters = parameters } ->
                    let inputCount = externalInputParameterCount(parameters)
                    in
                        if inputCount == 0
                        then
                            ExprLambda(
                                "__ext_unit",
                                ExprCall(ExprVar(name))(ExprVar("__ext_unit"))(false)(callArgumentsInline),
                                None
                            )
                        else
                            let paramNames = buildExternalParameterNameList(inputCount)(0)
                            in
                                paramNames
                                |> applyExternalParameters(ExprVar(name))
                                |> wrapExternalParameters(paramNames)

let finishCoreExternalReference layout lower state =
    match layout with
        | CoreExternalFunctionLayout { name = name, abi = abi } ->
            match abi with
                | ExternalFunctionAbi { directOnly = directOnly } ->
                    if directOnly
                    then failure(state)(CoreExternalDirectOnlyViolation(name))
                    else
                        lower(externalWrapperLambda(layout))(state)

// A nullary external is called as `f(Unit)`; the one argument, spanned or not, is dropped.
let unwrapNullaryExternalArgs expectedCount arguments =
    if expectedCount == 0
    then
        match arguments with
            | single :: [] ->
                match unspanArgument(single) with
                    | ExprVar("Unit") -> []
                    | _ -> arguments
            | other -> other
    else arguments

let recursive emitInstructions (instructions: List(IrInstructionKind)) state =
    match instructions with
        | [] -> state
        | inst :: rest ->
            state
            |> emit(inst)
            |> emitInstructions(rest)

let lowerExternalCallArguments abi arguments lower state =
    match lowerCoreValues(arguments)(lower)(state) with
        | LoweredCoreValues { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValues { state = valuesState, temps = argTemps, semanticTypes = argTypes, error = None } ->
            match valuesState with
                | CoreLoweringState { nextTemp = startTemp, nextLocal = startLocal } ->
                    match emitDirectExternalCall(abi)(startTemp)(startLocal)(argTemps)(argTypes) with
                        | CoreExternalLoweringEmission { error = Some(err) } -> failure(valuesState)(UnsupportedCoreExternalLowering(err))
                        | CoreExternalLoweringEmission { instructions = callInstrs, nextTemp = endTemp, nextLocal = endLocal, resultTemp = resTemp, resultType = resType, error = None } ->
                            let emittedState =
                                valuesState
                                |> emitInstructions(callInstrs)
                                |> withNextTemp(endTemp)
                                |> withNextLocal(endLocal)
                            in success(resTemp)(resType)(emittedState)

// A direct external call under the resource contract of its parameters: the arguments are
// checked before lowering and their bindings released after it.
let lowerExternalCallWithResources (abi: ExternalFunctionAbi) arguments lower state =
    match checkExternalResourceArguments(abi)(externalInputParameters(abi.parameters))(arguments)(state) with
        | Some(error) -> failure(state)(error)
        | None ->
            state
            |> lowerExternalCallArguments(abi)(arguments)(lower)
            |> applyLoweredExternalResourceTransfers(abi)(arguments)

let tryLowerExternalCall expression lower state =
    match collectCallSpine(expression) with
        | CoreCallSpine { root = ExprVar(name), arguments = arguments } ->
            match externalLayout(name)(state) with
                | None -> None
                | Some(layout) ->
                    match layout with
                        | CoreExternalFunctionLayout { abi = abi } ->
                            match abi with
                                | ExternalFunctionAbi { parameters = parameters } ->
                                    let expectedCount = externalInputParameterCount(parameters)
                                    in
                                        let normalizedArgs = unwrapNullaryExternalArgs(expectedCount)(arguments)
                                        in
                                            if coreListLength(normalizedArgs) == expectedCount
                                            then
                                                state
                                                |> lowerExternalCallWithResources(abi)(normalizedArgs)(lower)
                                                |> Some
                                            else None
        | CoreCallSpine { root = ExprQualifiedVar(_moduleName, name), arguments = arguments } ->
            match externalLayout(name)(state) with
                | None -> None
                | Some(layout) ->
                    match layout with
                        | CoreExternalFunctionLayout { abi = abi } ->
                            match abi with
                                | ExternalFunctionAbi { parameters = parameters } ->
                                    let expectedCount = externalInputParameterCount(parameters)
                                    in
                                        let normalizedArgs = unwrapNullaryExternalArgs(expectedCount)(arguments)
                                        in
                                            if coreListLength(normalizedArgs) == expectedCount
                                            then
                                                state
                                                |> lowerExternalCallArguments(abi)(normalizedArgs)(lower)
                                                |> Some
                                            else None
        | _ -> None

let recursive findNamedField (name: Str) (fields: List((Str, Expr))) =
    match fields with
        | [] -> None
        | (candidate, expression) :: rest ->
            if name == candidate
            then Some(expression)
            else findNamedField(name)(rest)

let recursive orderRecordArguments (typeName: Str) (fieldNames: List(Str)) (fields: List((Str, Expr))) reversed =
    match fieldNames with
        | [] -> CoreRecordArguments(expressions = reverse(reversed), error = None)
        | fieldName :: rest ->
            match findNamedField(fieldName)(fields) with
                | None ->
                    CoreRecordArguments(expressions = [], error = fieldName
                    |> UnknownCoreRecordField(typeName)
                    |> Some)
                | Some(expression) -> orderRecordArguments(typeName)(rest)(fields)(expression :: reversed)

let lowerRecord name fields lower state =
    match constructorLayout(name)(state) with
        | None -> failure(state)(UnknownLoweringBinding(name))
        | Some(CoreConstructorLayout { fieldNames = [] }) ->
            ""
            |> UnknownCoreRecordField(name)
            |> failure(state)
        | Some(CoreConstructorLayout { fieldNames = fieldNames } as layout) ->
            match orderRecordArguments(name)(fieldNames)(fields)([]) with
                | CoreRecordArguments { error = Some(error) } -> failure(state)(error)
                | CoreRecordArguments { expressions = expressions, error = None } ->
                    lowerConstructor(
                        layout,
                        expressions,
                        consumerRequestOf(state),
                        lower,
                        state
                    )

let emitRecordFieldLoad receiverTemp fieldType index tagless fresh =
    match fresh with
        | FreshTemp { state = state, temp = fieldTemp } ->
            state
            |> emit(GetAdtField(fieldTemp)(receiverTemp)(index)(tagless))
            |> success(fieldTemp)(resolveType(state)(fieldType))

let loadResolvedRecordField typeName fieldName receiverTemp fieldNames fieldTypes tagless typed =
    match typed with
        | (failedState, Some(error)) -> failure(failedState)(error)
        | (typedState, None) ->
            match findPatternField(fieldName)(fieldNames)(fieldTypes)(0) with
                | None ->
                    fieldName
                    |> UnknownCoreRecordField(typeName)
                    |> failure(typedState)
                | Some(CorePatternField { index = index, semanticType = fieldType }) ->
                    typedState
                    |> freshTemp
                    |> emitRecordFieldLoad(receiverTemp)(fieldType)(index)(tagless)

let finishRecordFieldShape typeName fieldName receiverTemp receiverType fieldNames shape =
    match shape with
        | CoreConstructorShape { state = state, layout = CoreConstructorLayout { tagless = tagless }, parameterTypes = fieldTypes, resultType = resultType } ->
            state
            |> bindType(receiverType)(resultType)
            |> loadResolvedRecordField(typeName)(fieldName)(receiverTemp)(fieldNames)(fieldTypes)(tagless)

let finishRecordFieldLayout typeName fieldName receiverTemp receiverType state layout =
    match layout with
        | None -> failure(state)(CoreRecordUpdateRequiresRecord(receiverType))
        | Some(CoreConstructorLayout { fieldNames = fieldNames } as constructor) ->
            state
            |> instantiateConstructor(constructor)
            |> finishRecordFieldShape(typeName)(fieldName)(receiverTemp)(receiverType)(fieldNames)

let recursive recordLayoutsDeclaringField (fieldName: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> []
        | (CoreConstructorLayout { fieldNames = fieldNames } as layout) :: rest ->
            if containsName(fieldName)(fieldNames)
            then layout :: recordLayoutsDeclaringField(fieldName)(rest)
            else recordLayoutsDeclaringField(fieldName)(rest)

// Stage 0's `ResolveRecordReceiverByFieldName`: a receiver whose type is still a variable at the
// access (a parameter read before any call constrains it) is resolved structurally when exactly
// one record type declares the field, by unifying the receiver with a fresh instance of that
// type. An ambiguous or undeclared field leaves the receiver unresolved.
let resolveRecordReceiverByFieldName (fieldName: Str) (receiverType: SemanticType) (state: CoreLoweringState) =
    match state |> stateConstructorsOfField(fieldName) with
        | layout :: [] ->
            match (instantiateConstructor(layout)(state), constructorResultName(layout)) with
                | (CoreConstructorShape { state = instantiated, resultType = resultType }, Some(typeName)) ->
                    match instantiated
                    |> withStateRuntimeAdtRequested(stateRuntimeAdtRequested(state))
                    |> bindType(receiverType)(resultType) with
                        | (typedState, None) -> (typedState, Some(typeName))
                        | (failedState, Some(_error)) -> (failedState, None)
                | (CoreConstructorShape { state = instantiated }, None) ->
                    (withStateRuntimeAdtRequested(stateRuntimeAdtRequested(state))(instantiated), None)
        | _ -> (state, None)

let finishRecordFieldAccess _receiverName fieldName lowered =
    match lowered with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = state, temp = receiverTemp, semanticType = receiverType, error = None } ->
            match resolveType(state)(receiverType) with
                | SemNamed(_symbolId, typeName, _arguments) ->
                    state
                    |> recordLayout(typeName)
                    |> finishRecordFieldLayout(typeName)(fieldName)(receiverTemp)(receiverType)(state)
                | SemVariable(_id) ->
                    match resolveRecordReceiverByFieldName(fieldName)(receiverType)(state) with
                        | (typedState, Some(typeName)) ->
                            typedState
                            |> recordLayout(typeName)
                            |> finishRecordFieldLayout(typeName)(fieldName)(receiverTemp)(receiverType)(typedState)
                        | (typedState, None) ->
                            receiverType
                            |> resolveType(typedState)
                            |> CoreRecordUpdateRequiresRecord
                            |> failure(typedState)
                | other -> failure(state)(CoreRecordUpdateRequiresRecord(other))

// The receiver of a field access is read without the owned-read borrow: stage 0's
// `TryLowerRecordFieldLoad` loads the binding's slot directly and takes the field from it.
let lowerRecordFieldAccess receiverName fieldName state =
    match state with
        | CoreLoweringState { bindings = bindings } ->
            match lookupBinding(receiverName)(bindings) with
                | None -> failure(state)(UnknownLoweringBinding(receiverName + "." + fieldName))
                | Some(binding) ->
                    state
                    |> lowerBoundVariable((binding with ownedRead = false, patternFact = None))
                    |> finishRecordFieldAccess(receiverName)(fieldName)

let finishUpdatedRecordField expectedType reversedTemps reversedTypes lowered =
    match lowered with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failedCoreValues(failedState)(error)
        | LoweredCoreValue { state = valueState, temp = temp, semanticType = semanticType, error = None } ->
            match bindType(expectedType)(semanticType)(valueState) with
                | (failedState, Some(error)) -> failedCoreValues(failedState)(error)
                | (typedState, None) ->
                    LoweredCoreValues(
                        state = typedState,
                        temps = temp :: reversedTemps,
                        semanticTypes = expectedType :: reversedTypes,
                        error = None
                    )

// Stage 0's `RecordCallArgumentArenaRetain` for a record update's unchanged field: collected
// while an argument the callee's result may reach is lowered.
let recordCallArgumentRetain (duplicate: Int) (slot: Int) (fieldType: SemanticType) (state: CoreLoweringState) =
    if stateCollectingCallArgumentRetains(state)
    then withCallArgumentRetainFrame((duplicate, slot, fieldType) :: stateCallArgumentRecordRetains(state))(true)(state)
    else state

// Stage 0's `KeepLoopResultUpdateFieldDuplicateOwned`: the retain of an unchanged field of a loop
// parameter stored into the arena record a record update builds as a loop function's result goes
// to an owned slot, released once the function's result holds references of its own, since the
// return copies the record onto the reference-counted heap and the arena record releases nothing.
let keepLoopResultUpdateFieldDuplicateOwned (duplicate: Int) (fieldType: SemanticType) (state: CoreLoweringState) =
    if generalRcEnabled(state) && (match consumerRequestOf(state) with
        | ConsumerRequest { tailPosition = tailPosition, consTailPosition = consTailPosition } -> tailPosition || consTailPosition) && !containsUnresolvedLayout(resolveType(state)(fieldType))(state) && (match stateTcoLoopFrame(state) with
        | Some(_frame) -> true
        | None -> false)
    then
        match freshLocal(state) with
            | FreshLocal { state = allocated, local = ownedSlot } ->
                allocated
                |> emit(StoreLocal(ownedSlot)(duplicate))
                |> registerGeneralRcOwnedSlot(ownedSlot)(resolveType(state)(fieldType))
    else state

// Stage 0's `RetainUnchangedRecordUpdateField`: an unchanged heap-typed field of a record update
// whose target reads a loop parameter is a field read out of that parameter stored into the
// rebuilt cell, and takes the same retain marker as one. The back edge copies the successor's
// children and releases the successor's references to them, then releases the old parameter's
// own children through its structural walk, so a borrowed field would be freed twice.
let retainUnchangedRecordField (targetSlot: Maybe(Int)) (fieldType: SemanticType) (temp: Int) (state: CoreLoweringState) =
    match targetSlot with
        | None -> (state, temp)
        | Some(slot) ->
            if resultSurvivesReset(fieldType)(state)
            then (state, temp)
            else
                match freshTemp(state) with
                    | FreshTemp { state = allocated, temp = duplicate } ->
                        (allocated
                        |> emit(RcDup(duplicate)(temp)(false)(false))
                        |> (given (marked: CoreLoweringState) -> withStateTcoParameterRetainSites((duplicate, slot, fieldType) :: stateTcoParameterRetainSites(marked))(marked))
                        |> recordCallArgumentRetain(duplicate)(slot)(fieldType)
                        |> keepLoopResultUpdateFieldDuplicateOwned(duplicate)(fieldType), duplicate)

let loadUnchangedRecordField targetTemp targetSlot index tagless fieldType state reversedTemps reversedTypes =
    match freshTemp(state) with
        | FreshTemp { state = loadState, temp = temp } ->
            match loadState
            |> emit(GetAdtField(temp)(targetTemp)(index)(tagless))
            |> retainUnchangedRecordField(targetSlot)(fieldType)(temp) with
                | (retainedState, retainedTemp) ->
                    LoweredCoreValues(
                        state = retainedState,
                        temps = retainedTemp :: reversedTemps,
                        semanticTypes = fieldType :: reversedTypes,
                        error = None
                    )

// Stage 0's `HandFreshRecordUpdateChildToOwnedSlot`: a record update builds its cell in the arena,
// which never releases what it holds, so a fresh reference-counted value stored into an updated
// field goes to an owned slot, as a constructor's does, for the same records.
let handUpdatedFieldToOwnedSlot (expression: Expr) (fieldType: SemanticType) (handsOver: Bool) lowered =
    match lowered with
        | LoweredCoreValues { state = state, temps = temp :: _rest, error = None } ->
            if handsOver && generalRcEnabled(state)
            then
                lowered with state = handFreshChildToOwnedSlot(expression)(temp)(resolveType(state)(fieldType))(state)
            else lowered
        | _ -> lowered

// Stage 0's `BuildRecordUpdateFieldTemps`: an updated field is a constructor argument of the
// rebuilt cell, so it is lowered under the request a constructor hands its arguments, built from
// the update's own request for an arena cell (no runtime-managed parent). The transfer the update
// was asked for reaches the field's value that way: a binding read stored inside it (a pattern
// binding aliasing a match scrutinee's owner, wrapped in `Some(...)`) is retained for the cell
// before the owner's release, and a cons written as the value of a record successor's field is a
// record field cell.
let lowerUpdatedFieldValue (updateRequest: ConsumerRequest) (fieldType: SemanticType) (expression: Expr) lower (state: CoreLoweringState) =
    state
    |> withConsumerRequest(constructorArgumentRequest(updateRequest)(false)(expression)(fieldType)(state))
    |> lower(expression)

let recursive lowerRecordUpdateFields fieldNames fieldTypes updates targetTemp targetSlot index tagless handsOver (runtimeManaged: Bool) (updateRequest: ConsumerRequest) lower reversedTemps reversedTypes state =
    match (fieldNames, fieldTypes) with
        | ([], []) -> finishCoreValues(state)(reversedTemps)(reversedTypes)
        | (fieldName :: fieldRest, fieldType :: typeRest) ->
            let loweredField =
                match findNamedField(fieldName)(updates) with
                    | None ->
                        loadUnchangedRecordField(
                            targetTemp,
                            targetSlot,
                            index,
                            tagless,
                            fieldType,
                            state,
                            reversedTemps,
                            reversedTypes
                        )
                    | Some(expression) ->
                        state
                        |> lowerUpdatedFieldValue(updateRequest)(fieldType)(expression)(lower)
                        |> keepLoopResultFieldCellHeadDuplicateOwned(updateRequest)(runtimeManaged)(expression)
                        |> retainEscapingArgumentRead(updateRequest)(false)(expression)
                        |> finishUpdatedRecordField(fieldType)(reversedTemps)(reversedTypes)
                        |> handUpdatedFieldToOwnedSlot(expression)(fieldType)(handsOver)
            in
                match loweredField with
                    | LoweredCoreValues { state = failedState, error = Some(error) } -> failedCoreValues(failedState)(error)
                    | LoweredCoreValues { state = nextState, temps = nextTemps, semanticTypes = nextTypes, error = None } ->
                        lowerRecordUpdateFields(
                            fieldRest,
                            typeRest,
                            updates,
                            targetTemp,
                            targetSlot,
                            index + 1,
                            tagless,
                            handsOver,
                            runtimeManaged,
                            updateRequest,
                            lower,
                            nextTemps,
                            nextTypes,
                            nextState
                        )
        | _ -> failedCoreValues(state)(UnsupportedCoreLoweringExpression("record layout arity"))

let lowerTypedRecordUpdate layout resultType runtimeManaged fieldNames fieldTypes fields targetTemp targetSlot (updateRequest: ConsumerRequest) lower typed =
    match (layout, typed) with
        | (_layout, (failedState, Some(error))) -> failure(failedState)(error)
        | (CoreConstructorLayout { tagless = tagless }, (typedState, None)) ->
            typedState
            |> lowerRecordUpdateFields(fieldNames)(fieldTypes)(fields)(targetTemp)(targetSlot)(0)(tagless)(runtimeManaged == false && handsFreshChildrenToOwnedSlots(resultType)(typedState))(runtimeManaged)(updateRequest)(lower)([])([])
            |> finishConstructorAllocation([])(false)(layout)(resultType)(runtimeManaged)

let finishRecordUpdateShape layout fieldNames fields targetTemp targetSlot targetType (updateRequest: ConsumerRequest) lower shape =
    match shape with
        | CoreConstructorShape { state = state, parameterTypes = fieldTypes, resultType = resultType, constructorRuntimeManaged = runtimeManaged } ->
            state
            |> bindType(targetType)(resultType)
            |> lowerTypedRecordUpdate(layout)(resultType)(runtimeManaged)(fieldNames)(fieldTypes)(fields)(targetTemp)(targetSlot)(updateRequest)(lower)

let finishRecordUpdateLayout fields targetTemp targetSlot targetType (updateRequest: ConsumerRequest) lower state layout =
    match layout with
        | None -> failure(state)(CoreRecordUpdateRequiresRecord(targetType))
        | Some(CoreConstructorLayout { fieldNames = fieldNames } as constructor) ->
            state
            |> instantiateConstructor(constructor)
            |> finishRecordUpdateShape(constructor)(fieldNames)(fields)(targetTemp)(targetSlot)(targetType)(updateRequest)(lower)

let finishRecordUpdate fields targetSlot (updateRequest: ConsumerRequest) lower loweredTarget =
    match loweredTarget with
        | LoweredCoreValue { state = failedState, error = Some(error) } -> failure(failedState)(error)
        | LoweredCoreValue { state = state, temp = targetTemp, semanticType = targetType, error = None } ->
            match resolveType(state)(targetType) with
                | SemNamed(_symbolId, typeName, _arguments) ->
                    state
                    |> recordLayout(typeName)
                    |> finishRecordUpdateLayout(fields)(targetTemp)(targetSlot)(targetType)(updateRequest)(lower)(state)
                | other -> failure(state)(CoreRecordUpdateRequiresRecord(other))

// A record update whose target reads a loop parameter (or a record field of one) inside the
// loop body retains the unchanged fields it copies out of that parameter. The transfer of the
// update's children is for the fields it stores, not for its target, which stage 0 lowers under
// no request.
let lowerRecordUpdate target fields lower state =
    state
    |> withConsumerRequest((consumerRequestOf(state) with transfersRuntimeManagedChildren = false, callArgument = false))
    |> lower(target)
    |> finishRecordUpdate(fields)(loopParameterReadSlot(target)(state))(consumerRequestOf(state))(lower)

let failedCoreBinary state error =
    LoweredCoreBinary(
        state = state,
        leftTemp = -1,
        leftType = SemNever,
        rightTemp = -1,
        rightType = SemNever,
        error = Some(error)
    )

let finishCoreBinary leftTemp leftType loweredRight =
    match loweredRight with
        | LoweredCoreValue { state = state, error = Some(error) } -> failedCoreBinary(state)(error)
        | LoweredCoreValue { state = state, temp = rightTemp, semanticType = rightType, error = None } ->
            LoweredCoreBinary(
                state = state,
                leftTemp = leftTemp,
                leftType = leftType,
                rightTemp = rightTemp,
                rightType = rightType,
                error = None
            )

let lowerCoreBinaryRight right lower loweredLeft =
    match loweredLeft with
        | LoweredCoreValue { state = state, error = Some(error) } -> failedCoreBinary(state)(error)
        | LoweredCoreValue { state = state, temp = leftTemp, semanticType = leftType, error = None } ->
            state
            |> lower(right)
            |> finishCoreBinary(leftTemp)(leftType)

let lowerCoreBinaryOperands left right lower state =
    state
    |> lower(left)
    |> lowerCoreBinaryRight(right)(lower)

let numericDefault semanticType =
    match semanticType with
        | SemFloat -> SemFloat
        | SemRune -> SemRune
        | SemBigInt -> SemBigInt
        | SemUInt(bits) -> SemUInt(bits)
        | SemString -> SemString
        | _ -> SemInt

let finishCoreBinaryBinding (binary: LoweredCoreBinary) result =
    match result with
        | (failedState, Some(error)) -> failedCoreBinary(failedState)(error)
        | (typedState, None) -> binary with state = typedState

let bindCoreBinaryLeftType resolvedRight binary resolvedLeft =
    match binary with
        | LoweredCoreBinary { state = state, leftType = leftType } ->
            match resolvedLeft with
                | SemVariable(_id) ->
                    state
                    |> bindType(leftType)(numericDefault(resolvedRight))
                    |> finishCoreBinaryBinding(binary)
                | _ -> binary

let bindCoreBinaryLeft resolvedRight binary =
    match binary with
        | LoweredCoreBinary { state = state, leftType = leftType, error = None } ->
            leftType
            |> resolveType(state)
            |> bindCoreBinaryLeftType(resolvedRight)(binary)
        | failed -> failed
