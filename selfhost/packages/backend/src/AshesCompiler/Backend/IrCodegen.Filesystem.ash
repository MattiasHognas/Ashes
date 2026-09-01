// The filesystem and stdin/file line-reading emitters for `AshesCompiler.Backend.IrCodegen`:
// `Ashes.IO.readLine`/`File.readLine` (one shared fd-parameterized per-byte reader),
// `File.exists`/`open`/`readChunk`/`close`/`writeText`/`writeBytes`/`replace`, the whole-file
// read family (`readText`/`readAllBytes`/`mmap`), `makeExecutable`, and `Directory.createAll`
// over raw syscalls (`makeExecutable`'s `lstat` aside), `Directory.entries`/`removeTree` over
// the libc dynamic-import surface
// (`DirectoryExternals`, declared once per module; the per-occurrence internal-linkage
// name-append/comparator/`nftw`-visitor helper functions), the whole-buffer UTF-8 validator, and
// the shared `Ok(Unit)`/`Error(message)` status-result builder. `DirectoryExternals` also carries
// the `getenv`/`getcwd`/`readlink` rows `IrCodegen.Environment` calls through. Depends only on
// the LLVM bindings and `IrCodegen.Support`.

import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
import Ashes.Number.UInt
export (
    type DirectoryExternals(..),
    value declareDirectoryExternalFunctions,
    value emitReadLinePanicMessage,
    type ReadLineScratch(..),
    value emitReadLineScratch,
    type ReadLineBlocks(..),
    value emitReadLineBlocks,
    value emitReadLineReadAndInspect,
    value emitReadLineFinish,
    value emitReadLineFromFd,
    value emitReadLine,
    value emitUtf8ByteInRange,
    value emitUtf8ContinuationChain,
    value emitUtf8ValidateSequence,
    value emitUtf8ClassifyStep,
    value emitUtf8ClassifyLeadByte,
    value emitValidateUtf8LoopHead,
    value emitValidateUtf8,
    value emitFilesystemStatusResult,
    value fileWriteTextErrorCodes,
    value emitFileWriteText,
    value fileReplaceErrorCodes,
    value emitFileReplace,
    value directoryCreateAllErrorCodes,
    value emitDirectoryMkdirExistingOk,
    value emitDirectoryCreateLoopCheck,
    value emitDirectoryCreateComponentStep,
    value emitDirectoryCreateByteStep,
    value emitDirectoryCreateFinish,
    value emitDirectoryCreateAll,
    value directoryEntriesFailedCodes,
    value directoryEntriesInvalidUtf8Codes,
    value directoryRemoveTreeErrorCodes,
    value emitDirectoryNameAppendGrow,
    value emitDirectoryNameAppendFunction,
    value emitDirectoryNameCompareFunction,
    value emitRemoveTreeVisitFunction,
    value emitDirectoryEntriesOpenStream,
    value emitDirectoryEntriesReadError,
    value emitDirectoryEntryIsDot,
    value emitDirectoryEntriesLoop,
    value emitDirectoryEntriesClose,
    type DirectoryEntriesResultBlocks(..),
    value emitDirectoryEntriesResultBlocks,
    value emitDirectoryEntriesBuildList,
    value emitDirectoryEntriesCleanup,
    value emitDirectoryEntriesResult,
    value emitDirectoryEntries,
    value emitDirectoryRemoveTree,
    value fileOpenErrorCodes,
    value fileReadChunkErrorCodes,
    value emitFileOpen,
    value emitFileReadChunk,
    value emitFileClose,
    value fileReadFailedCodes,
    value fileReadInvalidUtf8Codes,
    value emitFileReadText,
    value emitFileReadAllBytes,
    value emitFileMmap,
    value fileMakeExecutableErrorCodes,
    value emitFileMakeExecutable,
)

// The libc surface `Directory.entries`/`Directory.removeTree` need beyond the core four below:
// the directory stream (`fdopendir`/`readdir`/`closedir` — `readdir` has no raw-syscall
// equivalent worth reinventing), the deterministic name sort (`qsort`/`strcmp`), the growable
// name array (`strlen`/`realloc`/`memmove`), and the recursive walk (`lstat`/`nftw`/`remove` —
// `nftw` is a libc userspace algorithm with no syscall equivalent at all), plus `__errno_location`
// for `readdir`'s end-vs-error disambiguation. Declared once per module alongside the core set;
// an unreferenced declaration never reaches the emitted object's symbol table, so a program that
// never touches these builtins links exactly as before.
type DirectoryExternals =
    | strlenFn: LLVMValueRef
    | strlenType: LLVMTypeRef
    | reallocFn: LLVMValueRef
    | reallocType: LLVMTypeRef
    | memmoveFn: LLVMValueRef
    | memmoveType: LLVMTypeRef
    | qsortFn: LLVMValueRef
    | qsortType: LLVMTypeRef
    | strcmpFn: LLVMValueRef
    | strcmpType: LLVMTypeRef
    | fdopendirFn: LLVMValueRef
    | fdopendirType: LLVMTypeRef
    | readdirFn: LLVMValueRef
    | readdirType: LLVMTypeRef
    | closedirFn: LLVMValueRef
    | closedirType: LLVMTypeRef
    | errnoLocationFn: LLVMValueRef
    | errnoLocationType: LLVMTypeRef
    | lstatFn: LLVMValueRef
    | lstatType: LLVMTypeRef
    | nftwFn: LLVMValueRef
    | nftwType: LLVMTypeRef
    | removeFn: LLVMValueRef
    | removeType: LLVMTypeRef
    | getenvFn: LLVMValueRef
    | getenvType: LLVMTypeRef
    | getcwdFn: LLVMValueRef
    | getcwdType: LLVMTypeRef
    | readlinkFn: LLVMValueRef
    | readlinkType: LLVMTypeRef

let declareDirectoryExternalFunctions module_ context types =
    (let strlenType = functionType(types.i64)([types.ptrType])(1u32)(false)
    in
        let reallocType = functionType(types.ptrType)([types.ptrType, types.i64])(2u32)(false)
        in
            let memmoveType = functionType(types.ptrType)([types.ptrType, types.ptrType, types.i64])(3u32)(false)
            in
                let qsortType =
                    functionType(voidType(context))([types.ptrType, types.i64, types.i64, types.ptrType])(4u32)(false)
                in
                    let strcmpType = functionType(types.i32)([types.ptrType, types.ptrType])(2u32)(false)
                    in
                        let fdopendirType = functionType(types.ptrType)([types.i32])(1u32)(false)
                        in
                            let readdirType = functionType(types.ptrType)([types.ptrType])(1u32)(false)
                            in
                                let closedirType = functionType(types.i32)([types.ptrType])(1u32)(false)
                                in
                                    let errnoLocationType = functionType(types.ptrType)([])(0u32)(false)
                                    in
                                        let lstatType = functionType(types.i32)([types.ptrType, types.ptrType])(2u32)(false)
                                        in
                                            let nftwType = functionType(types.i32)([types.ptrType, types.ptrType, types.i32, types.i32])(4u32)(false)
                                            in
                                                let removeType = functionType(types.i32)([types.ptrType])(1u32)(false)
                                                in
                                                    let getenvType = functionType(types.ptrType)([types.ptrType])(1u32)(false)
                                                    in
                                                        let getcwdType = functionType(types.ptrType)([types.ptrType, types.i64])(2u32)(false)
                                                        in
                                                            let readlinkType = functionType(types.i64)([types.ptrType, types.ptrType, types.i64])(3u32)(false)
                                                            in
                                                                DirectoryExternals(
                                                                    strlenFn = addFunction(module_)("strlen")(strlenType),
                                                                    strlenType = strlenType,
                                                                    reallocFn = addFunction(module_)("realloc")(reallocType),
                                                                    reallocType = reallocType,
                                                                    memmoveFn = addFunction(module_)("memmove")(memmoveType),
                                                                    memmoveType = memmoveType,
                                                                    qsortFn = addFunction(module_)("qsort")(qsortType),
                                                                    qsortType = qsortType,
                                                                    strcmpFn = addFunction(module_)("strcmp")(strcmpType),
                                                                    strcmpType = strcmpType,
                                                                    fdopendirFn = addFunction(module_)("fdopendir")(fdopendirType),
                                                                    fdopendirType = fdopendirType,
                                                                    readdirFn = addFunction(module_)("readdir")(readdirType),
                                                                    readdirType = readdirType,
                                                                    closedirFn = addFunction(module_)("closedir")(closedirType),
                                                                    closedirType = closedirType,
                                                                    errnoLocationFn = addFunction(module_)("__errno_location")(errnoLocationType),
                                                                    errnoLocationType = errnoLocationType,
                                                                    lstatFn = addFunction(module_)("lstat")(lstatType),
                                                                    lstatType = lstatType,
                                                                    nftwFn = addFunction(module_)("nftw")(nftwType),
                                                                    nftwType = nftwType,
                                                                    removeFn = addFunction(module_)("remove")(removeType),
                                                                    removeType = removeType,
                                                                    getenvFn = addFunction(module_)("getenv")(getenvType),
                                                                    getenvType = getenvType,
                                                                    getcwdFn = addFunction(module_)("getcwd")(getcwdType),
                                                                    getcwdType = getcwdType,
                                                                    readlinkFn = addFunction(module_)("readlink")(readlinkType),
                                                                    readlinkType = readlinkType
                                                                ))

// `readLine`'s overflow exit: a line longer than the fixed 64 KiB scratch buffer — the same
// fixed-ASCII-line-then-exit-1 shape as `emitBytesGetPanicMessage`, matching stage 0's own
// `EmitReadLineFinish` overflow message text exactly ("readLine input too long").
let emitReadLinePanicMessage builder i64 i8 =
    (let bufferType = arrayType(i8)(24u64)
    in
        let buffer = buildAlloca(builder)(bufferType)("read_line_panic_msg")
        in
            // "readLine input too long\n"
            let _ = storeAsciiBytes(builder)(i64)(i8)(bufferType)(buffer)(0)([114, 101, 97, 100, 76, 105, 110, 101, 32, 105, 110, 112, 117, 116, 32, 116, 111, 111, 32, 108, 111, 110, 103, 10])
            in
                let addr = buildPtrToInt(builder)(buffer)(i64)("read_line_panic_addr")
                in
                    let _ =
                        false
                        |> constInt(i64)(24u64)
                        |> emitLinuxWrite(builder)(i64)(constInt(i64)(1u64)(false))(addr)
                    in
                        false
                        |> constInt(i64)(1u64)
                        |> emitLinuxProcessExitWithCode(builder)(i64))

// Scratch storage for `emitReadLine`, module-scoped only in the sense that every field lives for
// the whole call: `buffer`/`bufferAddr` accumulate the line, `lenSlot` its length so far,
// `byteSlot`/`byteAddr` the one byte just read, `resultSlot` the final `Option(Str)`.
type ReadLineScratch =
    | buffer: LLVMValueRef
    | bufferAddr: LLVMValueRef
    | lenSlot: LLVMValueRef
    | byteSlot: LLVMValueRef
    | byteAddr: LLVMValueRef
    | resultSlot: LLVMValueRef

let emitReadLineScratch builder i64 i8 =
    (let buffer =
        buildAlloca(builder)(arrayType(i8)(65536u64))("read_line_buf")
    in
        let byteSlot = buildAlloca(builder)(i8)("read_line_byte")
        in
            ReadLineScratch(
                buffer = buffer,
                bufferAddr = buildPtrToInt(builder)(buffer)(i64)("read_line_buf_addr"),
                lenSlot = buildAlloca(builder)(i64)("read_line_len"),
                byteSlot = byteSlot,
                byteAddr = buildPtrToInt(builder)(byteSlot)(i64)("read_line_byte_addr"),
                resultSlot = buildAlloca(builder)(i64)("read_line_result")
            ))

// The ten-block control-flow skeleton `emitReadLine`'s three phases share: read a byte and check
// EOF (`loopBlock`), classify it (`inspectBlock`/`skipCrBlock`), buffer it with an overflow guard
// (`storeByteBlock`/`appendByteBlock`), then land on one of three outcomes (`eofBlock` picks
// `finishSomeBlock` or `returnNoneBlock`; `overflowBlock` panics) before every path rejoins at
// `continueBlock`.
type ReadLineBlocks =
    | loopBlock: LLVMBasicBlockRef
    | inspectBlock: LLVMBasicBlockRef
    | skipCrBlock: LLVMBasicBlockRef
    | storeByteBlock: LLVMBasicBlockRef
    | appendByteBlock: LLVMBasicBlockRef
    | eofBlock: LLVMBasicBlockRef
    | finishSomeBlock: LLVMBasicBlockRef
    | returnNoneBlock: LLVMBasicBlockRef
    | overflowBlock: LLVMBasicBlockRef
    | continueBlock: LLVMBasicBlockRef

let emitReadLineBlocks context function_ =
    ReadLineBlocks(
        loopBlock = appendBasicBlock(context)(function_)("read_line_loop"),
        inspectBlock = appendBasicBlock(context)(function_)("read_line_inspect"),
        skipCrBlock = appendBasicBlock(context)(function_)("read_line_skip_cr"),
        storeByteBlock = appendBasicBlock(context)(function_)("read_line_store_byte"),
        appendByteBlock = appendBasicBlock(context)(function_)("read_line_append_byte"),
        eofBlock = appendBasicBlock(context)(function_)("read_line_eof"),
        finishSomeBlock = appendBasicBlock(context)(function_)("read_line_finish_some"),
        returnNoneBlock = appendBasicBlock(context)(function_)("read_line_return_none"),
        overflowBlock = appendBasicBlock(context)(function_)("read_line_overflow"),
        continueBlock = appendBasicBlock(context)(function_)("read_line_continue")
    )

