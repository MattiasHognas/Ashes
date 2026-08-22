// Lowers external function declarations, FFI calls, out parameters, and resource cleanups.
//
// Invariants:
// - Layouts carry validated external ABI metadata from ExternalAbi; this module never invents signatures.
// - Arguments arrive in strict source order, converting Str to C-string when required.
// - Compiler-owned out parameters are allocated, loaded, and bundled into the return result.
// - Direct-only external functions are rejected when referenced as first-class values.

import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.ExternalTyping
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeSchemes
import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
export (
    type CoreExternalFunctionLayout(..),
    type CoreExternalLoweringEmission(..),
    type CoreExternalCallShape(..),
    value externalInputParameterCount,
    value fromExternalAbiType,
    value emitDirectExternalCall,
    value tryFindExternalLayout,
    value emitCleanupResource,
)

type CoreExternalFunctionLayout =
    | name: Str
    | abi: ExternalFunctionAbi
    | scheme: TypeScheme

type CoreExternalLoweringEmission =
    | instructions: List(IrInstructionKind)
    | nextTemp: Int
    | nextLocal: Int
    | resultTemp: Int
    | resultType: SemanticType
    | error: Maybe(Str)

type CoreExternalCallShape =
    | layout: CoreExternalFunctionLayout
    | arguments: List(Expr)

let recursive coreListLength items =
    match items with
        | [] -> 0
        | _ :: tail -> 1 + coreListLength(tail)

let isOutParameterAbi (parameter: ExternalParameterAbi) =
    match parameter with
        | ExternalParameterAbi { abiType = ExternalAbiOut(_) } -> true
        | _ -> false

let recursive externalInputParameterCount parameters =
    match parameters with
        | [] -> 0
        | head :: tail ->
            let rest = externalInputParameterCount(tail)
            in
                if isOutParameterAbi(head)
                then rest
                else rest + 1

let recursive fromExternalAbiType abiType =
    match abiType with
        | ExternalAbiInt -> SemInt
        | ExternalAbiUInt(bits) -> SemUInt(bits)
        | ExternalAbiFloat64 -> SemFloat
        | ExternalAbiFloat32 -> SemFloat
        | ExternalAbiBool -> SemBool
        | ExternalAbiString -> SemString
        | ExternalAbiOpaque(name) -> SemOpaque(name)
        | ExternalAbiPointer(pointee) ->
            pointee
            |> fromExternalAbiType
            |> SemPointer
        | ExternalAbiBuffer(element) ->
            element
            |> fromExternalAbiType
            |> SemList
        | ExternalAbiOut(element) -> fromExternalAbiType(element)
        | ExternalAbiNativeString(nullable, _ownership, _destructor) ->
            if nullable
            then SemNamed(0)("Maybe")([SemString])
            else SemString
        | ExternalAbiVoid -> SemNamed(0)("Unit")([])

let emitCleanupResource temp resourceName destructor = CleanupResource(temp)(resourceName)(destructor)

let recursive tryFindExternalLayout name layouts =
    match layouts with
        | [] -> None
        | layout :: tail ->
            match layout with
                | CoreExternalFunctionLayout { name = candidate } ->
                    if name == candidate
                    then Some(layout)
                    else tryFindExternalLayout(name)(tail)

type ExternalArgEmission =
    | instructions: List(IrInstructionKind)
    | nextTemp: Int
    | nativeArgs: List(Int)
    | nativeParamTypes: List(ExternalAbiType)
    | outputSlots: List((Int, ExternalAbiType))
    | remainingArgs: List(Int)

