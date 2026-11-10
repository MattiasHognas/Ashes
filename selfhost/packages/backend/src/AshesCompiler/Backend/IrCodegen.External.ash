// The `external` call surface for `AshesCompiler.Backend.IrCodegen`, stage 0's
// `EmitCallExternalValues`, `GetLlvmFfiType` and `ConvertFfiArgument`. An `external` declaration
// names a C symbol and an ABI signature; a call to one loads its argument words, converts each to
// the native type its position declares, calls the symbol, and normalizes the result back to the
// universal `i64` word every temp holds.
//
// Every symbol a program calls is declared once, before any function body, from a scan of the
// whole program's instructions: a pure codegen fold has nowhere to record "already declared", and
// declaring the same name twice would leave LLVM renaming the second one. `ToCString` is here too,
// since it exists only to hand a `Str` argument to such a call.
//
// A `Buffer` parameter is copied from its immutable list into a native array in the current
// stack frame. Empty lists pass null. `Out` parameters use null-initialized native slots;
// `IrCodegen.ForeignStrings` copies and validates native string results before disposal.
import Ashes.Collection.List.append
import Ashes.Collection.List.length
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
export (
    type ExternalSymbolDeclaration(..),
    value collectExternalSymbols,
    value collectExternalLibraries,
    value declareExternalSymbols,
    value lookupExternalSymbol,
    value emitCallExternal,
    value emitAllocFfiOut,
    value emitLoadFfiOut,
)

// One declared C symbol: the name a call site names it by, the LLVM function value, and its
// function type (needed at every call, since LLVM's typed call takes the callee's type).
type ExternalSymbolDeclaration =
    | externalSymbolName: Str
    | externalSymbolValue: LLVMValueRef
    | externalSymbolType: LLVMTypeRef

// The native type an ABI position has. Buffers, out slots and native strings use pointers;
// argument marshalling below constructs the buffer storage before the call.
let recursive externalAbiLlvmType context types (abiType: ExternalAbiType) =
    match abiType with
        | ExternalAbiInt -> types.i64
        | ExternalAbiUInt(bits) ->
            if bits == 8
            then types.i8
            else
                if bits == 16
                then intType(context)(16u32)
                else
                    if bits == 32
                    then types.i32
                    else
                        if bits == 64
                        then types.i64
                        else Ashes.IO.panic("codegen: unsupported unsigned FFI width " + Ashes.Text.fromInt(bits))
        | ExternalAbiFloat64 -> doubleType(context)
        | ExternalAbiFloat32 -> floatType(context)
        | ExternalAbiBool -> types.i8
        | ExternalAbiString -> types.ptrType
        | ExternalAbiOpaque(_name) -> types.ptrType
        | ExternalAbiPointer(_element) -> types.ptrType
        | ExternalAbiBuffer(_element) -> types.ptrType
        | ExternalAbiOut(_element) -> types.ptrType
        | ExternalAbiNativeString(_nullable, _ownership, _free) -> types.ptrType
        | ExternalAbiVoid -> voidType(context)

let recursive externalAbiLlvmTypes context types (abiTypes: List(ExternalAbiType)) =
    match abiTypes with
        | [] -> []
        | abiType :: rest -> externalAbiLlvmType(context)(types)(abiType) :: externalAbiLlvmTypes(context)(types)(rest)

// The lowering owns null-to-None conversion and tuple assembly. These operations only pass
// a null-initialized native pointer slot and read its pointer word after the call.
let emitAllocFfiOut context builder types elementType =
    (let nativeType = externalAbiLlvmType(context)(types)(elementType)
    in
        let slot = buildEntryAlloca(builder)(nativeType)("ffi_out_slot")
        in
            let _ =
                buildStore(builder)(constNull(nativeType))(slot)
            in buildPtrToInt(builder)(slot)(types.i64)("ffi_out_slot_address"))

let emitLoadFfiOut context builder types slotAddress elementType =
    (let nativeType = externalAbiLlvmType(context)(types)(elementType)
    in
        let slot = buildIntToPtr(builder)(slotAddress)(types.ptrType)("ffi_out_slot_pointer")
        in
            let value = buildLoad(builder)(nativeType)(slot)("ffi_out_value")
            in buildPtrToInt(builder)(value)(types.i64)("ffi_out_value_word"))

