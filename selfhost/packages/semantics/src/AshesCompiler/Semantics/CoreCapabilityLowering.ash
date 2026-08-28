// Lowers capability handlers, dynamic perform operations, and static providers to IR.
//
// Invariants:
// - Handler frames snapshot the ambient capability evidence and uninstall in LIFO order.
// - Operations missing an active handler frame panic with deterministic runtime diagnostics.
// - Static providers dispatch directly without handler frame allocation or evidence swapping.

import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.Types
export (
    type CoreCapabilityOperationLayout(..),
    type CoreCapabilityLayout(..),
    type CoreStaticProviderLayout(..),
    type CoreCapabilityPerformEmission(..),
    type CoreCapabilityHandleEmission(..),
    type ParsedHandlerArms(..),
    value emitDynamicPerform,
    value emitStaticProviderCall,
    value findCapabilityLayout,
    value findCapabilityOperationIndex,
    value findStaticProvider,
    value findProviderOperation,
    value splitHandlerArms,
)

type CoreCapabilityOperationLayout =
    | name: Str
    | index: Int
    deriving {Eq, Show}

type CoreCapabilityLayout =
    | name: Str
    | index: Int
    | operations: List(CoreCapabilityOperationLayout)
    deriving {Eq, Show}

type CoreStaticProviderLayout =
    | capabilityName: Str
    | typeArguments: List(SemanticType)
    | operations: List((Str, Expr))

type CoreCapabilityPerformEmission =
    | instructions: List(IrInstructionKind)
    | nextTemp: Int
    | nextLocal: Int
    | resultTemp: Int
    | semanticType: SemanticType
    | error: Maybe(Str)

type CoreCapabilityHandleEmission =
    | instructions: List(IrInstructionKind)
    | nextTemp: Int
    | nextLocal: Int
    | resultTemp: Int
    | semanticType: SemanticType
    | error: Maybe(Str)

let recursive findCapabilityLayout (name: Str) (layouts: List(CoreCapabilityLayout)) =
    match layouts with
        | [] -> None
        | (CoreCapabilityLayout { name = candidate } as layout) :: tail ->
            if name == candidate
            then Some(layout)
            else findCapabilityLayout(name)(tail)

let recursive findCapabilityOperationIndex (opName: Str) (ops: List(CoreCapabilityOperationLayout)) =
    match ops with
        | [] -> None
        | CoreCapabilityOperationLayout { name = candidate, index = idx } :: tail ->
            if opName == candidate
            then Some(idx)
            else findCapabilityOperationIndex(opName)(tail)

let recursive findStaticProviderExact (capabilityName: Str) (requiredTypeArguments: List(SemanticType)) (providers: List(CoreStaticProviderLayout)) =
    match providers with
        | [] -> None
        | (CoreStaticProviderLayout { capabilityName = candidateName, typeArguments = candidateArguments } as provider) :: tail ->
            if capabilityName == candidateName
            then
                if requiredTypeArguments == candidateArguments
                then Some(provider)
                else findStaticProviderExact(capabilityName)(requiredTypeArguments)(tail)
            else findStaticProviderExact(capabilityName)(requiredTypeArguments)(tail)

let recursive collectStaticProvidersByName (capabilityName: Str) (providers: List(CoreStaticProviderLayout)) =
    match providers with
        | [] -> []
        | (CoreStaticProviderLayout { capabilityName = candidateName } as provider) :: tail ->
            if capabilityName == candidateName
            then provider :: collectStaticProvidersByName(capabilityName)(tail)
            else collectStaticProvidersByName(capabilityName)(tail)

let recursive allStaticProvidersShareTypeArguments (typeArguments: List(SemanticType)) (providers: List(CoreStaticProviderLayout)) =
    match providers with
        | [] -> true
        | CoreStaticProviderLayout { typeArguments = candidateArguments } :: tail ->
            if typeArguments == candidateArguments
            then allStaticProvidersShareTypeArguments(typeArguments)(tail)
            else false

