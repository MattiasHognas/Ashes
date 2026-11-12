// Native string results mirror stage 0's EmitCopyFfiString: bounded NUL scan, strict UTF-8
// validation, copy into the compiler-managed arena, then dispose an owned non-null pointer once.
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Arena
import AshesCompiler.Backend.IrCodegen.Support
import AshesCompiler.Backend.IrCodegen.External
import AshesCompiler.Semantics.ExternalAbi
export (
    value emitCopyFfiString,
)

type NativeStringEmitter =
    | context: LLVMContextRef
    | function_: LLVMValueRef
    | builder: LLVMBuilderRef
    | types: CoreLlvmTypes
    | arena: ArenaRuntime
    | memcpyFn: LLVMValueRef
    | memcpyType: LLVMTypeRef

let nativeWord (cx: NativeStringEmitter) (value: Int) =
    (let types = cx.types
    in
        constInt(types.i64)(Ashes.Number.UInt.fromInt64(value))(false))

let nativeRange (cx: NativeStringEmitter) value lower upper name =
    buildAnd(cx.builder)(buildICmp(cx.builder)(intPredicateUge)(value)(nativeWord(cx)(lower))(name + "_min"))(buildICmp(cx.builder)(intPredicateUle)(value)(nativeWord(cx)(upper))(name + "_max"))(name)

