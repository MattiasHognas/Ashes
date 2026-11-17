// `Ashes.Text.Regex` over the vendored PCRE2 8-bit payload (stage 0's
// `LlvmCodegenBuiltins.Regex.cs`). The program's module declares the PCRE2 entry points it calls;
// the payload's bitcode, linked into the module after optimization, defines them. A compiled
// pattern is the `pcre2_code*` as one word. Results are arena values, as stage 0 builds them.
//
// Unlike stage 0, which gives PCRE2 a bump allocator over a region of its own, the payload's
// `malloc`/`free` are libc's here, the allocator the rest of this codegen already imports.

import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
import AshesCompiler.Backend.IrCodegen.Arena
import Ashes.Number.UInt
export (
    value declarePcre2Functions,
    value emitRegexCompile,
    value emitRegexCompileError,
    value emitRegexFind,
    value emitRegexCaptures,
    value emitRegexSubstitute,
)

// PCRE2_UTF | PCRE2_UCP: pattern and subject are UTF-8, and \d \w \s use Unicode properties.
let pcre2CompileOptions = 655360u64

// PCRE2_SUBSTITUTE_GLOBAL | PCRE2_SUBSTITUTE_OVERFLOW_LENGTH.
let pcre2SubstituteOptions = 4352u64

// PCRE2_ERROR_NOMEMORY (-48) as the i32 the call returns.
let pcre2ErrorNoMemory = 4294967248u64

// The PCRE2 entry points, each with its LLVM function type.
let declarePcre2Functions module_ i64 i32 ptrType =
    (let declare =
        given (name: Str) ->
            given (functionType_: LLVMTypeRef) -> (name, addFunction(module_)(name)(functionType_), functionType_)
    in
        [
            false
            |> functionType(ptrType)([ptrType, i64, i32, ptrType, ptrType, ptrType])(6u32)
            |> declare("pcre2_compile_8"),
            false
            |> functionType(i32)([i32, ptrType, i64])(3u32)
            |> declare("pcre2_get_error_message_8"),
            false
            |> functionType(ptrType)([ptrType, ptrType])(2u32)
            |> declare("pcre2_match_data_create_from_pattern_8"),
            false
            |> functionType(i32)([ptrType, ptrType, i64, i64, i32, ptrType, ptrType])(7u32)
            |> declare("pcre2_match_8"),
            false
            |> functionType(ptrType)([ptrType])(1u32)
            |> declare("pcre2_get_ovector_pointer_8"),
            false
            |> functionType(i32)([ptrType, ptrType, i64, i64, i32, ptrType, ptrType, ptrType, i64, ptrType, ptrType])(11u32)
            |> declare("pcre2_substitute_8")
        ])

let recursive pcre2Function (name: Str) (functions: List((Str, LLVMValueRef, LLVMTypeRef))) =
    match functions with
        | [] -> Ashes.IO.panic("codegen: undeclared PCRE2 function " + name)
        | (candidate, function_, functionType_) :: rest ->
            if candidate == name
            then (function_, functionType_)
            else pcre2Function(name)(rest)

let callPcre2 builder (pcre2: List((Str, LLVMValueRef, LLVMTypeRef))) (name: Str) (arguments: List(LLVMValueRef)) (count: u32) (resultName: Str) =
    match pcre2Function(name)(pcre2) with
        | (function_, functionType_) -> buildCall(builder)(functionType_)(function_)(arguments)(count)(resultName)

// pcre2_compile_8(pattern, length, options, &errorcode, &erroffset, NULL), the error slots the
// caller's.
let emitPcre2Compile builder i64 i32 ptrType pcre2 patternRef errorCodeSlot errorOffsetSlot =
    match emitStringParts(builder)(i64)(ptrType)(patternRef)("rx_pattern") with
        | (patternLength, patternAddr) -> callPcre2(builder)(pcre2)("pcre2_compile_8")([buildIntToPtr(builder)(patternAddr)(ptrType)("rx_pattern_ptr"), patternLength, constInt(i32)(pcre2CompileOptions)(false), errorCodeSlot, errorOffsetSlot, constNull(ptrType)])(6u32)("rx_code")