// Stage 0 registers a static provider under a key built from capability name AND resolved type
// arguments (`Lowering.Capabilities.cs`'s `BuildProviderKey`), so `provide Log(Int)` and
// `provide Log(Str)` are two distinct, individually valid registrations that a name-only lookup
// can never tell apart. When the caller supplies `requiredTypeArguments` (known concrete type(s)
// for this call site), match on both name and type arguments exactly — sound because a `provide`
// declaration's own type arguments are always concrete (no free variables), so structural equality
// is meaningful regardless of which pass produced either side. When the caller doesn't know the
// required type arguments yet (`[]` — CoreLowering.ash's own call site has no way to derive them
// today), fall back to matching by name alone, but only when every candidate for that name shares
// the SAME type arguments (including the common case of a non-generic capability, where every
// provider's type arguments are trivially `[]` too) — a name match across genuinely DIFFERENT type
// arguments is ambiguous and correctly reports no match rather than silently picking whichever
// provider happens to be listed first.
let findStaticProvider (capabilityName: Str) (requiredTypeArguments: List(SemanticType)) (providers: List(CoreStaticProviderLayout)) =
    match requiredTypeArguments with
        | [] ->
            match collectStaticProvidersByName(capabilityName)(providers) with
                | [] -> None
                | (CoreStaticProviderLayout { typeArguments = firstArguments } as first) :: rest ->
                    if allStaticProvidersShareTypeArguments(firstArguments)(rest)
                    then Some(first)
                    else None
        | _ -> findStaticProviderExact(capabilityName)(requiredTypeArguments)(providers)

let recursive findProviderOperation (opName: Str) (operations: List((Str, Expr))) =
    match operations with
        | [] -> None
        | (candidate, expression) :: tail ->
            if opName == candidate
            then Some(expression)
            else findProviderOperation(opName)(tail)

let recursive emitPerformEvidenceSave (startTemp: Int) (frameTemp: Int) (currentK: Int) (globalCount: Int) (savedTemps: List(Int)) (instructions: List(IrInstructionKind)) =
    if currentK >= globalCount
    then (reverse(savedTemps), instructions, startTemp)
    else
        let savedTemp = startTemp
        in
            let nextSave = LoadCapabilityHandler(savedTemp)(currentK)
            in
                emitPerformEvidenceSave(
                    startTemp + 1
                )(
                    frameTemp
                )(
                    currentK + 1
                )(
                    globalCount
                )(
                    savedTemp :: savedTemps
                )(
                    append(instructions)([nextSave])
                )

let recursive emitPerformEvidenceSwitch (startTemp: Int) (frameTemp: Int) (currentK: Int) (globalCount: Int) (instructions: List(IrInstructionKind)) =
    if currentK >= globalCount
    then (instructions, startTemp)
    else
        let snapTemp = startTemp
        in
            let loadSnap = LoadMemOffset(snapTemp)(frameTemp)(currentK * 8)
            in
                let storeSnap = StoreCapabilityHandler(currentK)(snapTemp)
                in
                    emitPerformEvidenceSwitch(
                        startTemp + 1
                    )(
                        frameTemp
                    )(
                        currentK + 1
                    )(
                        globalCount
                    )(
                        append(instructions)([loadSnap, storeSnap])
                    )

let recursive emitPerformEvidenceRestore (currentK: Int) (globalCount: Int) (savedTemps: List(Int)) (instructions: List(IrInstructionKind)) =
    match savedTemps with
        | [] -> instructions
        | savedTemp :: tail ->
            let restoreInst = StoreCapabilityHandler(currentK)(savedTemp)
            in
                emitPerformEvidenceRestore(
                    currentK + 1
                )(
                    globalCount
                )(
                    tail
                )(
                    append(instructions)([restoreInst])
                )

let recursive emitCurriedClosureCalls (currentTemp: Int) (startTemp: Int) (argTemps: List(Int)) (instructions: List(IrInstructionKind)) =
    match argTemps with
        | [] -> (currentTemp, instructions, startTemp)
        | argTemp :: tail ->
            let callTarget = startTemp
            in
                let callInst = CallClosure(callTarget)(currentTemp)(argTemp)(-1)
                in
                    emitCurriedClosureCalls(
                        callTarget
                    )(
                        startTemp + 1
                    )(
                        tail
                    )(
                        append(instructions)([callInst])
                    )

