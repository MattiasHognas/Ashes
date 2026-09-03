// The `Ashes.IO.Process` emitters for `AshesCompiler.Backend.IrCodegen`:
// `spawn` (three `pipe2` pairs, `fork`, child-side `dup2`/`close` rewiring, `execve` with the
// parent environment from the entry-captured `__ashes_envp` global), `writeStdin` (a
// partial-write-safe write loop), `readStdoutLine`/`readStderrLine` (a per-byte scan into a
// fixed 64 KiB stack buffer, `Maybe(Str)`), `waitForExit` (`wait4` + `WEXITSTATUS`), and `kill`
// (`SIGTERM`) — `LlvmCodegenBuiltins.Process.cs`'s Linux arms emitter for emitter, message
// constants included. A `Process` value is a 32-byte RC payload
// `{stdinWriteFd@0, stdoutReadFd@8, stderrReadFd@16, pid@24}` (stage 0's exact layout). A
// `Process` dropped at its scope exit (`emitProcessDrop`) closes the pipes and reaps the child.
// Depends only on the LLVM bindings and `IrCodegen.Support`.

import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
import AshesCompiler.Backend.IrCodegen.Syscalls.LinuxX64
import AshesCompiler.Backend.IrCodegen.Arena
import Ashes.Number.UInt
export (
    value processSpawnForkFailedCodes,
    value emitSpawnProcess,
    value emitProcessWriteStdin,
    value emitProcessReadLine,
    value emitProcessWaitForExit,
    value emitProcessKill,
    value emitProcessDrop,
)

// "Process.spawn: fork failed" — stage 0's spawn error string.
let processSpawnForkFailedCodes = [80, 114, 111, 99, 101, 115, 115, 46, 115, 112, 97, 119, 110, 58, 32, 102, 111, 114, 107, 32, 102, 97, 105, 108, 101, 100]

// "Process.spawn: too many arguments\n" / "Process.readLine: line too long\n" — stage 0's panic
// texts, newline-terminated for the raw stderr-style write-then-exit path.
let processSpawnTooManyArgsCodes = [80, 114, 111, 99, 101, 115, 115, 46, 115, 112, 97, 119, 110, 58, 32, 116, 111, 111, 32, 109, 97, 110, 121, 32, 97, 114, 103, 117, 109, 101, 110, 116, 115, 10]

let processReadLineTooLongCodes = [80, 114, 111, 99, 101, 115, 115, 46, 114, 101, 97, 100, 76, 105, 110, 101, 58, 32, 108, 105, 110, 101, 32, 116, 111, 111, 32, 108, 111, 110, 103, 10]

// The fixed-ASCII-message-then-exit-1 panic shape `emitReadLinePanicMessage` uses, generalized to
// any static code list.
let emitProcessPanicMessage builder i64 i8 codes prefix =
    (let length = Ashes.Collection.List.length(codes)
    in
        let buffer =
            buildAlloca(builder)(length
            |> Ashes.Number.UInt.fromInt64
            |> arrayType(i8))(prefix + "_panic_msg")
        in
            let _ =
                storeAsciiBytes(builder)(i64)(i8)(length
                |> Ashes.Number.UInt.fromInt64
                |> arrayType(i8))(buffer)(0)(codes)
            in
                let _ =
                    false
                    |> constInt(i64)(Ashes.Number.UInt.fromInt64(length))
                    |> emitLinuxWrite(builder)(i64)(constInt(i64)(1u64)(false))(buildPtrToInt(builder)(buffer)(i64)(prefix + "_panic_addr"))
                in
                    false
                    |> constInt(i64)(1u64)
                    |> emitLinuxProcessExitWithCode(builder)(i64))

// `{stdinWriteFd@0, stdoutReadFd@8, stderrReadFd@16, pid@24}` field access over the RC payload.
let emitProcessFieldPtr builder i64 i8 ptrType processRef offset name =
    gepBytes(builder)(i64)(i8)(buildIntToPtr(builder)(processRef)(ptrType)(name + "_base"))(offset)(name)

let emitLoadProcessField builder i64 i8 ptrType processRef offset name =
    buildLoad(builder)(i64)(emitProcessFieldPtr(builder)(i64)(i8)(ptrType)(processRef)(offset)(name + "_ptr"))(name)

