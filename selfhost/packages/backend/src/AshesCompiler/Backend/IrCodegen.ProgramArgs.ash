// The program-argument list for `AshesCompiler.Backend.IrCodegen`, stage 0's
// `EmitLinuxProgramArgsInitialization` and its `LoadProgramArgs` case. The entry receives the
// initial stack pointer in `rdi`, where the SysV layout is `[argc][argv...][NULL][envp...]`, so
// `argv[i]` sits at `sp + 8 * (1 + i)`. The entry walks that vector once, from the last argument
// back to `argv[1]` (the executable path itself is not an argument), measures each NUL-terminated
// string, copies it into a fresh arena string and conses it onto the list being built, so the
// walk's descending order leaves the list in argument order.
//
// The list lives in one module global rather than a per-function stack cell: every function
// reading `Ashes.IO.args` loads the same cell whatever its call depth, which is what stage 0's
// #1030 fixed there (a stack cell leaves every read below the entry an empty list). The strings
// are arena values with the immortal header, like every other value the entry builds before any
// scope opens, so no reset reclaims them.
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
import AshesCompiler.Backend.IrCodegen.Arena
export (
    value programArgsGlobalName,
    value defineProgramArgsGlobal,
    value emitProgramArgsInit,
    value emitLoadProgramArgs,
)

let programArgsGlobalName = "__ashes_program_args"

// The global the entry fills and every `LoadProgramArgs` reads, zero until the entry runs.
let defineProgramArgsGlobal module_ i64 =
    programArgsGlobalName
    |> addGlobal(module_)(i64)
    |> (given (global_) ->
        Unit
        |> (given (_) ->
            false
            |> constInt(i64)(0u64)
            |> setInitializer(global_))
        |> (given (_) -> setLinkage(global_)(linkageInternal))
        |> (given (_) -> global_))

let emitLoadProgramArgs builder i64 programArgsGlobal name = buildLoad(builder)(i64)(programArgsGlobal)(name)

// The four frame cells the walk carries: the list built so far, the descending argv index, the
// `char*` of the argument being measured, and its length so far.
type ProgramArgsSlots =
    | argsListSlot: LLVMValueRef
    | argsIndexSlot: LLVMValueRef
    | argsPtrSlot: LLVMValueRef
    | argsLenSlot: LLVMValueRef

type ProgramArgsBlocks =
    | argsInitBlock: LLVMBasicBlockRef
    | argsLoopCheckBlock: LLVMBasicBlockRef
    | argsLenCheckBlock: LLVMBasicBlockRef
    | argsLenLoopCheckBlock: LLVMBasicBlockRef
    | argsLenBodyBlock: LLVMBasicBlockRef
    | argsBuildNodeBlock: LLVMBasicBlockRef
    | argsDoneBlock: LLVMBasicBlockRef

let programArgsSlotsOf builder i64 =
    ProgramArgsSlots(
        argsListSlot = buildAlloca(builder)(i64)("program_args_list"),
        argsIndexSlot = buildAlloca(builder)(i64)("program_args_index"),
        argsPtrSlot = buildAlloca(builder)(i64)("program_args_arg_ptr"),
        argsLenSlot = buildAlloca(builder)(i64)("program_args_arg_len")
    )

let programArgsBlocksOf context function_ =
    ProgramArgsBlocks(
        argsInitBlock = appendBasicBlock(context)(function_)("program_args_init"),
        argsLoopCheckBlock = appendBasicBlock(context)(function_)("program_args_loop_check"),
        argsLenCheckBlock = appendBasicBlock(context)(function_)("program_args_len_check"),
        argsLenLoopCheckBlock = appendBasicBlock(context)(function_)("program_args_len_loop_check"),
        argsLenBodyBlock = appendBasicBlock(context)(function_)("program_args_len_body"),
        argsBuildNodeBlock = appendBasicBlock(context)(function_)("program_args_build_node"),
        argsDoneBlock = appendBasicBlock(context)(function_)("program_args_done")
    )