let emitDynamicPerform (capabilityName: Str) (opName: Str) (capabilityIndex: Int) (opIndex: Int) (globalCount: Int) (startTemp: Int) (startLocal: Int) (argTemps: List(Int)) (resultType: SemanticType) =
    (let frameTemp = startTemp
    in
        let zeroTemp = startTemp + 1
        in
            let installedTemp = startTemp + 2
            in
                let siteId = Ashes.Text.fromInt(startTemp)
                in
                    let unhandledLabel = "capability_unhandled_" + siteId
                    in
                        let doneLabel = "capability_done_" + siteId
                        in
                            let panicMsgTemp = startTemp + 3
                            in
                                let checkInstructions =
                                    [
                                        LoadCapabilityHandler(frameTemp)(capabilityIndex),
                                        LoadConstInt(zeroTemp)(0),
                                        CmpIntNe(installedTemp)(frameTemp)(zeroTemp),
                                        JumpIfFalse(installedTemp)(unhandledLabel)
                                    ]
                                in
                                    match emitPerformEvidenceSave(startTemp + 4)(frameTemp)(0)(globalCount)([])(checkInstructions) with
                                        | (savedTemps, saveInstructions, afterSaveTemp) ->
                                            match emitPerformEvidenceSwitch(afterSaveTemp)(frameTemp)(0)(globalCount)(saveInstructions) with
                                                | (switchInstructions, afterSwitchTemp) ->
                                                    let closureTemp = afterSwitchTemp
                                                    in
                                                        let loadClosure = LoadMemOffset(closureTemp)(frameTemp)((globalCount + 1 + opIndex) * 8)
                                                        in
                                                            match emitCurriedClosureCalls(closureTemp)(closureTemp + 1)(argTemps)(append(switchInstructions)([loadClosure])) with
                                                                | (callResultTemp, callInstructions, afterCallTemp) ->
                                                                    let restoreInstructions = emitPerformEvidenceRestore(0)(globalCount)(savedTemps)(callInstructions)
                                                                    in
                                                                        let resultSlot = startLocal
                                                                        in
                                                                            let resultTemp = afterCallTemp
                                                                            in
                                                                                let panicMessage = "Unhandled capability operation '" + capabilityName + "." + opName + "'."
                                                                                in
                                                                                    let finalInstructions =
                                                                                        append(
                                                                                            restoreInstructions
                                                                                        )(
                                                                                            [
                                                                                                StoreLocal(resultSlot)(callResultTemp),
                                                                                                Jump(doneLabel),
                                                                                                Label(unhandledLabel),
                                                                                                LoadConstStr(panicMsgTemp)(panicMessage),
                                                                                                PanicStr(panicMsgTemp),
                                                                                                Label(doneLabel),
                                                                                                LoadLocal(resultTemp)(resultSlot)
                                                                                            ]
                                                                                        )
                                                                                    in
                                                                                        CoreCapabilityPerformEmission(
                                                                                            instructions = finalInstructions,
                                                                                            nextTemp = resultTemp + 1,
                                                                                            nextLocal = startLocal + 1,
                                                                                            resultTemp = resultTemp,
                                                                                            semanticType = resultType,
                                                                                            error = None
                                                                                        ))

let emitStaticProviderCall (provider: CoreStaticProviderLayout) (opName: Str) (providerClosureTemp: Int) (startTemp: Int) (startLocal: Int) (argTemps: List(Int)) (resultType: SemanticType) =
    match emitCurriedClosureCalls(providerClosureTemp)(startTemp)(argTemps)([]) with
        | (callResultTemp, callInstructions, afterCallTemp) ->
            CoreCapabilityPerformEmission(
                instructions = callInstructions,
                nextTemp = afterCallTemp,
                nextLocal = startLocal,
                resultTemp = callResultTemp,
                semanticType = resultType,
                error = None
            )

type ParsedHandlerArms =
    | opArms: List((Str, Str, List(Pattern), Expr))
    | returnArm: Maybe((Pattern, Expr))

let recursive splitHandlerArmsHelper arms parsed =
    match arms with
        | [] -> parsed
        | (None, "return", returnPat :: [], returnExpr) :: tail ->
            match parsed with
                | ParsedHandlerArms { opArms = ops } ->
                    splitHandlerArmsHelper(
                        tail
                    )(
                        ParsedHandlerArms(
                            opArms = ops,
                            returnArm = Some((returnPat, returnExpr))
                        )
                    )
        | (Some(capName), opName, pats, bodyExpr) :: tail ->
            match parsed with
                | ParsedHandlerArms { opArms = ops, returnArm = ret } ->
                    splitHandlerArmsHelper(
                        tail
                    )(
                        ParsedHandlerArms(
                            opArms = append(ops)([(capName, opName, pats, bodyExpr)]),
                            returnArm = ret
                        )
                    )
        | _ :: tail -> splitHandlerArmsHelper(tail)(parsed)

let splitHandlerArms (arms: List((Maybe(Str), Str, List(Pattern), Expr))) = splitHandlerArmsHelper(arms)(ParsedHandlerArms(opArms = [], returnArm = None))