let emitExternalBufferCount context function_ builder types value cursorSlot countSlot =
    (let loop = appendBasicBlock(context)(function_)("ffi_buffer_count_loop")
    in
        let body = appendBasicBlock(context)(function_)("ffi_buffer_count_body")
        in
            let doneBlock = appendBasicBlock(context)(function_)("ffi_buffer_count_done")
            in
                let _ = buildStore(builder)(value)(cursorSlot)
                in
                    let _ =
                        buildStore(builder)(constInt(types.i64)(0u64)(false))(countSlot)
                    in
                        let _ = buildBr(builder)(loop)
                        in
                            let _ = positionBuilderAtEnd(builder)(loop)
                            in
                                let cursor = buildLoad(builder)(types.i64)(cursorSlot)("ffi_buffer_count_cursor")
                                in
                                    let atEnd =
                                        buildICmp(builder)(intPredicateEq)(cursor)(constInt(types.i64)(0u64)(false))("ffi_buffer_count_at_end")
                                    in
                                        let _ = buildCondBr(builder)(atEnd)(doneBlock)(body)
                                        in
                                            let _ = positionBuilderAtEnd(builder)(body)
                                            in
                                                let count = buildLoad(builder)(types.i64)(countSlot)("ffi_buffer_count_value")
                                                in
                                                    let _ =
                                                        buildStore(builder)(buildAdd(builder)(count)(constInt(types.i64)(1u64)(false))("ffi_buffer_count_next"))(countSlot)
                                                    in
                                                        let tailPointer = memOffsetPtr(builder)(types.i64)(types.i8)(types.ptrType)(cursor)(8)("ffi_buffer_count_tail_ptr")
                                                        in
                                                            let _ =
                                                                buildStore(builder)(buildLoad(builder)(types.i64)(tailPointer)("ffi_buffer_count_tail"))(cursorSlot)
                                                            in
                                                                let _ = buildBr(builder)(loop)
                                                                in
                                                                    let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                    in buildLoad(builder)(types.i64)(countSlot)("ffi_buffer_final_count"))

let emitExternalBufferFill context function_ builder types value buffer elementType cursorSlot indexSlot =
    (let loop = appendBasicBlock(context)(function_)("ffi_buffer_fill_loop")
    in
        let body = appendBasicBlock(context)(function_)("ffi_buffer_fill_body")
        in
            let doneBlock = appendBasicBlock(context)(function_)("ffi_buffer_fill_done")
            in
                let _ = buildStore(builder)(value)(cursorSlot)
                in
                    let _ =
                        buildStore(builder)(constInt(types.i64)(0u64)(false))(indexSlot)
                    in
                        let _ = buildBr(builder)(loop)
                        in
                            let _ = positionBuilderAtEnd(builder)(loop)
                            in
                                let cursor = buildLoad(builder)(types.i64)(cursorSlot)("ffi_buffer_fill_cursor")
                                in
                                    let atEnd =
                                        buildICmp(builder)(intPredicateEq)(cursor)(constInt(types.i64)(0u64)(false))("ffi_buffer_fill_at_end")
                                    in
                                        let _ = buildCondBr(builder)(atEnd)(doneBlock)(body)
                                        in
                                            let _ = positionBuilderAtEnd(builder)(body)
                                            in
                                                let index = buildLoad(builder)(types.i64)(indexSlot)("ffi_buffer_index")
                                                in
                                                    let destination = buildGEP(builder)(elementType)(buffer)([index])(1u32)("ffi_buffer_element_ptr")
                                                    in
                                                        let headPointer = memOffsetPtr(builder)(types.i64)(types.i8)(types.ptrType)(cursor)(0)("ffi_buffer_head_ptr")
                                                        in
                                                            let handleWord = buildLoad(builder)(types.i64)(headPointer)("ffi_buffer_handle")
                                                            in
                                                                let _ =
                                                                    buildStore(builder)(buildIntToPtr(builder)(handleWord)(elementType)("ffi_buffer_handle_ptr"))(destination)
                                                                in
                                                                    let _ =
                                                                        buildStore(builder)(buildAdd(builder)(index)(constInt(types.i64)(1u64)(false))("ffi_buffer_index_next"))(indexSlot)
                                                                    in
                                                                        let tailPointer = memOffsetPtr(builder)(types.i64)(types.i8)(types.ptrType)(cursor)(8)("ffi_buffer_fill_tail_ptr")
                                                                        in
                                                                            let _ =
                                                                                buildStore(builder)(buildLoad(builder)(types.i64)(tailPointer)("ffi_buffer_fill_tail"))(cursorSlot)
                                                                            in
                                                                                let _ = buildBr(builder)(loop)
                                                                                in positionBuilderAtEnd(builder)(doneBlock))

