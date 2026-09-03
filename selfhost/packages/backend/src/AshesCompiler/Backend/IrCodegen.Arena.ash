// The scoped-arena runtime: the chunked bump allocator behind every non-RC-managed allocation
// (`AllocAdt`, `Alloc`, `MakeClosure` with `runtimeManaged = false`) and the real
// `SaveArenaState`/`RestoreArenaState`/`ReclaimArenaChunks` brackets, mirroring stage 0's
// `LlvmCodegenMemory.cs` (`EmitAlloc`, `EmitHeapGrow`, `EmitSaveArenaState`,
// `EmitRestoreArenaState`, `EmitReclaimArenaChunks`) and architecture.md's "Scoped arenas".
//
// Two `.bss` module globals, `__ashes_heap_cursor` and `__ashes_heap_end`, bound the current
// chunk's free span. Every OS chunk carries an 8-byte header at its base (the previous chunk's end
// value, `0` for the first chunk) and an 8-byte footer at its usable end (the chunk's own base), so
// the reclaim walk goes end -> base -> previous end without assuming a fixed chunk size. Usable
// allocations occupy `[base + 8, base + size - 8)` and the end global holds `base + size - 8`. A
// chunk is `arenaChunkBytes` unless one request needs more, in which case it is `2 * needed + 16`.
//
// The two slow paths live in module-level helpers so every allocation site stays a compare and a
// bump: `__ashes_heap_grow` (one `mmap` per new chunk) and `__ashes_reclaim_arena_chunks` (the
// `munmap` walk from the pre-restore chunk back to the saved one). `defineArenaRuntime` emits both
// once per module, before any function body, with the module's shared builder; the `ArenaRuntime`
// record hands their handles and the two globals to every function's codegen. The OS calls come
// from `IrCodegen.Syscalls.LinuxX64`, the one target this backend emits today.
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
import AshesCompiler.Backend.IrCodegen.Syscalls.LinuxX64
import Ashes.Number.UInt
export (
    type ArenaRuntime(..),
    value arenaChunkBytes,
    value defineArenaRuntime,
    value emitArenaInit,
    value emitArenaAlloc,
    value emitArenaAllocDynamic,
    value emitArenaAllocAdt,
    value emitSaveArenaState,
    value emitRestoreArenaState,
    value emitReclaimArenaChunks,
)

type ArenaRuntime =
    | arenaCursorGlobal: LLVMValueRef
    | arenaEndGlobal: LLVMValueRef
    | arenaGrowFn: LLVMValueRef
    | arenaGrowType: LLVMTypeRef
    | arenaReclaimFn: LLVMValueRef
    | arenaReclaimType: LLVMTypeRef
    | arenaFailureMessage: LLVMValueRef
    | arenaFailureMessageLength: Int

let arenaChunkBytes = 4194304

let arenaChunkHeaderBytes = 8

let arenaChunkFooterBytes = 8

// "Runtime error: failed to allocate heap memory from OS\n"
let arenaFailureMessageCodes = [82, 117, 110, 116, 105, 109, 101, 32, 101, 114, 114, 111, 114, 58, 32, 102, 97, 105, 108, 101, 100, 32, 116, 111, 32, 97, 108, 108, 111, 99, 97, 116, 101, 32, 104, 101, 97, 112, 32, 109, 101, 109, 111, 114, 121, 32, 102, 114, 111, 109, 32, 79, 83, 10]

let arenaConst i64 value =
    constInt(i64)(Ashes.Number.UInt.fromInt64(value))(false)

// Every arena allocation is a whole number of 8-byte words, as stage 0's `AlignRuntimeSize`.
let alignArenaSize sizeBytes = (sizeBytes + 7) / 8 * 8

let recursive byteConstants i8 codes =
    match codes with
        | [] -> []
        | code :: rest ->
            constInt(i8)(Ashes.Number.UInt.fromInt64(code))(false) :: byteConstants(i8)(rest)