// `argc > 1` starts the walk at `argc - 1`; a program invoked with no arguments jumps straight to
// the empty list.
let emitProgramArgsPrologue builder i64 i8 ptrType (slots: ProgramArgsSlots) (blocks: ProgramArgsBlocks) stackPointer =
    Unit
    |> (given (_) ->
        slots.argsListSlot |> buildStore(builder)(constInt(i64)(0u64)(false)))
    |> (given (_) -> loadWordAt(builder)(i64)(i8)(ptrType)(stackPointer)(0)("program_args_argc"))
    |> (given (argc) ->
        Unit
        |> (given (_) ->
            blocks.argsDoneBlock |> buildCondBr(builder)(buildICmp(builder)(intPredicateSgt)(argc)(constInt(i64)(1u64)(false))("program_args_has_args"))(blocks.argsInitBlock))
        |> (given (_) -> positionBuilderAtEnd(builder)(blocks.argsInitBlock))
        |> (given (_) ->
            slots.argsIndexSlot |> buildStore(builder)(buildSub(builder)(argc)(constInt(i64)(1u64)(false))("program_args_start_index")))
        |> (given (_) -> buildBr(builder)(blocks.argsLoopCheckBlock)))

// The outer loop over argv and the inner NUL scan that measures one argument.
let emitProgramArgsLoop builder i64 i8 ptrType (slots: ProgramArgsSlots) (blocks: ProgramArgsBlocks) stackPointer =
    Unit
    |> (given (_) -> positionBuilderAtEnd(builder)(blocks.argsLoopCheckBlock))
    |> (given (_) -> buildLoad(builder)(i64)(slots.argsIndexSlot)("program_args_index_value"))
    |> (given (index) ->
        blocks.argsDoneBlock
        |> buildCondBr(builder)(buildICmp(builder)(intPredicateSgt)(index)(constInt(i64)(0u64)(false))("program_args_continue"))(blocks.argsLenCheckBlock)
        |> (given (_) -> index))
    |> (given (_) -> positionBuilderAtEnd(builder)(blocks.argsLenCheckBlock))
    |> (given (_) -> buildLoad(builder)(i64)(slots.argsIndexSlot)("program_args_index_entry"))
    |> (given (index) ->
        buildAdd(builder)(stackPointer)(buildAdd(builder)(constInt(i64)(8u64)(false))(buildMul(builder)(index)(constInt(i64)(8u64)(false))("program_args_argv_entry_offset"))("program_args_argv_offset"))("program_args_argv_entry_addr"))
    |> (given (entryAddress) -> loadWordAt(builder)(i64)(i8)(ptrType)(entryAddress)(0)("program_args_argv_entry"))
    |> (given (argPtr) ->
        Unit
        |> (given (_) -> slots.argsPtrSlot |> buildStore(builder)(argPtr))
        |> (given (_) ->
            slots.argsLenSlot |> buildStore(builder)(constInt(i64)(0u64)(false)))
        |> (given (_) -> buildBr(builder)(blocks.argsLenLoopCheckBlock)))
    |> (given (_) -> positionBuilderAtEnd(builder)(blocks.argsLenLoopCheckBlock))
    |> (given (_) -> (buildLoad(builder)(i64)(slots.argsLenSlot)("program_args_current_len"), buildLoad(builder)(i64)(slots.argsPtrSlot)("program_args_current_arg_ptr")))
    |> (given (scan) ->
        match scan with
            | (currentLen, currentArgPtr) ->
                buildGEP(builder)(i8)(buildIntToPtr(builder)(currentArgPtr)(ptrType)("program_args_arg_bytes"))([currentLen])(1u32)("program_args_current_byte_ptr"))
    |> (given (bytePtr) -> buildLoad(builder)(i8)(bytePtr)("program_args_current_byte"))
    |> (given (currentByte) ->
        blocks.argsLenBodyBlock |> buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(currentByte)(constInt(i8)(0u64)(false))("program_args_reached_terminator"))(blocks.argsBuildNodeBlock))