// Mirrors stage 0's count, stack allocation and fill walks. The list's handles are borrowed;
// this temporary native array neither retains nor releases them.
let emitExternalBuffer context builder types value element =
    (let function_ = builderFunction(builder)
    in
        let cursorSlot = buildEntryAlloca(builder)(types.i64)("ffi_buffer_cursor")
        in
            let countSlot = buildEntryAlloca(builder)(types.i64)("ffi_buffer_count")
            in
                let count = emitExternalBufferCount(context)(function_)(builder)(types)(value)(cursorSlot)(countSlot)
                in
                    let isEmpty =
                        buildICmp(builder)(intPredicateEq)(count)(constInt(types.i64)(0u64)(false))("ffi_buffer_is_empty")
                    in
                        let allocationCount =
                            buildSelect(builder)(isEmpty)(constInt(types.i64)(1u64)(false))(count)("ffi_buffer_allocation_count")
                        in
                            let elementType = externalAbiLlvmType(context)(types)(element)
                            in
                                let buffer = buildArrayAlloca(builder)(elementType)(allocationCount)("ffi_buffer_storage")
                                in
                                    let indexSlot = buildEntryAlloca(builder)(types.i64)("ffi_buffer_index")
                                    in
                                        let _ = emitExternalBufferFill(context)(function_)(builder)(types)(value)(buffer)(elementType)(cursorSlot)(indexSlot)
                                        in
                                            let nullPointer =
                                                buildIntToPtr(builder)(constInt(types.i64)(0u64)(false))(types.ptrType)("ffi_buffer_null")
                                            in buildSelect(builder)(isEmpty)(nullPointer)(buffer)("ffi_buffer_argument"))

// The `i64` word a temp holds, converted to what the position declares: a float is the word's bits
// read as a double, a narrower integer is truncated, and every pointer-shaped position is the word
// as an address.
let convertExternalArgument context builder types value (abiType: ExternalAbiType) =
    match abiType with
        | ExternalAbiFloat64 ->
            buildBitCast(builder)(value)(doubleType(context))("ffi_arg_float")
        | ExternalAbiFloat32 ->
            buildFPTrunc(builder)(buildBitCast(builder)(value)(doubleType(context))("ffi_arg_f32_source"))(floatType(context))("ffi_arg_f32")
        | ExternalAbiBool -> buildTrunc(builder)(value)(types.i8)("ffi_arg_bool")
        | ExternalAbiUInt(bits) ->
            if bits == 64
            then value
            else
                buildTrunc(builder)(value)(externalAbiLlvmType(context)(types)(abiType))("ffi_arg_uint")
        | ExternalAbiString -> buildIntToPtr(builder)(value)(types.ptrType)("ffi_arg_str")
        | ExternalAbiOpaque(_name) -> buildIntToPtr(builder)(value)(types.ptrType)("ffi_arg_ptr")
        | ExternalAbiPointer(_element) -> buildIntToPtr(builder)(value)(types.ptrType)("ffi_arg_ptr")
        | ExternalAbiBuffer(element) -> emitExternalBuffer(context)(builder)(types)(value)(element)
        | ExternalAbiOut(_element) -> buildIntToPtr(builder)(value)(types.ptrType)("ffi_out_argument")
        | ExternalAbiNativeString(_nullable, _ownership, _free) -> Ashes.IO.panic("codegen: an external native-string parameter needs CopyFfiString, the native-string slice of CG-11")
        | _ -> value

