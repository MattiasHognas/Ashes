// The `Ashes.IO.Environment` emitters for `AshesCompiler.Backend.IrCodegen`:
// `currentDirectory`/`executableDirectory` over libc `getcwd`/`readlink("/proc/self/exe")` into a
// fixed 4 KiB path buffer (the executable path trimmed to its parent directory),
// `temporaryDirectory`/`cacheDirectory` as env-var lookups with stage 0's exact fallback chains
// (`TMPDIR` else the literal `/tmp`; `XDG_CACHE_HOME` else `HOME` + `/.cache`), and `get` over
// libc `getenv` returning `Ok(None)`/`Ok(Some(value))`/`Error(...)` — `LlvmCodegenBuiltins.Environment.cs`
// emitter for emitter, message constants included. The libc rows ride `DirectoryExternals`
// (declared once per module in `IrCodegen.Filesystem`). Depends on the LLVM bindings,
// `IrCodegen.Support`, and `IrCodegen.Filesystem` (the UTF-8 validator and the externals record).

import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
import AshesCompiler.Backend.IrCodegen.Filesystem
import Ashes.Number.UInt
export (
    value environmentReadFailedCodes,
    value environmentInvalidUtf8Codes,
    value environmentEmptyNameCodes,
    value emitEnvironmentCurrentDirectory,
    value emitEnvironmentExecutableDirectory,
    value emitEnvironmentTemporaryDirectory,
    value emitEnvironmentCacheDirectory,
    value emitEnvironmentGet,
)

// "Failed to read process environment." — stage 0's `EnvironmentReadFailedMessage`.
let environmentReadFailedCodes = [70, 97, 105, 108, 101, 100, 32, 116, 111, 32, 114, 101, 97, 100, 32, 112, 114, 111, 99, 101, 115, 115, 32, 101, 110, 118, 105, 114, 111, 110, 109, 101, 110, 116, 46]

// "Process environment contains invalid UTF-8." — stage 0's `EnvironmentInvalidUtf8Message`.
let environmentInvalidUtf8Codes = [80, 114, 111, 99, 101, 115, 115, 32, 101, 110, 118, 105, 114, 111, 110, 109, 101, 110, 116, 32, 99, 111, 110, 116, 97, 105, 110, 115, 32, 105, 110, 118, 97, 108, 105, 100, 32, 85, 84, 70, 45, 56, 46]

// "Environment variable name cannot be empty."
let environmentEmptyNameCodes = [69, 110, 118, 105, 114, 111, 110, 109, 101, 110, 116, 32, 118, 97, 114, 105, 97, 98, 108, 101, 32, 110, 97, 109, 101, 32, 99, 97, 110, 110, 111, 116, 32, 98, 101, 32, 101, 109, 112, 116, 121, 46]

// "TMPDIR", "/tmp", "XDG_CACHE_HOME", "HOME", "/.cache", "/proc/self/exe".
let environmentTmpdirNameCodes = [84, 77, 80, 68, 73, 82]

let environmentTmpFallbackCodes = [47, 116, 109, 112]

let environmentXdgCacheNameCodes = [88, 68, 71, 95, 67, 65, 67, 72, 69, 95, 72, 79, 77, 69]

let environmentHomeNameCodes = [72, 79, 77, 69]

let environmentCacheSuffixCodes = [47, 46, 99, 97, 99, 104, 101]

let environmentProcSelfExeCodes = [47, 112, 114, 111, 99, 47, 115, 101, 108, 102, 47, 101, 120, 101]

// libc returns pointers where this codegen's universal word is `i64` — a null check is an
// address-word comparison against zero, the same trick every other pointer test in this arc uses.
let emitEnvironmentPtrIsNull builder i64 ptr name =
    buildICmp(builder)(intPredicateEq)(buildPtrToInt(builder)(ptr)(i64)(name + "_addr"))(constInt(i64)(0u64)(false))(name)

let emitEnvironmentStrlen builder dirExt valuePtr name = buildCall(builder)(dirExt.strlenType)(dirExt.strlenFn)([valuePtr])(1u32)(name)