let recursive emitExternalParameters parameters argumentTemps startTemp current =
    match parameters with
        | [] -> current
        | ExternalParameterAbi { abiType = ExternalAbiOut(elementType) } :: tail ->
            match current with
                | ExternalArgEmission { instructions = instrs, nextTemp = temp, nativeArgs = args, nativeParamTypes = types, outputSlots = outs, remainingArgs = rem } ->
                    let slotTemp = temp
                    in
                        let nextEmission =
                            ExternalArgEmission(
                                instructions = append(instrs)([AllocFfiOut(slotTemp)(elementType)]),
                                nextTemp = temp + 1,
                                nativeArgs = append(args)([slotTemp]),
                                nativeParamTypes = append(types)([ExternalAbiPointer(elementType)]),
                                outputSlots = append(outs)([(slotTemp, elementType)]),
                                remainingArgs = rem
                            )
                        in emitExternalParameters(tail)(argumentTemps)(startTemp)(nextEmission)
        | ExternalParameterAbi { abiType = ExternalAbiString } :: tail ->
            match current with
                | ExternalArgEmission { instructions = instrs, nextTemp = temp, nativeArgs = args, nativeParamTypes = types, outputSlots = outs, remainingArgs = argTemp :: remTail } ->
                    let cStringTemp = temp
                    in
                        let nextEmission =
                            ExternalArgEmission(
                                instructions = append(instrs)([ToCString(cStringTemp)(argTemp)]),
                                nextTemp = temp + 1,
                                nativeArgs = append(args)([cStringTemp]),
                                nativeParamTypes = append(types)([ExternalAbiString]),
                                outputSlots = outs,
                                remainingArgs = remTail
                            )
                        in emitExternalParameters(tail)(argumentTemps)(startTemp)(nextEmission)
                | _ -> current
        | ExternalParameterAbi { abiType = otherType } :: tail ->
            match current with
                | ExternalArgEmission { instructions = instrs, nextTemp = temp, nativeArgs = args, nativeParamTypes = types, outputSlots = outs, remainingArgs = argTemp :: remTail } ->
                    let nextEmission =
                        ExternalArgEmission(
                            instructions = instrs,
                            nextTemp = temp,
                            nativeArgs = append(args)([argTemp]),
                            nativeParamTypes = append(types)([otherType]),
                            outputSlots = outs,
                            remainingArgs = remTail
                        )
                    in emitExternalParameters(tail)(argumentTemps)(startTemp)(nextEmission)
                | _ -> current

type MaterializedComponent =
    | temp: Int
    | semanticType: SemanticType

type OutMaterializationResult =
    | instructions: List(IrInstructionKind)
    | nextTemp: Int
    | nextLocal: Int
    | components: List(MaterializedComponent)

let emitOutSlotMaterialization (slotTemp: Int) (elementType: ExternalAbiType) (current: OutMaterializationResult) =
    match current with
        | OutMaterializationResult { instructions = instrs, nextTemp = temp, nextLocal = local, components = comps } ->
            match elementType with
                | ExternalAbiNativeString(_nullable, _ownership, _destructor) ->
                    let loadedTemp = temp
                    in
                        let strTemp = temp + 1
                        in
                            let copyInstruction =
                                None
                                |> ExternalAbiNativeString(true)(ExternalNativeStringBorrowed)
                                |> CopyFfiString(strTemp)(loadedTemp)
                            in
                                OutMaterializationResult(
                                    instructions = append(instrs)([LoadFfiOut(loadedTemp)(slotTemp)(elementType), copyInstruction]),
                                    nextTemp = temp + 2,
                                    nextLocal = local,
                                    components = append(comps)([MaterializedComponent(temp = strTemp, semanticType = SemNamed(0)("Maybe")([SemString]))])
                                )
                | other ->
                    let loadedTemp = temp
                    in
                        let zeroTemp = temp + 1
                        in
                            let isNullTemp = temp + 2
                            in
                                let noneTemp = temp + 3
                                in
                                    let someTemp = temp + 4
                                    in
                                        let resultTemp = temp + 5
                                        in
                                            let resultSlot = local
                                            in
                                                let someLabel = "ffi_out_some_" + Ashes.Text.fromInt(temp)
                                                in
                                                    let endLabel = "ffi_out_end_" + Ashes.Text.fromInt(temp)
                                                    in
                                                        let outInstructions =
                                                            [
                                                                LoadFfiOut(loadedTemp)(slotTemp)(other),
                                                                LoadConstInt(zeroTemp)(0),
                                                                CmpIntEq(isNullTemp)(loadedTemp)(zeroTemp),
                                                                JumpIfFalse(isNullTemp)(someLabel),
                                                                AllocAdt(noneTemp)(0)(0)(false),
                                                                StoreLocal(resultSlot)(noneTemp),
                                                                Jump(endLabel),
                                                                Label(someLabel),
                                                                AllocAdt(someTemp)(1)(1)(false),
                                                                SetAdtField(someTemp)(0)(loadedTemp),
                                                                StoreLocal(resultSlot)(someTemp),
                                                                Label(endLabel),
                                                                LoadLocal(resultTemp)(resultSlot)
                                                            ]
                                                        in
                                                            OutMaterializationResult(
                                                                instructions = append(instrs)(outInstructions),
                                                                nextTemp = temp + 6,
                                                                nextLocal = local + 1,
                                                                components = append(comps)([MaterializedComponent(temp = resultTemp, semanticType = SemNamed(0)("Maybe")([fromExternalAbiType(other)]))])
                                                            )

let recursive materializeOutSlots slots current =
    match slots with
        | [] -> current
        | (slotTemp, elementType) :: tail ->
            current
            |> emitOutSlotMaterialization(slotTemp)(elementType)
            |> materializeOutSlots(tail)