let recursive convertExternalArguments context builder types values (abiTypes: List(ExternalAbiType)) =
    match (values, abiTypes) with
        | (value :: restValues, abiType :: restTypes) -> convertExternalArgument(context)(builder)(types)(value)(abiType) :: convertExternalArguments(context)(builder)(types)(restValues)(restTypes)
        | _ -> []

// The call's result as the universal `i64` word: a `void` call has none and yields zero, a float
// is widened to a double and read as bits, a narrower integer is zero-extended, and a pointer is
// its address.
let normalizeExternalResult context builder types result (returnType: ExternalAbiType) =
    match returnType with
        | ExternalAbiVoid -> constInt(types.i64)(0u64)(false)
        | ExternalAbiFloat32 ->
            buildBitCast(builder)(buildFPExt(builder)(result)(doubleType(context))("ffi_ret_f32"))(types.i64)("ffi_ret_f32_bits")
        | ExternalAbiFloat64 -> buildBitCast(builder)(result)(types.i64)("ffi_ret_f64_bits")
        | ExternalAbiBool -> buildZExt(builder)(result)(types.i64)("ffi_ret_bool")
        | ExternalAbiUInt(bits) ->
            if bits == 64
            then result
            else buildZExt(builder)(result)(types.i64)("ffi_ret_uint")
        | ExternalAbiString -> buildPtrToInt(builder)(result)(types.i64)("ffi_ret_str")
        | ExternalAbiOpaque(_name) -> buildPtrToInt(builder)(result)(types.i64)("ffi_ret_ptr")
        | ExternalAbiPointer(_element) -> buildPtrToInt(builder)(result)(types.i64)("ffi_ret_ptr")
        | ExternalAbiNativeString(_nullable, _ownership, _free) -> buildPtrToInt(builder)(result)(types.i64)("ffi_ret_native_string")
        | _ -> result

// LLVM's C API counts parameters and arguments in `unsigned`, and there is no Int-to-`u32`
// conversion in the shipped surface (`Ashes.Number.UInt` narrows to `u8` and widens to `u64`), so
// the arities an `external` declaration can have are spelled out. A signature wider than this is
// beyond any C ABI a declaration in this compiler names.
let externalArity (count: Int) =
    match count with
        | 0 -> 0u32
        | 1 -> 1u32
        | 2 -> 2u32
        | 3 -> 3u32
        | 4 -> 4u32
        | 5 -> 5u32
        | 6 -> 6u32
        | 7 -> 7u32
        | 8 -> 8u32
        | 9 -> 9u32
        | 10 -> 10u32
        | 11 -> 11u32
        | 12 -> 12u32
        | _ -> Ashes.IO.panic("codegen: an external signature of " + Ashes.Text.fromInt(count) + " parameters is beyond the supported arities")

let recursive lookupExternalSymbol (name: Str) (declarations: List(ExternalSymbolDeclaration)) =
    match declarations with
        | [] -> Ashes.IO.panic("codegen: external symbol '" + name + "' was not declared for this module")
        | (ExternalSymbolDeclaration { externalSymbolName = candidate } as declaration) :: rest ->
            if candidate == name
            then declaration
            else lookupExternalSymbol(name)(rest)

let recursive containsExternalSymbol (name: Str) (signatures: List((Str, List(ExternalAbiType), ExternalAbiType))) =
    match signatures with
        | [] -> false
        | (candidate, _parameters, _result) :: rest -> candidate == name || containsExternalSymbol(name)(rest)