// `getenv(name)` for a runtime `Str` name: NUL-terminate it, call through the libc import, and
// hand back the raw `char*` (null when the variable is unset).
let emitEnvironmentRawGet builder i64 i8 ptrType mallocFn mallocType memcpyFn memcpyType dirExt nameRef prefix =
    prefix + "_name"
    |> emitStringToCString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(nameRef)
    |> (given (nameCstr) -> buildCall(builder)(dirExt.getenvType)(dirExt.getenvFn)([nameCstr])(1u32)(prefix + "_call"))

let emitEnvironmentRawGetNamed builder i64 i8 ptrType mallocFn mallocType memcpyFn memcpyType dirExt nameCodes prefix =
    prefix + "_name_str"
    |> emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(nameCodes)
    |> (given (nameRef) -> emitEnvironmentRawGet(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(dirExt)(nameRef)(prefix))

// Wrap `length` bytes at `bufferPtr` as `Ok(Str)` after a whole-buffer UTF-8 check — with `failed`
// short-circuiting to `Error(read-failed)` and an invalid buffer landing on `Error(invalid-UTF-8)`,
// stage 0's `EmitEnvironmentUtf8Result` block for block.
let emitEnvironmentUtf8Result context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType bufferPtr length failed prefix =
    (let resultSlot = buildEntryAlloca(builder)(i64)(prefix + "_result")
    in
        let validateBlock = appendBasicBlock(context)(function_)(prefix + "_validate")
        in
            let successBlock = appendBasicBlock(context)(function_)(prefix + "_success")
            in
                let failureBlock = appendBasicBlock(context)(function_)(prefix + "_failure")
                in
                    let invalidBlock = appendBasicBlock(context)(function_)(prefix + "_invalid_utf8")
                    in
                        let doneBlock = appendBasicBlock(context)(function_)(prefix + "_done")
                        in
                            validateBlock
                            |> buildCondBr(builder)(failed)(failureBlock)
                            |> (given (_) -> positionBuilderAtEnd(builder)(validateBlock))
                            |> (given (_) -> emitValidateUtf8(context)(function_)(i64)(i8)(builder)(bufferPtr)(length)(prefix + "_utf8"))
                            |> (given (valid) ->
                                buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(valid)(constInt(i64)(0u64)(false))(prefix + "_valid"))(successBlock)(invalidBlock))
                            |> (given (_) -> positionBuilderAtEnd(builder)(successBlock))
                            |> (given (_) ->
                                emitHeapStringFromBytesAddr(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(buildPtrToInt(builder)(bufferPtr)(i64)(prefix + "_bytes_addr"))(length)(prefix + "_string"))
                            |> (given (value) ->
                                buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(value)(prefix + "_ok"))(resultSlot))
                            |> (given (_) -> buildBr(builder)(doneBlock))
                            |> (given (_) -> positionBuilderAtEnd(builder)(failureBlock))
                            |> (given (_) ->
                                buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(environmentReadFailedCodes)(prefix + "_failed_msg"))(prefix + "_failed"))(resultSlot))
                            |> (given (_) -> buildBr(builder)(doneBlock))
                            |> (given (_) -> positionBuilderAtEnd(builder)(invalidBlock))
                            |> (given (_) ->
                                buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(environmentInvalidUtf8Codes)(prefix + "_invalid_msg"))(prefix + "_invalid"))(resultSlot))
                            |> (given (_) -> buildBr(builder)(doneBlock))
                            |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))
                            |> (given (_) -> buildLoad(builder)(i64)(resultSlot)(prefix + "_result_value")))

