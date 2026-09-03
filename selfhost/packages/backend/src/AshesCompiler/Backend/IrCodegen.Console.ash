// The `Ashes.IO.Console` emitters for `AshesCompiler.Backend.IrCodegen`: `enableRawInput`
// (save stdin's termios, clear the canonical/echo/signal flags, apply), `restoreInput` (put the
// saved termios back when raw mode is active), `pollInput` (`ppoll` on stdin with a millisecond
// timeout, then one `read` into a 4 KiB buffer: `Some(bytes)`, `Some("")` on timeout, `None` at
// end of input), and `monotonicMillis` (`clock_gettime(CLOCK_MONOTONIC)` in milliseconds) —
// `LlvmCodegenBuiltins.Console.cs`'s and `LlvmCodegenPlatform.cs`'s Linux arms. The saved
// termios and the raw-active flag are module globals, so a later `restoreInput` sees what an
// earlier `enableRawInput` saved; every other scratch value is a per-call stack slot.

import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
import AshesCompiler.Backend.IrCodegen.Syscalls.LinuxX64
import Ashes.Number.UInt
export (
    type ConsoleGlobals(..),
    value defineConsoleGlobals,
    value emitConsoleEnableRaw,
    value emitConsoleRestore,
    value emitConsolePoll,
    value emitMonotonicMillis,
)

type ConsoleGlobals =
    | consoleSavedTermios: LLVMValueRef
    | consoleRawActive: LLVMValueRef

// `struct termios` on linux-x64: 36 bytes, `c_iflag` at 0, `c_lflag` at 12, and the control
// characters from 17 (`VTIME` at 17 + 5, `VMIN` at 17 + 6).
let termiosSizeBytes = 36u64

let termiosIflagOffset = 0

let termiosLflagOffset = 12

let termiosVtimeOffset = 22

let termiosVminOffset = 23

// `~(IGNBRK|BRKINT|PARMRK|ISTRIP|INLCR|IGNCR|ICRNL|IXON)` and `~(ISIG|ICANON|ECHO|ECHONL|IEXTEN)`
// as the 32-bit words the flag fields are.
let termiosIflagRawKeepMask = 4294965780u64

let termiosLflagRawKeepMask = 4294934452u64

let termiosTcgets = 21505u64

let termiosTcsets = 21506u64

let consolePollBufferSize = 4096

// `struct pollfd { int fd = 0; short events = POLLIN; short revents = 0; }` as one little-endian
// 64-bit word.
let consoleStdinPollFdWord = 4294967296u64

let defineConsoleGlobals module_ i64 i8 =
    (let termiosType = arrayType(i8)(termiosSizeBytes)
    in
        let savedTermios = addGlobal(module_)(termiosType)("__ashes_console_saved_termios")
        in
            let rawActive = addGlobal(module_)(i64)("__ashes_console_raw_active")
            in
                Unit
                |> (given (_) ->
                    termiosType
                    |> constNull
                    |> setInitializer(savedTermios))
                |> (given (_) -> setLinkage(savedTermios)(linkageInternal))
                |> (given (_) ->
                    false
                    |> constInt(i64)(0u64)
                    |> setInitializer(rawActive))
                |> (given (_) -> setLinkage(rawActive)(linkageInternal))
                |> (given (_) -> ConsoleGlobals(consoleSavedTermios = savedTermios, consoleRawActive = rawActive)))

let consoleWord i64 value = constInt(i64)(value)(false)

// Copies the 36 termios bytes as nine 32-bit words.
let recursive copyTermiosWords builder i64 i8 i32 source destination offset =
    if offset >= 36
    then Unit
    else
        let word =
            buildLoad(builder)(i32)(gepBytes(builder)(i64)(i8)(source)(offset)("console_termios_src"))("console_termios_word")
        in
            let _ =
                "console_termios_dst"
                |> gepBytes(builder)(i64)(i8)(destination)(offset)
                |> buildStore(builder)(word)
            in copyTermiosWords(builder)(i64)(i8)(i32)(source)(destination)(offset + 4)

let andTermiosFlagWord builder i64 i8 i32 termiosPtr offset keepMask name =
    (let wordPtr = gepBytes(builder)(i64)(i8)(termiosPtr)(offset)(name + "_ptr")
    in
        let masked =
            buildAnd(builder)(buildLoad(builder)(i32)(wordPtr)(name + "_value"))(constInt(i32)(keepMask)(false))(name + "_masked")
        in buildStore(builder)(masked)(wordPtr))