let recursive collectInstructionExternalSymbols instructions (signatures: List((Str, List(ExternalAbiType), ExternalAbiType))) =
    match instructions with
        | [] -> signatures
        | IrInstruction { instruction = CopyFfiString(_, _, ExternalAbiNativeString(_, ExternalNativeStringOwned, Some(ExternalSymbolReference { symbolName = name }))) } :: rest ->
            if containsExternalSymbol(name)(signatures)
            then collectInstructionExternalSymbols(rest)(signatures)
            else
                [(name, [ExternalAbiPointer(ExternalAbiUInt(8))], ExternalAbiVoid)]
                |> append(signatures)
                |> collectInstructionExternalSymbols(rest)
        | IrInstruction { instruction = CallExternal(_target, symbolName, _library, _argTemps, parameterTypes, returnType) } :: rest ->
            if containsExternalSymbol(symbolName)(signatures)
            then collectInstructionExternalSymbols(rest)(signatures)
            else
                [(symbolName, parameterTypes, returnType)]
                |> append(signatures)
                |> collectInstructionExternalSymbols(rest)
        | _ :: rest -> collectInstructionExternalSymbols(rest)(signatures)

// Every `external` symbol the program calls, in first-appearance order, each with the signature of
// its first call site (an `external` declaration has exactly one ABI, so every later call to the
// same name repeats it).
let recursive collectExternalSymbols functions (signatures: List((Str, List(ExternalAbiType), ExternalAbiType))) =
    match functions with
        | [] -> signatures
        | IrFunction { instructions = instructions } :: rest ->
            signatures
            |> collectInstructionExternalSymbols(instructions)
            |> collectExternalSymbols(rest)

// Keep declaration-provided libraries separate from runtime defaults. The linker chooses only
// undefined symbols actually present in the object, so unused declarations add no dependency.
let recursive collectExternalLibraries (functions: List(ExternalFunctionAbi)) =
    match functions with
        | [] -> []
        | ExternalFunctionAbi { symbol = ExternalSymbolReference { symbolName = name, libraryName = Some(library) } } :: rest -> (name, library) :: collectExternalLibraries(rest)
        | _ :: rest -> collectExternalLibraries(rest)

let recursive lookupDeclaredSymbol (name: Str) (declared: List((Str, LLVMValueRef))) =
    match declared with
        | [] -> None
        | (candidate, value) :: rest ->
            if candidate == name
            then Some(value)
            else lookupDeclaredSymbol(name)(rest)

// One LLVM function per C symbol: a name the runtime already declared for its own use (`strlen`,
// `memcpy`, `getenv`, ...) is reused rather than declared again, since a second declaration of the
// same name gets renamed by LLVM (`strlen.1`) and then resolves against nothing at link time. The
// call still goes through the type the program's own `external` declares, which an opaque-pointer
// module allows: a function value is a symbol reference, and the call carries its own signature.
let recursive declareExternalSymbols module_ context types (declared: List((Str, LLVMValueRef))) (signatures: List((Str, List(ExternalAbiType), ExternalAbiType))) =
    match signatures with
        | [] -> []
        | (symbolName, parameterTypes, returnType) :: rest ->
            let functionTypeRef =
                functionType(externalAbiLlvmType(context)(types)(returnType))(externalAbiLlvmTypes(context)(types)(parameterTypes))(parameterTypes
                |> length
                |> externalArity)(false)
            in
                let symbolValue =
                    match lookupDeclaredSymbol(symbolName)(declared) with
                        | Some(value) -> value
                        | None -> addFunction(module_)(symbolName)(functionTypeRef)
                in
                    ExternalSymbolDeclaration(
                        externalSymbolName = symbolName,
                        externalSymbolValue = symbolValue,
                        externalSymbolType = functionTypeRef
                    ) :: declareExternalSymbols(module_)(context)(types)(declared)(rest)

// One call to a declared symbol: the argument words converted in ABI order, the call itself, and
// the result back as an `i64` word. A `void` call takes no result name, since LLVM rejects one.
let emitCallExternal context builder types (declaration: ExternalSymbolDeclaration) argumentValues (parameterTypes: List(ExternalAbiType)) (returnType: ExternalAbiType) =
    (let arguments = convertExternalArguments(context)(builder)(types)(argumentValues)(parameterTypes)
    in
        let resultName =
            match returnType with
                | ExternalAbiVoid -> ""
                | _ -> "ffi_call"
        in
            resultName
            |> buildCall(builder)(declaration.externalSymbolType)(declaration.externalSymbolValue)(arguments)(arguments
            |> length
            |> externalArity)
            |> (given (result) -> normalizeExternalResult(context)(builder)(types)(result)(returnType)))