// One `pipe2` result: the two `i32` fds at `pipeBuffer` offsets 0 and 4, zero-extended to the
// universal `i64` word.
let emitProcessPipeFds builder i64 i8 i32 pipeBuffer prefix =
    (let readFd =
        prefix + "_read_ptr"
        |> gepBytes(builder)(i64)(i8)(pipeBuffer)(0)
        |> (given (readPtr) -> buildLoad(builder)(i32)(readPtr)(prefix + "_read_i32"))
        |> (given (readRaw) -> buildZExt(builder)(readRaw)(i64)(prefix + "_read_fd"))
    in
        let writeFd =
            prefix + "_write_ptr"
            |> gepBytes(builder)(i64)(i8)(pipeBuffer)(4)
            |> (given (writePtr) -> buildLoad(builder)(i32)(writePtr)(prefix + "_write_i32"))
            |> (given (writeRaw) -> buildZExt(builder)(writeRaw)(i64)(prefix + "_write_fd"))
        in (readFd, writeFd))

// The six per-spawn scratch values that outlive the argv build.
type SpawnScratch =
    | spawnStdinPipe: LLVMValueRef
    | spawnStdoutPipe: LLVMValueRef
    | spawnStderrPipe: LLVMValueRef
    | spawnResultSlot: LLVMValueRef
    | spawnArgvBase: LLVMValueRef
    | spawnArgcSlot: LLVMValueRef
    | spawnCursorSlot: LLVMValueRef

let emitSpawnScratch builder i64 i8 i32 =
    (let argvArray =
        buildAlloca(builder)(arrayType(i64)(258u64))("spawn_argv")
    in
        SpawnScratch(
            spawnStdinPipe = buildAlloca(builder)(arrayType(i32)(2u64))("spawn_stdin_pipe"),
            spawnStdoutPipe = buildAlloca(builder)(arrayType(i32)(2u64))("spawn_stdout_pipe"),
            spawnStderrPipe = buildAlloca(builder)(arrayType(i32)(2u64))("spawn_stderr_pipe"),
            spawnResultSlot = buildAlloca(builder)(i64)("spawn_result"),
            spawnArgvBase = buildPtrToInt(builder)(argvArray)(i64)("spawn_argv_base"),
            spawnArgcSlot = buildAlloca(builder)(i64)("spawn_argc"),
            spawnCursorSlot = buildAlloca(builder)(i64)("spawn_list_cursor")
        ))