// `loopBlock`: one `read` syscall for the next byte; `n <= 0` means EOF.
// `inspectBlock`/`skipCrBlock`: `\n` finishes the line, a `\r` right before it is dropped, anything
// else falls through to `storeByteBlock`.
// `storeByteBlock`/`appendByteBlock`: append the byte unless the buffer is already at capacity.
let emitReadLineReadAndInspect i64 i8 builder fd scratch blocks =
    (let _ = positionBuilderAtEnd(builder)(blocks.loopBlock)
    in
        let n =
            false
            |> constInt(i64)(1u64)
            |> emitLinuxRead(builder)(i64)(fd)(scratch.byteAddr)
        in
            let atEof =
                buildICmp(builder)(intPredicateSle)(n)(constInt(i64)(0u64)(false))("read_line_at_eof")
            in
                let _ = buildCondBr(builder)(atEof)(blocks.eofBlock)(blocks.inspectBlock)
                in
                    let _ = positionBuilderAtEnd(builder)(blocks.inspectBlock)
                    in
                        let currentByte = buildLoad(builder)(i8)(scratch.byteSlot)("read_line_current_byte")
                        in
                            let isLf =
                                buildICmp(builder)(intPredicateEq)(currentByte)(constInt(i8)(10u64)(false))("read_line_is_lf")
                            in
                                let _ = buildCondBr(builder)(isLf)(blocks.finishSomeBlock)(blocks.skipCrBlock)
                                in
                                    let _ = positionBuilderAtEnd(builder)(blocks.skipCrBlock)
                                    in
                                        let isCr =
                                            buildICmp(builder)(intPredicateEq)(currentByte)(constInt(i8)(13u64)(false))("read_line_is_cr")
                                        in
                                            let _ = buildCondBr(builder)(isCr)(blocks.loopBlock)(blocks.storeByteBlock)
                                            in
                                                let _ = positionBuilderAtEnd(builder)(blocks.storeByteBlock)
                                                in
                                                    let currentLen = buildLoad(builder)(i64)(scratch.lenSlot)("read_line_len_value")
                                                    in
                                                        let atCapacity =
                                                            buildICmp(builder)(intPredicateUge)(currentLen)(constInt(i64)(65536u64)(false))("read_line_at_capacity")
                                                        in
                                                            let _ = buildCondBr(builder)(atCapacity)(blocks.overflowBlock)(blocks.appendByteBlock)
                                                            in
                                                                let _ = positionBuilderAtEnd(builder)(blocks.appendByteBlock)
                                                                in
                                                                    let destPtr = buildGEP(builder)(i8)(scratch.buffer)([currentLen])(1u32)("read_line_dest_ptr")
                                                                    in
                                                                        let _ = buildStore(builder)(currentByte)(destPtr)
                                                                        in
                                                                            let _ =
                                                                                buildStore(builder)(buildAdd(builder)(currentLen)(constInt(i64)(1u64)(false))("read_line_len_next"))(scratch.lenSlot)
                                                                            in buildBr(builder)(blocks.loopBlock))