// Trim an executable path to its parent directory: scan backward for the last `/` (or `\`),
// keeping index `1` when the separator is the root itself — stage 0's `EmitParentPathLength`.
let emitParentPathLength context function_ i64 i8 builder bufferPtr length prefix =
    (let cursorSlot = buildEntryAlloca(builder)(i64)(prefix + "_cursor")
    in
        let checkBlock = appendBasicBlock(context)(function_)(prefix + "_check")
        in
            let byteBlock = appendBasicBlock(context)(function_)(prefix + "_byte")
            in
                let separatorBlock = appendBasicBlock(context)(function_)(prefix + "_separator")
                in
                    let decrementBlock = appendBasicBlock(context)(function_)(prefix + "_decrement")
                    in
                        let doneBlock = appendBasicBlock(context)(function_)(prefix + "_done")
                        in
                            let cursor =
                                cursorSlot
                                |> buildStore(builder)(length)
                                |> (given (_) -> buildBr(builder)(checkBlock))
                                |> (given (_) -> positionBuilderAtEnd(builder)(checkBlock))
                                |> (given (_) -> buildLoad(builder)(i64)(cursorSlot)(prefix + "_cursor_value"))
                            in
                                let index =
                                    byteBlock
                                    |> buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(cursor)(constInt(i64)(0u64)(false))(prefix + "_at_start"))(doneBlock)
                                    |> (given (_) -> positionBuilderAtEnd(builder)(byteBlock))
                                    |> (given (_) ->
                                        buildSub(builder)(cursor)(constInt(i64)(1u64)(false))(prefix + "_index"))
                                in
                                    let byteValue = emitLoadByteAtI64(builder)(i64)(i8)(bufferPtr)(index)(prefix + "_byte")
                                    in
                                        decrementBlock
                                        |> buildCondBr(builder)(buildOr(builder)(buildICmp(builder)(intPredicateEq)(byteValue)(constInt(i64)(47u64)(false))(prefix + "_slash"))(buildICmp(builder)(intPredicateEq)(byteValue)(constInt(i64)(92u64)(false))(prefix + "_backslash"))(prefix + "_is_separator"))(separatorBlock)
                                        |> (given (_) -> positionBuilderAtEnd(builder)(separatorBlock))
                                        |> (given (_) ->
                                            buildSelect(builder)(buildICmp(builder)(intPredicateEq)(index)(constInt(i64)(0u64)(false))(prefix + "_root_separator"))(constInt(i64)(1u64)(false))(index)(prefix + "_parent_length"))
                                        |> (given (parentLength) -> buildStore(builder)(parentLength)(cursorSlot))
                                        |> (given (_) -> buildBr(builder)(doneBlock))
                                        |> (given (_) -> positionBuilderAtEnd(builder)(decrementBlock))
                                        |> (given (_) -> buildStore(builder)(index)(cursorSlot))
                                        |> (given (_) -> buildBr(builder)(checkBlock))
                                        |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))
                                        |> (given (_) -> buildLoad(builder)(i64)(cursorSlot)(prefix + "_length")))