// `Regex.compile`'s handle: the compiled pattern, or 0 when it does not compile.
let emitRegexCompile builder i64 i32 ptrType pcre2 patternRef =
    (let errorCodeSlot = buildEntryAlloca(builder)(i32)("rx_errcode")
    in
        let errorOffsetSlot = buildEntryAlloca(builder)(i64)("rx_erroffset")
        in
            let code = emitPcre2Compile(builder)(i64)(i32)(ptrType)(pcre2)(patternRef)(errorCodeSlot)(errorOffsetSlot)
            in buildPtrToInt(builder)(code)(i64)("rx_code_word"))

// The compile error message of a pattern: empty when it compiles, else PCRE2's text for the code.
let emitRegexCompileError context function_ builder i64 i8 i32 ptrType (arena: ArenaRuntime) memcpyFn memcpyType pcre2 patternRef =
    (let errorCodeSlot = buildEntryAlloca(builder)(i32)("rx_err_errcode")
    in
        let errorOffsetSlot = buildEntryAlloca(builder)(i64)("rx_err_erroffset")
        in
            let resultSlot = buildEntryAlloca(builder)(i64)("rx_err_result")
            in
                let bufferType = arrayType(i8)(256u64)
                in
                    let buffer = buildEntryAlloca(builder)(bufferType)("rx_err_buffer")
                    in
                        let code = emitPcre2Compile(builder)(i64)(i32)(ptrType)(pcre2)(patternRef)(errorCodeSlot)(errorOffsetSlot)
                        in
                            let failed =
                                buildICmp(builder)(intPredicateEq)(buildPtrToInt(builder)(code)(i64)("rx_err_code_word"))(constInt(i64)(0u64)(false))("rx_err_failed")
                            in
                                let errorBlock = appendBasicBlock(context)(function_)("rx_err_error")
                                in
                                    let okBlock = appendBasicBlock(context)(function_)("rx_err_ok")
                                    in
                                        let doneBlock = appendBasicBlock(context)(function_)("rx_err_done")
                                        in
                                            let _ = buildCondBr(builder)(failed)(errorBlock)(okBlock)
                                            in
                                                let _ = positionBuilderAtEnd(builder)(okBlock)
                                                in
                                                    let empty =
                                                        emitArenaStringFromBytesAddr(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(memcpyFn)(memcpyType)(buildPtrToInt(builder)(buffer)(i64)("rx_err_empty_src"))(constInt(i64)(0u64)(false))("rx_err_empty")
                                                    in
                                                        let _ = buildStore(builder)(empty)(resultSlot)
                                                        in
                                                            let _ = buildBr(builder)(doneBlock)
                                                            in
                                                                let _ = positionBuilderAtEnd(builder)(errorBlock)
                                                                in
                                                                    let messageLength = callPcre2(builder)(pcre2)("pcre2_get_error_message_8")([buildLoad(builder)(i32)(errorCodeSlot)("rx_err_code"), buffer, constInt(i64)(256u64)(false)])(3u32)("rx_err_message_length")
                                                                    in
                                                                        let length64 = buildSExt(builder)(messageLength)(i64)("rx_err_message_length64")
                                                                        in
                                                                            let negative =
                                                                                buildICmp(builder)(intPredicateSlt)(length64)(constInt(i64)(0u64)(false))("rx_err_negative")
                                                                            in
                                                                                let clamped =
                                                                                    buildSelect(builder)(negative)(constInt(i64)(0u64)(false))(length64)("rx_err_length")
                                                                                in
                                                                                    let message =
                                                                                        emitArenaStringFromBytesAddr(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(memcpyFn)(memcpyType)(buildPtrToInt(builder)(buffer)(i64)("rx_err_buffer_addr"))(clamped)("rx_err_message")
                                                                                    in
                                                                                        let _ = buildStore(builder)(message)(resultSlot)
                                                                                        in
                                                                                            let _ = buildBr(builder)(doneBlock)
                                                                                            in
                                                                                                let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                                                in buildLoad(builder)(i64)(resultSlot)("rx_err_result_value"))

// One match from `start`: the return code and the ovector's address, as stage 0's `EmitPcre2Match`.
let emitPcre2Match builder i64 i32 ptrType pcre2 code subjectAddr subjectLength start =
    (let matchData = callPcre2(builder)(pcre2)("pcre2_match_data_create_from_pattern_8")([code, constNull(ptrType)])(2u32)("rx_match_data")
    in
        let returnCode = callPcre2(builder)(pcre2)("pcre2_match_8")([code, buildIntToPtr(builder)(subjectAddr)(ptrType)("rx_subject_ptr"), subjectLength, start, constInt(i32)(0u64)(false), matchData, constNull(ptrType)])(7u32)("rx_rc")
        in
            let ovector = callPcre2(builder)(pcre2)("pcre2_get_ovector_pointer_8")([matchData])(1u32)("rx_ovector")
            in (returnCode, buildPtrToInt(builder)(ovector)(i64)("rx_ovector_word")))

// `find(code, subject, start)`: `Some((start, end))` of the next match's byte offsets, else `None`.
let emitRegexFind context function_ builder i64 i8 i32 ptrType (arena: ArenaRuntime) pcre2 codeWord subjectRef start =
    (let resultSlot = buildEntryAlloca(builder)(i64)("rx_find_result")
    in
        let _ =
            buildStore(builder)(emitArenaNone(context)(function_)(builder)(i64)(i8)(ptrType)(arena)("rx_find_none"))(resultSlot)
        in
            match emitStringParts(builder)(i64)(ptrType)(subjectRef)("rx_find_subject") with
                | (subjectLength, subjectAddr) ->
                    match emitPcre2Match(builder)(i64)(i32)(ptrType)(pcre2)(buildIntToPtr(builder)(codeWord)(ptrType)("rx_find_code"))(subjectAddr)(subjectLength)(start) with
                        | (returnCode, ovector) ->
                            let matched =
                                buildICmp(builder)(intPredicateSge)(returnCode)(constInt(i32)(1u64)(false))("rx_find_matched")
                            in
                                let hitBlock = appendBasicBlock(context)(function_)("rx_find_hit")
                                in
                                    let doneBlock = appendBasicBlock(context)(function_)("rx_find_done")
                                    in
                                        let _ = buildCondBr(builder)(matched)(hitBlock)(doneBlock)
                                        in
                                            let _ = positionBuilderAtEnd(builder)(hitBlock)
                                            in
                                                let tuple = emitArenaAlloc(context)(function_)(builder)(i64)(arena)(16)("rx_find_tuple")
                                                in
                                                    let _ =
                                                        storeWordAt(builder)(i64)(i8)(ptrType)(tuple)(0)(loadWordAt(builder)(i64)(i8)(ptrType)(ovector)(0)("rx_find_start"))("rx_find_tuple_start")
                                                    in
                                                        let _ =
                                                            storeWordAt(builder)(i64)(i8)(ptrType)(tuple)(8)(loadWordAt(builder)(i64)(i8)(ptrType)(ovector)(8)("rx_find_end"))("rx_find_tuple_end")
                                                        in
                                                            let _ =
                                                                buildStore(builder)(emitArenaSome(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(tuple)("rx_find_some"))(resultSlot)
                                                            in
                                                                let _ = buildBr(builder)(doneBlock)
                                                                in
                                                                    let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                    in buildLoad(builder)(i64)(resultSlot)("rx_find_result_value"))

// `captures(code, subject, start)`: `Some` of every group's substring (`None` for an unset group),
// group 0 first, else `None`. The list is built from the last group back, one cons per group.
let emitRegexCaptures context function_ builder i64 i8 i32 ptrType (arena: ArenaRuntime) pcre2 codeWord subjectRef start =
    (let resultSlot = buildEntryAlloca(builder)(i64)("rx_cap_result")
    in
        let listSlot = buildEntryAlloca(builder)(i64)("rx_cap_list")
        in
            let indexSlot = buildEntryAlloca(builder)(i64)("rx_cap_index")
            in
                let elementSlot = buildEntryAlloca(builder)(i64)("rx_cap_element")
                in
                    let _ =
                        buildStore(builder)(emitArenaNone(context)(function_)(builder)(i64)(i8)(ptrType)(arena)("rx_cap_none"))(resultSlot)
                    in
                        match emitStringParts(builder)(i64)(ptrType)(subjectRef)("rx_cap_subject") with
                            | (subjectLength, subjectAddr) ->
                                match emitPcre2Match(builder)(i64)(i32)(ptrType)(pcre2)(buildIntToPtr(builder)(codeWord)(ptrType)("rx_cap_code"))(subjectAddr)(subjectLength)(start) with
                                    | (returnCode, ovector) ->
                                        let hitBlock = appendBasicBlock(context)(function_)("rx_cap_hit")
                                        in
                                            let loopCheckBlock = appendBasicBlock(context)(function_)("rx_cap_loop_check")
                                            in
                                                let loopBodyBlock = appendBasicBlock(context)(function_)("rx_cap_loop_body")
                                                in
                                                    let unsetBlock = appendBasicBlock(context)(function_)("rx_cap_unset")
                                                    in
                                                        let setBlock = appendBasicBlock(context)(function_)("rx_cap_set")
                                                        in
                                                            let consBlock = appendBasicBlock(context)(function_)("rx_cap_cons")
                                                            in
                                                                let loopDoneBlock = appendBasicBlock(context)(function_)("rx_cap_loop_done")
                                                                in
                                                                    let doneBlock = appendBasicBlock(context)(function_)("rx_cap_done")
                                                                    in
                                                                        let _ =
                                                                            buildCondBr(builder)(buildICmp(builder)(intPredicateSge)(returnCode)(constInt(i32)(1u64)(false))("rx_cap_matched"))(hitBlock)(doneBlock)
                                                                        in
                                                                            let _ = positionBuilderAtEnd(builder)(hitBlock)
                                                                            in
                                                                                let _ =
                                                                                    buildStore(builder)(constInt(i64)(0u64)(false))(listSlot)
                                                                                in
                                                                                    let _ =
                                                                                        buildStore(builder)(buildSExt(builder)(returnCode)(i64)("rx_cap_count"))(indexSlot)
                                                                                    in
                                                                                        let _ = buildBr(builder)(loopCheckBlock)
                                                                                        in
                                                                                            let _ = positionBuilderAtEnd(builder)(loopCheckBlock)
                                                                                            in
                                                                                                let index = buildLoad(builder)(i64)(indexSlot)("rx_cap_index_value")
                                                                                                in
                                                                                                    let _ =
                                                                                                        buildCondBr(builder)(buildICmp(builder)(intPredicateUgt)(index)(constInt(i64)(0u64)(false))("rx_cap_more"))(loopBodyBlock)(loopDoneBlock)
                                                                                                    in
                                                                                                        let _ = positionBuilderAtEnd(builder)(loopBodyBlock)
                                                                                                        in
                                                                                                            let group =
                                                                                                                buildSub(builder)(index)(constInt(i64)(1u64)(false))("rx_cap_group")
                                                                                                            in
                                                                                                                let _ = buildStore(builder)(group)(indexSlot)
                                                                                                                in
                                                                                                                    let pairAddr =
                                                                                                                        buildAdd(builder)(ovector)(buildMul(builder)(group)(constInt(i64)(16u64)(false))("rx_cap_pair_offset"))("rx_cap_pair")
                                                                                                                    in
                                                                                                                        let groupStart = loadWordAt(builder)(i64)(i8)(ptrType)(pairAddr)(0)("rx_cap_group_start")
                                                                                                                        in
                                                                                                                            let groupEnd = loadWordAt(builder)(i64)(i8)(ptrType)(pairAddr)(8)("rx_cap_group_end")
                                                                                                                            in
                                                                                                                                let _ =
                                                                                                                                    buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(groupStart)(constInt(i64)(0u64)(false))("rx_cap_is_unset"))(unsetBlock)(setBlock)
                                                                                                                                in
                                                                                                                                    let _ = positionBuilderAtEnd(builder)(unsetBlock)
                                                                                                                                    in
                                                                                                                                        let _ =
                                                                                                                                            buildStore(builder)(emitArenaNone(context)(function_)(builder)(i64)(i8)(ptrType)(arena)("rx_cap_unset_none"))(elementSlot)
                                                                                                                                        in
                                                                                                                                            let _ = buildBr(builder)(consBlock)
                                                                                                                                            in
                                                                                                                                                let _ = positionBuilderAtEnd(builder)(setBlock)
                                                                                                                                                in
                                                                                                                                                    let view =
                                                                                                                                                        emitArenaStringView(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(buildAdd(builder)(subjectAddr)(groupStart)("rx_cap_group_addr"))(buildSub(builder)(groupEnd)(groupStart)("rx_cap_group_length"))("rx_cap_view")
                                                                                                                                                    in
                                                                                                                                                        let _ =
                                                                                                                                                            buildStore(builder)(emitArenaSome(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(view)("rx_cap_some"))(elementSlot)
                                                                                                                                                        in
                                                                                                                                                            let _ = buildBr(builder)(consBlock)
                                                                                                                                                            in
                                                                                                                                                                let _ = positionBuilderAtEnd(builder)(consBlock)
                                                                                                                                                                in
                                                                                                                                                                    let cell = emitArenaAlloc(context)(function_)(builder)(i64)(arena)(16)("rx_cap_cell")
                                                                                                                                                                    in
                                                                                                                                                                        let _ =
                                                                                                                                                                            storeWordAt(builder)(i64)(i8)(ptrType)(cell)(0)(buildLoad(builder)(i64)(elementSlot)("rx_cap_element_value"))("rx_cap_head")
                                                                                                                                                                        in
                                                                                                                                                                            let _ =
                                                                                                                                                                                storeWordAt(builder)(i64)(i8)(ptrType)(cell)(8)(buildLoad(builder)(i64)(listSlot)("rx_cap_previous"))("rx_cap_tail")
                                                                                                                                                                            in
                                                                                                                                                                                let _ = buildStore(builder)(cell)(listSlot)
                                                                                                                                                                                in
                                                                                                                                                                                    let _ = buildBr(builder)(loopCheckBlock)
                                                                                                                                                                                    in
                                                                                                                                                                                        let _ = positionBuilderAtEnd(builder)(loopDoneBlock)
                                                                                                                                                                                        in
                                                                                                                                                                                            let _ =
                                                                                                                                                                                                buildStore(builder)(emitArenaSome(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(buildLoad(builder)(i64)(listSlot)("rx_cap_final_list"))("rx_cap_some_list"))(resultSlot)
                                                                                                                                                                                            in
                                                                                                                                                                                                let _ = buildBr(builder)(doneBlock)
                                                                                                                                                                                                in
                                                                                                                                                                                                    let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                                                                                                                                                    in buildLoad(builder)(i64)(resultSlot)("rx_cap_result_value"))

// One `pcre2_substitute_8` call writing into the buffer at `bufferAddr`, its capacity in and its
// used (or, on overflow, required) length out through `lengthSlot`.
let emitPcre2Substitute builder i64 i32 ptrType pcre2 code subjectAddr subjectLength replacementAddr replacementLength bufferAddr lengthSlot name = callPcre2(builder)(pcre2)("pcre2_substitute_8")([code, buildIntToPtr(builder)(subjectAddr)(ptrType)(name + "_subject"), subjectLength, constInt(i64)(0u64)(false), constInt(i32)(pcre2SubstituteOptions)(false), constNull(ptrType), constNull(ptrType), buildIntToPtr(builder)(replacementAddr)(ptrType)(name + "_replacement"), replacementLength, buildIntToPtr(builder)(bufferAddr)(ptrType)(name + "_buffer"), lengthSlot])(11u32)(name)

// `substitute(code, subject, replacement)`: every match replaced (`$1` names a group), as stage 0's
// `EmitRegexSubstitute`: a first attempt into 2 * subject + 256 bytes, and on overflow one retry
// with exactly the length PCRE2 reported.
let emitRegexSubstitute context function_ builder i64 i8 i32 ptrType (arena: ArenaRuntime) memcpyFn memcpyType pcre2 codeWord subjectRef replacementRef =
    match emitStringParts(builder)(i64)(ptrType)(subjectRef)("rx_sub_subject") with
        | (subjectLength, subjectAddr) ->
            match emitStringParts(builder)(i64)(ptrType)(replacementRef)("rx_sub_replacement") with
                | (replacementLength, replacementAddr) ->
                    let code = buildIntToPtr(builder)(codeWord)(ptrType)("rx_sub_code")
                    in
                        let lengthSlot = buildEntryAlloca(builder)(i64)("rx_sub_length")
                        in
                            let bufferSlot = buildEntryAlloca(builder)(i64)("rx_sub_buffer")
                            in
                                let capacity =
                                    buildAdd(builder)(buildMul(builder)(subjectLength)(constInt(i64)(2u64)(false))("rx_sub_double"))(constInt(i64)(256u64)(false))("rx_sub_capacity")
                                in
                                    let firstBuffer = emitArenaAllocDynamic(context)(function_)(builder)(i64)(arena)(capacity)("rx_sub_first_buffer")
                                    in
                                        let _ = buildStore(builder)(firstBuffer)(bufferSlot)
                                        in
                                            let _ = buildStore(builder)(capacity)(lengthSlot)
                                            in
                                                let firstCode = emitPcre2Substitute(builder)(i64)(i32)(ptrType)(pcre2)(code)(subjectAddr)(subjectLength)(replacementAddr)(replacementLength)(firstBuffer)(lengthSlot)("rx_sub_first")
                                                in
                                                    let retryBlock = appendBasicBlock(context)(function_)("rx_sub_retry")
                                                    in
                                                        let doneBlock = appendBasicBlock(context)(function_)("rx_sub_done")
                                                        in
                                                            let _ =
                                                                buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(firstCode)(constInt(i32)(pcre2ErrorNoMemory)(false))("rx_sub_overflow"))(retryBlock)(doneBlock)
                                                            in
                                                                let _ = positionBuilderAtEnd(builder)(retryBlock)
                                                                in
                                                                    let needed = buildLoad(builder)(i64)(lengthSlot)("rx_sub_needed")
                                                                    in
                                                                        let secondBuffer = emitArenaAllocDynamic(context)(function_)(builder)(i64)(arena)(needed)("rx_sub_second_buffer")
                                                                        in
                                                                            let _ = buildStore(builder)(secondBuffer)(bufferSlot)
                                                                            in
                                                                                let _ = buildStore(builder)(needed)(lengthSlot)
                                                                                in
                                                                                    let _ = emitPcre2Substitute(builder)(i64)(i32)(ptrType)(pcre2)(code)(subjectAddr)(subjectLength)(replacementAddr)(replacementLength)(secondBuffer)(lengthSlot)("rx_sub_second")
                                                                                    in
                                                                                        let _ = buildBr(builder)(doneBlock)
                                                                                        in
                                                                                            let _ = positionBuilderAtEnd(builder)(doneBlock)
                                                                                            in
                                                                                                emitArenaStringFromBytesAddr(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(memcpyFn)(memcpyType)(buildLoad(builder)(i64)(bufferSlot)("rx_sub_buffer_value"))(buildLoad(builder)(i64)(lengthSlot)("rx_sub_length_value"))("rx_sub_result")