// The three terminal outcomes: `eofBlock` picks `Some`/`None` by whether anything was buffered,
// `finishSomeBlock` copies the buffer into a fresh heap string and wraps it, `returnNoneBlock`
// stores `None`, and `overflowBlock` panics — all four store into `resultSlot` (or never return)
// and jump to `continueBlock`.
let emitReadLineFinish i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType scratch blocks =
    (let _ = positionBuilderAtEnd(builder)(blocks.eofBlock)
    in
        let lenAtEof = buildLoad(builder)(i64)(scratch.lenSlot)("read_line_len_at_eof")
        in
            let isEmpty =
                buildICmp(builder)(intPredicateEq)(lenAtEof)(constInt(i64)(0u64)(false))("read_line_is_empty")
            in
                let _ = buildCondBr(builder)(isEmpty)(blocks.returnNoneBlock)(blocks.finishSomeBlock)
                in
                    let _ = positionBuilderAtEnd(builder)(blocks.finishSomeBlock)
                    in
                        let finalLen = buildLoad(builder)(i64)(scratch.lenSlot)("read_line_final_len")
                        in
                            let stringRef = emitHeapStringFromBytesAddr(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(scratch.bufferAddr)(finalLen)("read_line_string")
                            in
                                let someValue = emitAllocAdtRuntimeManaged(builder)(i64)(i8)(mallocFn)(mallocType)(1)(1)("read_line_some")
                                in
                                    let someFieldPtr =
                                        gepBytes(builder)(i64)(i8)(buildIntToPtr(builder)(someValue)(ptrType)("read_line_some_ptr"))(8)("read_line_some_field_ptr")
                                    in
                                        let _ = buildStore(builder)(stringRef)(someFieldPtr)
                                        in
                                            let _ = buildStore(builder)(someValue)(scratch.resultSlot)
                                            in
                                                let _ = buildBr(builder)(blocks.continueBlock)
                                                in
                                                    let _ = positionBuilderAtEnd(builder)(blocks.returnNoneBlock)
                                                    in
                                                        let _ =
                                                            buildStore(builder)(emitAllocAdtRuntimeManaged(builder)(i64)(i8)(mallocFn)(mallocType)(0)(0)("read_line_none"))(scratch.resultSlot)
                                                        in
                                                            let _ = buildBr(builder)(blocks.continueBlock)
                                                            in
                                                                let _ = positionBuilderAtEnd(builder)(blocks.overflowBlock)
                                                                in emitReadLinePanicMessage(builder)(i64)(i8))

// `Ashes.IO.readLine`: one line from stdin as `Option(Str)`, `None` only at EOF with nothing left
// unread. Bytes are read one at a time via a raw `read` syscall (no read-ahead buffering — stage
// 0's own `EmitReadLine` batches reads through a persistent module-global ring for throughput; this
// codegen has no equivalent module-global-state mechanism yet, so it trades syscall count for
// simplicity, matching this file's other "correct, not yet optimized" stand-ins). `\n` ends the
// line (not included); a `\r` immediately before it is dropped; every other byte accumulates into a
// fixed 64 KiB stack buffer — safe even inside a `musttail`-driven loop, since each call's frame
// (and its alloca) is reused by the very next tail call rather than piling up. A line at or past
// that capacity panics rather than silently truncating, matching stage 0's own overflow contract.
let emitReadLineFromFd context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType fd =
    (let scratch = emitReadLineScratch(builder)(i64)(i8)
    in
        let blocks = emitReadLineBlocks(context)(function_)
        in
            scratch.lenSlot
            |> buildStore(builder)(constInt(i64)(0u64)(false))
            |> (given (_) -> buildBr(builder)(blocks.loopBlock))
            |> (given (_) -> emitReadLineReadAndInspect(i64)(i8)(builder)(fd)(scratch)(blocks))
            |> (given (_) -> emitReadLineFinish(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(scratch)(blocks))
            |> (given (_) -> positionBuilderAtEnd(builder)(blocks.continueBlock))
            |> (given (_) -> buildLoad(builder)(i64)(scratch.resultSlot)("read_line_result_value")))

let emitReadLine context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType =
    false
    |> constInt(i64)(0u64)
    |> emitReadLineFromFd(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)

let emitUtf8ByteInRange builder i64 byteVal minVal maxVal name =
    buildAnd(builder)(buildICmp(builder)(intPredicateUge)(byteVal)(constInt(i64)(Ashes.Number.UInt.fromInt64(minVal))(false))(name + "_ge"))(buildICmp(builder)(intPredicateUle)(byteVal)(constInt(i64)(Ashes.Number.UInt.fromInt64(maxVal))(false))(name + "_le"))(name)

// Every continuation byte a multi-byte UTF-8 sequence still needs, checked once the caller has
// already confirmed enough bytes remain — safe to load unconditionally (no separate per-byte block,
// unlike stage 0's own `EmitUtf8SequenceValidation`) precisely because that bounds check already ran.
let recursive emitUtf8ContinuationChain builder i64 i8 bytesPtr index checks acc =
    match checks with
        | [] -> acc
        | (offset, minVal, maxVal) :: rest ->
            "utf8_cont_idx"
            |> buildAdd(builder)(index)(constInt(i64)(Ashes.Number.UInt.fromInt64(offset))(false))
            |> (given (byteAddr) -> emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(byteAddr)("utf8_cont_byte"))
            |> (given (byteVal) -> emitUtf8ByteInRange(builder)(i64)(byteVal)(minVal)(maxVal)("utf8_cont_range"))
            |> (given (inRange) ->
                "utf8_cont_and"
                |> buildAnd(builder)(acc)(inRange)
                |> emitUtf8ContinuationChain(builder)(i64)(i8)(bytesPtr)(index)(rest))

// One UTF-8 lead-byte classification: `sequenceLength` total bytes (the lead byte plus every entry
// in `checks`, each an `(offset, min, max)` continuation-byte range relative to the lead byte's own
// index), matching one call site of stage 0's own `EmitUtf8SequenceValidation`. Positions the
// builder at `entryBlock`, checks enough bytes remain (`invalidBlock` otherwise), validates every
// continuation byte's range, advances `indexSlot` by `sequenceLength` and jumps to `loopBlock` on
// success, `invalidBlock` otherwise.
let emitUtf8ValidateSequence context function_ i64 i8 builder bytesPtr len indexSlot sequenceLength checks entryBlock loopBlock invalidBlock =
    (let bodyBlock = appendBasicBlock(context)(function_)("utf8_seq_body")
    in
        let advanceBlock = appendBasicBlock(context)(function_)("utf8_seq_advance")
        in
            let index =
                entryBlock
                |> positionBuilderAtEnd(builder)
                |> (given (_) -> buildLoad(builder)(i64)(indexSlot)("utf8_seq_index"))
            in
                "utf8_seq_enough"
                |> buildICmp(builder)(intPredicateUge)(buildSub(builder)(len)(index)("utf8_seq_remaining"))(constInt(i64)(Ashes.Number.UInt.fromInt64(sequenceLength))(false))
                |> (given (enough) -> buildCondBr(builder)(enough)(bodyBlock)(invalidBlock))
                |> (given (_) -> positionBuilderAtEnd(builder)(bodyBlock))
                |> (given (_) ->
                    false
                    |> constInt(int1Type(context))(1u64)
                    |> emitUtf8ContinuationChain(builder)(i64)(i8)(bytesPtr)(index)(checks))
                |> (given (bytesOk) -> buildCondBr(builder)(bytesOk)(advanceBlock)(invalidBlock))
                |> (given (_) -> positionBuilderAtEnd(builder)(advanceBlock))
                |> (given (_) ->
                    buildStore(builder)(buildAdd(builder)(index)(constInt(i64)(Ashes.Number.UInt.fromInt64(sequenceLength))(false))("utf8_seq_next"))(indexSlot))
                |> (given (_) -> buildBr(builder)(loopBlock)))

// One step of the lead-byte classification chain: if `leadByte` matches (`predicate` against
// `boundary`), validate a `sequenceLength`-byte sequence with `checks`' continuation ranges;
// otherwise fall through to the next step's block, where the builder is left positioned.
let emitUtf8ClassifyStep context function_ i64 i8 builder bytesPtr len indexSlot loopBlock invalidBlock leadByte predicate boundary sequenceLength checks name =
    (let matchBlock = appendBasicBlock(context)(function_)(name)
    in
        let afterBlock = appendBasicBlock(context)(function_)(name + "_after")
        in
            afterBlock
            |> buildCondBr(builder)(buildICmp(builder)(predicate)(leadByte)(constInt(i64)(boundary)(false))(name + "_check"))(matchBlock)
            |> (given (_) -> emitUtf8ValidateSequence(context)(function_)(i64)(i8)(builder)(bytesPtr)(len)(indexSlot)(sequenceLength)(checks)(matchBlock)(loopBlock)(invalidBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(afterBlock)))

// Lead-byte classification chain: dispatches a non-ASCII lead byte to the matching
// `emitUtf8ValidateSequence` call (or straight to `invalidBlock`), matching stage 0's own
// `EmitValidateUtf8ClassifyNonAscii` range table exactly (`0xC2`-`0xDF` two-byte; `0xE0`/`0xED`
// three-byte with a narrowed second-byte range for the two overlong/surrogate-adjacent leads,
// `0xE1`-`0xEC`/`0xEE`-`0xEF` the standard `0x80`-`0xBF` range; `0xF0`/`0xF4` four-byte with a
// narrowed second-byte range, `0xF1`-`0xF3` the standard range; everything past `0xF4` invalid).
let emitUtf8ClassifyLeadByte context function_ i64 i8 builder bytesPtr len indexSlot leadByte loopBlock invalidBlock =
    (let step = emitUtf8ClassifyStep(context)(function_)(i64)(i8)(builder)(bytesPtr)(len)(indexSlot)(loopBlock)(invalidBlock)(leadByte)
    in
        let inspectBlock = appendBasicBlock(context)(function_)("utf8_inspect")
        in
            inspectBlock
            |> buildCondBr(builder)(buildICmp(builder)(intPredicateUlt)(leadByte)(constInt(i64)(194u64)(false))("utf8_lt_c2"))(invalidBlock)
            |> (given (_) -> positionBuilderAtEnd(builder)(inspectBlock))
            |> (given (_) -> step(intPredicateUle)(223u64)(2)([(1, 128, 191)])("utf8_two"))
            |> (given (_) -> step(intPredicateEq)(224u64)(3)([(1, 160, 191), (2, 128, 191)])("utf8_e0"))
            |> (given (_) -> step(intPredicateUle)(236u64)(3)([(1, 128, 191), (2, 128, 191)])("utf8_three_a"))
            |> (given (_) -> step(intPredicateEq)(237u64)(3)([(1, 128, 159), (2, 128, 191)])("utf8_ed"))
            |> (given (_) -> step(intPredicateUle)(239u64)(3)([(1, 128, 191), (2, 128, 191)])("utf8_three_b"))
            |> (given (_) -> step(intPredicateEq)(240u64)(4)([(1, 144, 191), (2, 128, 191), (3, 128, 191)])("utf8_f0"))
            |> (given (_) -> step(intPredicateUle)(243u64)(4)([(1, 128, 191), (2, 128, 191), (3, 128, 191)])("utf8_four"))
            |> (given (_) -> step(intPredicateEq)(244u64)(4)([(1, 128, 143), (2, 128, 191), (3, 128, 191)])("utf8_f4"))
            |> (given (_) -> buildBr(builder)(invalidBlock)))

// Loop head of `emitValidateUtf8`: end-of-input check, lead-byte load, and the ASCII/non-ASCII
// branch. Leaves the builder positioned in the fresh non-ASCII block and returns the lead byte.
let emitValidateUtf8LoopHead context function_ i64 i8 builder bytesPtr len indexSlot loopBlock asciiBlock validBlock prefix =
    (let inspectBlock = appendBasicBlock(context)(function_)(prefix + "_inspect")
    in
        let nonAsciiBlock = appendBasicBlock(context)(function_)(prefix + "_non_ascii")
        in
            let leadByte =
                loopBlock
                |> positionBuilderAtEnd(builder)
                |> (given (_) -> buildLoad(builder)(i64)(indexSlot)(prefix + "_index_value"))
                |> (given (index) ->
                    buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(index)(len)(prefix + "_done"))(validBlock)(inspectBlock))
                |> (given (_) -> positionBuilderAtEnd(builder)(inspectBlock))
                |> (given (_) -> buildLoad(builder)(i64)(indexSlot)(prefix + "_lead_index"))
                |> (given (index) -> emitLoadByteAtI64(builder)(i64)(i8)(bytesPtr)(index)(prefix + "_lead"))
            in
                nonAsciiBlock
                |> buildCondBr(builder)(buildICmp(builder)(intPredicateUlt)(leadByte)(constInt(i64)(128u64)(false))(prefix + "_is_ascii"))(asciiBlock)
                |> (given (_) -> positionBuilderAtEnd(builder)(nonAsciiBlock))
                |> (given (_) -> leadByte))

// Whole-buffer UTF-8 validity (`1` valid, `0` invalid), matching stage 0's own `EmitValidateUtf8`
// exactly — needed by `Directory.entries`, which (unlike `File.readText`'s validated decode, not
// yet ported here either) must reject a non-UTF-8 directory entry name rather than propagate it.
let emitValidateUtf8 context function_ i64 i8 builder bytesPtr len prefix =
    (let indexSlot = buildAlloca(builder)(i64)(prefix + "_index")
    in
        let resultSlot = buildAlloca(builder)(i64)(prefix + "_result")
        in
            let loopBlock = appendBasicBlock(context)(function_)(prefix + "_loop")
            in
                let asciiBlock = appendBasicBlock(context)(function_)(prefix + "_ascii")
                in
                    let validBlock = appendBasicBlock(context)(function_)(prefix + "_valid")
                    in
                        let invalidBlock = appendBasicBlock(context)(function_)(prefix + "_invalid")
                        in
                            let continueBlock = appendBasicBlock(context)(function_)(prefix + "_continue")
                            in
                                let leadByte =
                                    indexSlot
                                    |> buildStore(builder)(constInt(i64)(0u64)(false))
                                    |> (given (_) -> buildBr(builder)(loopBlock))
                                    |> (given (_) -> emitValidateUtf8LoopHead(context)(function_)(i64)(i8)(builder)(bytesPtr)(len)(indexSlot)(loopBlock)(asciiBlock)(validBlock)(prefix))
                                in
                                    invalidBlock
                                    |> emitUtf8ClassifyLeadByte(context)(function_)(i64)(i8)(builder)(bytesPtr)(len)(indexSlot)(leadByte)(loopBlock)
                                    |> (given (_) -> positionBuilderAtEnd(builder)(asciiBlock))
                                    |> (given (_) ->
                                        buildStore(builder)(buildAdd(builder)(buildLoad(builder)(i64)(indexSlot)(prefix + "_ascii_index"))(constInt(i64)(1u64)(false))(prefix + "_ascii_next"))(indexSlot))
                                    |> (given (_) -> buildBr(builder)(loopBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(validBlock))
                                    |> (given (_) ->
                                        buildStore(builder)(constInt(i64)(1u64)(false))(resultSlot))
                                    |> (given (_) -> buildBr(builder)(continueBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(invalidBlock))
                                    |> (given (_) ->
                                        buildStore(builder)(constInt(i64)(0u64)(false))(resultSlot))
                                    |> (given (_) -> buildBr(builder)(continueBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(continueBlock))
                                    |> (given (_) -> buildLoad(builder)(i64)(resultSlot)(prefix + "_result_value")))

// `Ok(Unit)` if `succeeded`, `Error(message)` (built from `codes`, an ASCII code-point list, via
// `emitAsciiHeapString`) otherwise — the raw-syscall-convention ("non-negative return means
// success") counterpart of stage 0's own `EmitFilesystemStatusResult`, shared by every filesystem
// builtin below that reports a plain "did it work" outcome.
let emitFilesystemStatusResult context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType succeeded codes prefix =
    (let resultSlot = buildAlloca(builder)(i64)(prefix + "_status_result")
    in
        let okBlock = appendBasicBlock(context)(function_)(prefix + "_status_ok")
        in
            let errorBlock = appendBasicBlock(context)(function_)(prefix + "_status_error")
            in
                let continueBlock = appendBasicBlock(context)(function_)(prefix + "_status_continue")
                in
                    errorBlock
                    |> buildCondBr(builder)(succeeded)(okBlock)
                    |> (given (_) -> positionBuilderAtEnd(builder)(okBlock))
                    |> (given (_) ->
                        buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(emitAllocAdtStack(builder)(i64)(0)(prefix + "_unit"))(prefix + "_ok"))(resultSlot))
                    |> (given (_) -> buildBr(builder)(continueBlock))
                    |> (given (_) -> positionBuilderAtEnd(builder)(errorBlock))
                    |> (given (_) ->
                        buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(codes)(prefix + "_error_msg"))(prefix + "_error"))(resultSlot))
                    |> (given (_) -> buildBr(builder)(continueBlock))
                    |> (given (_) -> positionBuilderAtEnd(builder)(continueBlock))
                    |> (given (_) -> buildLoad(builder)(i64)(resultSlot)(prefix + "_status_value")))

// "Ashes.IO.File.writeText() failed" — stage 0's `FileWriteFailedMessage`, shared by `writeText`
// and `writeBytes` exactly as stage 0 shares the one constant across both writers.
let fileWriteTextErrorCodes = [65, 115, 104, 101, 115, 46, 73, 79, 46, 70, 105, 108, 101, 46, 119, 114, 105, 116, 101, 84, 101, 120, 116, 40, 41, 32, 102, 97, 105, 108, 101, 100]

// `Ashes.IO.File.writeText(path, text)`: `openat(O_WRONLY|O_CREAT|O_TRUNC, 0644)`, one `write`
// syscall for the whole payload — matching this file's other direct-write paths (`emitWriteStrBytesToFd`),
// which also assume a single `write` covers the buffer rather than porting stage 0's full
// partial-write retry loop — then `close`. Only `open` failure is surfaced as `Error`.
let emitFileWriteText context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType pathRef textRef =
    (let writeBlock = appendBasicBlock(context)(function_)("file_write_text_write")
    in
        let joinBlock = appendBasicBlock(context)(function_)("file_write_text_join")
        in
            let fd =
                "file_write_text_path"
                |> emitStringToCString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(pathRef)
                |> (given (pathCstr) -> buildPtrToInt(builder)(pathCstr)(i64)("file_write_text_path_addr"))
                |> (given (pathAddr) ->
                    false
                    |> constInt(i64)(420u64)
                    |> emitLinuxOpenat(builder)(i64)(pathAddr)(constInt(i64)(577u64)(false)))
            in
                let openOk =
                    buildICmp(builder)(intPredicateSge)(fd)(constInt(i64)(0u64)(false))("file_write_text_open_ok")
                in
                    match emitStringParts(builder)(i64)(ptrType)(textRef)("file_write_text_text") with
                        | (textLen, textAddr) ->
                            joinBlock
                            |> buildCondBr(builder)(openOk)(writeBlock)
                            |> (given (_) -> positionBuilderAtEnd(builder)(writeBlock))
                            |> (given (_) -> emitLinuxWrite(builder)(i64)(fd)(textAddr)(textLen))
                            |> (given (_) -> emitLinuxClose(builder)(i64)(fd))
                            |> (given (_) -> buildBr(builder)(joinBlock))
                            |> (given (_) -> positionBuilderAtEnd(builder)(joinBlock))
                            |> (given (_) -> emitFilesystemStatusResult(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(openOk)(fileWriteTextErrorCodes)("file_write_text")))

let fileReplaceErrorCodes = [65, 115, 104, 101, 115, 46, 73, 79, 46, 70, 105, 108, 101, 46, 114, 101, 112, 108, 97, 99, 101, 58, 32, 99, 111, 117, 108, 100, 32, 110, 111, 116, 32, 114, 101, 112, 108, 97, 99, 101, 32, 100, 101, 115, 116, 105, 110, 97, 116, 105, 111, 110]

// `Ashes.IO.File.replace(source, destination)`: rejects a `source` that names a directory (probed
// via `openat(source, O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC, 0)` — the exact flag value stage 0's own
// `EmitLinuxFileReplace` uses for this probe), otherwise `rename(source, destination)`. The probe
// fd is closed unconditionally on the join path — closing an invalid fd is a harmless `-EBADF` at
// the raw-syscall level, so no branch is needed to skip it.
let emitFileReplace context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType sourceRef destinationRef =
    (let renameBlock = appendBasicBlock(context)(function_)("file_replace_rename")
    in
        let joinBlock = appendBasicBlock(context)(function_)("file_replace_join")
        in
            let statusSlot = buildAlloca(builder)(i64)("file_replace_status")
            in
                let sourceAddr =
                    "file_replace_source"
                    |> emitStringToCString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(sourceRef)
                    |> (given (sourceCstr) -> buildPtrToInt(builder)(sourceCstr)(i64)("file_replace_source_addr"))
                in
                    let destinationAddr =
                        "file_replace_destination"
                        |> emitStringToCString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(destinationRef)
                        |> (given (destinationCstr) -> buildPtrToInt(builder)(destinationCstr)(i64)("file_replace_destination_addr"))
                    in
                        let probeFd =
                            false
                            |> constInt(i64)(0u64)
                            |> emitLinuxOpenat(builder)(i64)(sourceAddr)(constInt(i64)(2293760u64)(false))
                        in
                            statusSlot
                            |> buildStore(builder)(constInt(i64)(Ashes.Number.UInt.fromInt64(-1))(false))
                            |> (given (_) ->
                                buildCondBr(builder)(buildICmp(builder)(intPredicateSge)(probeFd)(constInt(i64)(0u64)(false))("file_replace_source_is_directory"))(joinBlock)(renameBlock))
                            |> (given (_) -> positionBuilderAtEnd(builder)(renameBlock))
                            |> (given (_) ->
                                buildStore(builder)(emitLinuxRename(builder)(i64)(sourceAddr)(destinationAddr))(statusSlot))
                            |> (given (_) -> buildBr(builder)(joinBlock))
                            |> (given (_) -> positionBuilderAtEnd(builder)(joinBlock))
                            |> (given (_) -> emitLinuxClose(builder)(i64)(probeFd))
                            |> (given (_) -> buildLoad(builder)(i64)(statusSlot)("file_replace_status_value"))
                            |> (given (status) ->
                                buildICmp(builder)(intPredicateSge)(status)(constInt(i64)(0u64)(false))("file_replace_succeeded"))
                            |> (given (succeeded) -> emitFilesystemStatusResult(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(succeeded)(fileReplaceErrorCodes)("file_replace")))

let directoryCreateAllErrorCodes = [65, 115, 104, 101, 115, 46, 73, 79, 46, 68, 105, 114, 101, 99, 116, 111, 114, 121, 46, 99, 114, 101, 97, 116, 101, 65, 108, 108, 58, 32, 99, 111, 117, 108, 100, 32, 110, 111, 116, 32, 99, 114, 101, 97, 116, 101, 32, 100, 105, 114, 101, 99, 116, 111, 114, 121]

// One path component: `mkdir(path, 0777)`, tolerating an already-existing directory — probed via
// `openat(path, O_DIRECTORY, 0)` (closed unconditionally afterward; closing an invalid fd is a
// harmless `-EBADF` at the raw-syscall level, so no branch is needed to skip it) — the same
// "created OR already a directory" success condition stage 0's own `EmitLinuxMkdirExistingOk` checks.
let emitDirectoryMkdirExistingOk builder i64 pathAddr =
    (let mkdirStatus =
        false
        |> constInt(i64)(511u64)
        |> emitLinuxMkdir(builder)(i64)(pathAddr)
    in
        let openStatus =
            false
            |> constInt(i64)(0u64)
            |> emitLinuxOpenat(builder)(i64)(pathAddr)(constInt(i64)(720896u64)(false))
        in
            let opened =
                buildICmp(builder)(intPredicateSge)(openStatus)(constInt(i64)(0u64)(false))("dir_create_opened")
            in
                openStatus
                |> emitLinuxClose(builder)(i64)
                |> (given (_) ->
                    buildOr(builder)(buildICmp(builder)(intPredicateSge)(mkdirStatus)(constInt(i64)(0u64)(false))("dir_create_mkdir_ok"))(opened)("dir_create_component_ok"))
                |> (given (componentOk) ->
                    buildSelect(builder)(componentOk)(constInt(i64)(0u64)(false))(constInt(i64)(Ashes.Number.UInt.fromInt64(-1))(false))("dir_create_component_status")))

// `emitDirectoryCreateAll`'s loop head: still-in-range check, dispatching to the byte inspection
// or the final whole-path `mkdir`.
let emitDirectoryCreateLoopCheck builder i64 length indexSlot checkBlock byteBlock finalBlock =
    checkBlock
    |> positionBuilderAtEnd(builder)
    |> (given (_) -> buildLoad(builder)(i64)(indexSlot)("dir_create_index_value"))
    |> (given (index) ->
        buildCondBr(builder)(buildICmp(builder)(intPredicateUlt)(index)(length)("dir_create_in_range"))(byteBlock)(finalBlock))

// `emitDirectoryCreateAll`'s per-component `mkdir`: NUL-terminate at the current `/`, `mkdir` the
// prefix, restore the byte, and stop the walk (`joinBlock`) if the component failed. The component
// status round-trips through `statusSlot` (stored, then reloaded for the failure check) so the
// join block reads the most recent component's outcome either way.
let emitDirectoryCreateComponentStep builder i64 i8 pathAddr indexSlot statusSlot bytePtr current componentBlock advanceBlock joinBlock =
    componentBlock
    |> positionBuilderAtEnd(builder)
    |> (given (_) ->
        buildStore(builder)(constInt(i8)(0u64)(false))(bytePtr))
    |> (given (_) -> emitDirectoryMkdirExistingOk(builder)(i64)(pathAddr))
    |> (given (componentStatus) -> buildStore(builder)(componentStatus)(statusSlot))
    |> (given (_) -> buildStore(builder)(current)(bytePtr))
    |> (given (_) -> buildLoad(builder)(i64)(statusSlot)("dir_create_component_status_value"))
    |> (given (componentStatus) ->
        buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(componentStatus)(constInt(i64)(0u64)(false))("dir_create_component_failed"))(joinBlock)(advanceBlock))

// `emitDirectoryCreateAll`'s byte inspection and advance: read the byte at the walk index, take
// the component step at each `/`, then advance the index and loop.
let emitDirectoryCreateByteStep builder i64 i8 pathCstr pathAddr indexSlot statusSlot byteBlock componentBlock advanceBlock joinBlock checkBlock =
    (let bytePtr =
        byteBlock
        |> positionBuilderAtEnd(builder)
        |> (given (_) -> buildLoad(builder)(i64)(indexSlot)("dir_create_byte_index"))
        |> (given (index) -> buildGEP(builder)(i8)(pathCstr)([index])(1u32)("dir_create_byte_ptr"))
    in
        let current = buildLoad(builder)(i8)(bytePtr)("dir_create_byte_value")
        in
            advanceBlock
            |> buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(current)(constInt(i8)(47u64)(false))("dir_create_is_slash"))(componentBlock)
            |> (given (_) -> emitDirectoryCreateComponentStep(builder)(i64)(i8)(pathAddr)(indexSlot)(statusSlot)(bytePtr)(current)(componentBlock)(advanceBlock)(joinBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(advanceBlock))
            |> (given (_) ->
                buildStore(builder)(buildAdd(builder)(buildLoad(builder)(i64)(indexSlot)("dir_create_advance_index"))(constInt(i64)(1u64)(false))("dir_create_next"))(indexSlot))
            |> (given (_) -> buildBr(builder)(checkBlock)))

// `emitDirectoryCreateAll`'s finish: one final `mkdir` on the whole path, then the join block turns
// the stored status into `Ok(Unit)`/`Error(...)`.
let emitDirectoryCreateFinish context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType pathAddr statusSlot finalBlock joinBlock =
    finalBlock
    |> positionBuilderAtEnd(builder)
    |> (given (_) ->
        buildStore(builder)(emitDirectoryMkdirExistingOk(builder)(i64)(pathAddr))(statusSlot))
    |> (given (_) -> buildBr(builder)(joinBlock))
    |> (given (_) -> positionBuilderAtEnd(builder)(joinBlock))
    |> (given (_) -> buildLoad(builder)(i64)(statusSlot)("dir_create_status_value"))
    |> (given (status) ->
        buildICmp(builder)(intPredicateEq)(status)(constInt(i64)(0u64)(false))("dir_create_succeeded"))
    |> (given (succeeded) -> emitFilesystemStatusResult(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(succeeded)(directoryCreateAllErrorCodes)("dir_create"))

// `Ashes.IO.Directory.createAll(path)`: `mkdir`s each path component in turn, temporarily
// NUL-terminating the C string at each `/` byte (restored immediately after, exactly like stage 0's
// own `EmitLinuxDirectoryCreateAll` byte-walk) so `emitDirectoryMkdirExistingOk` only ever sees one
// path prefix at a time, then a final call on the whole (untouched) path. `statusSlot` starts at
// `-1`, so an empty path — which never reaches either the per-component or final call — falls
// through to `Error` on its own, with no separate empty-path check needed.
let emitDirectoryCreateAll context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType pathRef =
    (let checkBlock = appendBasicBlock(context)(function_)("dir_create_check")
    in
        let byteBlock = appendBasicBlock(context)(function_)("dir_create_byte")
        in
            let componentBlock = appendBasicBlock(context)(function_)("dir_create_component")
            in
                let advanceBlock = appendBasicBlock(context)(function_)("dir_create_advance")
                in
                    let finalBlock = appendBasicBlock(context)(function_)("dir_create_final")
                    in
                        let joinBlock = appendBasicBlock(context)(function_)("dir_create_join")
                        in
                            let indexSlot = buildAlloca(builder)(i64)("dir_create_index")
                            in
                                let statusSlot = buildAlloca(builder)(i64)("dir_create_status_slot")
                                in
                                    let pathCstr = emitStringToCString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(pathRef)("dir_create_path")
                                    in
                                        let pathAddr = buildPtrToInt(builder)(pathCstr)(i64)("dir_create_path_addr")
                                        in
                                            match emitStringParts(builder)(i64)(ptrType)(pathRef)("dir_create_len") with
                                                | (length, _sourceAddr) ->
                                                    indexSlot
                                                    |> buildStore(builder)(constInt(i64)(1u64)(false))
                                                    |> (given (_) ->
                                                        buildStore(builder)(constInt(i64)(Ashes.Number.UInt.fromInt64(-1))(false))(statusSlot))
                                                    |> (given (_) ->
                                                        buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(length)(constInt(i64)(0u64)(false))("dir_create_nonempty"))(checkBlock)(joinBlock))
                                                    |> (given (_) -> emitDirectoryCreateLoopCheck(builder)(i64)(length)(indexSlot)(checkBlock)(byteBlock)(finalBlock))
                                                    |> (given (_) -> emitDirectoryCreateByteStep(builder)(i64)(i8)(pathCstr)(pathAddr)(indexSlot)(statusSlot)(byteBlock)(componentBlock)(advanceBlock)(joinBlock)(checkBlock))
                                                    |> (given (_) -> emitDirectoryCreateFinish(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(pathAddr)(statusSlot)(finalBlock)(joinBlock)))

let directoryEntriesFailedCodes = [65, 115, 104, 101, 115, 46, 73, 79, 46, 68, 105, 114, 101, 99, 116, 111, 114, 121, 46, 101, 110, 116, 114, 105, 101, 115, 58, 32, 99, 111, 117, 108, 100, 32, 110, 111, 116, 32, 101, 110, 117, 109, 101, 114, 97, 116, 101, 32, 100, 105, 114, 101, 99, 116, 111, 114, 121]

let directoryEntriesInvalidUtf8Codes = [65, 115, 104, 101, 115, 46, 73, 79, 46, 68, 105, 114, 101, 99, 116, 111, 114, 121, 46, 101, 110, 116, 114, 105, 101, 115, 58, 32, 101, 110, 116, 114, 121, 32, 110, 97, 109, 101, 32, 105, 115, 32, 110, 111, 116, 32, 118, 97, 108, 105, 100, 32, 85, 84, 70, 45, 56]

let directoryRemoveTreeErrorCodes = [65, 115, 104, 101, 115, 46, 73, 79, 46, 68, 105, 114, 101, 99, 116, 111, 114, 121, 46, 114, 101, 109, 111, 118, 101, 84, 114, 101, 101, 58, 32, 99, 111, 117, 108, 100, 32, 110, 111, 116, 32, 114, 101, 109, 111, 118, 101, 32, 116, 114, 101, 101]

// The grow-and-publish half of the name-append trampoline below: reload the array and count
// through the caller's stack slots, `realloc` one more pointer slot, and store the fresh copy —
// or free it and return `-1` if the grow failed.
let emitDirectoryNameAppendGrow builder i64 i32 ptrType dirExt fn copy reallocFailedBlock successBlock =
    (let arraySlot = getParam(fn)(0u32)
    in
        let countSlot = getParam(fn)(1u32)
        in
            let count = buildLoad(builder)(i64)(countSlot)("name_count")
            in
                let newArray =
                    buildCall(builder)(dirExt.reallocType)(dirExt.reallocFn)([buildLoad(builder)(ptrType)(arraySlot)("name_array"), buildMul(builder)(buildAdd(builder)(count)(constInt(i64)(1u64)(false))("name_next_count"))(constInt(i64)(8u64)(false))("name_array_bytes")])(2u32)("name_array_grown")
                in
                    successBlock
                    |> buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(buildPtrToInt(builder)(newArray)(i64)("name_array_grown_int"))(constInt(i64)(0u64)(false))("name_realloc_failed"))(reallocFailedBlock)
                    |> (given (_) -> positionBuilderAtEnd(builder)(successBlock))
                    |> (given (_) ->
                        "name_slot"
                        |> buildGEP(builder)(ptrType)(newArray)([count])(1u32)
                        |> buildStore(builder)(copy))
                    |> (given (_) -> buildStore(builder)(newArray)(arraySlot))
                    |> (given (_) ->
                        buildStore(builder)(buildAdd(builder)(count)(constInt(i64)(1u64)(false))("stored_count"))(countSlot))
                    |> (given (_) ->
                        false
                        |> constInt(i32)(0u64)
                        |> buildRet(builder)))

// `<prefix>_append(arraySlot, countSlot, name) -> i32`: the growable-name-array helper
// `Directory.entries` calls per kept entry — copy the NUL-terminated name into a fresh `malloc`
// buffer, grow the pointer array by one slot, and publish both through the caller's stack slots,
// returning `0`/`-1` — stage 0's own `__ashes_directory_name_append` exactly. A fresh
// internal-linkage copy per instruction occurrence (the `prefix` carries the target temp) stands
// in for stage 0's get-or-create lookup: `LLVMGetNamedFunction` has no binding here, and
// `addFunction` on a taken name silently renames, which would orphan a second occurrence's calls.
// Internal linkage is load-bearing, not tidiness — the same `dso_local` address-taking constraint
// the lifted-function declaration loop documents applies to `qsort` receiving this function's
// address below.
let emitDirectoryNameAppendFunction module_ context builder i64 i8 i32 ptrType mallocFn mallocType freeFn freeType dirExt prefix =
    (let fnType = functionType(i32)([ptrType, ptrType, ptrType])(3u32)(false)
    in
        let fn = addFunction(module_)(prefix + "_append")(fnType)
        in
            let allocationFailedBlock =
                linkageInternal
                |> setLinkage(fn)
                |> (given (_) ->
                    "entry"
                    |> appendBasicBlock(context)(fn)
                    |> positionBuilderAtEnd(builder))
                |> (given (_) -> appendBasicBlock(context)(fn)("allocation_failed"))
            in
                let copyBlock = appendBasicBlock(context)(fn)("copy")
                in
                    let reallocFailedBlock = appendBasicBlock(context)(fn)("realloc_failed")
                    in
                        let successBlock = appendBasicBlock(context)(fn)("success")
                        in
                            let name = getParam(fn)(2u32)
                            in
                                let nameLen = buildCall(builder)(dirExt.strlenType)(dirExt.strlenFn)([name])(1u32)("name_length")
                                in
                                    let copy =
                                        buildCall(builder)(mallocType)(mallocFn)([buildAdd(builder)(nameLen)(constInt(i64)(1u64)(false))("copy_size")])(1u32)("name_copy")
                                    in
                                        copyBlock
                                        |> buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(buildPtrToInt(builder)(copy)(i64)("name_copy_int"))(constInt(i64)(0u64)(false))("name_copy_failed"))(allocationFailedBlock)
                                        |> (given (_) -> positionBuilderAtEnd(builder)(copyBlock))
                                        |> (given (_) ->
                                            buildCall(builder)(dirExt.memmoveType)(dirExt.memmoveFn)([copy, name, buildAdd(builder)(nameLen)(constInt(i64)(1u64)(false))("copy_bytes")])(3u32)("copy_name"))
                                        |> (given (_) -> emitDirectoryNameAppendGrow(builder)(i64)(i32)(ptrType)(dirExt)(fn)(copy)(reallocFailedBlock)(successBlock))
                                        |> (given (_) -> positionBuilderAtEnd(builder)(reallocFailedBlock))
                                        |> (given (_) -> buildCall(builder)(freeType)(freeFn)([copy])(1u32)(""))
                                        |> (given (_) ->
                                            false
                                            |> constInt(i32)(Ashes.Number.UInt.fromInt64(-1))
                                            |> buildRet(builder))
                                        |> (given (_) -> positionBuilderAtEnd(builder)(allocationFailedBlock))
                                        |> (given (_) ->
                                            false
                                            |> constInt(i32)(Ashes.Number.UInt.fromInt64(-1))
                                            |> buildRet(builder))
                                        |> (given (_) -> (fn, fnType)))

// `<prefix>_compare(leftSlot, rightSlot) -> i32`: `qsort`'s element comparator — each argument is
// a pointer INTO the name array, so one load recovers each `char*` before `strcmp` — stage 0's own
// `__ashes_directory_name_compare` exactly.
let emitDirectoryNameCompareFunction module_ context builder i32 ptrType dirExt prefix =
    (let fnType = functionType(i32)([ptrType, ptrType])(2u32)(false)
    in
        let fn = addFunction(module_)(prefix + "_compare")(fnType)
        in
            let left =
                linkageInternal
                |> setLinkage(fn)
                |> (given (_) ->
                    "entry"
                    |> appendBasicBlock(context)(fn)
                    |> positionBuilderAtEnd(builder))
                |> (given (_) ->
                    buildLoad(builder)(ptrType)(getParam(fn)(0u32))("left"))
            in
                "right"
                |> buildLoad(builder)(ptrType)(getParam(fn)(1u32))
                |> (given (right) ->
                    "comparison"
                    |> buildCall(builder)(dirExt.strcmpType)(dirExt.strcmpFn)([left, right])(2u32)
                    |> buildRet(builder))
                |> (given (_) -> fn))

// `<prefix>_visit(path, stat, typeflag, ftwbuf) -> i32`: `nftw`'s per-node callback — `remove`
// works on files and (post-order, thanks to `FTW_DEPTH` below) freshly-emptied directories alike —
// stage 0's own `__ashes_remove_tree_visit` exactly.
let emitRemoveTreeVisitFunction module_ context builder i32 ptrType dirExt prefix =
    (let fnType = functionType(i32)([ptrType, ptrType, i32, ptrType])(4u32)(false)
    in
        let fn = addFunction(module_)(prefix + "_visit")(fnType)
        in
            linkageInternal
            |> setLinkage(fn)
            |> (given (_) ->
                "entry"
                |> appendBasicBlock(context)(fn)
                |> positionBuilderAtEnd(builder))
            |> (given (_) ->
                "remove_tree_entry"
                |> buildCall(builder)(dirExt.removeType)(dirExt.removeFn)([getParam(fn)(0u32)])(1u32)
                |> buildRet(builder))
            |> (given (_) -> fn))

// `openat(path, O_DIRECTORY, 0)` then `fdopendir` — the raw open (the same probe flags
// `emitDirectoryMkdirExistingOk` uses) hands its fd to libc's stream so `readdir` can walk it.
// Either failure path lands in `finishBlock` with `statusSlot` still holding its failure
// initializer. Returns the `DIR*` stream, which dominates every later use (the loop and close
// blocks are reachable only through the stream block).
let emitDirectoryEntriesOpenStream context function_ i64 i32 ptrType builder dirExt pathAddr loopBlock finishBlock prefix =
    (let streamBlock = appendBasicBlock(context)(function_)(prefix + "_stream")
    in
        let streamErrorBlock = appendBasicBlock(context)(function_)(prefix + "_stream_error")
        in
            let fd =
                false
                |> constInt(i64)(0u64)
                |> emitLinuxOpenat(builder)(i64)(pathAddr)(constInt(i64)(720896u64)(false))
            in
                let dir =
                    streamBlock
                    |> buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(fd)(constInt(i64)(0u64)(false))(prefix + "_open_failed"))(finishBlock)
                    |> (given (_) -> positionBuilderAtEnd(builder)(streamBlock))
                    |> (given (_) -> buildCall(builder)(dirExt.fdopendirType)(dirExt.fdopendirFn)([buildTrunc(builder)(fd)(i32)(prefix + "_fd_i32")])(1u32)(prefix + "_fdopendir"))
                in
                    loopBlock
                    |> buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(buildPtrToInt(builder)(dir)(i64)(prefix + "_stream_int"))(constInt(i64)(0u64)(false))(prefix + "_stream_failed"))(streamErrorBlock)
                    |> (given (_) -> positionBuilderAtEnd(builder)(streamErrorBlock))
                    |> (given (_) -> emitLinuxClose(builder)(i64)(fd))
                    |> (given (_) -> buildBr(builder)(finishBlock))
                    |> (given (_) -> dir))