// argv[0] = the executable, argv[1..] = the args list (each NUL-terminated), argv[n] = null —
// capped at 256 entries with stage 0's exact too-many-arguments panic. Cons cells are the
// untagged 16-byte `[head@0][tail@8]` pair, nil the zero word.
let emitSpawnBuildArgv context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType scratch exeRef argsRef =
    (let listLoopBlock = appendBasicBlock(context)(function_)("spawn_list_loop")
    in
        let listBodyBlock = appendBasicBlock(context)(function_)("spawn_list_body")
        in
            let listAppendBlock = appendBasicBlock(context)(function_)("spawn_list_append")
            in
                let listTooManyBlock = appendBasicBlock(context)(function_)("spawn_list_too_many")
                in
                    let listDoneBlock = appendBasicBlock(context)(function_)("spawn_list_done")
                    in
                        let exeCstr = emitStringToCString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(exeRef)("spawn_exe")
                        in
                            let _ =
                                "spawn_slot0"
                                |> buildIntToPtr(builder)(scratch.spawnArgvBase)(ptrType)
                                |> (given (slot0) ->
                                    buildStore(builder)(buildPtrToInt(builder)(exeCstr)(i64)("spawn_exe_addr"))(slot0))
                                |> (given (_) ->
                                    buildStore(builder)(constInt(i64)(1u64)(false))(scratch.spawnArgcSlot))
                                |> (given (_) -> buildStore(builder)(argsRef)(scratch.spawnCursorSlot))
                                |> (given (_) -> buildBr(builder)(listLoopBlock))
                                |> (given (_) -> positionBuilderAtEnd(builder)(listLoopBlock))
                                |> (given (_) -> buildLoad(builder)(i64)(scratch.spawnCursorSlot)("spawn_cursor"))
                                |> (given (cursor) ->
                                    buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(cursor)(constInt(i64)(0u64)(false))("spawn_is_nil"))(listDoneBlock)(listBodyBlock))
                                |> (given (_) -> positionBuilderAtEnd(builder)(listBodyBlock))
                                |> (given (_) -> buildLoad(builder)(i64)(scratch.spawnArgcSlot)("spawn_argc_value"))
                                |> (given (argc) ->
                                    listAppendBlock
                                    |> buildCondBr(builder)(buildICmp(builder)(intPredicateUge)(argc)(constInt(i64)(256u64)(false))("spawn_too_many"))(listTooManyBlock)
                                    |> (given (_) -> positionBuilderAtEnd(builder)(listAppendBlock))
                                    |> (given (_) -> buildLoad(builder)(i64)(scratch.spawnCursorSlot)("spawn_cursor_cell"))
                                    |> (given (cursor) ->
                                        let headRef =
                                            buildLoad(builder)(i64)(buildIntToPtr(builder)(cursor)(ptrType)("spawn_head_ptr"))("spawn_head")
                                        in
                                            let headCstr = emitStringToCString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(headRef)("spawn_arg")
                                            in
                                                let slotAddr =
                                                    buildAdd(builder)(scratch.spawnArgvBase)(buildMul(builder)(argc)(constInt(i64)(8u64)(false))("spawn_slot_off"))("spawn_slot_addr")
                                                in
                                                    let _ =
                                                        "spawn_slot_ptr"
                                                        |> buildIntToPtr(builder)(slotAddr)(ptrType)
                                                        |> buildStore(builder)(buildPtrToInt(builder)(headCstr)(i64)("spawn_arg_addr"))
                                                    in
                                                        let _ =
                                                            buildStore(builder)(buildAdd(builder)(argc)(constInt(i64)(1u64)(false))("spawn_argc_next"))(scratch.spawnArgcSlot)
                                                        in
                                                            let tail =
                                                                buildLoad(builder)(i64)(gepBytes(builder)(i64)(i8)(buildIntToPtr(builder)(cursor)(ptrType)("spawn_cell_ptr"))(8)("spawn_tail_ptr"))("spawn_tail")
                                                            in buildStore(builder)(tail)(scratch.spawnCursorSlot))
                                    |> (given (_) -> buildBr(builder)(listLoopBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(listTooManyBlock))
                                    |> (given (_) -> emitProcessPanicMessage(builder)(i64)(i8)(processSpawnTooManyArgsCodes)("spawn_too_many")))
                            in
                                let _ = positionBuilderAtEnd(builder)(listDoneBlock)
                                in
                                    let finalArgc = buildLoad(builder)(i64)(scratch.spawnArgcSlot)("spawn_final_argc")
                                    in
                                        let nullAddr =
                                            buildAdd(builder)(scratch.spawnArgvBase)(buildMul(builder)(finalArgc)(constInt(i64)(8u64)(false))("spawn_null_off"))("spawn_null_addr")
                                        in
                                            let _ =
                                                "spawn_null_ptr"
                                                |> buildIntToPtr(builder)(nullAddr)(ptrType)
                                                |> buildStore(builder)(constInt(i64)(0u64)(false))
                                            in exeCstr)

