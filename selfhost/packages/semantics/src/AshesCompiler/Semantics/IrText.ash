// Renders lowered and final semantic IR for human-readable dumps and stage diffing.
//
// Invariants:
// - Function order is lowered-function order followed by the entry function.
// - Instructions have no ordinal; labels remain left-aligned control-flow anchors.
// - False, absent, empty-text, and optional -1 operands are omitted while zero remains meaningful.
// - Collection operands render only their stable element count.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrFunctionSelection
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.Types
export (
    type IrDumpStage(..),
    value formatIr,
    value formatIrInstruction,
)

type IrDumpStage =
    | LoweredIr
    | FinalIr
    deriving {Eq, Show}

type IrInstructionDescription =
    | opcode: Str
    | operands: List(Maybe(Str))

let namedOperand name value = Some(name + "=" + value)

let intText value = Ashes.Text.fromInt(value)

let requiredIntOperand name value =
    value
    |> intText
    |> namedOperand(name)

let optionalIntOperand name value =
    if value == -1
    then None
    else requiredIntOperand(name)(value)

let floatOperand name value =
    value
    |> Ashes.Text.fromFloat
    |> namedOperand(name)

let boolOperand name value =
    if value
    then namedOperand(name)("true")
    else None

let textOperand name value =
    if value == ""
    then None
    else namedOperand(name)(value)

let maybeTextOperand name value =
    match value with
        | None -> None
        | Some(text) -> textOperand(name)(text)

let collectionOperand name values =
    values
    |> length
    |> intText
    |> (given (count) -> namedOperand(name)("[" + count + "]"))

let environmentDirectoryText kind =
    match kind with
        | CurrentDirectory -> "Current"
        | ExecutableDirectory -> "Executable"
        | TemporaryDirectory -> "Temporary"
        | CacheDirectory -> "Cache"

let copyOutPurposeText purpose =
    match purpose with
        | RcNormalization -> "RcNormalization"
        | ArenaScopeBoundary -> "ArenaScopeBoundary"
        | ArenaCallBoundary -> "ArenaCallBoundary"
        | ArenaTcoCompaction -> "ArenaTcoCompaction"
        | IndependentClone -> "IndependentClone"

let listHeadCopyText kind =
    match kind with
        | InlineListHead -> "Inline"
        | StringListHead -> "String"
        | InnerListHead -> "InnerList"

let externalOwnershipText ownership =
    match ownership with
        | ExternalNativeStringBorrowed -> "Borrowed"
        | ExternalNativeStringOwned -> "Owned"

let boolText value =
    if value
    then "True"
    else "False"

let maybeTextValue value =
    match value with
        | None -> ""
        | Some(text) -> text

let externalDestructorValueText destructor =
    match destructor with
        | ExternalSymbolReference { symbolName = symbol, libraryName = library } ->
            let symbolText = "DestructorSymbol = " + symbol
            in symbolText + ", DestructorLibrary = " + maybeTextValue(library)

let externalDestructorText destructor =
    match destructor with
        | None -> "DestructorSymbol = , DestructorLibrary = "
        | Some(value) -> externalDestructorValueText(value)

let recursive externalAbiTypeText abiType =
    match abiType with
        | ExternalAbiInt -> "Int { }"
        | ExternalAbiUInt(bits) -> "UInt { Bits = " + intText(bits) + " }"
        | ExternalAbiFloat64 -> "Float { }"
        | ExternalAbiFloat32 -> "Float32 { }"
        | ExternalAbiBool -> "Bool { }"
        | ExternalAbiString -> "Str { }"
        | ExternalAbiOpaque(name) -> "Opaque { Name = " + name + " }"
        | ExternalAbiPointer(pointee) -> "Ptr { Pointee = " + externalAbiTypeText(pointee) + " }"
        | ExternalAbiBuffer(element) -> "Buffer { Element = " + externalAbiTypeText(element) + " }"
        | ExternalAbiOut(element) -> "Out { Element = " + externalAbiTypeText(element) + " }"
        | ExternalAbiNativeString(nullable, ownership, destructor) ->
            "NativeString { Nullable = " + boolText(nullable) + ", Ownership = " + externalOwnershipText(
                ownership
            ) + ", " + externalDestructorText(destructor) + " }"
        | ExternalAbiVoid -> "Void { }"

let maybeSemanticTypeOperand name semanticType =
    match semanticType with
        | None -> None
        | Some(value) ->
            value
            |> formatSemanticType
            |> namedOperand(name)

let externalAbiTypeOperand name value =
    value
    |> externalAbiTypeText
    |> namedOperand(name)

let maybeExternalFunctionOperand name value =
    match value with
        | None -> None
        | Some(ExternalFunctionAbi { name = functionName }) -> namedOperand(name)(functionName)

let includeOperand operand tail =
    match operand with
        | None -> tail
        | Some(value) ->
            if tail == ""
            then value
            else value + " " + tail

let recursive joinOperands operands =
    match operands with
        | [] -> ""
        | operand :: rest ->
            rest
            |> joinOperands
            |> includeOperand(operand)

let recursive spaces count =
    if count <= 0
    then ""
    else " " + spaces(count - 1)

let paddedOpcode opcode = opcode + spaces(22 - Ashes.Text.length(opcode))

let originKindText kind =
    match kind with
        | ProgramEntryOrigin -> "ProgramEntry"
        | SourceFunctionOriginKind -> "SourceFunction"
        | ClosureHelperOrigin -> "ClosureHelper"
        | ReuseSpecializationOrigin -> "ReuseSpecialization"
        | ParallelSpecializationOrigin -> "ParallelSpecialization"
        | TraitOperatorSpecializationOrigin -> "TraitOperatorSpecialization"
        | MutualRecursionDispatchOrigin -> "MutualRecursionDispatch"
        | MutualRecursionWrapperOrigin -> "MutualRecursionWrapper"
        | CoroutineOrigin -> "Coroutine"
        | CoroutineFrameDropperOrigin -> "CoroutineFrameDropper"
        | ExternalThunkOrigin -> "ExternalThunk"
        | RuntimeManagedAdtDropperOrigin -> "RuntimeManagedAdtDropper"
        | ResourceAdtDropperOrigin -> "ResourceAdtDropper"
        | ClosureEnvironmentNormalizerOrigin -> "ClosureEnvironmentNormalizer"
        | RuntimeManagedClosureDropperOrigin -> "RuntimeManagedClosureDropper"
        | ResourceClosureDropperOrigin -> "ResourceClosureDropper"
        | AdtDeepCopierOrigin -> "AdtDeepCopier"
        | ListDeepCopierOrigin -> "ListDeepCopier"
        | StructuralOwnerDropperOrigin -> "StructuralOwnerDropper"