// `readdir` returned NULL: errno still `0` means end-of-stream (status `0`), anything else a real
// read error (status `1`) — the pre-`readdir` errno reset in the loop head makes this
// distinguishable at all.
let emitDirectoryEntriesReadError builder i64 i32 errnoPtr statusSlot endBlock closeBlock prefix =
    endBlock
    |> positionBuilderAtEnd(builder)
    |> (given (_) -> buildLoad(builder)(i32)(errnoPtr)(prefix + "_errno_value"))
    |> (given (errnoValue) ->
        buildICmp(builder)(intPredicateNe)(errnoValue)(constInt(i32)(0u64)(false))(prefix + "_read_error"))
    |> (given (readError) ->
        buildStore(builder)(buildSelect(builder)(readError)(constInt(i64)(1u64)(false))(constInt(i64)(0u64)(false))(prefix + "_read_status"))(statusSlot))
    |> (given (_) -> buildBr(builder)(closeBlock))

// `.` or `..` — the two entries every directory reports that the builtin's contract excludes.
let emitDirectoryEntryIsDot builder i64 i8 namePtr prefix =
    (let first = buildLoad(builder)(i8)(namePtr)(prefix + "_first")
    in
        let second =
            buildLoad(builder)(i8)(buildGEP(builder)(i8)(namePtr)([constInt(i64)(1u64)(false)])(1u32)(prefix + "_second_ptr"))(prefix + "_second")
        in
            let third =
                buildLoad(builder)(i8)(buildGEP(builder)(i8)(namePtr)([constInt(i64)(2u64)(false)])(1u32)(prefix + "_third_ptr"))(prefix + "_third")
            in
                prefix + "_first_dot"
                |> buildICmp(builder)(intPredicateEq)(first)(constInt(i8)(46u64)(false))
                |> (given (firstDot) ->
                    buildAnd(builder)(firstDot)(buildOr(builder)(buildICmp(builder)(intPredicateEq)(second)(constInt(i8)(0u64)(false))(prefix + "_one_dot"))(buildAnd(builder)(buildICmp(builder)(intPredicateEq)(second)(constInt(i8)(46u64)(false))(prefix + "_second_dot"))(buildICmp(builder)(intPredicateEq)(third)(constInt(i8)(0u64)(false))(prefix + "_third_zero"))(prefix + "_two_dots"))(prefix + "_dot_tail"))(prefix + "_is_dot")))