let addZeroWordGlobal module_ i64 name =
    name
    |> addGlobal(module_)(i64)
    |> (given (global) ->
        Unit
        |> (given (_) ->
            false
            |> constInt(i64)(0u64)
            |> setInitializer(global))
        |> (given (_) -> setLinkage(global)(linkageInternal))
        |> (given (_) -> global))

let addFailureMessageGlobal module_ i8 =
    arenaFailureMessageCodes
    |> Ashes.Collection.List.length
    |> Ashes.Number.UInt.fromInt64
    |> (given (length) ->
        "__ashes_heap_failure_message"
        |> addGlobal(module_)(arrayType(i8)(length))
        |> (given (global) ->
            Unit
            |> (given (_) ->
                length
                |> constArray(i8)(byteConstants(i8)(arenaFailureMessageCodes))
                |> setInitializer(global))
            |> (given (_) -> setGlobalConstant(global)(true))
            |> (given (_) -> setLinkage(global)(linkageInternal))
            |> (given (_) -> global)))

let addInternalFunction module_ name type_ =
    type_
    |> addFunction(module_)(name)
    |> (given (function_) ->
        Unit
        |> (given (_) -> setLinkage(function_)(linkageInternal))
        |> (given (_) -> function_))

// Writes a fresh OS chunk's header and footer and points the cursor/end globals at its usable span.
let emitArenaChunkSetup builder i64 i8 ptrType (arena: ArenaRuntime) chunkBase chunkSize prevEnd prefix =
    prefix + "_end"
    |> buildAdd(builder)(chunkBase)(buildSub(builder)(chunkSize)(arenaConst(i64)(arenaChunkFooterBytes))(prefix + "_usable_span"))
    |> (given (chunkEnd) ->
        Unit
        |> (given (_) ->
            prefix + "_header"
            |> memOffsetPtr(builder)(i64)(i8)(ptrType)(chunkBase)(0)
            |> buildStore(builder)(prevEnd))
        |> (given (_) ->
            prefix + "_footer"
            |> memOffsetPtr(builder)(i64)(i8)(ptrType)(chunkEnd)(0)
            |> buildStore(builder)(chunkBase))
        |> (given (_) ->
            buildStore(builder)(buildAdd(builder)(chunkBase)(arenaConst(i64)(arenaChunkHeaderBytes))(prefix + "_cursor_start"))(arena.arenaCursorGlobal))
        |> (given (_) -> buildStore(builder)(chunkEnd)(arena.arenaEndGlobal))
        |> (given (_) -> Unit))

// `mmap`s `chunkSize` bytes; a result at or above `-4096` unsigned is an errno, on which the
// program writes the failure message to stderr and exits with `1`, as stage 0 does.
let emitArenaOsAllocate context function_ builder i64 (arena: ArenaRuntime) chunkSize prefix =
    match (appendBasicBlock(context)(function_)(prefix + "_mmap_failed"), appendBasicBlock(context)(function_)(prefix + "_mmap_ok")) with
        | (failedBlock, okBlock) ->
            chunkSize
            |> emitLinuxMmapAnonymous(builder)(i64)
            |> (given (chunkBase) ->
                Unit
                |> (given (_) ->
                    buildCondBr(builder)(buildICmp(builder)(intPredicateUgt)(chunkBase)(arenaConst(i64)(-4096))(prefix + "_mmap_check"))(failedBlock)(okBlock))
                |> (given (_) -> positionBuilderAtEnd(builder)(failedBlock))
                |> (given (_) ->
                    arena.arenaFailureMessageLength
                    |> arenaConst(i64)
                    |> emitLinuxWrite(builder)(i64)(arenaConst(i64)(2))(buildPtrToInt(builder)(arena.arenaFailureMessage)(i64)(prefix + "_message")))
                |> (given (_) ->
                    1
                    |> arenaConst(i64)
                    |> emitLinuxProcessExitWithCode(builder)(i64))
                |> (given (_) -> positionBuilderAtEnd(builder)(okBlock))
                |> (given (_) -> chunkBase))