// `currentDirectory`/`executableDirectory`: fill a fixed 4 KiB stack buffer via `getcwd` or
// `readlink("/proc/self/exe")` (the latter trimmed to the parent directory), then wrap it —
// stage 0's Linux `EmitEnvironmentPlatformDirectory` arm.
let emitEnvironmentPlatformDirectory context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType dirExt wantExecutable =
    (let prefix =
        if wantExecutable
        then "env_exe"
        else "env_cwd"
    in
        let bufferPtr =
            buildEntryAlloca(builder)(arrayType(i8)(4096u64))(prefix + "_buffer")
        in
            let _ =
                buildStore(builder)(constInt(i8)(0u64)(false))(bufferPtr)
            in
                match if wantExecutable
                then
                    let pathCstr =
                        prefix + "_path"
                        |> emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(environmentProcSelfExeCodes)
                        |> (given (pathRef) -> emitStringToCString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(pathRef)(prefix + "_path_c"))
                    in
                        let written = buildCall(builder)(dirExt.readlinkType)(dirExt.readlinkFn)([pathCstr, bufferPtr, constInt(i64)(4096u64)(false)])(3u32)(prefix + "_readlink")
                        in
                            (written, buildICmp(builder)(intPredicateSle)(written)(constInt(i64)(0u64)(false))(prefix + "_failed"))
                else
                    let returned = buildCall(builder)(dirExt.getcwdType)(dirExt.getcwdFn)([bufferPtr, constInt(i64)(4096u64)(false)])(2u32)(prefix + "_getcwd")
                    in (emitEnvironmentStrlen(builder)(dirExt)(bufferPtr)(prefix + "_strlen"), emitEnvironmentPtrIsNull(builder)(i64)(returned)(prefix + "_failed")) with
                    | (rawLength, failed) ->
                        let safeLength =
                            buildSelect(builder)(failed)(constInt(i64)(0u64)(false))(rawLength)(prefix + "_safe_length")
                        in
                            let length =
                                if wantExecutable
                                then emitParentPathLength(context)(function_)(i64)(i8)(builder)(bufferPtr)(safeLength)(prefix + "_parent")
                                else safeLength
                            in emitEnvironmentUtf8Result(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(bufferPtr)(length)(failed)(prefix))

let emitEnvironmentCurrentDirectory context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType dirExt = emitEnvironmentPlatformDirectory(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(dirExt)(false)

let emitEnvironmentExecutableDirectory context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType dirExt = emitEnvironmentPlatformDirectory(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(dirExt)(true)

// One candidate env value: UTF-8-validate it, append the static `suffixCodes` when non-empty
// (`HOME` + `/.cache`), and store `Ok` — stage 0's `EmitEnvironmentFallbackValue`.
let emitEnvironmentFallbackValue context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType valuePtr length suffixCodes resultSlot invalidBlock doneBlock prefix =
    (let validBlock = appendBasicBlock(context)(function_)(prefix + "_valid")
    in
        let value =
            prefix + "_utf8"
            |> emitValidateUtf8(context)(function_)(i64)(i8)(builder)(valuePtr)(length)
            |> (given (valid) ->
                buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(valid)(constInt(i64)(0u64)(false))(prefix + "_is_valid"))(validBlock)(invalidBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(validBlock))
            |> (given (_) ->
                emitHeapStringFromBytesAddr(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(buildPtrToInt(builder)(valuePtr)(i64)(prefix + "_value_addr"))(length)(prefix + "_string"))
        in
            let suffixed =
                match suffixCodes with
                    | [] -> value
                    | _ -> emitStringConcatN(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)([value, emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(suffixCodes)(prefix + "_suffix")])
            in
                suffixed
                |> (given (final) ->
                    buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(final)(prefix + "_ok"))(resultSlot))
                |> (given (_) -> buildBr(builder)(doneBlock)))

// `temporaryDirectory`/`cacheDirectory`: try `preferredCodes` as an env variable; on unset/empty
// fall back — to the literal `fallbackCodes` when `suffixCodes` is empty (`TMPDIR` else `/tmp`),
// otherwise to env `fallbackCodes` + `suffixCodes` (`XDG_CACHE_HOME` else `HOME` + `/.cache`) —
// stage 0's `EmitEnvironmentFallbackDirectory`.
let emitEnvironmentFallbackDirectory context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType dirExt preferredCodes fallbackCodes suffixCodes prefix =
    (let resultSlot = buildEntryAlloca(builder)(i64)(prefix + "_result")
    in
        let preferredBlock = appendBasicBlock(context)(function_)(prefix + "_preferred_value")
        in
            let fallbackBlock = appendBasicBlock(context)(function_)(prefix + "_fallback")
            in
                let missingBlock = appendBasicBlock(context)(function_)(prefix + "_missing")
                in
                    let invalidBlock = appendBasicBlock(context)(function_)(prefix + "_invalid_utf8")
                    in
                        let doneBlock = appendBasicBlock(context)(function_)(prefix + "_done")
                        in
                            let preferred = emitEnvironmentRawGetNamed(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(dirExt)(preferredCodes)(prefix + "_preferred")
                            in
                                let preferredOkBlock = appendBasicBlock(context)(function_)(prefix + "_preferred_ok")
                                in
                                    let _ =
                                        preferredBlock
                                        |> buildCondBr(builder)(emitEnvironmentPtrIsNull(builder)(i64)(preferred)(prefix + "_preferred_missing"))(fallbackBlock)
                                        |> (given (_) -> positionBuilderAtEnd(builder)(preferredBlock))
                                        |> (given (_) -> emitEnvironmentStrlen(builder)(dirExt)(preferred)(prefix + "_preferred_length"))
                                        |> (given (preferredLength) ->
                                            preferredOkBlock
                                            |> buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(preferredLength)(constInt(i64)(0u64)(false))(prefix + "_preferred_empty"))(fallbackBlock)
                                            |> (given (_) -> positionBuilderAtEnd(builder)(preferredOkBlock))
                                            |> (given (_) -> emitEnvironmentFallbackValue(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(preferred)(preferredLength)([])(resultSlot)(invalidBlock)(doneBlock)(prefix + "_preferred")))
                                    in
                                        let _ = positionBuilderAtEnd(builder)(fallbackBlock)
                                        in
                                            let _ =
                                                match suffixCodes with
                                                    | [] ->
                                                        prefix + "_fallback_literal"
                                                        |> emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(fallbackCodes)
                                                        |> (given (literal) ->
                                                            buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(literal)(prefix + "_fallback_ok"))(resultSlot))
                                                        |> (given (_) -> buildBr(builder)(doneBlock))
                                                    | _ ->
                                                        let fallbackValueBlock = appendBasicBlock(context)(function_)(prefix + "_fallback_value")
                                                        in
                                                            prefix + "_fallback_env"
                                                            |> emitEnvironmentRawGetNamed(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(dirExt)(fallbackCodes)
                                                            |> (given (fallback) ->
                                                                fallbackValueBlock
                                                                |> buildCondBr(builder)(emitEnvironmentPtrIsNull(builder)(i64)(fallback)(prefix + "_fallback_missing"))(missingBlock)
                                                                |> (given (_) -> positionBuilderAtEnd(builder)(fallbackValueBlock))
                                                                |> (given (_) -> emitEnvironmentStrlen(builder)(dirExt)(fallback)(prefix + "_fallback_length"))
                                                                |> (given (fallbackLength) -> emitEnvironmentFallbackValue(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(fallback)(fallbackLength)(suffixCodes)(resultSlot)(invalidBlock)(doneBlock)(prefix + "_fallback")))
                                            in
                                                missingBlock
                                                |> positionBuilderAtEnd(builder)
                                                |> (given (_) ->
                                                    buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(environmentReadFailedCodes)(prefix + "_missing_msg"))(prefix + "_missing_err"))(resultSlot))
                                                |> (given (_) -> buildBr(builder)(doneBlock))
                                                |> (given (_) -> positionBuilderAtEnd(builder)(invalidBlock))
                                                |> (given (_) ->
                                                    buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(environmentInvalidUtf8Codes)(prefix + "_invalid_msg"))(prefix + "_invalid_err"))(resultSlot))
                                                |> (given (_) -> buildBr(builder)(doneBlock))
                                                |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))
                                                |> (given (_) -> buildLoad(builder)(i64)(resultSlot)(prefix + "_result_value")))