// The `readdir` loop: reset errno, read one entry (NULL ends the loop through the errno check
// above), skip `.`/`..`, reject a non-UTF-8 name (status `2`), and append everything else through
// the name-append trampoline (whose failure also stops the walk). The UTF-8 validator allocates
// two scratch slots at the current position, so a huge directory costs two stack words per kept
// entry — the same correct-not-yet-optimized trade `readLine`'s per-call buffer makes.
let emitDirectoryEntriesLoop context function_ i64 i8 i32 ptrType builder dirExt appendFn appendFnType arraySlot countSlot statusSlot dir loopBlock closeBlock prefix =
    (let direntBlock = appendBasicBlock(context)(function_)(prefix + "_entry")
    in
        let nameBlock = appendBasicBlock(context)(function_)(prefix + "_name")
        in
            let appendBlock = appendBasicBlock(context)(function_)(prefix + "_append")
            in
                let endBlock = appendBasicBlock(context)(function_)(prefix + "_end")
                in
                    let invalidBlock = appendBasicBlock(context)(function_)(prefix + "_invalid_utf8")
                    in
                        let errnoPtr =
                            loopBlock
                            |> positionBuilderAtEnd(builder)
                            |> (given (_) -> buildCall(builder)(dirExt.errnoLocationType)(dirExt.errnoLocationFn)([])(0u32)(prefix + "_errno"))
                        in
                            let entry =
                                errnoPtr
                                |> buildStore(builder)(constInt(i32)(0u64)(false))
                                |> (given (_) -> buildCall(builder)(dirExt.readdirType)(dirExt.readdirFn)([dir])(1u32)(prefix + "_readdir"))
                            in
                                let namePtr =
                                    direntBlock
                                    |> buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(buildPtrToInt(builder)(entry)(i64)(prefix + "_entry_int"))(constInt(i64)(0u64)(false))(prefix + "_at_end"))(endBlock)
                                    |> (given (_) -> emitDirectoryEntriesReadError(builder)(i64)(i32)(errnoPtr)(statusSlot)(endBlock)(closeBlock)(prefix))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(direntBlock))
                                    |> (given (_) -> buildGEP(builder)(i8)(entry)([constInt(i64)(19u64)(false)])(1u32)(prefix + "_name_ptr"))
                                in
                                    prefix
                                    |> emitDirectoryEntryIsDot(builder)(i64)(i8)(namePtr)
                                    |> (given (isDot) -> buildCondBr(builder)(isDot)(loopBlock)(nameBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(nameBlock))
                                    |> (given (_) -> buildCall(builder)(dirExt.strlenType)(dirExt.strlenFn)([namePtr])(1u32)(prefix + "_name_length"))
                                    |> (given (nameLen) -> emitValidateUtf8(context)(function_)(i64)(i8)(builder)(namePtr)(nameLen)(prefix + "_utf8"))
                                    |> (given (validation) ->
                                        buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(validation)(constInt(i64)(0u64)(false))(prefix + "_name_valid"))(appendBlock)(invalidBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(appendBlock))
                                    |> (given (_) -> buildCall(builder)(appendFnType)(appendFn)([arraySlot, countSlot, namePtr])(3u32)(prefix + "_append_call"))
                                    |> (given (appendStatus) ->
                                        buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(appendStatus)(constInt(i32)(0u64)(false))(prefix + "_append_failed"))(closeBlock)(loopBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(invalidBlock))
                                    |> (given (_) ->
                                        buildStore(builder)(constInt(i64)(2u64)(false))(statusSlot))
                                    |> (given (_) -> buildBr(builder)(closeBlock)))

// `closedir` regardless of how the loop ended; a close failure overrides an otherwise-clean
// status with the generic failure `1`.
let emitDirectoryEntriesClose builder i64 i32 ptrType dirExt dir statusSlot closeBlock finishBlock prefix =
    closeBlock
    |> positionBuilderAtEnd(builder)
    |> (given (_) -> buildCall(builder)(dirExt.closedirType)(dirExt.closedirFn)([dir])(1u32)(prefix + "_closedir"))
    |> (given (closeStatus) ->
        buildICmp(builder)(intPredicateEq)(closeStatus)(constInt(i32)(0u64)(false))(prefix + "_close_ok"))
    |> (given (closeOk) ->
        buildStore(builder)(buildSelect(builder)(closeOk)(buildLoad(builder)(i64)(statusSlot)(prefix + "_prior_status"))(constInt(i64)(1u64)(false))(prefix + "_final_status"))(statusSlot))
    |> (given (_) -> buildBr(builder)(finishBlock))

type DirectoryEntriesResultBlocks =
    | deSortBlock: LLVMBasicBlockRef
    | deBuildCheckBlock: LLVMBasicBlockRef
    | deBuildBlock: LLVMBasicBlockRef
    | deOkBlock: LLVMBasicBlockRef
    | deCleanupCheckBlock: LLVMBasicBlockRef
    | deCleanupBlock: LLVMBasicBlockRef
    | deErrorBlock: LLVMBasicBlockRef
    | deDoneBlock: LLVMBasicBlockRef

let emitDirectoryEntriesResultBlocks context function_ prefix =
    DirectoryEntriesResultBlocks(
        deSortBlock = appendBasicBlock(context)(function_)(prefix + "_sort"),
        deBuildCheckBlock = appendBasicBlock(context)(function_)(prefix + "_build_check"),
        deBuildBlock = appendBasicBlock(context)(function_)(prefix + "_build"),
        deOkBlock = appendBasicBlock(context)(function_)(prefix + "_ok"),
        deCleanupCheckBlock = appendBasicBlock(context)(function_)(prefix + "_cleanup_check"),
        deCleanupBlock = appendBasicBlock(context)(function_)(prefix + "_cleanup"),
        deErrorBlock = appendBasicBlock(context)(function_)(prefix + "_error"),
        deDoneBlock = appendBasicBlock(context)(function_)(prefix + "_done")
    )

// The success half of the result build: walk the sorted array from the end so each prepend leaves
// `array[0]` at the final list's head, copying every C-string name into a fresh RC-managed `Str`
// and every cons cell into a fresh RC-managed 16-byte `[head][tail]` payload (the same layout
// `BytesFromList`'s reading side documents: `nil` is `0`, `cons` is head at `+0`/tail at `+8`
// past an ordinary RC header). Each name copy and finally the array itself go back to `free`.
let emitDirectoryEntriesBuildList builder i64 i8 ptrType mallocFn mallocType memcpyFn memcpyType freeFn freeType dirExt array listSlot indexSlot resultSlot blocks prefix =
    (let nextIndex =
        blocks.deBuildCheckBlock
        |> positionBuilderAtEnd(builder)
        |> (given (_) -> buildLoad(builder)(i64)(indexSlot)(prefix + "_index"))
        |> (given (index) ->
            buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(index)(constInt(i64)(0u64)(false))(prefix + "_has_next"))(blocks.deBuildBlock)(blocks.deOkBlock))
        |> (given (_) -> positionBuilderAtEnd(builder)(blocks.deBuildBlock))
        |> (given (_) ->
            buildSub(builder)(buildLoad(builder)(i64)(indexSlot)(prefix + "_build_index"))(constInt(i64)(1u64)(false))(prefix + "_next_index"))
    in
        let namePtr =
            buildLoad(builder)(ptrType)(buildGEP(builder)(ptrType)(array)([nextIndex])(1u32)(prefix + "_name_slot"))(prefix + "_name_value")
        in
            let stringRef =
                prefix + "_name_len"
                |> buildCall(builder)(dirExt.strlenType)(dirExt.strlenFn)([namePtr])(1u32)
                |> (given (nameLen) ->
                    emitHeapStringFromBytesAddr(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(buildPtrToInt(builder)(namePtr)(i64)(prefix + "_name_addr"))(nameLen)(prefix + "_string"))
            in
                let consPtr = emitRcAllocPayloadPtr(builder)(i64)(i8)(mallocFn)(mallocType)(16)(prefix + "_cons")
                in
                    consPtr
                    |> buildStore(builder)(stringRef)
                    |> (given (_) ->
                        prefix + "_tail_ptr"
                        |> gepBytes(builder)(i64)(i8)(consPtr)(8)
                        |> buildStore(builder)(buildLoad(builder)(i64)(listSlot)(prefix + "_tail")))
                    |> (given (_) ->
                        buildStore(builder)(buildPtrToInt(builder)(consPtr)(i64)(prefix + "_cons_value"))(listSlot))
                    |> (given (_) -> buildCall(builder)(freeType)(freeFn)([namePtr])(1u32)(""))
                    |> (given (_) -> buildStore(builder)(nextIndex)(indexSlot))
                    |> (given (_) -> buildBr(builder)(blocks.deBuildCheckBlock))
                    |> (given (_) -> positionBuilderAtEnd(builder)(blocks.deOkBlock))
                    |> (given (_) -> buildCall(builder)(freeType)(freeFn)([array])(1u32)(""))
                    |> (given (_) ->
                        buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(buildLoad(builder)(i64)(listSlot)(prefix + "_list"))(prefix + "_ok"))(resultSlot))
                    |> (given (_) -> buildBr(builder)(blocks.deDoneBlock)))