// Instruction descriptions are exhaustive and kept in stage-0 declaration order below.
let describeInstruction instruction =
    match instruction with
        | LoadConstInt(target, value) ->
            IrInstructionDescription(
                opcode = "LoadConstInt",
                operands = [
                    optionalIntOperand("Target")(target),
                    requiredIntOperand("Value")(value)
                ]
            )
        | LoadConstFloat(target, value) ->
            IrInstructionDescription(
                opcode = "LoadConstFloat",
                operands = [
                    optionalIntOperand("Target")(target),
                    floatOperand("Value")(value)
                ]
            )
        | LoadConstBool(target, value) ->
            IrInstructionDescription(
                opcode = "LoadConstBool",
                operands = [
                    optionalIntOperand("Target")(target),
                    boolOperand("Value")(value)
                ]
            )
        | LoadConstStr(target, strLabel) ->
            IrInstructionDescription(
                opcode = "LoadConstStr",
                operands = [
                    optionalIntOperand("Target")(target),
                    textOperand("StrLabel")(strLabel)
                ]
            )
        | LoadProgramArgs(target) ->
            IrInstructionDescription(
                opcode = "LoadProgramArgs",
                operands = [
                    optionalIntOperand("Target")(target)
                ]
            )
        | LoadLocal(target, slot) ->
            IrInstructionDescription(
                opcode = "LoadLocal",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Slot")(slot)
                ]
            )
        | StoreLocal(slot, source) ->
            IrInstructionDescription(
                opcode = "StoreLocal",
                operands = [
                    optionalIntOperand("Slot")(slot),
                    optionalIntOperand("Source")(source)
                ]
            )
        | LoadEnv(target, index) ->
            IrInstructionDescription(
                opcode = "LoadEnv",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Index")(index)
                ]
            )
        | StoreMemOffset(basePtr, offsetBytes, source) ->
            IrInstructionDescription(
                opcode = "StoreMemOffset",
                operands = [
                    optionalIntOperand("BasePtr")(basePtr),
                    optionalIntOperand("OffsetBytes")(offsetBytes),
                    optionalIntOperand("Source")(source)
                ]
            )
        | LoadMemOffset(target, basePtr, offsetBytes) ->
            IrInstructionDescription(
                opcode = "LoadMemOffset",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("BasePtr")(basePtr),
                    optionalIntOperand("OffsetBytes")(offsetBytes)
                ]
            )
        | AddInt(target, left, right) ->
            IrInstructionDescription(
                opcode = "AddInt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | SubInt(target, left, right) ->
            IrInstructionDescription(
                opcode = "SubInt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | MulInt(target, left, right) ->
            IrInstructionDescription(
                opcode = "MulInt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | DivInt(target, left, right) ->
            IrInstructionDescription(
                opcode = "DivInt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | DivUInt(target, left, right) ->
            IrInstructionDescription(
                opcode = "DivUInt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | AndInt(target, left, right) ->
            IrInstructionDescription(
                opcode = "AndInt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | OrInt(target, left, right) ->
            IrInstructionDescription(
                opcode = "OrInt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | XorInt(target, left, right) ->
            IrInstructionDescription(
                opcode = "XorInt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | ShlInt(target, left, right) ->
            IrInstructionDescription(
                opcode = "ShlInt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | ShrInt(target, left, right) ->
            IrInstructionDescription(
                opcode = "ShrInt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | AddFloat(target, left, right) ->
            IrInstructionDescription(
                opcode = "AddFloat",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | SubFloat(target, left, right) ->
            IrInstructionDescription(
                opcode = "SubFloat",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | MulFloat(target, left, right) ->
            IrInstructionDescription(
                opcode = "MulFloat",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | DivFloat(target, left, right) ->
            IrInstructionDescription(
                opcode = "DivFloat",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpIntGt(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpIntGt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpIntGe(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpIntGe",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpIntLt(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpIntLt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpIntLe(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpIntLe",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpUIntGt(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpUIntGt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpUIntGe(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpUIntGe",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpUIntLt(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpUIntLt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpUIntLe(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpUIntLe",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpIntEq(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpIntEq",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpIntNe(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpIntNe",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpFloatGt(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpFloatGt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpFloatGe(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpFloatGe",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpFloatLt(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpFloatLt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpFloatLe(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpFloatLe",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpFloatEq(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpFloatEq",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpFloatNe(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpFloatNe",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | IntToFloat(target, valueTemp) ->
            IrInstructionDescription(
                opcode = "IntToFloat",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ValueTemp")(valueTemp)
                ]
            )
        | FloatToInt(target, valueTemp) ->
            IrInstructionDescription(
                opcode = "FloatToInt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ValueTemp")(valueTemp)
                ]
            )
        | FloatUnaryIntrinsic(target, valueTemp, llvmIntrinsic) ->
            IrInstructionDescription(
                opcode = "FloatUnaryIntrinsic",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ValueTemp")(valueTemp),
                    textOperand("LlvmIntrinsic")(llvmIntrinsic)
                ]
            )
        | CallLibm(target, symbol, args) ->
            IrInstructionDescription(
                opcode = "CallLibm",
                operands = [
                    optionalIntOperand("Target")(target),
                    textOperand("Symbol")(symbol),
                    collectionOperand("Args")(args)
                ]
            )
        | BigIntFromInt(target, valueTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BigIntFromInt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ValueTemp")(valueTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BigIntToString(target, valueTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BigIntToString",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ValueTemp")(valueTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BigIntToInt(target, valueTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BigIntToInt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ValueTemp")(valueTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BigIntFromString(target, valueTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BigIntFromString",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ValueTemp")(valueTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BigIntBinary(target, left, right, op, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BigIntBinary",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right),
                    textOperand("Op")(op),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BigIntCompare(target, left, right) ->
            IrInstructionDescription(
                opcode = "BigIntCompare",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpStrEq(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpStrEq",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | CmpStrNe(target, left, right) ->
            IrInstructionDescription(
                opcode = "CmpStrNe",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right)
                ]
            )
        | ConcatStr(target, left, right, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "ConcatStr",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | ConcatStrTip(target, left, right, resvStartSlot, resvEndSlot, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "ConcatStrTip",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Left")(left),
                    optionalIntOperand("Right")(right),
                    optionalIntOperand("ResvStartSlot")(resvStartSlot),
                    optionalIntOperand("ResvEndSlot")(resvEndSlot),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | ConcatStrN(target, parts, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "ConcatStrN",
                operands = [
                    optionalIntOperand("Target")(target),
                    collectionOperand("Parts")(parts),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | RegexCompile(target, pattern) ->
            IrInstructionDescription(
                opcode = "RegexCompile",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Pattern")(pattern)
                ]
            )
        | RegexCompileError(target, pattern) ->
            IrInstructionDescription(
                opcode = "RegexCompileError",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Pattern")(pattern)
                ]
            )
        | RegexFind(target, code, subject, start) ->
            IrInstructionDescription(
                opcode = "RegexFind",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Code")(code),
                    optionalIntOperand("Subject")(subject),
                    optionalIntOperand("Start")(start)
                ]
            )
        | RegexCaptures(target, code, subject, start) ->
            IrInstructionDescription(
                opcode = "RegexCaptures",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Code")(code),
                    optionalIntOperand("Subject")(subject),
                    optionalIntOperand("Start")(start)
                ]
            )
        | RegexSubstitute(target, code, subject, replacement) ->
            IrInstructionDescription(
                opcode = "RegexSubstitute",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Code")(code),
                    optionalIntOperand("Subject")(subject),
                    optionalIntOperand("Replacement")(replacement)
                ]
            )
        | MakeClosure(target, funcLabel, envPtrTemp, envSizeBytes, runtimeManaged, returnsRuntimeManaged, acceptsRuntimeManagedArgument) ->
            IrInstructionDescription(
                opcode = "MakeClosure",
                operands = [
                    optionalIntOperand("Target")(target),
                    textOperand("FuncLabel")(funcLabel),
                    optionalIntOperand("EnvPtrTemp")(envPtrTemp),
                    optionalIntOperand("EnvSizeBytes")(envSizeBytes),
                    boolOperand("RuntimeManaged")(runtimeManaged),
                    boolOperand("ReturnsRuntimeManaged")(returnsRuntimeManaged),
                    boolOperand("AcceptsRuntimeManagedArgument")(acceptsRuntimeManagedArgument)
                ]
            )
        | MakeClosureStack(target, funcLabel, envPtrTemp, envSizeBytes, returnsRuntimeManaged, acceptsRuntimeManagedArgument) ->
            IrInstructionDescription(
                opcode = "MakeClosureStack",
                operands = [
                    optionalIntOperand("Target")(target),
                    textOperand("FuncLabel")(funcLabel),
                    optionalIntOperand("EnvPtrTemp")(envPtrTemp),
                    optionalIntOperand("EnvSizeBytes")(envSizeBytes),
                    boolOperand("ReturnsRuntimeManaged")(returnsRuntimeManaged),
                    boolOperand("AcceptsRuntimeManagedArgument")(acceptsRuntimeManagedArgument)
                ]
            )
        | LoadFuncAddr(target, funcLabel) ->
            IrInstructionDescription(
                opcode = "LoadFuncAddr",
                operands = [
                    optionalIntOperand("Target")(target),
                    textOperand("FuncLabel")(funcLabel)
                ]
            )
        | CallClosure(target, closureTemp, argTemp, runtimeManagedArgumentFlagTemp) ->
            IrInstructionDescription(
                opcode = "CallClosure",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ClosureTemp")(closureTemp),
                    optionalIntOperand("ArgTemp")(argTemp),
                    optionalIntOperand("RuntimeManagedArgumentFlagTemp")(runtimeManagedArgumentFlagTemp)
                ]
            )
        | CallKnown(target, funcLabel, envTemp, argTemp, runtimeManagedArgumentFlagTemp, environmentIsStackAllocated) ->
            IrInstructionDescription(
                opcode = "CallKnown",
                operands = [
                    optionalIntOperand("Target")(target),
                    textOperand("FuncLabel")(funcLabel),
                    optionalIntOperand("EnvTemp")(envTemp),
                    optionalIntOperand("ArgTemp")(argTemp),
                    optionalIntOperand("RuntimeManagedArgumentFlagTemp")(runtimeManagedArgumentFlagTemp),
                    boolOperand("EnvironmentIsStackAllocated")(environmentIsStackAllocated)
                ]
            )
        | LoadArgumentOwnership(target) ->
            IrInstructionDescription(
                opcode = "LoadArgumentOwnership",
                operands = [
                    optionalIntOperand("Target")(target)
                ]
            )
        | Alloc(target, sizeBytes, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "Alloc",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SizeBytes")(sizeBytes),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | AllocStack(target, sizeBytes) ->
            IrInstructionDescription(
                opcode = "AllocStack",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SizeBytes")(sizeBytes)
                ]
            )
        | AllocAdt(target, tag, fieldCount, runtimeManaged, tagless) ->
            IrInstructionDescription(
                opcode = "AllocAdt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Tag")(tag),
                    optionalIntOperand("FieldCount")(fieldCount),
                    boolOperand("RuntimeManaged")(runtimeManaged),
                    boolOperand("Tagless")(tagless)
                ]
            )
        | AllocAdtStack(target, tag, fieldCount, tagless) ->
            IrInstructionDescription(
                opcode = "AllocAdtStack",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Tag")(tag),
                    optionalIntOperand("FieldCount")(fieldCount),
                    boolOperand("Tagless")(tagless)
                ]
            )
        | AllocAdtToSpace(target, tag, fieldCount, tagless) ->
            IrInstructionDescription(
                opcode = "AllocAdtToSpace",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Tag")(tag),
                    optionalIntOperand("FieldCount")(fieldCount),
                    boolOperand("Tagless")(tagless)
                ]
            )
        | DropReuse(target, sourceTemp, fieldCount, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "DropReuse",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SourceTemp")(sourceTemp),
                    optionalIntOperand("FieldCount")(fieldCount),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | AllocReusing(target, tag, fieldCount, tokenTemp, runtimeManaged, listCell, tagless) ->
            IrInstructionDescription(
                opcode = "AllocReusing",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Tag")(tag),
                    optionalIntOperand("FieldCount")(fieldCount),
                    optionalIntOperand("TokenTemp")(tokenTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged),
                    boolOperand("ListCell")(listCell),
                    boolOperand("Tagless")(tagless)
                ]
            )
        | SetAdtField(ptr, fieldIndex, source, tagless) ->
            IrInstructionDescription(
                opcode = "SetAdtField",
                operands = [
                    optionalIntOperand("Ptr")(ptr),
                    optionalIntOperand("FieldIndex")(fieldIndex),
                    optionalIntOperand("Source")(source),
                    boolOperand("Tagless")(tagless)
                ]
            )
        | SaveStackPointer(slot) ->
            IrInstructionDescription(
                opcode = "SaveStackPointer",
                operands = [
                    optionalIntOperand("Slot")(slot)
                ]
            )
        | RestoreStackPointer(slot) ->
            IrInstructionDescription(
                opcode = "RestoreStackPointer",
                operands = [
                    optionalIntOperand("Slot")(slot)
                ]
            )
        | GetAdtTag(target, ptr) ->
            IrInstructionDescription(
                opcode = "GetAdtTag",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Ptr")(ptr)
                ]
            )
        | GetAdtField(target, ptr, fieldIndex, tagless) ->
            IrInstructionDescription(
                opcode = "GetAdtField",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("Ptr")(ptr),
                    optionalIntOperand("FieldIndex")(fieldIndex),
                    boolOperand("Tagless")(tagless)
                ]
            )
        | PrintInt(source) ->
            IrInstructionDescription(
                opcode = "PrintInt",
                operands = [
                    optionalIntOperand("Source")(source)
                ]
            )
        | PrintStr(source) ->
            IrInstructionDescription(
                opcode = "PrintStr",
                operands = [
                    optionalIntOperand("Source")(source)
                ]
            )
        | PrintBool(source) ->
            IrInstructionDescription(
                opcode = "PrintBool",
                operands = [
                    optionalIntOperand("Source")(source)
                ]
            )
        | WriteStr(source) ->
            IrInstructionDescription(
                opcode = "WriteStr",
                operands = [
                    optionalIntOperand("Source")(source)
                ]
            )
        | WriteErrorStr(source, appendNewline) ->
            IrInstructionDescription(
                opcode = "WriteErrorStr",
                operands = [
                    optionalIntOperand("Source")(source),
                    boolOperand("AppendNewline")(appendNewline)
                ]
            )
        | ExitProcess(source) ->
            IrInstructionDescription(
                opcode = "ExitProcess",
                operands = [
                    optionalIntOperand("Source")(source)
                ]
            )
        | WriteBufferedStr(source, appendNewline) ->
            IrInstructionDescription(
                opcode = "WriteBufferedStr",
                operands = [
                    optionalIntOperand("Source")(source),
                    boolOperand("AppendNewline")(appendNewline)
                ]
            )
        | FlushStdout -> IrInstructionDescription(opcode = "FlushStdout", operands = [])
        | ReadLine(target) ->
            IrInstructionDescription(
                opcode = "ReadLine",
                operands = [
                    optionalIntOperand("Target")(target)
                ]
            )
        | ReadExact(target, countTemp) ->
            IrInstructionDescription(
                opcode = "ReadExact",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("CountTemp")(countTemp)
                ]
            )
        | ConsoleEnableRaw(target) ->
            IrInstructionDescription(
                opcode = "ConsoleEnableRaw",
                operands = [
                    optionalIntOperand("Target")(target)
                ]
            )
        | ConsoleRestore(target) ->
            IrInstructionDescription(
                opcode = "ConsoleRestore",
                operands = [
                    optionalIntOperand("Target")(target)
                ]
            )
        | ConsolePoll(target, timeoutTemp) ->
            IrInstructionDescription(
                opcode = "ConsolePoll",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("TimeoutTemp")(timeoutTemp)
                ]
            )
        | MonotonicMillis(target) ->
            IrInstructionDescription(
                opcode = "MonotonicMillis",
                operands = [
                    optionalIntOperand("Target")(target)
                ]
            )
        | TextByteLength(target, textTemp) ->
            IrInstructionDescription(
                opcode = "TextByteLength",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("TextTemp")(textTemp)
                ]
            )
        | FileReadText(target, pathTemp) ->
            IrInstructionDescription(
                opcode = "FileReadText",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("PathTemp")(pathTemp)
                ]
            )
        | FileReadAllBytes(target, pathTemp) ->
            IrInstructionDescription(
                opcode = "FileReadAllBytes",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("PathTemp")(pathTemp)
                ]
            )
        | FileMmap(target, pathTemp) ->
            IrInstructionDescription(
                opcode = "FileMmap",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("PathTemp")(pathTemp)
                ]
            )
        | FileWriteText(target, pathTemp, textTemp) ->
            IrInstructionDescription(
                opcode = "FileWriteText",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("PathTemp")(pathTemp),
                    optionalIntOperand("TextTemp")(textTemp)
                ]
            )
        | FileExists(target, pathTemp) ->
            IrInstructionDescription(
                opcode = "FileExists",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("PathTemp")(pathTemp)
                ]
            )
        | FileReplace(target, sourceTemp, destinationTemp) ->
            IrInstructionDescription(
                opcode = "FileReplace",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SourceTemp")(sourceTemp),
                    optionalIntOperand("DestinationTemp")(destinationTemp)
                ]
            )
        | FileMakeExecutable(target, pathTemp) ->
            IrInstructionDescription(
                opcode = "FileMakeExecutable",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("PathTemp")(pathTemp)
                ]
            )
        | DirectoryEntries(target, pathTemp) ->
            IrInstructionDescription(
                opcode = "DirectoryEntries",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("PathTemp")(pathTemp)
                ]
            )
        | DirectoryCreateAll(target, pathTemp) ->
            IrInstructionDescription(
                opcode = "DirectoryCreateAll",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("PathTemp")(pathTemp)
                ]
            )
        | DirectoryRemoveTree(target, pathTemp) ->
            IrInstructionDescription(
                opcode = "DirectoryRemoveTree",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("PathTemp")(pathTemp)
                ]
            )
        | EnvironmentDirectory(target, kind) ->
            IrInstructionDescription(
                opcode = "EnvironmentDirectory",
                operands = [
                    optionalIntOperand("Target")(target),
                    kind
                    |> environmentDirectoryText
                    |> namedOperand("Kind")
                ]
            )
        | EnvironmentGet(target, nameTemp) ->
            IrInstructionDescription(
                opcode = "EnvironmentGet",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("NameTemp")(nameTemp)
                ]
            )
        | FileOpen(target, pathTemp) ->
            IrInstructionDescription(
                opcode = "FileOpen",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("PathTemp")(pathTemp)
                ]
            )
        | FileReadChunk(target, handleTemp, countTemp) ->
            IrInstructionDescription(
                opcode = "FileReadChunk",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("HandleTemp")(handleTemp),
                    optionalIntOperand("CountTemp")(countTemp)
                ]
            )
        | FileReadLine(target, handleTemp) ->
            IrInstructionDescription(
                opcode = "FileReadLine",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("HandleTemp")(handleTemp)
                ]
            )
        | FileClose(target, handleTemp) ->
            IrInstructionDescription(
                opcode = "FileClose",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("HandleTemp")(handleTemp)
                ]
            )
        | TextUncons(target, textTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "TextUncons",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("TextTemp")(textTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | TextUnconsText(target, textTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "TextUnconsText",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("TextTemp")(textTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | RuneToText(target, runeTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "RuneToText",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("RuneTemp")(runeTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | RuneFromInt(target, intTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "RuneFromInt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("IntTemp")(intTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | TextParseInt(target, textTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "TextParseInt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("TextTemp")(textTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | TextParseFloat(target, textTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "TextParseFloat",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("TextTemp")(textTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | TextFromInt(target, valueTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "TextFromInt",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ValueTemp")(valueTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | TextFromFloat(target, valueTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "TextFromFloat",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ValueTemp")(valueTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | TextFormatFloat(target, valueTemp, decimalsTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "TextFormatFloat",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ValueTemp")(valueTemp),
                    optionalIntOperand("DecimalsTemp")(decimalsTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | TextToHex(target, valueTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "TextToHex",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ValueTemp")(valueTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | TextAsciiCase(target, sourceTemp, upper, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "TextAsciiCase",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SourceTemp")(sourceTemp),
                    boolOperand("Upper")(upper),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | HttpGet(target, urlTemp) ->
            IrInstructionDescription(
                opcode = "HttpGet",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("UrlTemp")(urlTemp)
                ]
            )
        | HttpPost(target, urlTemp, bodyTemp) ->
            IrInstructionDescription(
                opcode = "HttpPost",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("UrlTemp")(urlTemp),
                    optionalIntOperand("BodyTemp")(bodyTemp)
                ]
            )
        | NetTcpConnect(target, hostTemp, portTemp) ->
            IrInstructionDescription(
                opcode = "NetTcpConnect",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("HostTemp")(hostTemp),
                    optionalIntOperand("PortTemp")(portTemp)
                ]
            )
        | NetTcpSend(target, socketTemp, textTemp) ->
            IrInstructionDescription(
                opcode = "NetTcpSend",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SocketTemp")(socketTemp),
                    optionalIntOperand("TextTemp")(textTemp)
                ]
            )
        | NetTcpReceive(target, socketTemp, maxBytesTemp) ->
            IrInstructionDescription(
                opcode = "NetTcpReceive",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SocketTemp")(socketTemp),
                    optionalIntOperand("MaxBytesTemp")(maxBytesTemp)
                ]
            )
        | NetTcpClose(target, socketTemp) ->
            IrInstructionDescription(
                opcode = "NetTcpClose",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SocketTemp")(socketTemp)
                ]
            )
        | NetTcpListen(target, portTemp) ->
            IrInstructionDescription(
                opcode = "NetTcpListen",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("PortTemp")(portTemp)
                ]
            )
        | NetTcpAccept(target, socketTemp) ->
            IrInstructionDescription(
                opcode = "NetTcpAccept",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SocketTemp")(socketTemp)
                ]
            )
        | BytesEmpty(target, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BytesEmpty",
                operands = [
                    optionalIntOperand("Target")(target),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BytesSingleton(target, byteTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BytesSingleton",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ByteTemp")(byteTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BytesLength(target, bytesTemp) ->
            IrInstructionDescription(
                opcode = "BytesLength",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("BytesTemp")(bytesTemp)
                ]
            )
        | BytesGet(target, bytesTemp, indexTemp) ->
            IrInstructionDescription(
                opcode = "BytesGet",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("BytesTemp")(bytesTemp),
                    optionalIntOperand("IndexTemp")(indexTemp)
                ]
            )
        | BytesIndexOf(target, bytesTemp, needleTemp, fromTemp) ->
            IrInstructionDescription(
                opcode = "BytesIndexOf",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("BytesTemp")(bytesTemp),
                    optionalIntOperand("NeedleTemp")(needleTemp),
                    optionalIntOperand("FromTemp")(fromTemp)
                ]
            )
        | BytesCompare(target, leftTemp, rightTemp) ->
            IrInstructionDescription(
                opcode = "BytesCompare",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("LeftTemp")(leftTemp),
                    optionalIntOperand("RightTemp")(rightTemp)
                ]
            )
        | BytesScanHash(target, bytesTemp, needleTemp, fromTemp) ->
            IrInstructionDescription(
                opcode = "BytesScanHash",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("BytesTemp")(bytesTemp),
                    optionalIntOperand("NeedleTemp")(needleTemp),
                    optionalIntOperand("FromTemp")(fromTemp)
                ]
            )
        | BytesSubText(target, bytesTemp, startTemp, lenTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BytesSubText",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("BytesTemp")(bytesTemp),
                    optionalIntOperand("StartTemp")(startTemp),
                    optionalIntOperand("LenTemp")(lenTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BytesSubView(target, bytesTemp, startTemp, lenTemp) ->
            IrInstructionDescription(
                opcode = "BytesSubView",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("BytesTemp")(bytesTemp),
                    optionalIntOperand("StartTemp")(startTemp),
                    optionalIntOperand("LenTemp")(lenTemp)
                ]
            )
        | BytesAppend(target, leftTemp, rightTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BytesAppend",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("LeftTemp")(leftTemp),
                    optionalIntOperand("RightTemp")(rightTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BytesAppendByte(target, bytesTemp, byteTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BytesAppendByte",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("BytesTemp")(bytesTemp),
                    optionalIntOperand("ByteTemp")(byteTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BytesAllocate(target, lengthTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BytesAllocate",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("LengthTemp")(lengthTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BytesCopyRange(target, bytesTemp, offsetTemp, sourceTemp, sourceOffsetTemp, lengthTemp, reuseInput, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BytesCopyRange",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("BytesTemp")(bytesTemp),
                    optionalIntOperand("OffsetTemp")(offsetTemp),
                    optionalIntOperand("SourceTemp")(sourceTemp),
                    optionalIntOperand("SourceOffsetTemp")(sourceOffsetTemp),
                    optionalIntOperand("LengthTemp")(lengthTemp),
                    boolOperand("ReuseInput")(reuseInput),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BytesSet(target, bytesTemp, offsetTemp, valueTemp, reuseInput, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BytesSet",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("BytesTemp")(bytesTemp),
                    optionalIntOperand("OffsetTemp")(offsetTemp),
                    optionalIntOperand("ValueTemp")(valueTemp),
                    boolOperand("ReuseInput")(reuseInput),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BytesSetU16Le(target, bytesTemp, offsetTemp, valueTemp, reuseInput, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BytesSetU16Le",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("BytesTemp")(bytesTemp),
                    optionalIntOperand("OffsetTemp")(offsetTemp),
                    optionalIntOperand("ValueTemp")(valueTemp),
                    boolOperand("ReuseInput")(reuseInput),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BytesSetU32Le(target, bytesTemp, offsetTemp, valueTemp, reuseInput, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BytesSetU32Le",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("BytesTemp")(bytesTemp),
                    optionalIntOperand("OffsetTemp")(offsetTemp),
                    optionalIntOperand("ValueTemp")(valueTemp),
                    boolOperand("ReuseInput")(reuseInput),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BytesSetU64Le(target, bytesTemp, offsetTemp, valueTemp, reuseInput, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BytesSetU64Le",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("BytesTemp")(bytesTemp),
                    optionalIntOperand("OffsetTemp")(offsetTemp),
                    optionalIntOperand("ValueTemp")(valueTemp),
                    boolOperand("ReuseInput")(reuseInput),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BytesFromList(target, listTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BytesFromList",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ListTemp")(listTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BytesHash(target, bytesTemp) ->
            IrInstructionDescription(
                opcode = "BytesHash",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("BytesTemp")(bytesTemp)
                ]
            )
        | BytesU16Le(target, valueTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BytesU16Le",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ValueTemp")(valueTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BytesU32Le(target, valueTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BytesU32Le",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ValueTemp")(valueTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BytesU64Le(target, valueTemp, runtimeManaged) ->
            IrInstructionDescription(
                opcode = "BytesU64Le",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ValueTemp")(valueTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged)
                ]
            )
        | BytesGetU16Le(target, bytesTemp, offsetTemp) ->
            IrInstructionDescription(
                opcode = "BytesGetU16Le",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("BytesTemp")(bytesTemp),
                    optionalIntOperand("OffsetTemp")(offsetTemp)
                ]
            )
        | BytesGetU32Le(target, bytesTemp, offsetTemp) ->
            IrInstructionDescription(
                opcode = "BytesGetU32Le",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("BytesTemp")(bytesTemp),
                    optionalIntOperand("OffsetTemp")(offsetTemp)
                ]
            )
        | BytesGetU64Le(target, bytesTemp, offsetTemp) ->
            IrInstructionDescription(
                opcode = "BytesGetU64Le",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("BytesTemp")(bytesTemp),
                    optionalIntOperand("OffsetTemp")(offsetTemp)
                ]
            )
        | FileWriteBytes(target, pathTemp, bytesTemp) ->
            IrInstructionDescription(
                opcode = "FileWriteBytes",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("PathTemp")(pathTemp),
                    optionalIntOperand("BytesTemp")(bytesTemp)
                ]
            )
        | SpawnProcess(target, exeTemp, argsTemp) ->
            IrInstructionDescription(
                opcode = "SpawnProcess",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ExeTemp")(exeTemp),
                    optionalIntOperand("ArgsTemp")(argsTemp)
                ]
            )
        | ProcessWriteStdin(target, processTemp, textTemp) ->
            IrInstructionDescription(
                opcode = "ProcessWriteStdin",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ProcessTemp")(processTemp),
                    optionalIntOperand("TextTemp")(textTemp)
                ]
            )
        | ProcessReadStdoutLine(target, processTemp) ->
            IrInstructionDescription(
                opcode = "ProcessReadStdoutLine",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ProcessTemp")(processTemp)
                ]
            )
        | ProcessReadStderrLine(target, processTemp) ->
            IrInstructionDescription(
                opcode = "ProcessReadStderrLine",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ProcessTemp")(processTemp)
                ]
            )
        | ProcessWaitForExit(target, processTemp) ->
            IrInstructionDescription(
                opcode = "ProcessWaitForExit",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ProcessTemp")(processTemp)
                ]
            )
        | ProcessKill(target, processTemp) ->
            IrInstructionDescription(
                opcode = "ProcessKill",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ProcessTemp")(processTemp)
                ]
            )
        | CleanupResource(sourceTemp, typeName, destructor) ->
            IrInstructionDescription(
                opcode = "CleanupResource",
                operands = [
                    optionalIntOperand("SourceTemp")(sourceTemp),
                    textOperand("TypeName")(typeName),
                    maybeExternalFunctionOperand("Destructor")(destructor)
                ]
            )
        | RcDrop(sourceTemp, typeName, ownerSlot, runtimeManaged, mayBeEmpty, structuralDropperLabel) ->
            IrInstructionDescription(
                opcode = "RcDrop",
                operands = [
                    optionalIntOperand("SourceTemp")(sourceTemp),
                    textOperand("TypeName")(typeName),
                    optionalIntOperand("OwnerSlot")(ownerSlot),
                    boolOperand("RuntimeManaged")(runtimeManaged),
                    boolOperand("MayBeEmpty")(mayBeEmpty),
                    maybeTextOperand("StructuralDropperLabel")(structuralDropperLabel)
                ]
            )
        | RcDup(target, sourceTemp, runtimeManaged, mayBeEmpty) ->
            IrInstructionDescription(
                opcode = "RcDup",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SourceTemp")(sourceTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged),
                    boolOperand("MayBeEmpty")(mayBeEmpty)
                ]
            )
        | RcIsUnique(target, sourceTemp) ->
            IrInstructionDescription(
                opcode = "RcIsUnique",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SourceTemp")(sourceTemp)
                ]
            )
        | Borrow(target, sourceTemp) ->
            IrInstructionDescription(
                opcode = "Borrow",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SourceTemp")(sourceTemp)
                ]
            )
        | TcoResetPending(id, usedTemps, readLocalSlots) ->
            IrInstructionDescription(
                opcode = "TcoResetPending",
                operands = [
                    optionalIntOperand("Id")(id),
                    collectionOperand("UsedTemps")(usedTemps),
                    collectionOperand("ReadLocalSlots")(readLocalSlots)
                ]
            )
        | SaveArenaState(cursorLocalSlot, endLocalSlot, coroutineLoop) ->
            IrInstructionDescription(
                opcode = "SaveArenaState",
                operands = [
                    optionalIntOperand("CursorLocalSlot")(cursorLocalSlot),
                    optionalIntOperand("EndLocalSlot")(endLocalSlot),
                    boolOperand("CoroutineLoop")(coroutineLoop)
                ]
            )
        | RestoreArenaState(cursorLocalSlot, endLocalSlot, preRestoreEndSlot, coroutineLoop) ->
            IrInstructionDescription(
                opcode = "RestoreArenaState",
                operands = [
                    optionalIntOperand("CursorLocalSlot")(cursorLocalSlot),
                    optionalIntOperand("EndLocalSlot")(endLocalSlot),
                    optionalIntOperand("PreRestoreEndSlot")(preRestoreEndSlot),
                    boolOperand("CoroutineLoop")(coroutineLoop)
                ]
            )
        | ReclaimArenaChunks(savedEndSlot, preRestoreEndSlot, coroutineLoop) ->
            IrInstructionDescription(
                opcode = "ReclaimArenaChunks",
                operands = [
                    optionalIntOperand("SavedEndSlot")(savedEndSlot),
                    optionalIntOperand("PreRestoreEndSlot")(preRestoreEndSlot),
                    boolOperand("CoroutineLoop")(coroutineLoop)
                ]
            )
        | CopyOutArena(destTemp, srcTemp, staticSizeBytes, runtimeManaged, purpose, deferredElementType) ->
            IrInstructionDescription(
                opcode = "CopyOutArena",
                operands = [
                    optionalIntOperand("DestTemp")(destTemp),
                    optionalIntOperand("SrcTemp")(srcTemp),
                    optionalIntOperand("StaticSizeBytes")(staticSizeBytes),
                    boolOperand("RuntimeManaged")(runtimeManaged),
                    purpose
                    |> copyOutPurposeText
                    |> namedOperand("Purpose"),
                    maybeSemanticTypeOperand("DeferredElementType")(deferredElementType)
                ]
            )
        | CopyOutArenaToSpace(destTemp, srcTemp, staticSizeBytes) ->
            IrInstructionDescription(
                opcode = "CopyOutArenaToSpace",
                operands = [
                    optionalIntOperand("DestTemp")(destTemp),
                    optionalIntOperand("SrcTemp")(srcTemp),
                    optionalIntOperand("StaticSizeBytes")(staticSizeBytes)
                ]
            )
        | CopyFixedInto(destTemp, srcTemp, sizeBytes) ->
            IrInstructionDescription(
                opcode = "CopyFixedInto",
                operands = [
                    optionalIntOperand("DestTemp")(destTemp),
                    optionalIntOperand("SrcTemp")(srcTemp),
                    optionalIntOperand("SizeBytes")(sizeBytes)
                ]
            )
        | CopyStringIntoOrFresh(destTemp, oldBlobTemp, srcTemp) ->
            IrInstructionDescription(
                opcode = "CopyStringIntoOrFresh",
                operands = [
                    optionalIntOperand("DestTemp")(destTemp),
                    optionalIntOperand("OldBlobTemp")(oldBlobTemp),
                    optionalIntOperand("SrcTemp")(srcTemp)
                ]
            )
        | CopyFixedIntoOrFresh(destTemp, oldBlobTemp, srcTemp, sizeBytes) ->
            IrInstructionDescription(
                opcode = "CopyFixedIntoOrFresh",
                operands = [
                    optionalIntOperand("DestTemp")(destTemp),
                    optionalIntOperand("OldBlobTemp")(oldBlobTemp),
                    optionalIntOperand("SrcTemp")(srcTemp),
                    optionalIntOperand("SizeBytes")(sizeBytes)
                ]
            )
        | CopyOutList(destTemp, srcTemp, headCopy, runtimeManaged, purpose) ->
            IrInstructionDescription(
                opcode = "CopyOutList",
                operands = [
                    optionalIntOperand("DestTemp")(destTemp),
                    optionalIntOperand("SrcTemp")(srcTemp),
                    headCopy
                    |> listHeadCopyText
                    |> namedOperand("HeadCopy"),
                    boolOperand("RuntimeManaged")(runtimeManaged),
                    purpose
                    |> copyOutPurposeText
                    |> namedOperand("Purpose")
                ]
            )
        | CopyOutClosure(destTemp, srcTemp, runtimeManaged, purpose) ->
            IrInstructionDescription(
                opcode = "CopyOutClosure",
                operands = [
                    optionalIntOperand("DestTemp")(destTemp),
                    optionalIntOperand("SrcTemp")(srcTemp),
                    boolOperand("RuntimeManaged")(runtimeManaged),
                    purpose
                    |> copyOutPurposeText
                    |> namedOperand("Purpose")
                ]
            )
        | CopyOutTcoListCell(destTemp, srcTemp, headCopy, purpose) ->
            IrInstructionDescription(
                opcode = "CopyOutTcoListCell",
                operands = [
                    optionalIntOperand("DestTemp")(destTemp),
                    optionalIntOperand("SrcTemp")(srcTemp),
                    headCopy
                    |> listHeadCopyText
                    |> namedOperand("HeadCopy"),
                    purpose
                    |> copyOutPurposeText
                    |> namedOperand("Purpose")
                ]
            )
        | ToCString(target, strTemp) ->
            IrInstructionDescription(
                opcode = "ToCString",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("StrTemp")(strTemp)
                ]
            )
        | AllocFfiOut(target, elementType) ->
            IrInstructionDescription(
                opcode = "AllocFfiOut",
                operands = [
                    optionalIntOperand("Target")(target),
                    externalAbiTypeOperand("ElementType")(elementType)
                ]
            )
        | LoadFfiOut(target, slotTemp, elementType) ->
            IrInstructionDescription(
                opcode = "LoadFfiOut",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SlotTemp")(slotTemp),
                    externalAbiTypeOperand("ElementType")(elementType)
                ]
            )
        | CopyFfiString(target, pointerTemp, stringType) ->
            IrInstructionDescription(
                opcode = "CopyFfiString",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("PointerTemp")(pointerTemp),
                    externalAbiTypeOperand("StringType")(stringType)
                ]
            )
        | CopyFfiBytes(target, pointerTemp, lengthTemp) ->
            IrInstructionDescription(
                opcode = "CopyFfiBytes",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("PointerTemp")(pointerTemp),
                    optionalIntOperand("LengthTemp")(lengthTemp)
                ]
            )
        | CallExternal(target, symbolName, libraryName, argTemps, parameterTypes, returnType) ->
            IrInstructionDescription(
                opcode = "CallExternal",
                operands = [
                    optionalIntOperand("Target")(target),
                    textOperand("SymbolName")(symbolName),
                    maybeTextOperand("LibraryName")(libraryName),
                    collectionOperand("ArgTemps")(argTemps),
                    collectionOperand("ParameterTypes")(parameterTypes),
                    externalAbiTypeOperand("ReturnType")(returnType)
                ]
            )
        | CreateTask(target, closureTemp, stateStructSize, captureCount, frameDropperLabel, loopResetEligible) ->
            IrInstructionDescription(
                opcode = "CreateTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ClosureTemp")(closureTemp),
                    optionalIntOperand("StateStructSize")(stateStructSize),
                    optionalIntOperand("CaptureCount")(captureCount),
                    maybeTextOperand("FrameDropperLabel")(frameDropperLabel),
                    boolOperand("LoopResetEligible")(loopResetEligible)
                ]
            )
        | CreateCompletedTask(target, resultTemp) ->
            IrInstructionDescription(
                opcode = "CreateCompletedTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ResultTemp")(resultTemp)
                ]
            )
        | AwaitTask(target, taskTemp) ->
            IrInstructionDescription(
                opcode = "AwaitTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("TaskTemp")(taskTemp)
                ]
            )
        | RunTask(target, taskTemp) ->
            IrInstructionDescription(
                opcode = "RunTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("TaskTemp")(taskTemp)
                ]
            )
        | SpawnTask(target, taskTemp) ->
            IrInstructionDescription(
                opcode = "SpawnTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("TaskTemp")(taskTemp)
                ]
            )
        | CreateTaskScope(target, isExplicit) ->
            IrInstructionDescription(
                opcode = "CreateTaskScope",
                operands = [
                    optionalIntOperand("Target")(target),
                    boolOperand("IsExplicit")(isExplicit)
                ]
            )
        | CreateScopedTask(target, parentTaskTemp, scopeTemp) ->
            IrInstructionDescription(
                opcode = "CreateScopedTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("ParentTaskTemp")(parentTaskTemp),
                    optionalIntOperand("ScopeTemp")(scopeTemp)
                ]
            )
        | ForkScopedTask(target, ownerTaskTemp, taskTemp) ->
            IrInstructionDescription(
                opcode = "ForkScopedTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("OwnerTaskTemp")(ownerTaskTemp),
                    optionalIntOperand("TaskTemp")(taskTemp)
                ]
            )
        | JoinScopedTask(target, handleTemp) ->
            IrInstructionDescription(
                opcode = "JoinScopedTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("HandleTemp")(handleTemp)
                ]
            )
        | ParallelFork(descTarget, rightClosureTemp) ->
            IrInstructionDescription(
                opcode = "ParallelFork",
                operands = [
                    optionalIntOperand("DescTarget")(descTarget),
                    optionalIntOperand("RightClosureTemp")(rightClosureTemp)
                ]
            )
        | ParallelJoin(resultTarget, descTemp) ->
            IrInstructionDescription(
                opcode = "ParallelJoin",
                operands = [
                    optionalIntOperand("ResultTarget")(resultTarget),
                    optionalIntOperand("DescTemp")(descTemp)
                ]
            )
        | ParallelCleanup(descTemp) ->
            IrInstructionDescription(
                opcode = "ParallelCleanup",
                operands = [
                    optionalIntOperand("DescTemp")(descTemp)
                ]
            )
        | LoadParallelWorkerOverride(target) ->
            IrInstructionDescription(
                opcode = "LoadParallelWorkerOverride",
                operands = [
                    optionalIntOperand("Target")(target)
                ]
            )
        | StoreParallelWorkerOverride(source) ->
            IrInstructionDescription(
                opcode = "StoreParallelWorkerOverride",
                operands = [
                    optionalIntOperand("Source")(source)
                ]
            )
        | ParallelQueueStart(descTarget, fClosureTemp, combineClosureTemp, listTemp) ->
            IrInstructionDescription(
                opcode = "ParallelQueueStart",
                operands = [
                    optionalIntOperand("DescTarget")(descTarget),
                    optionalIntOperand("FClosureTemp")(fClosureTemp),
                    optionalIntOperand("CombineClosureTemp")(combineClosureTemp),
                    optionalIntOperand("ListTemp")(listTemp)
                ]
            )
        | ParallelQueueAwait(resultTarget, descTemp) ->
            IrInstructionDescription(
                opcode = "ParallelQueueAwait",
                operands = [
                    optionalIntOperand("ResultTarget")(resultTarget),
                    optionalIntOperand("DescTemp")(descTemp)
                ]
            )
        | ParallelQueueCleanup(descTemp) ->
            IrInstructionDescription(
                opcode = "ParallelQueueCleanup",
                operands = [
                    optionalIntOperand("DescTemp")(descTemp)
                ]
            )
        | Suspend(stateStructTemp, nextState, awaitedTaskTemp, saveVars) ->
            IrInstructionDescription(
                opcode = "Suspend",
                operands = [
                    optionalIntOperand("StateStructTemp")(stateStructTemp),
                    optionalIntOperand("NextState")(nextState),
                    optionalIntOperand("AwaitedTaskTemp")(awaitedTaskTemp),
                    collectionOperand("SaveVars")(saveVars)
                ]
            )
        | Resume(stateStructTemp, resultTemp, restoreVars) ->
            IrInstructionDescription(
                opcode = "Resume",
                operands = [
                    optionalIntOperand("StateStructTemp")(stateStructTemp),
                    optionalIntOperand("ResultTemp")(resultTemp),
                    collectionOperand("RestoreVars")(restoreVars)
                ]
            )
        | AsyncSleep(target, millisecondsTemp) ->
            IrInstructionDescription(
                opcode = "AsyncSleep",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("MillisecondsTemp")(millisecondsTemp)
                ]
            )
        | CreateTcpConnectTask(target, hostTemp, portTemp) ->
            IrInstructionDescription(
                opcode = "CreateTcpConnectTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("HostTemp")(hostTemp),
                    optionalIntOperand("PortTemp")(portTemp)
                ]
            )
        | CreateTcpSendTask(target, socketTemp, textTemp) ->
            IrInstructionDescription(
                opcode = "CreateTcpSendTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SocketTemp")(socketTemp),
                    optionalIntOperand("TextTemp")(textTemp)
                ]
            )
        | CreateTcpReceiveTask(target, socketTemp, maxBytesTemp) ->
            IrInstructionDescription(
                opcode = "CreateTcpReceiveTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SocketTemp")(socketTemp),
                    optionalIntOperand("MaxBytesTemp")(maxBytesTemp)
                ]
            )
        | CreateTcpCloseTask(target, socketTemp) ->
            IrInstructionDescription(
                opcode = "CreateTcpCloseTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SocketTemp")(socketTemp)
                ]
            )
        | CreateTcpListenTask(target, portTemp) ->
            IrInstructionDescription(
                opcode = "CreateTcpListenTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("PortTemp")(portTemp)
                ]
            )
        | CreateForkWorkersTask(target, portTemp, countTemp) ->
            IrInstructionDescription(
                opcode = "CreateForkWorkersTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("PortTemp")(portTemp),
                    optionalIntOperand("CountTemp")(countTemp)
                ]
            )
        | SetDrainTimeout(target, msTemp) ->
            IrInstructionDescription(
                opcode = "SetDrainTimeout",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("MsTemp")(msTemp)
                ]
            )
        | RequestServerStop(target) ->
            IrInstructionDescription(
                opcode = "RequestServerStop",
                operands = [
                    optionalIntOperand("Target")(target)
                ]
            )
        | CreateTcpAcceptTask(target, socketTemp) ->
            IrInstructionDescription(
                opcode = "CreateTcpAcceptTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SocketTemp")(socketTemp)
                ]
            )
        | CreateHttpGetTask(target, urlTemp) ->
            IrInstructionDescription(
                opcode = "CreateHttpGetTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("UrlTemp")(urlTemp)
                ]
            )
        | CreateHttpPostTask(target, urlTemp, bodyTemp) ->
            IrInstructionDescription(
                opcode = "CreateHttpPostTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("UrlTemp")(urlTemp),
                    optionalIntOperand("BodyTemp")(bodyTemp)
                ]
            )
        | CreateTlsConnectTask(target, hostTemp, portTemp) ->
            IrInstructionDescription(
                opcode = "CreateTlsConnectTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("HostTemp")(hostTemp),
                    optionalIntOperand("PortTemp")(portTemp)
                ]
            )
        | CreateTlsHandshakeTask(target, socketTemp, hostTemp) ->
            IrInstructionDescription(
                opcode = "CreateTlsHandshakeTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SocketTemp")(socketTemp),
                    optionalIntOperand("HostTemp")(hostTemp)
                ]
            )
        | CreateTlsServerHandshakeTask(target, socketTemp, certTemp, keyTemp) ->
            IrInstructionDescription(
                opcode = "CreateTlsServerHandshakeTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SocketTemp")(socketTemp),
                    optionalIntOperand("CertTemp")(certTemp),
                    optionalIntOperand("KeyTemp")(keyTemp)
                ]
            )
        | CreateTlsSendTask(target, sslTemp, textTemp) ->
            IrInstructionDescription(
                opcode = "CreateTlsSendTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SslTemp")(sslTemp),
                    optionalIntOperand("TextTemp")(textTemp)
                ]
            )
        | CreateTlsReceiveTask(target, sslTemp, maxBytesTemp) ->
            IrInstructionDescription(
                opcode = "CreateTlsReceiveTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SslTemp")(sslTemp),
                    optionalIntOperand("MaxBytesTemp")(maxBytesTemp)
                ]
            )
        | CreateTlsCloseTask(target, sslTemp) ->
            IrInstructionDescription(
                opcode = "CreateTlsCloseTask",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("SslTemp")(sslTemp)
                ]
            )
        | AsyncAll(target, taskListTemp) ->
            IrInstructionDescription(
                opcode = "AsyncAll",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("TaskListTemp")(taskListTemp)
                ]
            )
        | AsyncRace(target, taskListTemp) ->
            IrInstructionDescription(
                opcode = "AsyncRace",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("TaskListTemp")(taskListTemp)
                ]
            )
        | PanicStr(source) ->
            IrInstructionDescription(
                opcode = "PanicStr",
                operands = [
                    optionalIntOperand("Source")(source)
                ]
            )
        | LoadCapabilityHandler(target, capabilityIndex) ->
            IrInstructionDescription(
                opcode = "LoadCapabilityHandler",
                operands = [
                    optionalIntOperand("Target")(target),
                    optionalIntOperand("CapabilityIndex")(capabilityIndex)
                ]
            )
        | StoreCapabilityHandler(capabilityIndex, source) ->
            IrInstructionDescription(
                opcode = "StoreCapabilityHandler",
                operands = [
                    optionalIntOperand("CapabilityIndex")(capabilityIndex),
                    optionalIntOperand("Source")(source)
                ]
            )
        | Label(name) ->
            IrInstructionDescription(
                opcode = "Label",
                operands = [
                    textOperand("Name")(name)
                ]
            )
        | Jump(target) ->
            IrInstructionDescription(
                opcode = "Jump",
                operands = [
                    textOperand("Target")(target)
                ]
            )
        | JumpIfFalse(condTemp, target) ->
            IrInstructionDescription(
                opcode = "JumpIfFalse",
                operands = [
                    optionalIntOperand("CondTemp")(condTemp),
                    textOperand("Target")(target)
                ]
            )
        | SwitchTag(tagTemp, cases, defaultLabel) ->
            IrInstructionDescription(
                opcode = "SwitchTag",
                operands = [
                    optionalIntOperand("TagTemp")(tagTemp),
                    collectionOperand("Cases")(cases),
                    textOperand("DefaultLabel")(defaultLabel)
                ]
            )
        | Return(source) ->
            IrInstructionDescription(
                opcode = "Return",
                operands = [
                    optionalIntOperand("Source")(source)
                ]
            )