// `void __ashes_heap_grow(i64 needed)`: links a new chunk after the current one, sized to the
// standard chunk or to `2 * needed + 16` when the request alone would not fit.
let emitArenaGrowBody context builder i64 i8 ptrType (arena: ArenaRuntime) =
    "entry"
    |> appendBasicBlock(context)(arena.arenaGrowFn)
    |> positionBuilderAtEnd(builder)
    |> (given (_) -> getParam(arena.arenaGrowFn)(0u32))
    |> (given (needed) ->
        "grow_heap_fit_size"
        |> buildAdd(builder)(buildMul(builder)(needed)(arenaConst(i64)(2))("grow_heap_need2"))(arenaConst(i64)(arenaChunkHeaderBytes + arenaChunkFooterBytes))
        |> (given (fitSize) ->
            buildSelect(builder)(buildICmp(builder)(intPredicateUle)(fitSize)(arenaConst(i64)(arenaChunkBytes))("grow_heap_fits_standard"))(arenaConst(i64)(arenaChunkBytes))(fitSize)("grow_heap_chunk_size"))
        |> (given (chunkSize) ->
            "grow_heap_prev_end"
            |> buildLoad(builder)(i64)(arena.arenaEndGlobal)
            |> (given (prevEnd) ->
                "grow_heap"
                |> emitArenaOsAllocate(context)(arena.arenaGrowFn)(builder)(i64)(arena)(chunkSize)
                |> (given (chunkBase) -> emitArenaChunkSetup(builder)(i64)(i8)(ptrType)(arena)(chunkBase)(chunkSize)(prevEnd)("grow_heap"))))
        |> (given (_) -> buildRetVoid(builder)))

// `void __ashes_reclaim_arena_chunks(i64 savedEnd, i64 preRestoreEnd)`: `munmap`s every chunk
// from the pre-restore one back to (not including) the saved one, following the header links. The
// walk position lives in an entry-block slot, as this backend's LLVM surface has no `phi`.
let emitArenaReclaimBody context builder i64 i8 ptrType (arena: ArenaRuntime) =
    match (appendBasicBlock(context)(arena.arenaReclaimFn)("entry"), appendBasicBlock(context)(arena.arenaReclaimFn)("reclaim_loop"), appendBasicBlock(context)(arena.arenaReclaimFn)("reclaim_chunk"), appendBasicBlock(context)(arena.arenaReclaimFn)("reclaim_done")) with
        | (entryBlock, loopBlock, chunkBlock, doneBlock) ->
            Unit
            |> (given (_) -> positionBuilderAtEnd(builder)(entryBlock))
            |> (given (_) -> buildEntryAlloca(builder)(i64)("reclaim_current"))
            |> (given (currentSlot) ->
                Unit
                |> (given (_) ->
                    buildStore(builder)(getParam(arena.arenaReclaimFn)(1u32))(currentSlot))
                |> (given (_) -> buildBr(builder)(loopBlock))
                |> (given (_) -> positionBuilderAtEnd(builder)(loopBlock))
                |> (given (_) -> buildLoad(builder)(i64)(currentSlot)("reclaim_end"))
                |> (given (currentEnd) ->
                    Unit
                    |> (given (_) ->
                        buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(currentEnd)(getParam(arena.arenaReclaimFn)(0u32))("reclaim_reached_saved"))(doneBlock)(chunkBlock))
                    |> (given (_) -> positionBuilderAtEnd(builder)(chunkBlock))
                    |> (given (_) ->
                        buildLoad(builder)(i64)(memOffsetPtr(builder)(i64)(i8)(ptrType)(currentEnd)(0)("reclaim_footer"))("reclaim_base"))
                    |> (given (chunkBase) ->
                        Unit
                        |> (given (_) ->
                            buildLoad(builder)(i64)(memOffsetPtr(builder)(i64)(i8)(ptrType)(chunkBase)(0)("reclaim_header"))("reclaim_prev_end"))
                        |> (given (prevEnd) ->
                            Unit
                            |> (given (_) ->
                                "reclaim_chunk_size"
                                |> buildSub(builder)(buildAdd(builder)(currentEnd)(arenaConst(i64)(arenaChunkFooterBytes))("reclaim_chunk_limit"))(chunkBase)
                                |> emitLinuxMunmap(builder)(i64)(chunkBase))
                            |> (given (_) -> buildStore(builder)(prevEnd)(currentSlot))
                            |> (given (_) -> buildBr(builder)(loopBlock)))))
                |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))
                |> (given (_) -> buildRetVoid(builder)))