// The failure half: free every collected name copy and the array, then pick between the two error
// texts on the stored status (`2` marks the invalid-UTF-8 case).
let emitDirectoryEntriesCleanup builder i64 i8 ptrType mallocFn mallocType memcpyFn memcpyType freeFn freeType array status indexSlot resultSlot blocks prefix =
    (let cleanupNext =
        blocks.deCleanupCheckBlock
        |> positionBuilderAtEnd(builder)
        |> (given (_) -> buildLoad(builder)(i64)(indexSlot)(prefix + "_cleanup_index"))
        |> (given (cleanupIndex) ->
            buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(cleanupIndex)(constInt(i64)(0u64)(false))(prefix + "_cleanup_more"))(blocks.deCleanupBlock)(blocks.deErrorBlock))
        |> (given (_) -> positionBuilderAtEnd(builder)(blocks.deCleanupBlock))
        |> (given (_) ->
            buildSub(builder)(buildLoad(builder)(i64)(indexSlot)(prefix + "_cleanup_cur"))(constInt(i64)(1u64)(false))(prefix + "_cleanup_next"))
    in
        ""
        |> buildCall(builder)(freeType)(freeFn)([buildLoad(builder)(ptrType)(buildGEP(builder)(ptrType)(array)([cleanupNext])(1u32)(prefix + "_cleanup_slot"))(prefix + "_cleanup_name")])(1u32)
        |> (given (_) -> buildStore(builder)(cleanupNext)(indexSlot))
        |> (given (_) -> buildBr(builder)(blocks.deCleanupCheckBlock))
        |> (given (_) -> positionBuilderAtEnd(builder)(blocks.deErrorBlock))
        |> (given (_) -> buildCall(builder)(freeType)(freeFn)([array])(1u32)(""))
        |> (given (_) ->
            buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(buildSelect(builder)(buildICmp(builder)(intPredicateEq)(status)(constInt(i64)(2u64)(false))(prefix + "_invalid_utf8"))(emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(directoryEntriesInvalidUtf8Codes)(prefix + "_invalid_msg"))(emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(directoryEntriesFailedCodes)(prefix + "_failed_msg"))(prefix + "_error_text"))(prefix + "_error"))(resultSlot))
        |> (given (_) -> buildBr(builder)(blocks.deDoneBlock)))

// The result build: sort the collected names deterministically (`qsort` + the `strcmp` comparator
// trampoline), then either the list build or the cleanup/error path, converging on `deDoneBlock`.
let emitDirectoryEntriesResult context function_ i64 i8 i32 ptrType builder mallocFn mallocType memcpyFn memcpyType freeFn freeType dirExt compareFn arraySlot countSlot statusSlot finishBlock prefix =
    (let blocks = emitDirectoryEntriesResultBlocks(context)(function_)(prefix)
    in
        let array =
            finishBlock
            |> positionBuilderAtEnd(builder)
            |> (given (_) -> buildLoad(builder)(ptrType)(arraySlot)(prefix + "_array_value"))
        in
            let count = buildLoad(builder)(i64)(countSlot)(prefix + "_count_value")
            in
                let status = buildLoad(builder)(i64)(statusSlot)(prefix + "_status_read")
                in
                    let resultSlot = buildAlloca(builder)(i64)(prefix + "_result_slot")
                    in
                        let listSlot = buildAlloca(builder)(i64)(prefix + "_list_slot")
                        in
                            let indexSlot = buildAlloca(builder)(i64)(prefix + "_index_slot")
                            in
                                listSlot
                                |> buildStore(builder)(constInt(i64)(0u64)(false))
                                |> (given (_) -> buildStore(builder)(count)(indexSlot))
                                |> (given (_) ->
                                    buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(status)(constInt(i64)(0u64)(false))(prefix + "_succeeded"))(blocks.deSortBlock)(blocks.deCleanupCheckBlock))
                                |> (given (_) -> positionBuilderAtEnd(builder)(blocks.deSortBlock))
                                |> (given (_) -> buildCall(builder)(dirExt.qsortType)(dirExt.qsortFn)([array, count, constInt(i64)(8u64)(false), compareFn])(4u32)(""))
                                |> (given (_) -> buildBr(builder)(blocks.deBuildCheckBlock))
                                |> (given (_) -> emitDirectoryEntriesBuildList(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(freeFn)(freeType)(dirExt)(array)(listSlot)(indexSlot)(resultSlot)(blocks)(prefix))
                                |> (given (_) -> emitDirectoryEntriesCleanup(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(freeFn)(freeType)(array)(status)(indexSlot)(resultSlot)(blocks)(prefix))
                                |> (given (_) -> positionBuilderAtEnd(builder)(blocks.deDoneBlock))
                                |> (given (_) -> buildLoad(builder)(i64)(resultSlot)(prefix + "_result")))

// `Ashes.IO.Directory.entries(path)`: enumerate one directory (sorted, `.`/`..` excluded) into
// `Ok(list Str)`, or `Error(...)` on open/read/close failure or a non-UTF-8 entry name — the
// libc-stream port of stage 0's own `EmitLinuxDirectoryEntriesCore`. The current block is closed
// with a branch to a fresh resume block first, so the two trampoline function bodies can be built
// (repositioning the shared builder into them) without a `getInsertBlock` binding to restore from.
let emitDirectoryEntries moduleRef context function_ i64 i8 i32 ptrType builder mallocFn mallocType freeFn freeType memcpyFn memcpyType dirExt pathRef prefix =
    (let resumeBlock = appendBasicBlock(context)(function_)(prefix + "_resume")
    in
        let trampolines =
            resumeBlock
            |> buildBr(builder)
            |> (given (_) -> (emitDirectoryNameAppendFunction(moduleRef)(context)(builder)(i64)(i8)(i32)(ptrType)(mallocFn)(mallocType)(freeFn)(freeType)(dirExt)(prefix), emitDirectoryNameCompareFunction(moduleRef)(context)(builder)(i32)(ptrType)(dirExt)(prefix)))
        in
            match trampolines with
                | ((appendFn, appendFnType), compareFn) ->
                    let loopBlock = appendBasicBlock(context)(function_)(prefix + "_loop")
                    in
                        let closeBlock = appendBasicBlock(context)(function_)(prefix + "_close")
                        in
                            let finishBlock = appendBasicBlock(context)(function_)(prefix + "_finish")
                            in
                                let arraySlot =
                                    resumeBlock
                                    |> positionBuilderAtEnd(builder)
                                    |> (given (_) -> buildAlloca(builder)(ptrType)(prefix + "_array"))
                                in
                                    let countSlot = buildAlloca(builder)(i64)(prefix + "_count")
                                    in
                                        let statusSlot = buildAlloca(builder)(i64)(prefix + "_status")
                                        in
                                            let pathAddr =
                                                prefix + "_path"
                                                |> emitStringToCString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(pathRef)
                                                |> (given (pathCstr) -> buildPtrToInt(builder)(pathCstr)(i64)(prefix + "_path_addr"))
                                            in
                                                let dir =
                                                    arraySlot
                                                    |> buildStore(builder)(buildIntToPtr(builder)(constInt(i64)(0u64)(false))(ptrType)(prefix + "_null_array"))
                                                    |> (given (_) ->
                                                        buildStore(builder)(constInt(i64)(0u64)(false))(countSlot))
                                                    |> (given (_) ->
                                                        buildStore(builder)(constInt(i64)(1u64)(false))(statusSlot))
                                                    |> (given (_) -> emitDirectoryEntriesOpenStream(context)(function_)(i64)(i32)(ptrType)(builder)(dirExt)(pathAddr)(loopBlock)(finishBlock)(prefix))
                                                in
                                                    prefix
                                                    |> emitDirectoryEntriesLoop(context)(function_)(i64)(i8)(i32)(ptrType)(builder)(dirExt)(appendFn)(appendFnType)(arraySlot)(countSlot)(statusSlot)(dir)(loopBlock)(closeBlock)
                                                    |> (given (_) -> emitDirectoryEntriesClose(builder)(i64)(i32)(ptrType)(dirExt)(dir)(statusSlot)(closeBlock)(finishBlock)(prefix))
                                                    |> (given (_) -> emitDirectoryEntriesResult(context)(function_)(i64)(i8)(i32)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(freeFn)(freeType)(dirExt)(compareFn)(arraySlot)(countSlot)(statusSlot)(finishBlock)(prefix)))

// `Ashes.IO.Directory.removeTree(path)`: `lstat` first — a missing path is `Ok(Unit)` only when
// `errno` says `ENOENT`, anything else is a real failure — then libc's `nftw` post-order walk
// (`FTW_DEPTH|FTW_PHYS`, 32 fds) with the `remove`-everything visitor trampoline, exactly stage
// 0's own `EmitLinuxDirectoryRemoveTree`.
let emitDirectoryRemoveTree moduleRef context function_ i64 i8 i32 ptrType builder mallocFn mallocType memcpyFn memcpyType dirExt pathRef prefix =
    (let resumeBlock = appendBasicBlock(context)(function_)(prefix + "_resume")
    in
        let visitFn =
            resumeBlock
            |> buildBr(builder)
            |> (given (_) -> emitRemoveTreeVisitFunction(moduleRef)(context)(builder)(i32)(ptrType)(dirExt)(prefix))
        in
            let missingBlock = appendBasicBlock(context)(function_)(prefix + "_missing")
            in
                let walkBlock = appendBasicBlock(context)(function_)(prefix + "_walk")
                in
                    let doneBlock = appendBasicBlock(context)(function_)(prefix + "_done")
                    in
                        let pathCstr =
                            resumeBlock
                            |> positionBuilderAtEnd(builder)
                            |> (given (_) -> emitStringToCString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(pathRef)(prefix + "_path"))
                        in
                            let resultSlot = buildAlloca(builder)(i64)(prefix + "_result")
                            in
                                let statBuffer =
                                    buildAlloca(builder)(arrayType(i8)(256u64))(prefix + "_stat")
                                in
                                    prefix + "_lstat"
                                    |> buildCall(builder)(dirExt.lstatType)(dirExt.lstatFn)([pathCstr, statBuffer])(2u32)
                                    |> (given (lstatStatus) ->
                                        buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(lstatStatus)(constInt(i32)(0u64)(false))(prefix + "_is_missing"))(missingBlock)(walkBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(missingBlock))
                                    |> (given (_) -> buildCall(builder)(dirExt.errnoLocationType)(dirExt.errnoLocationFn)([])(0u32)(prefix + "_errno"))
                                    |> (given (errnoPtr) -> buildLoad(builder)(i32)(errnoPtr)(prefix + "_errno_value"))
                                    |> (given (errnoValue) ->
                                        buildICmp(builder)(intPredicateEq)(errnoValue)(constInt(i32)(2u64)(false))(prefix + "_enoent"))
                                    |> (given (isMissing) ->
                                        buildStore(builder)(emitFilesystemStatusResult(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(isMissing)(directoryRemoveTreeErrorCodes)(prefix + "_probe"))(resultSlot))
                                    |> (given (_) -> buildBr(builder)(doneBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(walkBlock))
                                    |> (given (_) -> buildCall(builder)(dirExt.nftwType)(dirExt.nftwFn)([pathCstr, visitFn, constInt(i32)(32u64)(false), constInt(i32)(9u64)(false)])(4u32)(prefix + "_nftw"))
                                    |> (given (nftwStatus) ->
                                        buildICmp(builder)(intPredicateEq)(nftwStatus)(constInt(i32)(0u64)(false))(prefix + "_walk_ok"))
                                    |> (given (walkOk) ->
                                        buildStore(builder)(emitFilesystemStatusResult(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(walkOk)(directoryRemoveTreeErrorCodes)(prefix + "_walk"))(resultSlot))
                                    |> (given (_) -> buildBr(builder)(doneBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))
                                    |> (given (_) -> buildLoad(builder)(i64)(resultSlot)(prefix + "_result_value")))

let fileOpenErrorCodes = [65, 115, 104, 101, 115, 46, 73, 79, 46, 70, 105, 108, 101, 46, 111, 112, 101, 110, 58, 32, 99, 111, 117, 108, 100, 32, 110, 111, 116, 32, 111, 112, 101, 110, 32, 102, 105, 108, 101]

let fileReadChunkErrorCodes = [65, 115, 104, 101, 115, 46, 73, 79, 46, 70, 105, 108, 101, 46, 114, 101, 97, 100, 67, 104, 117, 110, 107, 58, 32, 114, 101, 97, 100, 32, 102, 97, 105, 108, 101, 100]

// `Ashes.IO.File.open(path)`: `openat(path, O_RDONLY, 0)` — a `FileHandle` is the raw fd as one
// scalar word (stage 0's own `EmitFileOpen` contract), wrapped `Ok(fd)`/`Error(...)`. The
// resource-side contract (automatic close at scope exit, use-after-close/double-close
// diagnostics) is ownership work this codegen does not carry yet — an unclosed handle leaks its
// fd until process exit.
let emitFileOpen context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType pathRef =
    (let resultSlot = buildAlloca(builder)(i64)("file_open_result")
    in
        let okBlock = appendBasicBlock(context)(function_)("file_open_ok")
        in
            let errorBlock = appendBasicBlock(context)(function_)("file_open_error")
            in
                let continueBlock = appendBasicBlock(context)(function_)("file_open_continue")
                in
                    let fd =
                        "file_open_path"
                        |> emitStringToCString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(pathRef)
                        |> (given (pathCstr) -> buildPtrToInt(builder)(pathCstr)(i64)("file_open_path_addr"))
                        |> (given (pathAddr) ->
                            false
                            |> constInt(i64)(0u64)
                            |> emitLinuxOpenat(builder)(i64)(pathAddr)(constInt(i64)(0u64)(false)))
                    in
                        okBlock
                        |> buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(fd)(constInt(i64)(0u64)(false))("file_open_failed"))(errorBlock)
                        |> (given (_) -> positionBuilderAtEnd(builder)(okBlock))
                        |> (given (_) ->
                            buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(fd)("file_open_ok_result"))(resultSlot))
                        |> (given (_) -> buildBr(builder)(continueBlock))
                        |> (given (_) -> positionBuilderAtEnd(builder)(errorBlock))
                        |> (given (_) ->
                            buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(fileOpenErrorCodes)("file_open_error_msg"))("file_open_error_result"))(resultSlot))
                        |> (given (_) -> buildBr(builder)(continueBlock))
                        |> (given (_) -> positionBuilderAtEnd(builder)(continueBlock))
                        |> (given (_) -> buildLoad(builder)(i64)(resultSlot)("file_open_result_value")))