let instructionLocationText location =
    match location with
        | None -> ""
        | Some(IrSourceLocation { filePath = filePath, line = line, column = column }) ->
            let sourcePosition = filePath + ":" + intText(line) + ":" + intText(column)
            in "   (" + sourcePosition + ")"

let formatInstructionDescription location description =
    match description with
        | IrInstructionDescription { opcode = opcode, operands = operands } ->
            let line = "    " + paddedOpcode(opcode) + joinOperands(operands) + instructionLocationText(location)
            in Ashes.Text.trimEnd(line)

let formatIrInstruction wrappedInstruction =
    match wrappedInstruction with
        | IrInstruction { instruction = Label(name) } -> "  " + name + ":"
        | IrInstruction { instruction = instruction, location = location } ->
            instruction
            |> describeInstruction
            |> formatInstructionDescription(location)

let sourceOriginText sourceOrigin =
    match sourceOrigin with
        | None -> ""
        | Some(SourceFunctionOrigin { functionSourceName = "" }) -> ""
        | Some(SourceFunctionOrigin { functionSourceName = sourceName }) -> " from " + sourceName

let originValueText origin =
    match origin with
        | IrFunctionOrigin { originKind = kind, sourceOrigin = source } -> "  [" + originKindText(kind) + sourceOriginText(source) + "]"