// One byte per iteration. A lead byte sets the remaining continuation count and the first
// continuation's bounds; subsequent continuations use 80..BF. These special bounds reject
// overlong encodings, surrogate scalars and values above U+10FFFF without reading past length.
let emitNativeUtf8Validation (cx: NativeStringEmitter) pointer length =
    (let types = cx.types
    in
        let builder = cx.builder
        in
            let i64 = types.i64
            in
                let indexSlot = buildEntryAlloca(builder)(i64)("ffi_utf8_index")
                in
                    let remainingSlot = buildEntryAlloca(builder)(i64)("ffi_utf8_remaining")
                    in
                        let lowerSlot = buildEntryAlloca(builder)(i64)("ffi_utf8_lower")
                        in
                            let upperSlot = buildEntryAlloca(builder)(i64)("ffi_utf8_upper")
                            in
                                let resultSlot = buildEntryAlloca(builder)(i64)("ffi_utf8_result")
                                in
                                    let loop = appendBasicBlock(cx.context)(cx.function_)("ffi_utf8_loop")
                                    in
                                        let body = appendBasicBlock(cx.context)(cx.function_)("ffi_utf8_body")
                                        in
                                            let lead = appendBasicBlock(cx.context)(cx.function_)("ffi_utf8_lead")
                                            in
                                                let continuation = appendBasicBlock(cx.context)(cx.function_)("ffi_utf8_continuation")
                                                in
                                                    let advance = appendBasicBlock(cx.context)(cx.function_)("ffi_utf8_advance")
                                                    in
                                                        let finish = appendBasicBlock(cx.context)(cx.function_)("ffi_utf8_finish")
                                                        in
                                                            let invalid = appendBasicBlock(cx.context)(cx.function_)("ffi_utf8_invalid")
                                                            in
                                                                let doneBlock = appendBasicBlock(cx.context)(cx.function_)("ffi_utf8_done")
                                                                in
                                                                    Unit
                                                                    |> (given (_) ->
                                                                        buildStore(builder)(nativeWord(cx)(0))(indexSlot))
                                                                    |> (given (_) ->
                                                                        buildStore(builder)(nativeWord(cx)(0))(remainingSlot))
                                                                    |> (given (_) ->
                                                                        buildStore(builder)(nativeWord(cx)(128))(lowerSlot))
                                                                    |> (given (_) ->
                                                                        buildStore(builder)(nativeWord(cx)(191))(upperSlot))
                                                                    |> (given (_) ->
                                                                        buildStore(builder)(nativeWord(cx)(1))(resultSlot))
                                                                    |> (given (_) -> buildBr(builder)(loop))
                                                                    |> (given (_) -> positionBuilderAtEnd(builder)(loop))
                                                                    |> (given (_) ->
                                                                        let index = buildLoad(builder)(i64)(indexSlot)("ffi_utf8_position")
                                                                        in
                                                                            let exhausted = buildICmp(builder)(intPredicateUge)(index)(length)("ffi_utf8_exhausted")
                                                                            in
                                                                                let _ = buildCondBr(builder)(exhausted)(finish)(body)
                                                                                in
                                                                                    let _ = positionBuilderAtEnd(builder)(body)
                                                                                    in
                                                                                        let address = buildGEP(builder)(types.i8)(pointer)([index])(1u32)("ffi_utf8_byte_ptr")
                                                                                        in
                                                                                            let byte =
                                                                                                buildZExt(builder)(buildLoad(builder)(types.i8)(address)("ffi_utf8_byte"))(i64)("ffi_utf8_byte_word")
                                                                                            in
                                                                                                let remaining = buildLoad(builder)(i64)(remainingSlot)("ffi_utf8_pending")
                                                                                                in
                                                                                                    let _ =
                                                                                                        buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(remaining)(nativeWord(cx)(0))("ffi_utf8_needs_lead"))(lead)(continuation)
                                                                                                    in
                                                                                                        let _ = positionBuilderAtEnd(builder)(lead)
                                                                                                        in
                                                                                                            let ascii =
                                                                                                                buildICmp(builder)(intPredicateUle)(byte)(nativeWord(cx)(127))("ffi_utf8_ascii")
                                                                                                            in
                                                                                                                let validLead =
                                                                                                                    buildOr(builder)(ascii)(nativeRange(cx)(byte)(194)(244)("ffi_utf8_lead_range"))("ffi_utf8_valid_lead")
                                                                                                                in
                                                                                                                    let two =
                                                                                                                        buildICmp(builder)(intPredicateUle)(byte)(nativeWord(cx)(223))("ffi_utf8_two")
                                                                                                                    in
                                                                                                                        let three =
                                                                                                                            buildICmp(builder)(intPredicateUle)(byte)(nativeWord(cx)(239))("ffi_utf8_three")
                                                                                                                        in
                                                                                                                            let count =
                                                                                                                                buildSelect(builder)(ascii)(nativeWord(cx)(0))(buildSelect(builder)(two)(nativeWord(cx)(1))(buildSelect(builder)(three)(nativeWord(cx)(2))(nativeWord(cx)(3))("ffi_utf8_long_count"))("ffi_utf8_non_ascii_count"))("ffi_utf8_count")
                                                                                                                            in
                                                                                                                                let isE0 =
                                                                                                                                    buildICmp(builder)(intPredicateEq)(byte)(nativeWord(cx)(224))("ffi_utf8_e0")
                                                                                                                                in
                                                                                                                                    let isED =
                                                                                                                                        buildICmp(builder)(intPredicateEq)(byte)(nativeWord(cx)(237))("ffi_utf8_ed")
                                                                                                                                    in
                                                                                                                                        let isF0 =
                                                                                                                                            buildICmp(builder)(intPredicateEq)(byte)(nativeWord(cx)(240))("ffi_utf8_f0")
                                                                                                                                        in
                                                                                                                                            let isF4 =
                                                                                                                                                buildICmp(builder)(intPredicateEq)(byte)(nativeWord(cx)(244))("ffi_utf8_f4")
                                                                                                                                            in
                                                                                                                                                let lower =
                                                                                                                                                    buildSelect(builder)(isE0)(nativeWord(cx)(160))(buildSelect(builder)(isF0)(nativeWord(cx)(144))(nativeWord(cx)(128))("ffi_utf8_lower_four"))("ffi_utf8_lead_lower")
                                                                                                                                                in
                                                                                                                                                    let upper =
                                                                                                                                                        buildSelect(builder)(isED)(nativeWord(cx)(159))(buildSelect(builder)(isF4)(nativeWord(cx)(143))(nativeWord(cx)(191))("ffi_utf8_upper_four"))("ffi_utf8_lead_upper")
                                                                                                                                                    in
                                                                                                                                                        Unit
                                                                                                                                                        |> (given (_) -> buildStore(builder)(count)(remainingSlot))
                                                                                                                                                        |> (given (_) -> buildStore(builder)(lower)(lowerSlot))
                                                                                                                                                        |> (given (_) -> buildStore(builder)(upper)(upperSlot))
                                                                                                                                                        |> (given (_) -> buildCondBr(builder)(validLead)(advance)(invalid))
                                                                                                                                                        |> (given (_) -> positionBuilderAtEnd(builder)(continuation))
                                                                                                                                                        |> (given (_) ->
                                                                                                                                                            let minimum = buildLoad(builder)(i64)(lowerSlot)("ffi_utf8_minimum")
                                                                                                                                                            in
                                                                                                                                                                let maximum = buildLoad(builder)(i64)(upperSlot)("ffi_utf8_maximum")
                                                                                                                                                                in
                                                                                                                                                                    let accepted =
                                                                                                                                                                        buildAnd(builder)(buildICmp(builder)(intPredicateUge)(byte)(minimum)("ffi_utf8_above_min"))(buildICmp(builder)(intPredicateUle)(byte)(maximum)("ffi_utf8_below_max"))("ffi_utf8_accepted")
                                                                                                                                                                    in
                                                                                                                                                                        Unit
                                                                                                                                                                        |> (given (_) ->
                                                                                                                                                                            buildStore(builder)(buildSub(builder)(remaining)(nativeWord(cx)(1))("ffi_utf8_remaining_next"))(remainingSlot))
                                                                                                                                                                        |> (given (_) ->
                                                                                                                                                                            buildStore(builder)(nativeWord(cx)(128))(lowerSlot))
                                                                                                                                                                        |> (given (_) ->
                                                                                                                                                                            buildStore(builder)(nativeWord(cx)(191))(upperSlot))
                                                                                                                                                                        |> (given (_) -> buildCondBr(builder)(accepted)(advance)(invalid)))
                                                                                                                                                        |> (given (_) -> positionBuilderAtEnd(builder)(advance))
                                                                                                                                                        |> (given (_) ->
                                                                                                                                                            buildStore(builder)(buildAdd(builder)(index)(nativeWord(cx)(1))("ffi_utf8_next"))(indexSlot))
                                                                                                                                                        |> (given (_) -> buildBr(builder)(loop)))
                                                                    |> (given (_) -> positionBuilderAtEnd(builder)(finish))
                                                                    |> (given (_) ->
                                                                        buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(buildLoad(builder)(i64)(remainingSlot)("ffi_utf8_final_pending"))(nativeWord(cx)(0))("ffi_utf8_complete"))(doneBlock)(invalid))
                                                                    |> (given (_) -> positionBuilderAtEnd(builder)(invalid))
                                                                    |> (given (_) ->
                                                                        buildStore(builder)(nativeWord(cx)(0))(resultSlot))
                                                                    |> (given (_) -> buildBr(builder)(doneBlock))
                                                                    |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))
                                                                    |> (given (_) -> buildLoad(builder)(i64)(resultSlot)("ffi_utf8_valid")))