let storeTermiosByte builder i64 i8 termiosPtr offset value name =
    name + "_ptr"
    |> gepBytes(builder)(i64)(i8)(termiosPtr)(offset)
    |> buildStore(builder)(constInt(i8)(value)(false))

// `Ashes.IO.Console.enableRawInput()`: `true` once stdin's termios were read, rewritten, and
// applied; `false` (changing nothing) when either `ioctl` fails, as when stdin is not a terminal.
let emitConsoleEnableRaw context function_ builder i64 i8 i32 ptrType (globals: ConsoleGlobals) =
    (let applyBlock = appendBasicBlock(context)(function_)("console_raw_apply")
    in
        let okBlock = appendBasicBlock(context)(function_)("console_raw_ok")
        in
            let failBlock = appendBasicBlock(context)(function_)("console_raw_fail")
            in
                let doneBlock = appendBasicBlock(context)(function_)("console_raw_done")
                in
                    let resultSlot = buildEntryAlloca(builder)(i64)("console_raw_result")
                    in
                        let savedPtr =
                            buildIntToPtr(builder)(buildPtrToInt(builder)(globals.consoleSavedTermios)(i64)("console_saved_addr"))(ptrType)("console_saved_ptr")
                        in
                            let savedAddr = buildPtrToInt(builder)(savedPtr)(i64)("console_saved_termios_addr")
                            in
                                let workTermios =
                                    buildEntryAlloca(builder)(arrayType(i8)(termiosSizeBytes))("console_work_termios")
                                in
                                    let workAddr = buildPtrToInt(builder)(workTermios)(i64)("console_work_termios_addr")
                                    in
                                        "console_tcgets"
                                        |> emitLinuxIoctl(builder)(i64)(consoleWord(i64)(0u64))(consoleWord(i64)(termiosTcgets))(savedAddr)
                                        |> (given (getRet) ->
                                            buildICmp(builder)(intPredicateNe)(getRet)(consoleWord(i64)(0u64))("console_tcgets_failed"))
                                        |> (given (getFailed) -> buildCondBr(builder)(getFailed)(failBlock)(applyBlock))
                                        |> (given (_) -> positionBuilderAtEnd(builder)(applyBlock))
                                        |> (given (_) -> copyTermiosWords(builder)(i64)(i8)(i32)(savedPtr)(workTermios)(0))
                                        |> (given (_) -> andTermiosFlagWord(builder)(i64)(i8)(i32)(workTermios)(termiosIflagOffset)(termiosIflagRawKeepMask)("console_iflag"))
                                        |> (given (_) -> andTermiosFlagWord(builder)(i64)(i8)(i32)(workTermios)(termiosLflagOffset)(termiosLflagRawKeepMask)("console_lflag"))
                                        |> (given (_) -> storeTermiosByte(builder)(i64)(i8)(workTermios)(termiosVtimeOffset)(0u64)("console_vtime"))
                                        |> (given (_) -> storeTermiosByte(builder)(i64)(i8)(workTermios)(termiosVminOffset)(1u64)("console_vmin"))
                                        |> (given (_) ->
                                            emitLinuxIoctl(builder)(i64)(consoleWord(i64)(0u64))(consoleWord(i64)(termiosTcsets))(workAddr)("console_tcsets"))
                                        |> (given (setRet) ->
                                            buildICmp(builder)(intPredicateNe)(setRet)(consoleWord(i64)(0u64))("console_tcsets_failed"))
                                        |> (given (setFailed) -> buildCondBr(builder)(setFailed)(failBlock)(okBlock))
                                        |> (given (_) -> positionBuilderAtEnd(builder)(okBlock))
                                        |> (given (_) ->
                                            buildStore(builder)(consoleWord(i64)(1u64))(globals.consoleRawActive))
                                        |> (given (_) ->
                                            buildStore(builder)(consoleWord(i64)(1u64))(resultSlot))
                                        |> (given (_) -> buildBr(builder)(doneBlock))
                                        |> (given (_) -> positionBuilderAtEnd(builder)(failBlock))
                                        |> (given (_) ->
                                            buildStore(builder)(consoleWord(i64)(0u64))(resultSlot))
                                        |> (given (_) -> buildBr(builder)(doneBlock))
                                        |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))
                                        |> (given (_) -> buildLoad(builder)(i64)(resultSlot)("console_raw_result_value")))

