// The leak check's report, stage 0's `LlvmCodegenMemory.LeakCheck.cs`: a program compiled with
// the leak check on writes `ashes-rc-live N` to standard error when its entry returns, `N` being
// the reference-counted cells its region allocated and never released. A value the program keeps
// to its end counts as live, so a leak is told by running the same work twice: every cell one
// more round leaves behind is leaked. The digits are formatted the way `PrintInt` formats them,
// from the end of a 32-byte stack buffer, and written with the raw `write` syscall to fd 2.
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
import AshesCompiler.Backend.IrCodegen.Syscalls.LinuxX64
import AshesCompiler.Backend.IrCodegen.RcRegion
export (
    value emitLeakCheckReport,
)

// "ashes-rc-live " as byte codes.
let leakCheckLabelCodes = [97, 115, 104, 101, 115, 45, 114, 99, 45, 108, 105, 118, 101, 32]

let leakCheckStderrFd i64 = constInt(i64)(2u64)(false)

// The label, from a stack buffer.
let emitLeakCheckLabel builder i64 i8 =
    (let bufferType = arrayType(i8)(16u64)
    in
        let buffer = buildEntryAlloca(builder)(bufferType)("leak_check_label")
        in
            let _ = storeAsciiBytes(builder)(i64)(i8)(bufferType)(buffer)(0)(leakCheckLabelCodes)
            in
                leakCheckLabelCodes
                |> Ashes.Collection.List.length
                |> Ashes.Number.UInt.fromInt64
                |> (given (length) -> constInt(i64)(length)(false))
                |> emitLinuxWrite(builder)(i64)(leakCheckStderrFd(i64))(buildPtrToInt(builder)(buffer)(i64)("leak_check_label_addr")))

// The digits filled by the loop, then a newline, both to standard error.
let emitLeakCheckDigits builder i64 i8 (printState: PrintIntState) =
    (let count = buildLoad(builder)(i64)(printState.indexSlot)("leak_check_count")
    in
        let start =
            buildSub(builder)(constInt(i64)(32u64)(false))(count)("leak_check_start")
        in
            let digits = buildGEP(builder)(printState.bufferType)(printState.buffer)([constInt(i64)(0u64)(false), start])(2u32)("leak_check_digits")
            in
                let _ =
                    emitLinuxWrite(builder)(i64)(leakCheckStderrFd(i64))(buildPtrToInt(builder)(digits)(i64)("leak_check_digits_addr"))(count)
                in
                    let _ =
                        i64
                        |> leakCheckStderrFd
                        |> emitWriteNewlineToFd(builder)(i64)(i8)
                    in Unit)

// Writes the report when the region keeps a counter, leaving the builder in the block after it.
let emitLeakCheckReport context function_ builder i64 i8 (runtime: RcRegionRuntime) =
    match runtime.liveCellsGlobal with
        | None -> Unit
        | Some(global) ->
            let _ = emitLeakCheckLabel(builder)(i64)(i8)
            in
                let printState =
                    "leak_check_live"
                    |> buildLoad(builder)(i64)(global)
                    |> printIntPrologue(builder)(i64)(i8)
                in
                    let zeroBlock = appendBasicBlock(context)(function_)("leak_check_zero")
                    in
                        let loopCheckBlock = appendBasicBlock(context)(function_)("leak_check_loop_check")
                        in
                            let loopBodyBlock = appendBasicBlock(context)(function_)("leak_check_loop_body")
                            in
                                let writeBlock = appendBasicBlock(context)(function_)("leak_check_write")
                                in
                                    let initialWork = buildLoad(builder)(i64)(printState.workSlot)("leak_check_initial")
                                    in
                                        let _ =
                                            buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(initialWork)(constInt(i64)(0u64)(false))("leak_check_is_zero"))(zeroBlock)(loopCheckBlock)
                                        in
                                            let _ = positionBuilderAtEnd(builder)(zeroBlock)
                                            in
                                                let _ =
                                                    false
                                                    |> constInt(i64)(48u64)
                                                    |> storePrintBufferByte(builder)(i64)(i8)(printState.bufferType)(printState.buffer)(constInt(i64)(31u64)(false))
                                                in
                                                    let _ =
                                                        buildStore(builder)(constInt(i64)(1u64)(false))(printState.indexSlot)
                                                    in
                                                        let _ = buildBr(builder)(writeBlock)
                                                        in
                                                            let _ = positionBuilderAtEnd(builder)(loopCheckBlock)
                                                            in
                                                                let work = buildLoad(builder)(i64)(printState.workSlot)("leak_check_work")
                                                                in
                                                                    let _ =
                                                                        buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(work)(constInt(i64)(0u64)(false))("leak_check_done"))(writeBlock)(loopBodyBlock)
                                                                    in
                                                                        let _ = positionBuilderAtEnd(builder)(loopBodyBlock)
                                                                        in
                                                                            let _ = printIntDigitLoopBody(builder)(i64)(i8)(printState)(work)
                                                                            in
                                                                                let _ = buildBr(builder)(loopCheckBlock)
                                                                                in
                                                                                    let _ = positionBuilderAtEnd(builder)(writeBlock)
                                                                                    in emitLeakCheckDigits(builder)(i64)(i8)(printState)