let emitEnvironmentTemporaryDirectory context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType dirExt = emitEnvironmentFallbackDirectory(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(dirExt)(environmentTmpdirNameCodes)(environmentTmpFallbackCodes)([])("env_temp")

let emitEnvironmentCacheDirectory context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType dirExt = emitEnvironmentFallbackDirectory(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(dirExt)(environmentXdgCacheNameCodes)(environmentHomeNameCodes)(environmentCacheSuffixCodes)("env_cache")

// `Ashes.IO.Environment.get(name)`: empty name is an `Error`, an unset variable `Ok(None)`, a set
// one `Ok(Some(value))` after UTF-8 validation — stage 0's `EmitEnvironmentGet` block for block.
let emitEnvironmentGet context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType dirExt nameRef =
    (let resultSlot = buildEntryAlloca(builder)(i64)("env_get_result")
    in
        let invalidNameBlock = appendBasicBlock(context)(function_)("env_get_invalid_name")
        in
            let lookupBlock = appendBasicBlock(context)(function_)("env_get_lookup")
            in
                let missingBlock = appendBasicBlock(context)(function_)("env_get_missing")
                in
                    let valueBlock = appendBasicBlock(context)(function_)("env_get_value")
                    in
                        let validBlock = appendBasicBlock(context)(function_)("env_get_valid")
                        in
                            let invalidUtf8Block = appendBasicBlock(context)(function_)("env_get_invalid_utf8")
                            in
                                let doneBlock = appendBasicBlock(context)(function_)("env_get_done")
                                in
                                    let nameLength = emitStringLengthValue(builder)(i64)(ptrType)(nameRef)("env_get_name_length")
                                    in
                                        let value =
                                            lookupBlock
                                            |> buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(nameLength)(constInt(i64)(0u64)(false))("env_get_empty_name"))(invalidNameBlock)
                                            |> (given (_) -> positionBuilderAtEnd(builder)(invalidNameBlock))
                                            |> (given (_) ->
                                                buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(environmentEmptyNameCodes)("env_get_empty_msg"))("env_get_empty_err"))(resultSlot))
                                            |> (given (_) -> buildBr(builder)(doneBlock))
                                            |> (given (_) -> positionBuilderAtEnd(builder)(lookupBlock))
                                            |> (given (_) -> emitEnvironmentRawGet(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(dirExt)(nameRef)("env_get"))
                                        in
                                            let valueLength =
                                                valueBlock
                                                |> buildCondBr(builder)(emitEnvironmentPtrIsNull(builder)(i64)(value)("env_get_is_missing"))(missingBlock)
                                                |> (given (_) -> positionBuilderAtEnd(builder)(missingBlock))
                                                |> (given (_) -> emitAllocAdtRuntimeManaged(builder)(i64)(i8)(mallocFn)(mallocType)(0)(0)(false)("env_get_none"))
                                                |> (given (noneValue) ->
                                                    buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(noneValue)("env_get_none_ok"))(resultSlot))
                                                |> (given (_) -> buildBr(builder)(doneBlock))
                                                |> (given (_) -> positionBuilderAtEnd(builder)(valueBlock))
                                                |> (given (_) -> emitEnvironmentStrlen(builder)(dirExt)(value)("env_get_value_length"))
                                            in
                                                invalidUtf8Block
                                                |> buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(emitValidateUtf8(context)(function_)(i64)(i8)(builder)(value)(valueLength)("env_get_utf8"))(constInt(i64)(0u64)(false))("env_get_utf8_valid"))(validBlock)
                                                |> (given (_) -> positionBuilderAtEnd(builder)(validBlock))
                                                |> (given (_) ->
                                                    emitHeapStringFromBytesAddr(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(buildPtrToInt(builder)(value)(i64)("env_get_value_addr"))(valueLength)("env_get_string"))
                                                |> (given (stringValue) ->
                                                    let someValue = emitAllocAdtRuntimeManaged(builder)(i64)(i8)(mallocFn)(mallocType)(1)(1)(false)("env_get_some")
                                                    in
                                                        let _ =
                                                            "env_get_some_field"
                                                            |> gepBytes(builder)(i64)(i8)(buildIntToPtr(builder)(someValue)(ptrType)("env_get_some_ptr"))(8)
                                                            |> buildStore(builder)(stringValue)
                                                        in someValue)
                                                |> (given (someValue) ->
                                                    buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(someValue)("env_get_some_ok"))(resultSlot))
                                                |> (given (_) -> buildBr(builder)(doneBlock))
                                                |> (given (_) -> positionBuilderAtEnd(builder)(invalidUtf8Block))
                                                |> (given (_) ->
                                                    buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(environmentInvalidUtf8Codes)("env_get_invalid_msg"))("env_get_invalid_err"))(resultSlot))
                                                |> (given (_) -> buildBr(builder)(doneBlock))
                                                |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))
                                                |> (given (_) -> buildLoad(builder)(i64)(resultSlot)("env_get_result_value")))