// `Ashes.IO.File.readChunk(handle)(count)`: one `read` syscall of up to `count` bytes into a fresh
// RC string sized by the caller's count; `n < 0` is `Error`, `n == 0` (EOF) an empty-string `Ok`,
// exactly stage 0's `EmitFileReadChunk`.
let emitFileReadChunk context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType handleVal countVal =
    (let resultSlot = buildAlloca(builder)(i64)("file_chunk_result")
    in
        let okBlock = appendBasicBlock(context)(function_)("file_chunk_ok")
        in
            let errorBlock = appendBasicBlock(context)(function_)("file_chunk_error")
            in
                let continueBlock = appendBasicBlock(context)(function_)("file_chunk_continue")
                in
                    let payloadPtr =
                        "file_chunk_payload_size"
                        |> buildAdd(builder)(countVal)(constInt(i64)(8u64)(false))
                        |> (given (payloadSize) -> emitRcAllocPayloadPtrDynamic(builder)(i64)(i8)(mallocFn)(mallocType)(payloadSize)("file_chunk"))
                    in
                        let stringRef = buildPtrToInt(builder)(payloadPtr)(i64)("file_chunk_string")
                        in
                            let nRead =
                                "file_chunk_dest_addr"
                                |> buildAdd(builder)(stringRef)(constInt(i64)(8u64)(false))
                                |> (given (destAddr) -> emitLinuxRead(builder)(i64)(handleVal)(destAddr)(countVal))
                            in
                                okBlock
                                |> buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(nRead)(constInt(i64)(0u64)(false))("file_chunk_failed"))(errorBlock)
                                |> (given (_) -> positionBuilderAtEnd(builder)(okBlock))
                                |> (given (_) -> buildStore(builder)(nRead)(payloadPtr))
                                |> (given (_) ->
                                    buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(stringRef)("file_chunk_ok_result"))(resultSlot))
                                |> (given (_) -> buildBr(builder)(continueBlock))
                                |> (given (_) -> positionBuilderAtEnd(builder)(errorBlock))
                                |> (given (_) ->
                                    buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(fileReadChunkErrorCodes)("file_chunk_error_msg"))("file_chunk_error_result"))(resultSlot))
                                |> (given (_) -> buildBr(builder)(continueBlock))
                                |> (given (_) -> positionBuilderAtEnd(builder)(continueBlock))
                                |> (given (_) -> buildLoad(builder)(i64)(resultSlot)("file_chunk_result_value")))

// `Ashes.IO.File.close(handle)`: fire-and-forget `close` returning `Ok(Unit)`, stage 0's own
// `EmitFileClose` contract exactly (no failure mode is surfaced).
let emitFileClose builder i64 i8 ptrType mallocFn mallocType handleVal =
    handleVal
    |> emitLinuxClose(builder)(i64)
    |> (given (_) -> emitAllocAdtStack(builder)(i64)(0)("file_close_unit"))
    |> (given (unitValue) -> emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(unitValue)("file_close_ok"))

// "Ashes.IO.File.readText() failed" — stage 0's `FileReadFailedMessage`, shared by `readText`,
// `readAllBytes`, and `mmap` exactly as stage 0 shares the one constant across the read family.
let fileReadFailedCodes = [65, 115, 104, 101, 115, 46, 73, 79, 46, 70, 105, 108, 101, 46, 114, 101, 97, 100, 84, 101, 120, 116, 40, 41, 32, 102, 97, 105, 108, 101, 100]

// "Ashes.IO.File.readText() encountered invalid UTF-8" — stage 0's `FileReadInvalidUtf8Message`.
let fileReadInvalidUtf8Codes = [65, 115, 104, 101, 115, 46, 73, 79, 46, 70, 105, 108, 101, 46, 114, 101, 97, 100, 84, 101, 120, 116, 40, 41, 32, 101, 110, 99, 111, 117, 110, 116, 101, 114, 101, 100, 32, 105, 110, 118, 97, 108, 105, 100, 32, 85, 84, 70, 45, 56]

// The whole-file read's control-flow skeleton, shared by `readText` and `readAllBytes`: measure
// the file (`readMeasureBlock`/`readRewindBlock`), cap it (`readCapBlock`, `readText` only), allocate and fill
// (`readAllocBlock` + the `readLoopCheckBlock`/`readLoopBodyBlock`/`readAdvanceBlock` read loop), then land on one
// of three outcomes (`readOkBlock`, `readInvalidBlock` for a failed UTF-8 check, or `readCloseErrorBlock` →
// `readErrorBlock` for any syscall failure) before every path rejoins at `readContinueBlock`.
type FileReadBlocks =
    | readMeasureBlock: LLVMBasicBlockRef
    | readRewindBlock: LLVMBasicBlockRef
    | readCapBlock: LLVMBasicBlockRef
    | readAllocBlock: LLVMBasicBlockRef
    | readLoopCheckBlock: LLVMBasicBlockRef
    | readLoopBodyBlock: LLVMBasicBlockRef
    | readAdvanceBlock: LLVMBasicBlockRef
    | readFinishBlock: LLVMBasicBlockRef
    | readOkBlock: LLVMBasicBlockRef
    | readInvalidBlock: LLVMBasicBlockRef
    | readCloseErrorBlock: LLVMBasicBlockRef
    | readErrorBlock: LLVMBasicBlockRef
    | readContinueBlock: LLVMBasicBlockRef

let emitFileReadBlocks context function_ prefix =
    FileReadBlocks(
        readMeasureBlock = appendBasicBlock(context)(function_)(prefix + "_measure"),
        readRewindBlock = appendBasicBlock(context)(function_)(prefix + "_rewind"),
        readCapBlock = appendBasicBlock(context)(function_)(prefix + "_cap"),
        readAllocBlock = appendBasicBlock(context)(function_)(prefix + "_alloc"),
        readLoopCheckBlock = appendBasicBlock(context)(function_)(prefix + "_loop_check"),
        readLoopBodyBlock = appendBasicBlock(context)(function_)(prefix + "_loop_body"),
        readAdvanceBlock = appendBasicBlock(context)(function_)(prefix + "_advance"),
        readFinishBlock = appendBasicBlock(context)(function_)(prefix + "_finish"),
        readOkBlock = appendBasicBlock(context)(function_)(prefix + "_ok"),
        readInvalidBlock = appendBasicBlock(context)(function_)(prefix + "_invalid"),
        readCloseErrorBlock = appendBasicBlock(context)(function_)(prefix + "_close_error"),
        readErrorBlock = appendBasicBlock(context)(function_)(prefix + "_error"),
        readContinueBlock = appendBasicBlock(context)(function_)(prefix + "_continue")
    )

// Open the path read-only; a negative fd skips straight to `readErrorBlock` (nothing to close).
let emitFileReadOpen builder i64 i8 ptrType mallocFn mallocType memcpyFn memcpyType pathRef blocks prefix =
    (let fd =
        prefix + "_path"
        |> emitStringToCString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(pathRef)
        |> (given (pathCstr) -> buildPtrToInt(builder)(pathCstr)(i64)(prefix + "_path_addr"))
        |> (given (pathAddr) ->
            false
            |> constInt(i64)(0u64)
            |> emitLinuxOpenat(builder)(i64)(pathAddr)(constInt(i64)(0u64)(false)))
    in
        let _ =
            buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(fd)(constInt(i64)(0u64)(false))(prefix + "_open_failed"))(blocks.readErrorBlock)(blocks.readMeasureBlock)
        in fd)

// `readMeasureBlock`: `lseek(fd, 0, SEEK_END)` sizes the file. `readRewindBlock`: `lseek(fd, 0, SEEK_SET)`
// puts the read loop back at the start. `readCapBlock`: `readText` rejects a file larger than stage 0's
// `MaxFileReadBytes` (1 MiB); `readAllBytes` is uncapped on Linux, exactly stage 0's split.
let emitFileReadMeasure builder i64 fd capped blocks prefix =
    (let length =
        blocks.readMeasureBlock
        |> positionBuilderAtEnd(builder)
        |> (given (_) ->
            false
            |> constInt(i64)(2u64)
            |> emitLinuxLseek(builder)(i64)(fd)(constInt(i64)(0u64)(false)))
    in
        let _ =
            buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(length)(constInt(i64)(0u64)(false))(prefix + "_measure_failed"))(blocks.readCloseErrorBlock)(blocks.readRewindBlock)
        in
            let _ =
                blocks.readRewindBlock
                |> positionBuilderAtEnd(builder)
                |> (given (_) ->
                    false
                    |> constInt(i64)(0u64)
                    |> emitLinuxLseek(builder)(i64)(fd)(constInt(i64)(0u64)(false)))
                |> (given (rewound) ->
                    buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(rewound)(constInt(i64)(0u64)(false))(prefix + "_rewind_failed"))(blocks.readCloseErrorBlock)(blocks.readCapBlock))
            in
                let _ = positionBuilderAtEnd(builder)(blocks.readCapBlock)
                in
                    let _ =
                        if capped
                        then
                            buildCondBr(builder)(buildICmp(builder)(intPredicateUgt)(length)(constInt(i64)(1048576u64)(false))(prefix + "_too_large"))(blocks.readCloseErrorBlock)(blocks.readAllocBlock)
                        else buildBr(builder)(blocks.readAllocBlock)
                    in length)

// `readAllocBlock`: a fresh RC payload of `length + 8` bytes with the length word stored up front.
// The read loop then fills `[stringRef + 8, stringRef + 8 + length)` one `read` at a time until
// `remaining` hits zero; `n <= 0` (error or premature EOF) bails to `readCloseErrorBlock`.
let emitFileReadLoop builder i64 i8 mallocFn mallocType fd length remainingSlot cursorSlot blocks prefix =
    (let _ = positionBuilderAtEnd(builder)(blocks.readAllocBlock)
    in
        let payloadPtr =
            prefix + "_payload_size"
            |> buildAdd(builder)(length)(constInt(i64)(8u64)(false))
            |> (given (payloadSize) -> emitRcAllocPayloadPtrDynamic(builder)(i64)(i8)(mallocFn)(mallocType)(payloadSize)(prefix))
        in
            let stringRef = buildPtrToInt(builder)(payloadPtr)(i64)(prefix + "_string")
            in
                let _ = buildStore(builder)(length)(payloadPtr)
                in
                    let _ =
                        remainingSlot
                        |> buildStore(builder)(length)
                        |> (given (_) ->
                            buildStore(builder)(buildAdd(builder)(stringRef)(constInt(i64)(8u64)(false))(prefix + "_data_addr"))(cursorSlot))
                        |> (given (_) -> buildBr(builder)(blocks.readLoopCheckBlock))
                        |> (given (_) -> positionBuilderAtEnd(builder)(blocks.readLoopCheckBlock))
                        |> (given (_) -> buildLoad(builder)(i64)(remainingSlot)(prefix + "_remaining_value"))
                        |> (given (remaining) ->
                            buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(remaining)(constInt(i64)(0u64)(false))(prefix + "_done"))(blocks.readFinishBlock)(blocks.readLoopBodyBlock))
                    in
                        let _ =
                            blocks.readLoopBodyBlock
                            |> positionBuilderAtEnd(builder)
                            |> (given (_) ->
                                prefix + "_remaining_reload"
                                |> buildLoad(builder)(i64)(remainingSlot)
                                |> emitLinuxRead(builder)(i64)(fd)(buildLoad(builder)(i64)(cursorSlot)(prefix + "_cursor_value")))
                            |> (given (n) ->
                                blocks.readAdvanceBlock
                                |> buildCondBr(builder)(buildICmp(builder)(intPredicateSle)(n)(constInt(i64)(0u64)(false))(prefix + "_read_failed"))(blocks.readCloseErrorBlock)
                                |> (given (_) -> positionBuilderAtEnd(builder)(blocks.readAdvanceBlock))
                                |> (given (_) ->
                                    buildStore(builder)(buildSub(builder)(buildLoad(builder)(i64)(remainingSlot)(prefix + "_remaining_now"))(n)(prefix + "_remaining_next"))(remainingSlot))
                                |> (given (_) ->
                                    buildStore(builder)(buildAdd(builder)(buildLoad(builder)(i64)(cursorSlot)(prefix + "_cursor_now"))(n)(prefix + "_cursor_next"))(cursorSlot))
                                |> (given (_) -> buildBr(builder)(blocks.readLoopCheckBlock)))
                        in stringRef)