// One more measured byte, then one cons cell over the copied argument, then the next argv entry;
// the done block publishes whatever the walk built.
let emitProgramArgsBuild context function_ builder i64 i8 ptrType (arena: ArenaRuntime) mallocFn mallocType memcpyFn memcpyType (slots: ProgramArgsSlots) (blocks: ProgramArgsBlocks) programArgsGlobal =
    Unit
    |> (given (_) -> positionBuilderAtEnd(builder)(blocks.argsLenBodyBlock))
    |> (given (_) ->
        slots.argsLenSlot |> buildStore(builder)(buildAdd(builder)(buildLoad(builder)(i64)(slots.argsLenSlot)("program_args_len_before_inc"))(constInt(i64)(1u64)(false))("program_args_next_len")))
    |> (given (_) -> buildBr(builder)(blocks.argsLenLoopCheckBlock))
    |> (given (_) -> positionBuilderAtEnd(builder)(blocks.argsBuildNodeBlock))
    |> (given (_) -> buildLoad(builder)(i64)(slots.argsLenSlot)("program_args_arg_len_value"))
    |> (given (argLen) ->
        "program_args_string" |> emitPlacedStringFromBytesAddr(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(false)(buildLoad(builder)(i64)(slots.argsPtrSlot)("program_args_copy_arg_ptr"))(argLen))
    |> (given (stringRef) ->
        "program_args_cons"
        |> emitArenaAlloc(context)(function_)(builder)(i64)(arena)(16)
        |> (given (consRef) ->
            Unit
            |> (given (_) -> storeWordAt(builder)(i64)(i8)(ptrType)(consRef)(0)(stringRef)("program_args_cons_head"))
            |> (given (_) ->
                storeWordAt(builder)(i64)(i8)(ptrType)(consRef)(8)(buildLoad(builder)(i64)(slots.argsListSlot)("program_args_prev_list"))("program_args_cons_tail"))
            |> (given (_) -> slots.argsListSlot |> buildStore(builder)(consRef))))
    |> (given (_) ->
        slots.argsIndexSlot |> buildStore(builder)(buildSub(builder)(buildLoad(builder)(i64)(slots.argsIndexSlot)("program_args_index_before_dec"))(constInt(i64)(1u64)(false))("program_args_index_dec")))
    |> (given (_) -> buildBr(builder)(blocks.argsLoopCheckBlock))
    |> (given (_) -> positionBuilderAtEnd(builder)(blocks.argsDoneBlock))
    |> (given (_) ->
        programArgsGlobal |> buildStore(builder)(buildLoad(builder)(i64)(slots.argsListSlot)("program_args_final_list")))

// The whole walk, emitted into the entry after the arena's first chunk is mapped (every argument
// string and cons cell comes out of it).
let emitProgramArgsInit context function_ builder i64 i8 ptrType (arena: ArenaRuntime) mallocFn mallocType memcpyFn memcpyType stackPointer programArgsGlobal =
    (let slots = programArgsSlotsOf(builder)(i64)
    in
        let blocks = programArgsBlocksOf(context)(function_)
        in
            Unit
            |> (given (_) ->
                programArgsGlobal |> buildStore(builder)(constInt(i64)(0u64)(false)))
            |> (given (_) -> emitProgramArgsPrologue(builder)(i64)(i8)(ptrType)(slots)(blocks)(stackPointer))
            |> (given (_) -> emitProgramArgsLoop(builder)(i64)(i8)(ptrType)(slots)(blocks)(stackPointer))
            |> (given (_) -> emitProgramArgsBuild(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(slots)(blocks)(programArgsGlobal))
            |> (given (_) -> Unit))
