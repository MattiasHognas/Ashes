// The task emitters for `AshesCompiler.Backend.IrCodegen`: a task already completed with its
// result (`CreateCompletedTask`) and driving a task to its result (`RunTask`), over the task
// frame layout `TaskStructLayout` shares with lowering. A completed task is stage 0's
// `EmitCreateCompletedTask`: an arena frame of the header alone, its state the completed code and
// its result slot the value, every other header word zero. Lowering produces no other kind of
// task that reaches `RunTask` yet, so running a task reads its result slot; the scheduler that
// steps suspended coroutines, sleeps and composites is not ported.

import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
import AshesCompiler.Backend.IrCodegen.Arena
import AshesCompiler.Semantics.TaskStructLayout
import Ashes.Number.UInt
export (
    value emitCreateCompletedTask,
    value emitRunCompletedTask,
)

let storeTaskWord builder i64 i8 ptrType taskRef offsetBytes value =
    "task_word"
    |> memOffsetPtr(builder)(i64)(i8)(ptrType)(taskRef)(offsetBytes)
    |> buildStore(builder)(value)

let recursive storeZeroTaskWords builder i64 i8 ptrType taskRef (offsets: List(Int)) =
    match offsets with
        | [] -> Unit
        | offset :: rest ->
            let _ =
                false
                |> constInt(i64)(0u64)
                |> storeTaskWord(builder)(i64)(i8)(ptrType)(taskRef)(offset)
            in storeZeroTaskWords(builder)(i64)(i8)(ptrType)(taskRef)(rest)

let completedTaskZeroOffsets (layout: TaskStructLayout) =
    [
        layout.frameDropper,
        layout.coroutineFunction,
        layout.awaitedTask,
        layout.nextTask,
        layout.sleepDurationMilliseconds,
        layout.ioArgument0,
        layout.ioArgument1,
        layout.waitKind,
        layout.waitHandle,
        layout.waitData0,
        layout.waitData1,
        layout.arenaCursor,
        layout.arenaEnd,
        layout.readyNext,
        layout.waiter,
        layout.arenaOwner,
        layout.loopResetOk
    ]

let emitCreateCompletedTask context function_ builder i64 i8 ptrType (arena: ArenaRuntime) resultRef name =
    (let layout = taskStructLayout
    in
        let taskRef = emitArenaAlloc(context)(function_)(builder)(i64)(arena)(layout.headerSize)(name)
        in
            let _ =
                layout
                |> completedTaskZeroOffsets
                |> storeZeroTaskWords(builder)(i64)(i8)(ptrType)(taskRef)
            in
                let _ =
                    false
                    |> constInt(i64)(CompletedTaskState
                    |> taskStateCode
                    |> Ashes.Number.UInt.fromInt64)
                    |> storeTaskWord(builder)(i64)(i8)(ptrType)(taskRef)(layout.stateIndex)
                in
                    let _ = storeTaskWord(builder)(i64)(i8)(ptrType)(taskRef)(layout.resultSlot)(resultRef)
                    in
                        let _ =
                            false
                            |> constInt(i64)(Ashes.Number.UInt.fromInt64(layout.headerSize))
                            |> storeTaskWord(builder)(i64)(i8)(ptrType)(taskRef)(layout.frameSizeBytes)
                        in taskRef)

let emitRunCompletedTask builder i64 i8 ptrType taskRef name =
    buildLoad(builder)(i64)(memOffsetPtr(builder)(i64)(i8)(ptrType)(taskRef)(taskStructLayout.resultSlot)(name + "_result_ptr"))(name)