// `readFinishBlock`: `readText` validates the payload as UTF-8 before wrapping it (`readInvalidBlock`
// closes the fd and reports stage 0's invalid-UTF-8 message); `readAllBytes` wraps unconditionally.
// Every error path funnels through `readCloseErrorBlock` (close, then the shared read-failed message).
let emitFileReadFinish context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType fd validateUtf8 stringRef length resultSlot blocks prefix =
    (let _ = positionBuilderAtEnd(builder)(blocks.readFinishBlock)
    in
        let _ =
            if validateUtf8
            then
                prefix + "_bytes_ptr"
                |> buildIntToPtr(builder)(buildAdd(builder)(stringRef)(constInt(i64)(8u64)(false))(prefix + "_bytes_addr"))(ptrType)
                |> (given (bytesPtr) -> emitValidateUtf8(context)(function_)(i64)(i8)(builder)(bytesPtr)(length)(prefix + "_utf8"))
                |> (given (valid) ->
                    buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(valid)(constInt(i64)(0u64)(false))(prefix + "_utf8_invalid"))(blocks.readInvalidBlock)(blocks.readOkBlock))
            else buildBr(builder)(blocks.readOkBlock)
        in
            blocks.readOkBlock
            |> positionBuilderAtEnd(builder)
            |> (given (_) -> emitLinuxClose(builder)(i64)(fd))
            |> (given (_) ->
                buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(stringRef)(prefix + "_ok_result"))(resultSlot))
            |> (given (_) -> buildBr(builder)(blocks.readContinueBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(blocks.readInvalidBlock))
            |> (given (_) -> emitLinuxClose(builder)(i64)(fd))
            |> (given (_) ->
                buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(fileReadInvalidUtf8Codes)(prefix + "_invalid_msg"))(prefix + "_invalid_result"))(resultSlot))
            |> (given (_) -> buildBr(builder)(blocks.readContinueBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(blocks.readCloseErrorBlock))
            |> (given (_) -> emitLinuxClose(builder)(i64)(fd))
            |> (given (_) -> buildBr(builder)(blocks.readErrorBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(blocks.readErrorBlock))
            |> (given (_) ->
                buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(fileReadFailedCodes)(prefix + "_error_msg"))(prefix + "_error_result"))(resultSlot))
            |> (given (_) -> buildBr(builder)(blocks.readContinueBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(blocks.readContinueBlock))
            |> (given (_) -> buildLoad(builder)(i64)(resultSlot)(prefix + "_result_value")))

// The shared whole-file read: `open` → measure/rewind → allocate `length + 8` → read loop →
// wrap — stage 0's `EmitLinuxFileReadText` phase for phase, with `validateUtf8` selecting the
// `readText` (capped, validated `Str`) or `readAllBytes` (uncapped, raw `Bytes`) flavor.
let emitFileReadWhole context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType validateUtf8 pathRef prefix =
    (let resultSlot = buildAlloca(builder)(i64)(prefix + "_result")
    in
        let remainingSlot = buildAlloca(builder)(i64)(prefix + "_remaining")
        in
            let cursorSlot = buildAlloca(builder)(i64)(prefix + "_cursor")
            in
                let blocks = emitFileReadBlocks(context)(function_)(prefix)
                in
                    let fd = emitFileReadOpen(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(pathRef)(blocks)(prefix)
                    in
                        let length = emitFileReadMeasure(builder)(i64)(fd)(validateUtf8)(blocks)(prefix)
                        in
                            let stringRef = emitFileReadLoop(builder)(i64)(i8)(mallocFn)(mallocType)(fd)(length)(remainingSlot)(cursorSlot)(blocks)(prefix)
                            in emitFileReadFinish(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(fd)(validateUtf8)(stringRef)(length)(resultSlot)(blocks)(prefix))

let emitFileReadText context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType pathRef = emitFileReadWhole(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(true)(pathRef)("file_read_text")

let emitFileReadAllBytes context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType pathRef = emitFileReadWhole(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(false)(pathRef)("file_read_bytes")

// `Ashes.IO.File.mmap(path)`: `open` → `lseek(SEEK_END)` → `mmap(NULL, len, PROT_READ,
// MAP_PRIVATE, fd, 0)` → close → `Ok` of a zero-copy `Bytes` view `{len|VIEW, mappingAddr}` over
// the program-lifetime mapping, stage 0's `EmitFileMmap` exactly: an empty file short-circuits to
// `Ok` of an empty owned value (nothing to map), and a mapping failure (any result unsigned-above
// `-4096`) reports the shared read-failed message.
let emitFileMmap context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType pathRef =
    (let resultSlot = buildAlloca(builder)(i64)("file_mmap_result")
    in
        let measureBlock = appendBasicBlock(context)(function_)("file_mmap_measure")
        in
            let sizeBlock = appendBasicBlock(context)(function_)("file_mmap_size")
            in
                let emptyBlock = appendBasicBlock(context)(function_)("file_mmap_empty")
                in
                    let mapBlock = appendBasicBlock(context)(function_)("file_mmap_map")
                    in
                        let viewBlock = appendBasicBlock(context)(function_)("file_mmap_view")
                        in
                            let closeErrorBlock = appendBasicBlock(context)(function_)("file_mmap_close_error")
                            in
                                let errorBlock = appendBasicBlock(context)(function_)("file_mmap_error")
                                in
                                    let continueBlock = appendBasicBlock(context)(function_)("file_mmap_continue")
                                    in
                                        let fd =
                                            "file_mmap_path"
                                            |> emitStringToCString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(pathRef)
                                            |> (given (pathCstr) -> buildPtrToInt(builder)(pathCstr)(i64)("file_mmap_path_addr"))
                                            |> (given (pathAddr) ->
                                                false
                                                |> constInt(i64)(0u64)
                                                |> emitLinuxOpenat(builder)(i64)(pathAddr)(constInt(i64)(0u64)(false)))
                                        in
                                            let _ =
                                                buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(fd)(constInt(i64)(0u64)(false))("file_mmap_open_failed"))(errorBlock)(measureBlock)
                                            in
                                                let length =
                                                    measureBlock
                                                    |> positionBuilderAtEnd(builder)
                                                    |> (given (_) ->
                                                        false
                                                        |> constInt(i64)(2u64)
                                                        |> emitLinuxLseek(builder)(i64)(fd)(constInt(i64)(0u64)(false)))
                                                in
                                                    let _ =
                                                        sizeBlock
                                                        |> buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(length)(constInt(i64)(0u64)(false))("file_mmap_measure_failed"))(closeErrorBlock)
                                                        |> (given (_) -> positionBuilderAtEnd(builder)(sizeBlock))
                                                        |> (given (_) ->
                                                            buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(length)(constInt(i64)(0u64)(false))("file_mmap_is_empty"))(emptyBlock)(mapBlock))
                                                        |> (given (_) -> positionBuilderAtEnd(builder)(emptyBlock))
                                                        |> (given (_) -> emitLinuxClose(builder)(i64)(fd))
                                                        |> (given (_) ->
                                                            emitRcAllocPayloadPtrDynamic(builder)(i64)(i8)(mallocFn)(mallocType)(constInt(i64)(8u64)(false))("file_mmap_empty_payload"))
                                                        |> (given (emptyPtr) ->
                                                            emptyPtr
                                                            |> buildStore(builder)(constInt(i64)(0u64)(false))
                                                            |> (given (_) -> buildPtrToInt(builder)(emptyPtr)(i64)("file_mmap_empty_ref")))
                                                        |> (given (emptyRef) ->
                                                            buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(emptyRef)("file_mmap_empty_ok"))(resultSlot))
                                                        |> (given (_) -> buildBr(builder)(continueBlock))
                                                    in
                                                        let mapped =
                                                            mapBlock
                                                            |> positionBuilderAtEnd(builder)
                                                            |> (given (_) -> emitLinuxMmapReadPrivate(builder)(i64)(length)(fd))
                                                        in
                                                            viewBlock
                                                            |> buildCondBr(builder)(buildICmp(builder)(intPredicateUgt)(mapped)(constInt(i64)(Ashes.Number.UInt.fromInt64(-4096))(false))("file_mmap_failed"))(closeErrorBlock)
                                                            |> (given (_) -> positionBuilderAtEnd(builder)(viewBlock))
                                                            |> (given (_) -> emitLinuxClose(builder)(i64)(fd))
                                                            |> (given (_) ->
                                                                emitRcAllocPayloadPtrDynamic(builder)(i64)(i8)(mallocFn)(mallocType)(constInt(i64)(16u64)(false))("file_mmap_view_payload"))
                                                            |> (given (viewPtr) ->
                                                                viewPtr
                                                                |> buildStore(builder)(buildOr(builder)(length)(constInt(i64)(Ashes.Number.UInt.fromInt64(1 << 63))(false))("file_mmap_tagged_len"))
                                                                |> (given (_) ->
                                                                    "file_mmap_addr_word"
                                                                    |> gepBytes(builder)(i64)(i8)(viewPtr)(8)
                                                                    |> buildStore(builder)(mapped))
                                                                |> (given (_) -> buildPtrToInt(builder)(viewPtr)(i64)("file_mmap_view_ref")))
                                                            |> (given (viewRef) ->
                                                                buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(viewRef)("file_mmap_view_ok"))(resultSlot))
                                                            |> (given (_) -> buildBr(builder)(continueBlock))
                                                            |> (given (_) -> positionBuilderAtEnd(builder)(closeErrorBlock))
                                                            |> (given (_) -> emitLinuxClose(builder)(i64)(fd))
                                                            |> (given (_) -> buildBr(builder)(errorBlock))
                                                            |> (given (_) -> positionBuilderAtEnd(builder)(errorBlock))
                                                            |> (given (_) ->
                                                                buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(fileReadFailedCodes)("file_mmap_error_msg"))("file_mmap_error_result"))(resultSlot))
                                                            |> (given (_) -> buildBr(builder)(continueBlock))
                                                            |> (given (_) -> positionBuilderAtEnd(builder)(continueBlock))
                                                            |> (given (_) -> buildLoad(builder)(i64)(resultSlot)("file_mmap_result_value")))

let fileMakeExecutableErrorCodes = [65, 115, 104, 101, 115, 46, 73, 79, 46, 70, 105, 108, 101, 46, 109, 97, 107, 101, 69, 120, 101, 99, 117, 116, 97, 98, 108, 101, 58, 32, 99, 111, 117, 108, 100, 32, 110, 111, 116, 32, 109, 97, 114, 107, 32, 102, 105, 108, 101, 32, 101, 120, 101, 99, 117, 116, 97, 98, 108, 101]

// `Ashes.IO.File.makeExecutable(path)`: `lstat` the path (via the libc import — the raw `stat`
// buffer layout is libc's, `st_mode` an `i32` at offset 24 on x64), require a regular file
// (`(mode & 0xF000) == 0x8000`), then `chmod(path, 0o755)` — stage 0's
// `EmitLinuxFileMakeExecutable`, with the `chmod` as a raw syscall rather than a second libc
// import.
let emitFileMakeExecutable context function_ i64 i8 i32 ptrType builder mallocFn mallocType memcpyFn memcpyType dirExt pathRef =
    (let statusSlot = buildAlloca(builder)(i64)("file_executable_status")
    in
        let statBuffer =
            buildAlloca(builder)(arrayType(i8)(256u64))("file_executable_stat")
        in
            let chmodBlock = appendBasicBlock(context)(function_)("file_executable_chmod")
            in
                let joinBlock = appendBasicBlock(context)(function_)("file_executable_join")
                in
                    let pathCstr = emitStringToCString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(pathRef)("file_executable_path")
                    in
                        let statStatus = buildCall(builder)(dirExt.lstatType)(dirExt.lstatFn)([pathCstr, statBuffer])(2u32)("file_executable_lstat")
                        in
                            let mode =
                                "file_executable_mode"
                                |> gepBytes(builder)(i64)(i8)(statBuffer)(24)
                                |> (given (modePtr) -> buildLoad(builder)(i32)(modePtr)("file_executable_mode_raw"))
                                |> (given (modeRaw) -> buildZExt(builder)(modeRaw)(i64)("file_executable_mode_word"))
                            in
                                let supported =
                                    buildAnd(builder)(buildICmp(builder)(intPredicateEq)(statStatus)(constInt(i32)(0u64)(false))("file_executable_stat_ok"))(buildICmp(builder)(intPredicateEq)(buildAnd(builder)(mode)(constInt(i64)(61440u64)(false))("file_executable_mode_kind"))(constInt(i64)(32768u64)(false))("file_executable_is_regular"))("file_executable_supported")
                                in
                                    statusSlot
                                    |> buildStore(builder)(constInt(i64)(Ashes.Number.UInt.fromInt64(-1))(false))
                                    |> (given (_) -> buildCondBr(builder)(supported)(chmodBlock)(joinBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(chmodBlock))
                                    |> (given (_) -> buildPtrToInt(builder)(pathCstr)(i64)("file_executable_path_addr"))
                                    |> (given (pathAddr) ->
                                        false
                                        |> constInt(i64)(493u64)
                                        |> emitLinuxChmod(builder)(i64)(pathAddr))
                                    |> (given (chmodStatus) -> buildStore(builder)(chmodStatus)(statusSlot))
                                    |> (given (_) -> buildBr(builder)(joinBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(joinBlock))
                                    |> (given (_) -> buildLoad(builder)(i64)(statusSlot)("file_executable_status_value"))
                                    |> (given (status) ->
                                        buildICmp(builder)(intPredicateSge)(status)(constInt(i64)(0u64)(false))("file_executable_succeeded"))
                                    |> (given (succeeded) -> emitFilesystemStatusResult(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(succeeded)(fileMakeExecutableErrorCodes)("file_executable")))