let originText origin =
    match origin with
        | None -> ""
        | Some(value) -> originValueText(value)

let dictionaryEvidenceText annotation =
    match annotation with
        | TraitDictionaryAbiAnnotation { functionName = functionName, functionSource = functionSource, functionOffset = functionOffset, parameterIndex = parameterIndex, traitName = traitName, methods = methods, supertraits = supertraits } ->
            "  dictionary-parameter function=" + functionName + " source=" + functionSource + ":" + intText(
                functionOffset
            ) + " index=" + intText(parameterIndex) + " trait=" + traitName + " methods=[" + Ashes.Text.join(
                ","
            )(
                methods
            ) + "] supertraits=[" + Ashes.Text.join(",")(supertraits) + "]"

let resolutionEvidenceText annotation =
    match annotation with
        | TraitResolutionAnnotation { requirement = requirement, implementationModule = moduleName, implementationSource = source, implementationOffset = offset } ->
            let implementation =
                moduleName + " (" + source + ":" + intText(
                    offset
                ) + ")"
            in "  resolved requirement=" + requirement + " implementation=" + implementation

let traitEvidenceLines evidence =
    match evidence with
        | TraitEvidenceAnnotations { dictionaryParameters = [], resolvedImplementations = [] } -> []
        | TraitEvidenceAnnotations { dictionaryParameters = dictionaries, resolvedImplementations = resolutions } ->
            append(
                "trait evidence" :: map(dictionaryEvidenceText)(dictionaries)
            )(
                append(
                    map(resolutionEvidenceText)(resolutions)
                )(
                    [""]
                )
            )