let nativeStringError (cx: NativeStringEmitter) codes name =
    (let types = cx.types
    in
        let message = emitAsciiLiteralString(cx.context)(cx.builder)(types.i64)(types.i8)(codes)(name + "_message")
        in emitArenaResultAdt(cx.context)(cx.function_)(cx.builder)(types.i64)(types.i8)(types.ptrType)(cx.arena)(1)(message)(name))

let nativeStringSuccess (cx: NativeStringEmitter) pointer length (nullable: Bool) =
    (let types = cx.types
    in
        let copied = emitArenaStringFromBytesAddr(cx.context)(cx.function_)(cx.builder)(types.i64)(types.i8)(types.ptrType)(cx.arena)(cx.memcpyFn)(cx.memcpyType)(pointer)(length)("ffi_string_copy")
        in
            let value =
                if nullable
                then emitArenaSome(cx.context)(cx.function_)(cx.builder)(types.i64)(types.i8)(types.ptrType)(cx.arena)(copied)("ffi_string_some")
                else copied
            in emitArenaResultAdt(cx.context)(cx.function_)(cx.builder)(types.i64)(types.i8)(types.ptrType)(cx.arena)(0)(value)("ffi_string_ok"))

let nativeStringNullCodes = [78, 97, 116, 105, 118, 101, 32, 85, 84, 70, 45, 56, 32, 115, 116, 114, 105, 110, 103, 32, 119, 97, 115, 32, 110, 117, 108, 108, 46]