// Creates the two arena globals, the failure message, and both helper functions in `module_`,
// emitting the helper bodies with `builder` (repositioned by the caller afterwards).
let defineArenaRuntime module_ context builder i64 i8 ptrType =
    (functionType(voidType(context))([i64])(1u32)(false), functionType(voidType(context))([i64, i64])(2u32)(false))
    |> (given (types) ->
        match types with
            | (growType, reclaimType) ->
                ArenaRuntime(
                    arenaCursorGlobal = addZeroWordGlobal(module_)(i64)("__ashes_heap_cursor"),
                    arenaEndGlobal = addZeroWordGlobal(module_)(i64)("__ashes_heap_end"),
                    arenaGrowFn = addInternalFunction(module_)("__ashes_heap_grow")(growType),
                    arenaGrowType = growType,
                    arenaReclaimFn = addInternalFunction(module_)("__ashes_reclaim_arena_chunks")(reclaimType),
                    arenaReclaimType = reclaimType,
                    arenaFailureMessage = addFailureMessageGlobal(module_)(i8),
                    arenaFailureMessageLength = Ashes.Collection.List.length(arenaFailureMessageCodes)
                ))
    |> (given (arena) ->
        Unit
        |> (given (_) -> emitArenaGrowBody(context)(builder)(i64)(i8)(ptrType)(arena))
        |> (given (_) -> emitArenaReclaimBody(context)(builder)(i64)(i8)(ptrType)(arena))
        |> (given (_) -> arena))

// The entry prologue's first chunk: one standard chunk with no predecessor.
let emitArenaInit context function_ builder i64 i8 ptrType (arena: ArenaRuntime) =
    "init_heap"
    |> emitArenaOsAllocate(context)(function_)(builder)(i64)(arena)(arenaConst(i64)(arenaChunkBytes))
    |> (given (chunkBase) ->
        emitArenaChunkSetup(builder)(i64)(i8)(ptrType)(arena)(chunkBase)(arenaConst(i64)(arenaChunkBytes))(arenaConst(i64)(0))("init_heap"))