let functionLines function =
    match function with
        | IrFunction { label = label, instructions = instructions, localCount = locals, tempCount = temps, origin = origin } ->
            append(
                [
                    "function " + label + originText(origin),
                    "  locals=" + intText(locals) + " temps=" + intText(temps)
                ]
            )(
                append(
                    map(formatIrInstruction)(instructions)
                )(
                    [""]
                )
            )

let recursive matchingFunctionLines filter functions =
    match functions with
        | [] -> []
        | (IrFunction { label = label, origin = origin } as function) :: rest ->
            let tail = matchingFunctionLines(filter)(rest)
            in
                if matchesIrFunction(origin)(label)(filter)
                then
                    append(functionLines(function))(tail)
                else tail

let stageHeader stage =
    match stage with
        | LoweredIr -> ["IR (lowered)", "============", ""]
        | FinalIr -> ["IR (final)", "==========", ""]

let formatIr program stage filter =
    match program with
        | IrProgram { entryFunction = entryFunction, functions = functions, traitEvidence = traitEvidence } ->
            let selectedLines =
                [entryFunction]
                |> append(functions)
                |> matchingFunctionLines(filter)
            in
                append(
                    stageHeader(stage)
                )(
                    append(
                        traitEvidenceLines(traitEvidence)
                    )(
                        match selectedLines with
                            | [] -> ["  (no functions matched)"]
                            | _ -> selectedLines
                    )
                )