// `fork` and both sides: the child rewires its stdio over the pipe ends and `execve`s with the
// parent environment (`__ashes_envp`, captured by the entry function from the initial stack); the
// parent closes the child-side ends and wraps `{stdinW, stdoutR, stderrR, pid}` in `Ok`.
let emitSpawnForkExec context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType envpGlobal scratch exeCstr fds =
    match fds with
        | (stdinReadFd, stdinWriteFd, stdoutReadFd, stdoutWriteFd, stderrReadFd, stderrWriteFd) ->
            let childBlock = appendBasicBlock(context)(function_)("spawn_child")
            in
                let parentCheckBlock = appendBasicBlock(context)(function_)("spawn_parent_check")
                in
                    let parentBlock = appendBasicBlock(context)(function_)("spawn_parent")
                    in
                        let forkFailedBlock = appendBasicBlock(context)(function_)("spawn_fork_failed")
                        in
                            let spawnDoneBlock = appendBasicBlock(context)(function_)("spawn_done")
                            in
                                let pid = emitLinuxFork(builder)(i64)
                                in
                                    parentCheckBlock
                                    |> buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(pid)(constInt(i64)(0u64)(false))("spawn_is_child"))(childBlock)
                                    |> (given (_) -> positionBuilderAtEnd(builder)(childBlock))
                                    |> (given (_) ->
                                        false
                                        |> constInt(i64)(0u64)
                                        |> emitLinuxDup2(builder)(i64)(stdinReadFd))
                                    |> (given (_) ->
                                        false
                                        |> constInt(i64)(1u64)
                                        |> emitLinuxDup2(builder)(i64)(stdoutWriteFd))
                                    |> (given (_) ->
                                        false
                                        |> constInt(i64)(2u64)
                                        |> emitLinuxDup2(builder)(i64)(stderrWriteFd))
                                    |> (given (_) -> emitLinuxClose(builder)(i64)(stdinReadFd))
                                    |> (given (_) -> emitLinuxClose(builder)(i64)(stdinWriteFd))
                                    |> (given (_) -> emitLinuxClose(builder)(i64)(stdoutReadFd))
                                    |> (given (_) -> emitLinuxClose(builder)(i64)(stdoutWriteFd))
                                    |> (given (_) -> emitLinuxClose(builder)(i64)(stderrReadFd))
                                    |> (given (_) -> emitLinuxClose(builder)(i64)(stderrWriteFd))
                                    |> (given (_) -> buildLoad(builder)(i64)(envpGlobal)("spawn_envp"))
                                    |> (given (envp) ->
                                        emitLinuxExecve(builder)(i64)(buildPtrToInt(builder)(exeCstr)(i64)("spawn_exe_int"))(scratch.spawnArgvBase)(envp))
                                    |> (given (_) ->
                                        false
                                        |> constInt(i64)(1u64)
                                        |> emitLinuxProcessExitWithCode(builder)(i64))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(parentCheckBlock))
                                    |> (given (_) ->
                                        buildCondBr(builder)(buildICmp(builder)(intPredicateSlt)(pid)(constInt(i64)(0u64)(false))("spawn_fork_failed_flag"))(forkFailedBlock)(parentBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(forkFailedBlock))
                                    |> (given (_) ->
                                        buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(1)(emitAsciiHeapString(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(processSpawnForkFailedCodes)("spawn_fork_err_msg"))("spawn_fork_err"))(scratch.spawnResultSlot))
                                    |> (given (_) -> buildBr(builder)(spawnDoneBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(parentBlock))
                                    |> (given (_) -> emitLinuxClose(builder)(i64)(stdinReadFd))
                                    |> (given (_) -> emitLinuxClose(builder)(i64)(stdoutWriteFd))
                                    |> (given (_) -> emitLinuxClose(builder)(i64)(stderrWriteFd))
                                    |> (given (_) ->
                                        emitRcAllocPayloadPtrDynamic(builder)(i64)(i8)(mallocFn)(mallocType)(constInt(i64)(32u64)(false))("spawn_proc"))
                                    |> (given (procPtr) ->
                                        procPtr
                                        |> buildStore(builder)(stdinWriteFd)
                                        |> (given (_) ->
                                            "spawn_proc_stdout"
                                            |> gepBytes(builder)(i64)(i8)(procPtr)(8)
                                            |> buildStore(builder)(stdoutReadFd))
                                        |> (given (_) ->
                                            "spawn_proc_stderr"
                                            |> gepBytes(builder)(i64)(i8)(procPtr)(16)
                                            |> buildStore(builder)(stderrReadFd))
                                        |> (given (_) ->
                                            "spawn_proc_pid"
                                            |> gepBytes(builder)(i64)(i8)(procPtr)(24)
                                            |> buildStore(builder)(pid))
                                        |> (given (_) -> buildPtrToInt(builder)(procPtr)(i64)("spawn_proc_ref")))
                                    |> (given (procRef) ->
                                        buildStore(builder)(emitResultAdt(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(0)(procRef)("spawn_ok"))(scratch.spawnResultSlot))
                                    |> (given (_) -> buildBr(builder)(spawnDoneBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(spawnDoneBlock))
                                    |> (given (_) -> buildLoad(builder)(i64)(scratch.spawnResultSlot)("spawn_result_value"))

// `Ashes.IO.Process.spawn(exe)(args)`: `Result(Str, Process)` — stage 0's
// `EmitLinuxSpawnProcess` phase for phase.
let emitSpawnProcess context function_ i64 i8 i32 ptrType builder mallocFn mallocType memcpyFn memcpyType envpGlobal exeRef argsRef =
    (let scratch = emitSpawnScratch(builder)(i64)(i8)(i32)
    in
        let _ =
            buildStore(builder)(constInt(i64)(0u64)(false))(scratch.spawnResultSlot)
        in
            let exeCstr = emitSpawnBuildArgv(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(scratch)(exeRef)(argsRef)
            in
                let _ =
                    "spawn_stdin_pipe_addr"
                    |> buildPtrToInt(builder)(scratch.spawnStdinPipe)(i64)
                    |> emitLinuxPipe2(builder)(i64)
                in
                    let _ =
                        "spawn_stdout_pipe_addr"
                        |> buildPtrToInt(builder)(scratch.spawnStdoutPipe)(i64)
                        |> emitLinuxPipe2(builder)(i64)
                    in
                        let _ =
                            "spawn_stderr_pipe_addr"
                            |> buildPtrToInt(builder)(scratch.spawnStderrPipe)(i64)
                            |> emitLinuxPipe2(builder)(i64)
                        in
                            match emitProcessPipeFds(builder)(i64)(i8)(i32)(scratch.spawnStdinPipe)("spawn_stdin") with
                                | (stdinReadFd, stdinWriteFd) ->
                                    match emitProcessPipeFds(builder)(i64)(i8)(i32)(scratch.spawnStdoutPipe)("spawn_stdout") with
                                        | (stdoutReadFd, stdoutWriteFd) ->
                                            match emitProcessPipeFds(builder)(i64)(i8)(i32)(scratch.spawnStderrPipe)("spawn_stderr") with
                                                | (stderrReadFd, stderrWriteFd) -> emitSpawnForkExec(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(envpGlobal)(scratch)(exeCstr)((stdinReadFd, stdinWriteFd, stdoutReadFd, stdoutWriteFd, stderrReadFd, stderrWriteFd)))

// `Ashes.IO.Process.writeStdin(process)(text)`: a partial-write-safe loop over the child's stdin
// pipe (a pipe accepts at most a buffer's worth per `write`), stopping on any non-positive
// count — stage 0's `EmitProcessWriteStdin`. Returns `Unit`.
let emitProcessWriteStdin context function_ i64 i8 ptrType builder arena processRef textRef =
    (let writtenSlot = buildAlloca(builder)(i64)("proc_write_written")
    in
        let writeLoopBlock = appendBasicBlock(context)(function_)("proc_write_loop")
        in
            let writeBodyBlock = appendBasicBlock(context)(function_)("proc_write_body")
            in
                let writeOkBlock = appendBasicBlock(context)(function_)("proc_write_ok")
                in
                    let writeDoneBlock = appendBasicBlock(context)(function_)("proc_write_done")
                    in
                        let stdinFd = emitLoadProcessField(builder)(i64)(i8)(ptrType)(processRef)(0)("proc_stdin_fd")
                        in
                            match emitStringParts(builder)(i64)(ptrType)(textRef)("proc_write_text") with
                                | (textLen, textAddr) ->
                                    writtenSlot
                                    |> buildStore(builder)(constInt(i64)(0u64)(false))
                                    |> (given (_) -> buildBr(builder)(writeLoopBlock))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(writeLoopBlock))
                                    |> (given (_) -> buildLoad(builder)(i64)(writtenSlot)("proc_written_value"))
                                    |> (given (written) ->
                                        writeBodyBlock
                                        |> buildCondBr(builder)(buildICmp(builder)(intPredicateUge)(written)(textLen)("proc_all_written"))(writeDoneBlock)
                                        |> (given (_) -> positionBuilderAtEnd(builder)(writeBodyBlock))
                                        |> (given (_) ->
                                            "proc_write_remaining"
                                            |> buildSub(builder)(textLen)(written)
                                            |> emitLinuxWrite(builder)(i64)(stdinFd)(buildAdd(builder)(textAddr)(written)("proc_write_cursor")))
                                        |> (given (writtenNow) ->
                                            writeOkBlock
                                            |> buildCondBr(builder)(buildICmp(builder)(intPredicateSle)(writtenNow)(constInt(i64)(0u64)(false))("proc_write_failed"))(writeDoneBlock)
                                            |> (given (_) -> positionBuilderAtEnd(builder)(writeOkBlock))
                                            |> (given (_) ->
                                                buildStore(builder)(buildAdd(builder)(written)(writtenNow)("proc_write_new_written"))(writtenSlot))
                                            |> (given (_) -> buildBr(builder)(writeLoopBlock))))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(writeDoneBlock))
                                    |> (given (_) -> emitArenaAllocAdt(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(0)(0)("proc_write_unit")))

// `Ashes.IO.Process.readStdoutLine`/`readStderrLine`: one line from the child's pipe as
// `Maybe(Str)` — a fresh per-call 64 KiB stack buffer scanned one byte at a time (`\n` ends the
// line, a `\r` before it is dropped, EOF with nothing buffered is `None`), stage 0's
// `EmitProcessReadLine` exactly, its own overflow panic message included. Distinct from
// `emitReadLineFromFd` deliberately: the stdin reader mirrors stage 0's stdin path (which
// batches through a persistent module-global ring there), this one stage 0's process path.
let emitProcessReadLine context function_ i64 i8 ptrType builder mallocFn mallocType memcpyFn memcpyType readStdout processRef =
    (let prefix =
        if readStdout
        then "proc_rdout"
        else "proc_rderr"
    in
        let fieldOffset =
            if readStdout
            then 8
            else 16
        in
            let buffer =
                buildAlloca(builder)(arrayType(i8)(65536u64))(prefix + "_buf")
            in
                let byteSlot = buildAlloca(builder)(i8)(prefix + "_byte")
                in
                    let lenSlot = buildAlloca(builder)(i64)(prefix + "_len")
                    in
                        let resultSlot = buildAlloca(builder)(i64)(prefix + "_result")
                        in
                            let loopBlock = appendBasicBlock(context)(function_)(prefix + "_loop")
                            in
                                let inspectBlock = appendBasicBlock(context)(function_)(prefix + "_inspect")
                                in
                                    let skipCrBlock = appendBasicBlock(context)(function_)(prefix + "_skip_cr")
                                    in
                                        let storeByteBlock = appendBasicBlock(context)(function_)(prefix + "_store_byte")
                                        in
                                            let appendByteBlock = appendBasicBlock(context)(function_)(prefix + "_append_byte")
                                            in
                                                let eofBlock = appendBasicBlock(context)(function_)(prefix + "_eof")
                                                in
                                                    let finishSomeBlock = appendBasicBlock(context)(function_)(prefix + "_finish_some")
                                                    in
                                                        let returnNoneBlock = appendBasicBlock(context)(function_)(prefix + "_return_none")
                                                        in
                                                            let overflowBlock = appendBasicBlock(context)(function_)(prefix + "_overflow")
                                                            in
                                                                let continueBlock = appendBasicBlock(context)(function_)(prefix + "_continue")
                                                                in
                                                                    let fd = emitLoadProcessField(builder)(i64)(i8)(ptrType)(processRef)(fieldOffset)(prefix + "_fd")
                                                                    in
                                                                        let _ =
                                                                            lenSlot
                                                                            |> buildStore(builder)(constInt(i64)(0u64)(false))
                                                                            |> (given (_) ->
                                                                                buildStore(builder)(constInt(i64)(0u64)(false))(resultSlot))
                                                                            |> (given (_) -> buildBr(builder)(loopBlock))
                                                                            |> (given (_) -> positionBuilderAtEnd(builder)(loopBlock))
                                                                            |> (given (_) ->
                                                                                false
                                                                                |> constInt(i64)(1u64)
                                                                                |> emitLinuxRead(builder)(i64)(fd)(buildPtrToInt(builder)(byteSlot)(i64)(prefix + "_byte_addr")))
                                                                            |> (given (bytesRead) ->
                                                                                buildCondBr(builder)(buildICmp(builder)(intPredicateSgt)(bytesRead)(constInt(i64)(0u64)(false))(prefix + "_has_byte"))(inspectBlock)(eofBlock))
                                                                            |> (given (_) -> positionBuilderAtEnd(builder)(inspectBlock))
                                                                            |> (given (_) -> buildLoad(builder)(i8)(byteSlot)(prefix + "_current_byte"))
                                                                            |> (given (currentByte) ->
                                                                                skipCrBlock
                                                                                |> buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(currentByte)(constInt(i8)(10u64)(false))(prefix + "_is_lf"))(finishSomeBlock)
                                                                                |> (given (_) -> positionBuilderAtEnd(builder)(skipCrBlock))
                                                                                |> (given (_) ->
                                                                                    buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(currentByte)(constInt(i8)(13u64)(false))(prefix + "_is_cr"))(loopBlock)(storeByteBlock)))
                                                                            |> (given (_) -> positionBuilderAtEnd(builder)(storeByteBlock))
                                                                            |> (given (_) -> buildLoad(builder)(i64)(lenSlot)(prefix + "_len_value"))
                                                                            |> (given (currentLen) ->
                                                                                appendByteBlock
                                                                                |> buildCondBr(builder)(buildICmp(builder)(intPredicateUge)(currentLen)(constInt(i64)(65536u64)(false))(prefix + "_at_capacity"))(overflowBlock)
                                                                                |> (given (_) -> positionBuilderAtEnd(builder)(appendByteBlock))
                                                                                |> (given (_) ->
                                                                                    prefix + "_dest_ptr"
                                                                                    |> buildGEP(builder)(i8)(buffer)([currentLen])(1u32)
                                                                                    |> buildStore(builder)(buildLoad(builder)(i8)(byteSlot)(prefix + "_append_value")))
                                                                                |> (given (_) ->
                                                                                    buildStore(builder)(buildAdd(builder)(currentLen)(constInt(i64)(1u64)(false))(prefix + "_len_next"))(lenSlot))
                                                                                |> (given (_) -> buildBr(builder)(loopBlock)))
                                                                        in
                                                                            eofBlock
                                                                            |> positionBuilderAtEnd(builder)
                                                                            |> (given (_) -> buildLoad(builder)(i64)(lenSlot)(prefix + "_len_at_eof"))
                                                                            |> (given (lenAtEof) ->
                                                                                buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(lenAtEof)(constInt(i64)(0u64)(false))(prefix + "_is_empty"))(returnNoneBlock)(finishSomeBlock))
                                                                            |> (given (_) -> positionBuilderAtEnd(builder)(finishSomeBlock))
                                                                            |> (given (_) -> buildLoad(builder)(i64)(lenSlot)(prefix + "_final_len"))
                                                                            |> (given (finalLen) ->
                                                                                emitHeapStringFromBytesAddr(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(buildPtrToInt(builder)(buffer)(i64)(prefix + "_buf_addr"))(finalLen)(prefix + "_string"))
                                                                            |> (given (stringRef) ->
                                                                                let someValue = emitAllocAdtRuntimeManaged(builder)(i64)(i8)(mallocFn)(mallocType)(1)(1)(prefix + "_some")
                                                                                in
                                                                                    let _ =
                                                                                        prefix + "_some_field"
                                                                                        |> gepBytes(builder)(i64)(i8)(buildIntToPtr(builder)(someValue)(ptrType)(prefix + "_some_ptr"))(8)
                                                                                        |> buildStore(builder)(stringRef)
                                                                                    in someValue)
                                                                            |> (given (someValue) -> buildStore(builder)(someValue)(resultSlot))
                                                                            |> (given (_) -> buildBr(builder)(continueBlock))
                                                                            |> (given (_) -> positionBuilderAtEnd(builder)(returnNoneBlock))
                                                                            |> (given (_) ->
                                                                                buildStore(builder)(emitAllocAdtRuntimeManaged(builder)(i64)(i8)(mallocFn)(mallocType)(0)(0)(prefix + "_none"))(resultSlot))
                                                                            |> (given (_) -> buildBr(builder)(continueBlock))
                                                                            |> (given (_) -> positionBuilderAtEnd(builder)(overflowBlock))
                                                                            |> (given (_) -> emitProcessPanicMessage(builder)(i64)(i8)(processReadLineTooLongCodes)(prefix))
                                                                            |> (given (_) -> positionBuilderAtEnd(builder)(continueBlock))
                                                                            |> (given (_) -> buildLoad(builder)(i64)(resultSlot)(prefix + "_result_value")))

// `Ashes.IO.Process.waitForExit(process)`: block in `wait4` and return `WEXITSTATUS`
// (`(status >> 8) & 0xff`) — stage 0's `EmitProcessWaitForExit`.
let emitProcessWaitForExit builder i64 i8 ptrType processRef =
    (let statusSlot = buildAlloca(builder)(i64)("proc_wait_status")
    in
        let pid = emitLoadProcessField(builder)(i64)(i8)(ptrType)(processRef)(24)("proc_wait_pid")
        in
            statusSlot
            |> buildStore(builder)(constInt(i64)(0u64)(false))
            |> (given (_) ->
                false
                |> constInt(i64)(0u64)
                |> emitLinuxWait4(builder)(i64)(pid)(buildPtrToInt(builder)(statusSlot)(i64)("proc_wait_status_addr")))
            |> (given (_) -> buildLoad(builder)(i64)(statusSlot)("proc_wait_status_value"))
            |> (given (status) ->
                buildLShr(builder)(status)(constInt(i64)(8u64)(false))("proc_exit_shifted"))
            |> (given (shifted) ->
                buildAnd(builder)(shifted)(constInt(i64)(255u64)(false))("proc_exit_code")))

// A dropped `Process` (its `CleanupResource` at scope exit): close the three pipe ends, reap the
// child with a non-blocking `wait4`, and when it is still running, `SIGKILL` it and wait —
// stage 0's `EmitProcessDrop`. Fire-and-forget: a closed pipe or an already-reaped child is
// harmless.
let emitProcessDrop context function_ builder i64 i8 ptrType processRef =
    (let terminateBlock = appendBasicBlock(context)(function_)("proc_drop_terminate")
    in
        let doneBlock = appendBasicBlock(context)(function_)("proc_drop_done")
        in
            let zero = constInt(i64)(0u64)(false)
            in
                "proc_drop_stdin"
                |> emitLoadProcessField(builder)(i64)(i8)(ptrType)(processRef)(0)
                |> emitLinuxClose(builder)(i64)
                |> (given (_) -> emitLoadProcessField(builder)(i64)(i8)(ptrType)(processRef)(8)("proc_drop_stdout"))
                |> emitLinuxClose(builder)(i64)
                |> (given (_) -> emitLoadProcessField(builder)(i64)(i8)(ptrType)(processRef)(16)("proc_drop_stderr"))
                |> emitLinuxClose(builder)(i64)
                |> (given (_) -> emitLoadProcessField(builder)(i64)(i8)(ptrType)(processRef)(24)("proc_drop_pid"))
                |> (given (pid) ->
                    false
                    |> constInt(i64)(1u64)
                    |> emitLinuxWait4(builder)(i64)(pid)(zero)
                    |> (given (waitResult) -> buildICmp(builder)(intPredicateEq)(waitResult)(zero)("proc_drop_running"))
                    |> (given (stillRunning) -> buildCondBr(builder)(stillRunning)(terminateBlock)(doneBlock))
                    |> (given (_) -> positionBuilderAtEnd(builder)(terminateBlock))
                    |> (given (_) ->
                        false
                        |> constInt(i64)(9u64)
                        |> emitLinuxKill(builder)(i64)(pid))
                    |> (given (_) -> emitLinuxWait4(builder)(i64)(pid)(zero)(zero))
                    |> (given (_) -> buildBr(builder)(doneBlock))
                    |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))))

// `Ashes.IO.Process.kill(process)`: `SIGTERM` the child, returning `Unit` — stage 0's
// `EmitProcessKill`.
let emitProcessKill context function_ builder i64 i8 ptrType arena processRef =
    "proc_kill_pid"
    |> emitLoadProcessField(builder)(i64)(i8)(ptrType)(processRef)(24)
    |> (given (pid) ->
        false
        |> constInt(i64)(15u64)
        |> emitLinuxKill(builder)(i64)(pid))
    |> (given (_) -> emitArenaAllocAdt(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(0)(0)("proc_kill_unit"))