// Bumps the cursor by `sizeRef` (an already-aligned `i64` byte count), growing first when the
// request does not fit in the current chunk, and returns the allocation's address word.
let emitArenaAllocDynamic context function_ builder i64 (arena: ArenaRuntime) sizeRef name =
    match (appendBasicBlock(context)(function_)(name + "_arena_grow"), appendBasicBlock(context)(function_)(name + "_arena_ok")) with
        | (growBlock, okBlock) ->
            Unit
            |> (given (_) -> buildLoad(builder)(i64)(arena.arenaCursorGlobal)(name + "_check_cursor"))
            |> (given (cursor) ->
                buildICmp(builder)(intPredicateUgt)(buildAdd(builder)(cursor)(sizeRef)(name + "_check_needed"))(buildLoad(builder)(i64)(arena.arenaEndGlobal)(name + "_check_end"))(name + "_overflow"))
            |> (given (overflow) -> buildCondBr(builder)(overflow)(growBlock)(okBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(growBlock))
            |> (given (_) -> buildCall(builder)(arena.arenaGrowType)(arena.arenaGrowFn)([sizeRef])(1u32)(""))
            |> (given (_) -> buildBr(builder)(okBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(okBlock))
            |> (given (_) -> buildLoad(builder)(i64)(arena.arenaCursorGlobal)(name))
            |> (given (address) ->
                Unit
                |> (given (_) ->
                    buildStore(builder)(buildAdd(builder)(address)(sizeRef)(name + "_next_cursor"))(arena.arenaCursorGlobal))
                |> (given (_) -> address))

let emitArenaAlloc context function_ builder i64 (arena: ArenaRuntime) sizeBytes name =
    emitArenaAllocDynamic(context)(function_)(builder)(i64)(arena)(sizeBytes
    |> alignArenaSize
    |> arenaConst(i64))(name)

// A `[tag][field0]...[fieldN-1]` cell in the arena, the tag written and the fields left to
// `SetAdtField`, returned as the address word.
let emitArenaAllocAdt context function_ builder i64 i8 ptrType (arena: ArenaRuntime) tag fieldCount name =
    name
    |> emitArenaAlloc(context)(function_)(builder)(i64)(arena)((fieldCount + 1) * 8)
    |> (given (address) ->
        Unit
        |> (given (_) ->
            name + "_tag"
            |> memOffsetPtr(builder)(i64)(i8)(ptrType)(address)(0)
            |> buildStore(builder)(arenaConst(i64)(tag)))
        |> (given (_) -> address))

// `SaveArenaState`: the cursor and end globals into the two local slots.
let emitSaveArenaState builder i64 (arena: ArenaRuntime) cursorSlot endSlot =
    Unit
    |> (given (_) ->
        buildStore(builder)(buildLoad(builder)(i64)(arena.arenaCursorGlobal)("arena_save_cursor"))(cursorSlot))
    |> (given (_) ->
        buildStore(builder)(buildLoad(builder)(i64)(arena.arenaEndGlobal)("arena_save_end"))(endSlot))

// `RestoreArenaState`: records the current end in `preRestoreSlot` for the reclaim that follows,
// then resets both globals to the saved watermark. Chunks are not released here so an
// intervening copy-out can still read them.
let emitRestoreArenaState builder i64 (arena: ArenaRuntime) cursorSlot endSlot preRestoreSlot =
    Unit
    |> (given (_) ->
        buildStore(builder)(buildLoad(builder)(i64)(arena.arenaEndGlobal)("arena_pre_restore_end"))(preRestoreSlot))
    |> (given (_) ->
        buildStore(builder)(buildLoad(builder)(i64)(cursorSlot)("arena_restore_cursor"))(arena.arenaCursorGlobal))
    |> (given (_) ->
        buildStore(builder)(buildLoad(builder)(i64)(endSlot)("arena_restore_end"))(arena.arenaEndGlobal))

// `ReclaimArenaChunks`: nothing to free when the pre-restore end still names the saved chunk;
// otherwise the helper walks and `munmap`s the abandoned chunks.
let emitReclaimArenaChunks context function_ builder i64 (arena: ArenaRuntime) savedEndSlot preRestoreSlot =
    match (appendBasicBlock(context)(function_)("reclaim_free_chunks"), appendBasicBlock(context)(function_)("reclaim_merge")) with
        | (freeBlock, mergeBlock) ->
            match (buildLoad(builder)(i64)(savedEndSlot)("reclaim_saved_end"), buildLoad(builder)(i64)(preRestoreSlot)("reclaim_pre_restore_end")) with
                | (savedEnd, preRestoreEnd) ->
                    Unit
                    |> (given (_) ->
                        buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(preRestoreEnd)(savedEnd)("reclaim_same_chunk"))(mergeBlock)(freeBlock))
                    |> (given (_) -> positionBuilderAtEnd(builder)(freeBlock))
                    |> (given (_) -> buildCall(builder)(arena.arenaReclaimType)(arena.arenaReclaimFn)([savedEnd, preRestoreEnd])(2u32)(""))
                    |> (given (_) -> buildBr(builder)(mergeBlock))
                    |> (given (_) -> positionBuilderAtEnd(builder)(mergeBlock))