let recursive extractComponentTypes components =
    match components with
        | [] -> []
        | MaterializedComponent { semanticType = semType } :: tail -> semType :: extractComponentTypes(tail)

let recursive emitTupleStores tupleTemp offset components =
    match components with
        | [] -> []
        | MaterializedComponent { temp = compTemp } :: tail -> StoreMemOffset(tupleTemp)(offset)(compTemp) :: emitTupleStores(tupleTemp)(offset + 8)(tail)

let packageMaterializedResults returnInstrs startTemp components =
    match components with
        | [] ->
            let unitTemp = startTemp
            in
                CoreExternalLoweringEmission(
                    instructions = append(returnInstrs)([AllocAdt(unitTemp)(0)(0)(false)]),
                    nextTemp = startTemp + 1,
                    nextLocal = 0,
                    resultTemp = unitTemp,
                    resultType = SemNamed(0)("Unit")([]),
                    error = None
                )
        | MaterializedComponent { temp = singleTemp, semanticType = singleType } :: [] ->
            CoreExternalLoweringEmission(
                instructions = returnInstrs,
                nextTemp = startTemp,
                nextLocal = 0,
                resultTemp = singleTemp,
                resultType = singleType,
                error = None
            )
        | multiple ->
            let tupleTemp = startTemp
            in
                let count = coreListLength(multiple)
                in
                    let allocInstruction = Alloc(tupleTemp)(count * 8)(false)
                    in
                        let storeInstructions = emitTupleStores(tupleTemp)(0)(multiple)
                        in
                            CoreExternalLoweringEmission(
                                instructions = append(returnInstrs)(allocInstruction :: storeInstructions),
                                nextTemp = startTemp + 1,
                                nextLocal = 0,
                                resultTemp = tupleTemp,
                                resultType = multiple
                                |> extractComponentTypes
                                |> SemTuple,
                                error = None
                            )

let emitDirectExternalCall abi startTemp startLocal argumentTemps _argumentTypes =
    match abi with
        | ExternalFunctionAbi { symbol = symbol, parameters = parameters, returnType = retType } ->
            match symbol with
                | ExternalSymbolReference { symbolName = symName, libraryName = libName } ->
                    let initialEmission =
                        ExternalArgEmission(
                            instructions = [],
                            nextTemp = startTemp,
                            nativeArgs = [],
                            nativeParamTypes = [],
                            outputSlots = [],
                            remainingArgs = argumentTemps
                        )
                    in
                        match emitExternalParameters(parameters)(argumentTemps)(startTemp)(initialEmission) with
                            | ExternalArgEmission { instructions = argInstrs, nextTemp = callStartTemp, nativeArgs = nativeArgs, nativeParamTypes = nativeTypes, outputSlots = outputSlots } ->
                                let nativeResultTemp = callStartTemp
                                in
                                    let callInstruction = CallExternal(nativeResultTemp)(symName)(libName)(nativeArgs)(nativeTypes)(retType)
                                    in
                                        let afterCallTemp = callStartTemp + 1
                                        in
                                            match match retType with
                                                | ExternalAbiNativeString(nullable, _ownership, _destructor) ->
                                                    let strTemp = afterCallTemp
                                                    in
                                                        let copyInstr = CopyFfiString(strTemp)(nativeResultTemp)(retType)
                                                        in
                                                            let semType =
                                                                if nullable
                                                                then SemNamed(0)("Maybe")([SemString])
                                                                else SemString
                                                            in ([callInstruction, copyInstr], Some(MaterializedComponent(temp = strTemp, semanticType = semType)), strTemp + 1)
                                                | ExternalAbiVoid -> ([callInstruction], None, afterCallTemp)
                                                | other -> ([callInstruction], Some(MaterializedComponent(temp = nativeResultTemp, semanticType = fromExternalAbiType(other))), afterCallTemp) with
                                                | (returnInstrs, returnComponent, compStartTemp) ->
                                                    let initialMaterialization =
                                                        OutMaterializationResult(
                                                            instructions = append(argInstrs)(returnInstrs),
                                                            nextTemp = compStartTemp,
                                                            nextLocal = startLocal,
                                                            components = match returnComponent with
                                                                | Some(c) -> [c]
                                                                | None -> []
                                                        )
                                                    in
                                                        match materializeOutSlots(outputSlots)(initialMaterialization) with
                                                            | OutMaterializationResult { instructions = finalInstrs, nextTemp = finalTemp, nextLocal = finalLocal, components = finalComps } ->
                                                                let emission = packageMaterializedResults(finalInstrs)(finalTemp)(finalComps)
                                                                in emission with nextLocal = finalLocal