// `Ashes.IO.Console.restoreInput()`: put the saved termios back when raw mode is active, a no-op
// otherwise.
let emitConsoleRestore context function_ builder i64 ptrType (globals: ConsoleGlobals) =
    (let restoreBlock = appendBasicBlock(context)(function_)("console_restore_apply")
    in
        let doneBlock = appendBasicBlock(context)(function_)("console_restore_done")
        in
            "console_raw_active_value"
            |> buildLoad(builder)(i64)(globals.consoleRawActive)
            |> (given (active) ->
                buildICmp(builder)(intPredicateNe)(active)(consoleWord(i64)(0u64))("console_raw_is_active"))
            |> (given (isActive) -> buildCondBr(builder)(isActive)(restoreBlock)(doneBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(restoreBlock))
            |> (given (_) ->
                emitLinuxIoctl(builder)(i64)(consoleWord(i64)(0u64))(consoleWord(i64)(termiosTcsets))(buildPtrToInt(builder)(globals.consoleSavedTermios)(i64)("console_restore_addr"))("console_restore_tcsets"))
            |> (given (_) ->
                buildStore(builder)(consoleWord(i64)(0u64))(globals.consoleRawActive))
            |> (given (_) -> buildBr(builder)(doneBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock)))

// A `Maybe(Str)` `Some` around the string built from `length` bytes at `bufferAddr`.
let emitConsolePollSome builder i64 i8 ptrType mallocFn mallocType memcpyFn memcpyType bufferAddr length resultSlot doneBlock name =
    (let stringRef = emitHeapStringFromBytesAddr(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(bufferAddr)(length)(name + "_string")
    in
        let someValue = emitAllocAdtRuntimeManaged(builder)(i64)(i8)(mallocFn)(mallocType)(1)(1)(name + "_some")
        in
            let fieldPtr =
                gepBytes(builder)(i64)(i8)(buildIntToPtr(builder)(someValue)(ptrType)(name + "_some_ptr"))(8)(name + "_some_field")
            in
                Unit
                |> (given (_) -> buildStore(builder)(stringRef)(fieldPtr))
                |> (given (_) -> buildStore(builder)(someValue)(resultSlot))
                |> (given (_) -> buildBr(builder)(doneBlock)))

// `Ashes.IO.Console.pollInput(timeoutMs)`: wait up to `timeoutMs` (a negative value is `0`) for
// stdin to become readable, then read once: `Some(bytes)` when input arrived, `Some("")` when the
// timeout elapsed, `None` when the read reports end of input.
let emitConsolePoll context function_ builder i64 i8 ptrType mallocFn mallocType memcpyFn memcpyType timeoutMs =
    (let readBlock = appendBasicBlock(context)(function_)("console_poll_read")
    in
        let someBlock = appendBasicBlock(context)(function_)("console_poll_some")
        in
            let emptyBlock = appendBasicBlock(context)(function_)("console_poll_empty")
            in
                let noneBlock = appendBasicBlock(context)(function_)("console_poll_none")
                in
                    let doneBlock = appendBasicBlock(context)(function_)("console_poll_done")
                    in
                        let resultSlot = buildEntryAlloca(builder)(i64)("console_poll_result")
                        in
                            let buffer =
                                buildEntryAlloca(builder)(consolePollBufferSize
                                |> Ashes.Number.UInt.fromInt64
                                |> arrayType(i8))("console_poll_buf")
                            in
                                let bufferAddr = buildPtrToInt(builder)(buffer)(i64)("console_poll_buf_addr")
                                in
                                    let pollFd = buildEntryAlloca(builder)(i64)("console_pollfd")
                                    in
                                        let timespec =
                                            buildEntryAlloca(builder)(arrayType(i64)(2u64))("console_poll_ts")
                                        in
                                            let timespecAddr = buildPtrToInt(builder)(timespec)(i64)("console_poll_ts_addr")
                                            in
                                                let negative =
                                                    buildICmp(builder)(intPredicateSlt)(timeoutMs)(consoleWord(i64)(0u64))("console_poll_timeout_negative")
                                                in
                                                    let clamped =
                                                        buildSelect(builder)(negative)(consoleWord(i64)(0u64))(timeoutMs)("console_poll_timeout")
                                                    in
                                                        let seconds =
                                                            buildSDiv(builder)(clamped)(consoleWord(i64)(1000u64))("console_poll_ts_sec")
                                                        in
                                                            let nanos =
                                                                buildMul(builder)(buildSub(builder)(clamped)(buildMul(builder)(seconds)(consoleWord(i64)(1000u64))("console_poll_ts_sec_ms"))("console_poll_ts_rem"))(consoleWord(i64)(1000000u64))("console_poll_ts_nsec")
                                                            in
                                                                Unit
                                                                |> (given (_) ->
                                                                    buildStore(builder)(consoleWord(i64)(consoleStdinPollFdWord))(pollFd))
                                                                |> (given (_) ->
                                                                    "console_poll_ts_sec_ptr"
                                                                    |> gepBytes(builder)(i64)(i8)(timespec)(0)
                                                                    |> buildStore(builder)(seconds))
                                                                |> (given (_) ->
                                                                    "console_poll_ts_nsec_ptr"
                                                                    |> gepBytes(builder)(i64)(i8)(timespec)(8)
                                                                    |> buildStore(builder)(nanos))
                                                                |> (given (_) ->
                                                                    emitLinuxPpoll(builder)(i64)(buildPtrToInt(builder)(pollFd)(i64)("console_pollfd_addr"))(consoleWord(i64)(1u64))(timespecAddr)(consoleWord(i64)(0u64))(consoleWord(i64)(8u64))("console_ppoll"))
                                                                |> (given (pollRet) ->
                                                                    buildICmp(builder)(intPredicateSgt)(pollRet)(consoleWord(i64)(0u64))("console_poll_readable"))
                                                                |> (given (readable) -> buildCondBr(builder)(readable)(readBlock)(emptyBlock))
                                                                |> (given (_) -> positionBuilderAtEnd(builder)(readBlock))
                                                                |> (given (_) ->
                                                                    consolePollBufferSize
                                                                    |> Ashes.Number.UInt.fromInt64
                                                                    |> consoleWord(i64)
                                                                    |> emitLinuxRead(builder)(i64)(consoleWord(i64)(0u64))(bufferAddr))
                                                                |> (given (bytesRead) ->
                                                                    Unit
                                                                    |> (given (_) ->
                                                                        buildCondBr(builder)(buildICmp(builder)(intPredicateSgt)(bytesRead)(consoleWord(i64)(0u64))("console_poll_got_bytes"))(someBlock)(noneBlock))
                                                                    |> (given (_) -> positionBuilderAtEnd(builder)(someBlock))
                                                                    |> (given (_) -> emitConsolePollSome(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(bufferAddr)(bytesRead)(resultSlot)(doneBlock)("console_poll")))
                                                                |> (given (_) -> positionBuilderAtEnd(builder)(emptyBlock))
                                                                |> (given (_) ->
                                                                    emitConsolePollSome(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(bufferAddr)(consoleWord(i64)(0u64))(resultSlot)(doneBlock)("console_poll_empty"))
                                                                |> (given (_) -> positionBuilderAtEnd(builder)(noneBlock))
                                                                |> (given (_) ->
                                                                    buildStore(builder)(emitAllocAdtRuntimeManaged(builder)(i64)(i8)(mallocFn)(mallocType)(0)(0)("console_poll_none"))(resultSlot))
                                                                |> (given (_) -> buildBr(builder)(doneBlock))
                                                                |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))
                                                                |> (given (_) -> buildLoad(builder)(i64)(resultSlot)("console_poll_result_value")))

// `Ashes.IO.Console.monotonicMillis()`: `clock_gettime(CLOCK_MONOTONIC)` as whole milliseconds.
let emitMonotonicMillis builder i64 i8 =
    (let timespec =
        buildEntryAlloca(builder)(arrayType(i64)(2u64))("monotonic_timespec")
    in
        let timespecAddr = buildPtrToInt(builder)(timespec)(i64)("monotonic_timespec_addr")
        in
            Unit
            |> (given (_) ->
                emitLinuxClockGettime(builder)(i64)(consoleWord(i64)(1u64))(timespecAddr))
            |> (given (_) ->
                buildLoad(builder)(i64)(gepBytes(builder)(i64)(i8)(timespec)(0)("monotonic_sec_ptr"))("monotonic_sec"))
            |> (given (seconds) ->
                buildAdd(builder)(buildMul(builder)(seconds)(consoleWord(i64)(1000u64))("monotonic_sec_ms"))(buildSDiv(builder)(buildLoad(builder)(i64)(gepBytes(builder)(i64)(i8)(timespec)(8)("monotonic_nsec_ptr"))("monotonic_nsec"))(consoleWord(i64)(1000000u64))("monotonic_nsec_ms"))("monotonic_millis")))