let nativeStringInvalidCodes = [78, 97, 116, 105, 118, 101, 32, 85, 84, 70, 45, 56, 32, 115, 116, 114, 105, 110, 103, 32, 105, 115, 32, 105, 110, 118, 97, 108, 105, 100, 46]

let nativeStringLongCodes = [78, 97, 116, 105, 118, 101, 32, 85, 84, 70, 45, 56, 32, 115, 116, 114, 105, 110, 103, 32, 101, 120, 99, 101, 101, 100, 115, 32, 49, 48, 55, 51, 55, 52, 49, 56, 50, 52, 32, 98, 121, 116, 101, 115, 46]

let emitNativeString (cx: NativeStringEmitter) pointerAddress (nullable: Bool) ownership destructor symbols =
    (let types = cx.types
    in
        let builder = cx.builder
        in
            let i64 = types.i64
            in
                let pointer = buildIntToPtr(builder)(pointerAddress)(types.ptrType)("ffi_string_pointer")
                in
                    let resultSlot = buildEntryAlloca(builder)(i64)("ffi_string_result")
                    in
                        let lengthSlot = buildEntryAlloca(builder)(i64)("ffi_string_length")
                        in
                            let nullBlock = appendBasicBlock(cx.context)(cx.function_)("ffi_string_null")
                            in
                                let scan = appendBasicBlock(cx.context)(cx.function_)("ffi_string_scan")
                                in
                                    let scanByte = appendBasicBlock(cx.context)(cx.function_)("ffi_string_scan_byte")
                                    in
                                        let validate = appendBasicBlock(cx.context)(cx.function_)("ffi_string_validate")
                                        in
                                            let tooLong = appendBasicBlock(cx.context)(cx.function_)("ffi_string_too_long")
                                            in
                                                let valid = appendBasicBlock(cx.context)(cx.function_)("ffi_string_valid")
                                                in
                                                    let invalid = appendBasicBlock(cx.context)(cx.function_)("ffi_string_invalid")
                                                    in
                                                        let release = appendBasicBlock(cx.context)(cx.function_)("ffi_string_release")
                                                        in
                                                            let doneBlock = appendBasicBlock(cx.context)(cx.function_)("ffi_string_done")
                                                            in
                                                                Unit
                                                                |> (given (_) ->
                                                                    buildStore(builder)(nativeWord(cx)(0))(lengthSlot))
                                                                |> (given (_) ->
                                                                    buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(pointerAddress)(nativeWord(cx)(0))("ffi_string_is_null"))(nullBlock)(scan))
                                                                |> (given (_) -> positionBuilderAtEnd(builder)(nullBlock))
                                                                |> (given (_) ->
                                                                    let nullResult =
                                                                        if nullable
                                                                        then
                                                                            let none = emitArenaNone(cx.context)(cx.function_)(builder)(i64)(types.i8)(types.ptrType)(cx.arena)("ffi_string_none")
                                                                            in emitArenaResultAdt(cx.context)(cx.function_)(builder)(i64)(types.i8)(types.ptrType)(cx.arena)(0)(none)("ffi_string_ok_none")
                                                                        else nativeStringError(cx)(nativeStringNullCodes)("ffi_string_null_error")
                                                                    in buildStore(builder)(nullResult)(resultSlot))
                                                                |> (given (_) -> buildBr(builder)(doneBlock))
                                                                |> (given (_) -> positionBuilderAtEnd(builder)(scan))
                                                                |> (given (_) ->
                                                                    let length = buildLoad(builder)(i64)(lengthSlot)("ffi_string_scan_length")
                                                                    in
                                                                        let _ =
                                                                            buildCondBr(builder)(buildICmp(builder)(intPredicateUge)(length)(nativeWord(cx)(1073741824))("ffi_string_at_limit"))(tooLong)(scanByte)
                                                                        in
                                                                            let _ = positionBuilderAtEnd(builder)(scanByte)
                                                                            in
                                                                                let bytePointer = buildGEP(builder)(types.i8)(pointer)([length])(1u32)("ffi_string_byte_ptr")
                                                                                in
                                                                                    let byte = buildLoad(builder)(types.i8)(bytePointer)("ffi_string_byte")
                                                                                    in
                                                                                        let _ =
                                                                                            buildStore(builder)(buildAdd(builder)(length)(nativeWord(cx)(1))("ffi_string_next_length"))(lengthSlot)
                                                                                        in
                                                                                            buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(byte)(constInt(types.i8)(0u64)(false))("ffi_string_terminated"))(validate)(scan))
                                                                |> (given (_) -> positionBuilderAtEnd(builder)(validate))
                                                                |> (given (_) ->
                                                                    let length =
                                                                        buildSub(builder)(buildLoad(builder)(i64)(lengthSlot)("ffi_string_length_with_nul"))(nativeWord(cx)(1))("ffi_string_final_length")
                                                                    in
                                                                        let accepted = emitNativeUtf8Validation(cx)(pointer)(length)
                                                                        in
                                                                            let _ =
                                                                                buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(accepted)(nativeWord(cx)(0))("ffi_string_utf8_valid"))(valid)(invalid)
                                                                            in
                                                                                let _ = positionBuilderAtEnd(builder)(valid)
                                                                                in
                                                                                    let _ =
                                                                                        buildStore(builder)(nativeStringSuccess(cx)(pointerAddress)(length)(nullable))(resultSlot)
                                                                                    in buildBr(builder)(release))
                                                                |> (given (_) -> positionBuilderAtEnd(builder)(invalid))
                                                                |> (given (_) ->
                                                                    buildStore(builder)(nativeStringError(cx)(nativeStringInvalidCodes)("ffi_string_invalid_error"))(resultSlot))
                                                                |> (given (_) -> buildBr(builder)(release))
                                                                |> (given (_) -> positionBuilderAtEnd(builder)(tooLong))
                                                                |> (given (_) ->
                                                                    buildStore(builder)(nativeStringError(cx)(nativeStringLongCodes)("ffi_string_long_error"))(resultSlot))
                                                                |> (given (_) -> buildBr(builder)(release))
                                                                |> (given (_) -> positionBuilderAtEnd(builder)(release))
                                                                |> (given (_) ->
                                                                    match ownership with
                                                                        | ExternalNativeStringBorrowed -> Unit
                                                                        | ExternalNativeStringOwned ->
                                                                            match destructor with
                                                                                | None -> Ashes.IO.panic("codegen: owned native string has no destructor")
                                                                                | Some(ExternalSymbolReference { symbolName = name }) ->
                                                                                    let declaration = lookupExternalSymbol(name)(symbols)
                                                                                    in
                                                                                        let _ = emitCallExternal(cx.context)(builder)(cx.types)(declaration)([pointerAddress])([ExternalAbiPointer(ExternalAbiUInt(8))])(ExternalAbiVoid)
                                                                                        in Unit)
                                                                |> (given (_) -> buildBr(builder)(doneBlock))
                                                                |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))
                                                                |> (given (_) -> buildLoad(builder)(i64)(resultSlot)("ffi_string_result_value")))

let emitCopyFfiString context builder types arena memcpyFn memcpyType symbols pointerAddress (abiType: ExternalAbiType) =
    (let cx = NativeStringEmitter(context = context, function_ = builderFunction(builder), builder = builder, types = types, arena = arena, memcpyFn = memcpyFn, memcpyType = memcpyType)
    in
        match abiType with
            | ExternalAbiNativeString(nullable, ownership, destructor) -> emitNativeString(cx)(pointerAddress)(nullable)(ownership)(destructor)(symbols)
            | _ -> Ashes.IO.panic("codegen: CopyFfiString requires a native-string ABI"))
