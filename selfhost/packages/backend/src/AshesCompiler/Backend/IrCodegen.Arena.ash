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
//
// The copy-out instructions (`CopyOutArena`, `CopyOutList`) move a value that lives above a
// scope's restored watermark to storage that survives the reset, mirroring stage 0's
// `EmitCopyOutArena`/`EmitCopyOutList`. They too live in module-level helpers
// (`__ashes_copy_out_fixed`, `__ashes_copy_out_string`, `__ashes_copy_out_bigint`,
// `__ashes_copy_out_list`, plus `__ashes_move_bytes` and the two list-building helpers), so an
// instruction site is one call and the loop state each helper needs lives in that helper's own
// entry block rather than in the calling function's frame. They are defined only for a module
// whose IR carries a copy-out instruction (`CopyOutRuntime`). Every helper takes the
// `runtimeManaged` flag as an `i64` argument: `1` puts the copy in a `malloc`'d RC cell behind the
// real 16-byte `{count, size}` header, `0` bumps it from the arena (a `Str`/`BigInt` copy gets the
// immortal-count header so a retain or release on it is a no-op, as stage 0's
// `EmitArenaValueAllocDynamic` writes).
import AshesCompiler.Semantics.IrInstructions
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
    value emitArenaValueAllocDynamic,
    value emitMoveBytes,
    value emitCopyOutArena,
    value emitCopyOutList,
    value emitSaveArenaState,
    value emitRestoreArenaState,
    value emitReclaimArenaChunks,
)

// The copy-out helper functions. They call libc's `malloc`/`free`/`memcpy`, which would turn
// every program into a dynamically linked one, so they are only defined (`arenaCopyOut = Some`)
// for a module whose IR contains a `CopyOutArena` or `CopyOutList`.
type CopyOutRuntime =
    | moveBytesFn: LLVMValueRef
    | moveBytesType: LLVMTypeRef
    | copyOutFixedFn: LLVMValueRef
    | copyOutFixedType: LLVMTypeRef
    | copyOutStringFn: LLVMValueRef
    | copyOutStringType: LLVMTypeRef
    | copyOutBigIntFn: LLVMValueRef
    | copyOutBigIntType: LLVMTypeRef
    | copyOutCellFn: LLVMValueRef
    | copyOutCellType: LLVMTypeRef
    | copyOutListHeadFn: LLVMValueRef
    | copyOutListHeadType: LLVMTypeRef
    | copyOutListFn: LLVMValueRef
    | copyOutListType: LLVMTypeRef

type ArenaRuntime =
    | arenaCursorGlobal: LLVMValueRef
    | arenaEndGlobal: LLVMValueRef
    | arenaGrowFn: LLVMValueRef
    | arenaGrowType: LLVMTypeRef
    | arenaReclaimFn: LLVMValueRef
    | arenaReclaimType: LLVMTypeRef
    | arenaFailureMessage: LLVMValueRef
    | arenaFailureMessageLength: Int
    | arenaCopyOut: Maybe(CopyOutRuntime)

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
            |> (given (_) -> buildAlloca(builder)(i64)("reclaim_current"))
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

// Word-aligns a runtime `i64` byte count, as `alignArenaSize` does for a compile-time one.
let alignArenaSizeDynamic builder i64 sizeRef name =
    buildAnd(builder)(buildAdd(builder)(sizeRef)(arenaConst(i64)(7))(name + "_plus_7"))(arenaConst(i64)(-8))(name)

// The `1 << 62` immortal count an arena-resident `Str`/`BigInt` copy carries in its header (the
// same sentinel a string literal's `.rodata` global holds): a retain or release on the value never
// reaches zero, so only the arena reset that owns it ever frees it.
let arenaValueImmortalCount = 1 << 62

let loadWordAt builder i64 i8 ptrType baseRef offsetBytes name =
    buildLoad(builder)(i64)(memOffsetPtr(builder)(i64)(i8)(ptrType)(baseRef)(offsetBytes)(name + "_ptr"))(name)

let storeWordAt builder i64 i8 ptrType baseRef offsetBytes value name =
    name
    |> memOffsetPtr(builder)(i64)(i8)(ptrType)(baseRef)(offsetBytes)
    |> buildStore(builder)(value)

// An arena block of `16 + valueSize` bytes with the immortal RC header written, returned as the
// value address (the word after the header) — stage 0's `EmitArenaValueAllocDynamic`.
let emitArenaValueAllocDynamic context function_ builder i64 i8 ptrType (arena: ArenaRuntime) valueSize name =
    name + "_total"
    |> buildAdd(builder)(valueSize)(arenaConst(i64)(16))
    |> (given (total) -> alignArenaSizeDynamic(builder)(i64)(total)(name + "_aligned"))
    |> (given (aligned) -> emitArenaAllocDynamic(context)(function_)(builder)(i64)(arena)(aligned)(name + "_base"))
    |> (given (base) ->
        Unit
        |> (given (_) ->
            storeWordAt(builder)(i64)(i8)(ptrType)(base)(0)(arenaConst(i64)(arenaValueImmortalCount))(name + "_count"))
        |> (given (_) ->
            storeWordAt(builder)(i64)(i8)(ptrType)(base)(8)(arenaConst(i64)(0))(name + "_size"))
        |> (given (_) ->
            buildAdd(builder)(base)(arenaConst(i64)(16))(name)))

// A `malloc`'d RC cell for `size` payload bytes, as the value word.
let emitRcAllocWord builder i64 i8 mallocFn mallocType size name =
    buildPtrToInt(builder)(emitRcAllocPayloadPtrDynamic(builder)(i64)(i8)(mallocFn)(mallocType)(size)(name))(i64)(name + "_word")

// Copies `len` bytes from address word `srcRef` to `destRef` through `__ashes_move_bytes`, whose
// ascending byte loop stays correct when the ranges overlap with `dest <= src` — the layout an
// arena copy lands in once the cursor has been rewound below its source.
let emitMoveBytes builder i64 ptrType (copyOut: CopyOutRuntime) destRef srcRef len name = buildCall(builder)(copyOut.moveBytesType)(copyOut.moveBytesFn)([buildIntToPtr(builder)(destRef)(ptrType)(name + "_dest"), buildIntToPtr(builder)(srcRef)(ptrType)(name + "_src"), len])(3u32)("")

// `void __ashes_move_bytes(ptr dest, ptr src, i64 len)`: one byte per iteration, ascending, so
// every byte is read before a later write can reach it when `dest <= src`.
let emitMoveBytesBody context builder i64 i8 (copyOut: CopyOutRuntime) =
    match (appendBasicBlock(context)(copyOut.moveBytesFn)("entry"), appendBasicBlock(context)(copyOut.moveBytesFn)("move_head"), appendBasicBlock(context)(copyOut.moveBytesFn)("move_body"), appendBasicBlock(context)(copyOut.moveBytesFn)("move_exit")) with
        | (entryBlock, headBlock, bodyBlock, exitBlock) ->
            match (getParam(copyOut.moveBytesFn)(0u32), getParam(copyOut.moveBytesFn)(1u32), getParam(copyOut.moveBytesFn)(2u32)) with
                | (dest, src, len) ->
                    Unit
                    |> (given (_) -> positionBuilderAtEnd(builder)(entryBlock))
                    |> (given (_) -> buildAlloca(builder)(i64)("move_idx"))
                    |> (given (indexSlot) ->
                        Unit
                        |> (given (_) ->
                            buildStore(builder)(arenaConst(i64)(0))(indexSlot))
                        |> (given (_) -> buildBr(builder)(headBlock))
                        |> (given (_) -> positionBuilderAtEnd(builder)(headBlock))
                        |> (given (_) -> buildLoad(builder)(i64)(indexSlot)("move_i"))
                        |> (given (index) ->
                            Unit
                            |> (given (_) ->
                                buildCondBr(builder)(buildICmp(builder)(intPredicateUlt)(index)(len)("move_more"))(bodyBlock)(exitBlock))
                            |> (given (_) -> positionBuilderAtEnd(builder)(bodyBlock))
                            |> (given (_) ->
                                "move_dest"
                                |> buildGEP(builder)(i8)(dest)([index])(1u32)
                                |> buildStore(builder)(buildLoad(builder)(i8)(buildGEP(builder)(i8)(src)([index])(1u32)("move_src"))("move_byte")))
                            |> (given (_) ->
                                buildStore(builder)(buildAdd(builder)(index)(arenaConst(i64)(1))("move_next"))(indexSlot))
                            |> (given (_) -> buildBr(builder)(headBlock)))
                        |> (given (_) -> positionBuilderAtEnd(builder)(exitBlock))
                        |> (given (_) -> buildRetVoid(builder)))

// Ends the block being written with a branch on a helper's runtime `managed` word: `rcBlock`
// for `1`, `arenaBlock` for `0`.
let emitManagedBranch builder i64 managed rcBlock arenaBlock =
    buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(managed)(arenaConst(i64)(0))("copy_out_managed"))(rcBlock)(arenaBlock)

// One destination path of a copy-out helper: `allocate` the result for `size` bytes, move that
// many bytes from `src` into it, and return it.
let emitCopyOutReturnPath builder i64 ptrType (copyOut: CopyOutRuntime) block src size allocate name =
    Unit
    |> (given (_) -> positionBuilderAtEnd(builder)(block))
    |> (given (_) -> allocate(size))
    |> (given (dest) ->
        Unit
        |> (given (_) -> emitMoveBytes(builder)(i64)(ptrType)(copyOut)(dest)(src)(size)(name))
        |> (given (_) -> buildRet(builder)(dest)))

// `i64 __ashes_copy_out_fixed(i64 src, i64 size, i64 managed)`: `size` bytes of `src` copied to a
// fresh RC cell or arena block; a nil `src` (an empty list tail) passes through as `0` without
// touching memory.
let emitCopyOutFixedBody context builder i64 i8 ptrType (arena: ArenaRuntime) (copyOut: CopyOutRuntime) mallocFn mallocType =
    match (appendBasicBlock(context)(copyOut.copyOutFixedFn)("entry"), appendBasicBlock(context)(copyOut.copyOutFixedFn)("copy_out_nil"), appendBasicBlock(context)(copyOut.copyOutFixedFn)("copy_out_pick"), appendBasicBlock(context)(copyOut.copyOutFixedFn)("copy_out_rc"), appendBasicBlock(context)(copyOut.copyOutFixedFn)("copy_out_arena")) with
        | (entryBlock, nilBlock, pickBlock, rcBlock, arenaBlock) ->
            match (getParam(copyOut.copyOutFixedFn)(0u32), getParam(copyOut.copyOutFixedFn)(1u32), getParam(copyOut.copyOutFixedFn)(2u32)) with
                | (src, size, managed) ->
                    Unit
                    |> (given (_) -> positionBuilderAtEnd(builder)(entryBlock))
                    |> (given (_) ->
                        buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(src)(arenaConst(i64)(0))("copy_out_src_nil"))(nilBlock)(pickBlock))
                    |> (given (_) -> positionBuilderAtEnd(builder)(nilBlock))
                    |> (given (_) ->
                        0
                        |> arenaConst(i64)
                        |> buildRet(builder))
                    |> (given (_) -> positionBuilderAtEnd(builder)(pickBlock))
                    |> (given (_) -> emitManagedBranch(builder)(i64)(managed)(rcBlock)(arenaBlock))
                    |> (given (_) ->
                        emitCopyOutReturnPath(builder)(i64)(ptrType)(copyOut)(rcBlock)(src)(size)(given (bytes) -> emitRcAllocWord(builder)(i64)(i8)(mallocFn)(mallocType)(bytes)("copy_out_rc"))("copy_out_rc"))
                    |> (given (_) ->
                        emitCopyOutReturnPath(builder)(i64)(ptrType)(copyOut)(arenaBlock)(src)(size)(given (bytes) ->
                            emitArenaAllocDynamic(context)(copyOut.copyOutFixedFn)(builder)(i64)(arena)(alignArenaSizeDynamic(builder)(i64)(bytes)("copy_out_arena_size"))("copy_out_arena"))("copy_out_arena"))

// `i64 __ashes_copy_out_string(i64 src, i64 managed)`: a fresh owned `{len, bytes}` value holding
// `src`'s bytes (a view collapses into an owned copy) — a `malloc`'d RC string, or an arena value
// behind the immortal header with the length word written before the bytes move in.
let emitCopyOutStringBody context builder i64 i8 ptrType (arena: ArenaRuntime) (copyOut: CopyOutRuntime) mallocFn mallocType memcpyFn memcpyType =
    match (appendBasicBlock(context)(copyOut.copyOutStringFn)("entry"), appendBasicBlock(context)(copyOut.copyOutStringFn)("copy_out_str_rc"), appendBasicBlock(context)(copyOut.copyOutStringFn)("copy_out_str_arena")) with
        | (entryBlock, rcBlock, arenaBlock) ->
            match (getParam(copyOut.copyOutStringFn)(0u32), getParam(copyOut.copyOutStringFn)(1u32)) with
                | (src, managed) ->
                    Unit
                    |> (given (_) -> positionBuilderAtEnd(builder)(entryBlock))
                    |> (given (_) -> emitStringParts(builder)(i64)(ptrType)(src)("copy_out_str"))
                    |> (given (parts) ->
                        match parts with
                            | (len, bytesAddr) ->
                                Unit
                                |> (given (_) -> emitManagedBranch(builder)(i64)(managed)(rcBlock)(arenaBlock))
                                |> (given (_) -> positionBuilderAtEnd(builder)(rcBlock))
                                |> (given (_) ->
                                    "copy_out_rc_str"
                                    |> emitHeapStringFromBytesAddr(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(bytesAddr)(len)
                                    |> buildRet(builder))
                                |> (given (_) -> positionBuilderAtEnd(builder)(arenaBlock))
                                |> (given (_) ->
                                    emitArenaValueAllocDynamic(context)(copyOut.copyOutStringFn)(builder)(i64)(i8)(ptrType)(arena)(buildAdd(builder)(len)(arenaConst(i64)(8))("copy_out_arena_str_size"))("copy_out_arena_str"))
                                |> (given (dest) ->
                                    Unit
                                    |> (given (_) -> storeWordAt(builder)(i64)(i8)(ptrType)(dest)(0)(len)("copy_out_arena_str_len"))
                                    |> (given (_) ->
                                        emitMoveBytes(builder)(i64)(ptrType)(copyOut)(buildAdd(builder)(dest)(arenaConst(i64)(8))("copy_out_arena_str_bytes"))(bytesAddr)(len)("copy_out_arena_str"))
                                    |> (given (_) -> buildRet(builder)(dest))))

// `i64 __ashes_copy_out_bigint(i64 src, i64 managed)`: a `BigInt` is `{header = neg << 32 |
// limbCount, limbs...}` with no internal pointers, so `8 + 8 * limbCount` bytes move whole.
let emitCopyOutBigIntBody context builder i64 i8 ptrType (arena: ArenaRuntime) (copyOut: CopyOutRuntime) mallocFn mallocType =
    match (appendBasicBlock(context)(copyOut.copyOutBigIntFn)("entry"), appendBasicBlock(context)(copyOut.copyOutBigIntFn)("copy_out_bigint_rc"), appendBasicBlock(context)(copyOut.copyOutBigIntFn)("copy_out_bigint_arena")) with
        | (entryBlock, rcBlock, arenaBlock) ->
            match (getParam(copyOut.copyOutBigIntFn)(0u32), getParam(copyOut.copyOutBigIntFn)(1u32)) with
                | (src, managed) ->
                    Unit
                    |> (given (_) -> positionBuilderAtEnd(builder)(entryBlock))
                    |> (given (_) -> loadWordAt(builder)(i64)(i8)(ptrType)(src)(0)("copy_out_bigint_header"))
                    |> (given (header) ->
                        buildAnd(builder)(header)(arenaConst(i64)(4294967295))("copy_out_bigint_limbs"))
                    |> (given (limbs) ->
                        buildAdd(builder)(buildMul(builder)(limbs)(arenaConst(i64)(8))("copy_out_bigint_limb_bytes"))(arenaConst(i64)(8))("copy_out_bigint_size"))
                    |> (given (size) ->
                        Unit
                        |> (given (_) -> emitManagedBranch(builder)(i64)(managed)(rcBlock)(arenaBlock))
                        |> (given (_) ->
                            emitCopyOutReturnPath(builder)(i64)(ptrType)(copyOut)(rcBlock)(src)(size)(given (bytes) -> emitRcAllocWord(builder)(i64)(i8)(mallocFn)(mallocType)(bytes)("copy_out_rc_bigint"))("copy_out_rc_bigint"))
                        |> (given (_) ->
                            emitCopyOutReturnPath(builder)(i64)(ptrType)(copyOut)(arenaBlock)(src)(size)(given (bytes) -> emitArenaValueAllocDynamic(context)(copyOut.copyOutBigIntFn)(builder)(i64)(i8)(ptrType)(arena)(bytes)("copy_out_arena_bigint"))("copy_out_arena_bigint")))

// `i64 __ashes_copy_out_cell(i64 managed)`: a fresh 16-byte `{head, tail}` cons cell, RC or arena.
let emitCopyOutCellBody context builder i64 i8 (arena: ArenaRuntime) (copyOut: CopyOutRuntime) mallocFn mallocType =
    match (appendBasicBlock(context)(copyOut.copyOutCellFn)("entry"), appendBasicBlock(context)(copyOut.copyOutCellFn)("copy_cell_rc"), appendBasicBlock(context)(copyOut.copyOutCellFn)("copy_cell_arena")) with
        | (entryBlock, rcBlock, arenaBlock) ->
            Unit
            |> (given (_) -> positionBuilderAtEnd(builder)(entryBlock))
            |> (given (_) ->
                emitManagedBranch(builder)(i64)(getParam(copyOut.copyOutCellFn)(0u32))(rcBlock)(arenaBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(rcBlock))
            |> (given (_) ->
                "copy_cell_rc"
                |> emitRcAllocWord(builder)(i64)(i8)(mallocFn)(mallocType)(arenaConst(i64)(16))
                |> buildRet(builder))
            |> (given (_) -> positionBuilderAtEnd(builder)(arenaBlock))
            |> (given (_) ->
                "copy_cell_arena"
                |> emitArenaAllocDynamic(context)(copyOut.copyOutCellFn)(builder)(i64)(arena)(arenaConst(i64)(16))
                |> buildRet(builder))

// The `ListHeadCopyKind` word `__ashes_copy_out_list` and `__ashes_copy_out_list_head` take.
let listHeadKindCode kind =
    match kind with
        | InlineListHead -> 0
        | StringListHead -> 1
        | InnerListHead -> 2

let emitIsStringHeadKind builder i64 kind =
    buildICmp(builder)(intPredicateEq)(kind)(StringListHead
    |> listHeadKindCode
    |> arenaConst(i64))("copy_list_string_kind")

// `i64 __ashes_copy_out_list_head(i64 head, i64 kind, i64 managed)`: an inline head passes
// through, a string head goes through `__ashes_copy_out_string`, and an inner list of inline
// elements through `__ashes_copy_out_list` itself.
let emitCopyOutListHeadBody context builder i64 (copyOut: CopyOutRuntime) =
    match (appendBasicBlock(context)(copyOut.copyOutListHeadFn)("entry"), appendBasicBlock(context)(copyOut.copyOutListHeadFn)("copy_head_not_string"), appendBasicBlock(context)(copyOut.copyOutListHeadFn)("copy_head_inline"), appendBasicBlock(context)(copyOut.copyOutListHeadFn)("copy_head_string"), appendBasicBlock(context)(copyOut.copyOutListHeadFn)("copy_head_inner")) with
        | (entryBlock, notStringBlock, inlineBlock, stringBlock, innerBlock) ->
            match (getParam(copyOut.copyOutListHeadFn)(0u32), getParam(copyOut.copyOutListHeadFn)(1u32), getParam(copyOut.copyOutListHeadFn)(2u32)) with
                | (head, kind, managed) ->
                    Unit
                    |> (given (_) -> positionBuilderAtEnd(builder)(entryBlock))
                    |> (given (_) ->
                        buildCondBr(builder)(emitIsStringHeadKind(builder)(i64)(kind))(stringBlock)(notStringBlock))
                    |> (given (_) -> positionBuilderAtEnd(builder)(notStringBlock))
                    |> (given (_) ->
                        buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(kind)(InnerListHead
                        |> listHeadKindCode
                        |> arenaConst(i64))("copy_head_inner_kind"))(innerBlock)(inlineBlock))
                    |> (given (_) -> positionBuilderAtEnd(builder)(inlineBlock))
                    |> (given (_) -> buildRet(builder)(head))
                    |> (given (_) -> positionBuilderAtEnd(builder)(stringBlock))
                    |> (given (_) ->
                        "copy_head_string_copy"
                        |> buildCall(builder)(copyOut.copyOutStringType)(copyOut.copyOutStringFn)([head, managed])(2u32)
                        |> buildRet(builder))
                    |> (given (_) -> positionBuilderAtEnd(builder)(innerBlock))
                    |> (given (_) ->
                        "copy_head_inner_copy"
                        |> buildCall(builder)(copyOut.copyOutListType)(copyOut.copyOutListFn)([head, InlineListHead
                        |> listHeadKindCode
                        |> arenaConst(i64), managed])(3u32)
                        |> buildRet(builder))

// `__ashes_copy_out_list`'s three parameters and the entry-block slots its walks share.
type CopyOutListState =
    | listSrc: LLVMValueRef
    | listKind: LLVMValueRef
    | listManaged: LLVMValueRef
    | listCountSlot: LLVMValueRef
    | listCurrentSlot: LLVMValueRef
    | listStringBytesSlot: LLVMValueRef
    | listOffsetSlot: LLVMValueRef
    | listIndexSlot: LLVMValueRef
    | listPrevSlot: LLVMValueRef

let emitZeroedSlot builder i64 name =
    name
    |> buildAlloca(builder)(i64)
    |> (given (slot) ->
        Unit
        |> (given (_) ->
            buildStore(builder)(arenaConst(i64)(0))(slot))
        |> (given (_) -> slot))

let createCopyOutListState builder i64 function_ =
    CopyOutListState(
        listSrc = getParam(function_)(0u32),
        listKind = getParam(function_)(1u32),
        listManaged = getParam(function_)(2u32),
        listCountSlot = emitZeroedSlot(builder)(i64)("copy_list_count"),
        listCurrentSlot = buildAlloca(builder)(i64)("copy_list_current"),
        listStringBytesSlot = emitZeroedSlot(builder)(i64)("copy_list_string_bytes"),
        listOffsetSlot = buildAlloca(builder)(i64)("copy_list_offset"),
        listIndexSlot = buildAlloca(builder)(i64)("copy_list_index"),
        listPrevSlot = buildAlloca(builder)(i64)("copy_list_prev")
    )

let emitAddToSlot builder i64 slot amount name =
    buildStore(builder)(buildAdd(builder)(buildLoad(builder)(i64)(slot)(name + "_old"))(amount)(name + "_new"))(slot)

// A cons-chain walk: the head block loads the current cell from `currentSlot` and exits to
// `doneBlock` on nil; otherwise `body(cell)` runs (it may branch internally as long as it leaves
// the builder in an open block), the tail word replaces the current cell, and the walk loops.
// Leaves the builder at the end of `doneBlock`.
let emitListWalk context function_ builder i64 i8 ptrType currentSlot doneBlock prefix body =
    match (appendBasicBlock(context)(function_)(prefix + "_head"), appendBasicBlock(context)(function_)(prefix + "_body")) with
        | (headBlock, bodyBlock) ->
            Unit
            |> (given (_) -> buildBr(builder)(headBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(headBlock))
            |> (given (_) -> buildLoad(builder)(i64)(currentSlot)(prefix + "_cur"))
            |> (given (cell) ->
                Unit
                |> (given (_) ->
                    buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(cell)(arenaConst(i64)(0))(prefix + "_nil"))(doneBlock)(bodyBlock))
                |> (given (_) -> positionBuilderAtEnd(builder)(bodyBlock))
                |> (given (_) -> body(cell))
                |> (given (_) ->
                    buildStore(builder)(loadWordAt(builder)(i64)(i8)(ptrType)(cell)(8)(prefix + "_tail"))(currentSlot))
                |> (given (_) -> buildBr(builder)(headBlock)))
            |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))

// Count phase: walks the source chain, leaving the cell count in `listCountSlot` and returning it.
let emitCopyOutListCount context function_ builder i64 i8 ptrType (state: CopyOutListState) =
    "copy_list_count_done"
    |> appendBasicBlock(context)(function_)
    |> (given (doneBlock) ->
        Unit
        |> (given (_) -> buildStore(builder)(state.listSrc)(state.listCurrentSlot))
        |> (given (_) ->
            emitListWalk(context)(function_)(builder)(i64)(i8)(ptrType)(state.listCurrentSlot)(doneBlock)("copy_list_count")(given (_cell) ->
                emitAddToSlot(builder)(i64)(state.listCountSlot)(arenaConst(i64)(1))("copy_list_count")))
        |> (given (_) -> buildLoad(builder)(i64)(state.listCountSlot)("copy_list_total_cells")))

// Scratch bytes for string heads: for the `StringListHead` kind, sums every head's word-aligned
// `8 + len` into `listStringBytesSlot` (left at `0` for the other kinds). The cache phase copies
// each string completely into that scratch space before any destination allocation could
// overwrite it; caching only the pointers would not survive an earlier destination cell landing
// on a later source string.
let emitCopyOutListStringBytes context function_ builder i64 i8 ptrType (state: CopyOutListState) =
    match (appendBasicBlock(context)(function_)("copy_list_string_sum"), appendBasicBlock(context)(function_)("copy_list_string_sum_done")) with
        | (sumBlock, doneBlock) ->
            Unit
            |> (given (_) ->
                buildCondBr(builder)(emitIsStringHeadKind(builder)(i64)(state.listKind))(sumBlock)(doneBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(sumBlock))
            |> (given (_) -> buildStore(builder)(state.listSrc)(state.listCurrentSlot))
            |> (given (_) ->
                emitListWalk(context)(function_)(builder)(i64)(i8)(ptrType)(state.listCurrentSlot)(doneBlock)("copy_list_string_sum")(given (cell) ->
                    "copy_list_string_head"
                    |> loadWordAt(builder)(i64)(i8)(ptrType)(cell)(0)
                    |> (given (head) -> emitStringLengthValue(builder)(i64)(ptrType)(head)("copy_list_string_len"))
                    |> (given (len) ->
                        alignArenaSizeDynamic(builder)(i64)(buildAdd(builder)(len)(arenaConst(i64)(8))("copy_list_string_size"))("copy_list_string_aligned"))
                    |> (given (size) -> emitAddToSlot(builder)(i64)(state.listStringBytesSlot)(size)("copy_list_string_bytes"))))

// Copies one string head into the scratch tail at `listOffsetSlot` (length word, then bytes) and
// stores that scratch address in the head slot, advancing the offset by the aligned size.
let emitCopyOutListCacheString builder i64 i8 ptrType memcpyFn memcpyType (state: CopyOutListState) headBuf head slotPtr =
    match emitStringParts(builder)(i64)(ptrType)(head)("copy_list_cache_str") with
        | (len, bytesAddr) ->
            "copy_list_cache_offset"
            |> buildLoad(builder)(i64)(state.listOffsetSlot)
            |> (given (offset) ->
                "copy_list_cache_scratch"
                |> buildGEP(builder)(i8)(headBuf)([offset])(1u32)
                |> (given (scratchPtr) ->
                    Unit
                    |> (given (_) -> buildStore(builder)(len)(scratchPtr))
                    |> (given (_) -> buildCall(builder)(memcpyType)(memcpyFn)([gepBytes(builder)(i64)(i8)(scratchPtr)(8)("copy_list_cache_scratch_bytes"), buildIntToPtr(builder)(bytesAddr)(ptrType)("copy_list_cache_src_bytes"), len])(3u32)("copy_list_cache_memcpy"))
                    |> (given (_) ->
                        buildStore(builder)(buildPtrToInt(builder)(scratchPtr)(i64)("copy_list_cache_scratch_addr"))(slotPtr))
                    |> (given (_) ->
                        "copy_list_cache_str_aligned"
                        |> alignArenaSizeDynamic(builder)(i64)(buildAdd(builder)(len)(arenaConst(i64)(8))("copy_list_cache_str_size"))
                        |> buildAdd(builder)(offset)
                        |> (given (build) -> build("copy_list_cache_next_offset"))
                        |> (given (nextOffset) -> buildStore(builder)(nextOffset)(state.listOffsetSlot)))))

// One cache step: `buf[index] = head` for an inline or inner-list head, the scratch copy's address
// for a string head. Leaves the builder in the shared advance block with the index bumped.
let emitCopyOutListCacheCell context function_ builder i64 i8 ptrType memcpyFn memcpyType (state: CopyOutListState) headBuf cell =
    match (appendBasicBlock(context)(function_)("copy_list_cache_string"), appendBasicBlock(context)(function_)("copy_list_cache_inline"), appendBasicBlock(context)(function_)("copy_list_cache_advance")) with
        | (stringBlock, inlineBlock, advanceBlock) ->
            match (loadWordAt(builder)(i64)(i8)(ptrType)(cell)(0)("copy_list_cache_head"), buildLoad(builder)(i64)(state.listIndexSlot)("copy_list_cache_index")) with
                | (head, index) ->
                    "copy_list_cache_slot"
                    |> buildGEP(builder)(i64)(headBuf)([index])(1u32)
                    |> (given (slotPtr) ->
                        Unit
                        |> (given (_) ->
                            buildCondBr(builder)(emitIsStringHeadKind(builder)(i64)(state.listKind))(stringBlock)(inlineBlock))
                        |> (given (_) -> positionBuilderAtEnd(builder)(inlineBlock))
                        |> (given (_) -> buildStore(builder)(head)(slotPtr))
                        |> (given (_) -> buildBr(builder)(advanceBlock))
                        |> (given (_) -> positionBuilderAtEnd(builder)(stringBlock))
                        |> (given (_) -> emitCopyOutListCacheString(builder)(i64)(i8)(ptrType)(memcpyFn)(memcpyType)(state)(headBuf)(head)(slotPtr))
                        |> (given (_) -> buildBr(builder)(advanceBlock))
                        |> (given (_) -> positionBuilderAtEnd(builder)(advanceBlock))
                        |> (given (_) ->
                            buildStore(builder)(buildAdd(builder)(index)(arenaConst(i64)(1))("copy_list_cache_next"))(state.listIndexSlot)))

// Cache phase: `malloc`s `8 * cells + stringBytes` scratch bytes and walks the source chain once
// more, caching every head (see `emitCopyOutListCacheCell`). Returns the scratch buffer; nothing
// reads a source cell after this phase.
let emitCopyOutListCache context function_ builder i64 i8 ptrType mallocFn mallocType memcpyFn memcpyType (state: CopyOutListState) totalCells =
    "copy_list_head_words"
    |> buildMul(builder)(totalCells)(arenaConst(i64)(8))
    |> (given (headWords) ->
        "copy_list_cache_bytes"
        |> buildAdd(builder)(headWords)(buildLoad(builder)(i64)(state.listStringBytesSlot)("copy_list_string_bytes_total"))
        |> (given (totalBytes) -> buildCall(builder)(mallocType)(mallocFn)([totalBytes])(1u32)("copy_list_head_buf"))
        |> (given (headBuf) ->
            "copy_list_cache_done"
            |> appendBasicBlock(context)(function_)
            |> (given (doneBlock) ->
                Unit
                |> (given (_) -> buildStore(builder)(state.listSrc)(state.listCurrentSlot))
                |> (given (_) ->
                    buildStore(builder)(arenaConst(i64)(0))(state.listIndexSlot))
                |> (given (_) -> buildStore(builder)(headWords)(state.listOffsetSlot))
                |> (given (_) ->
                    emitListWalk(context)(function_)(builder)(i64)(i8)(ptrType)(state.listCurrentSlot)(doneBlock)("copy_list_cache")(given (cell) -> emitCopyOutListCacheCell(context)(function_)(builder)(i64)(i8)(ptrType)(memcpyFn)(memcpyType)(state)(headBuf)(cell)))
                |> (given (_) -> headBuf))))

// A destination cell for cached head `index`: the head copied per the head kind first, then a
// fresh cell (`__ashes_copy_out_cell`) holding it with a nil tail.
let emitCopyOutListNewCell builder i64 i8 ptrType (copyOut: CopyOutRuntime) (state: CopyOutListState) headBuf index =
    "copy_list_build_head"
    |> buildLoad(builder)(i64)(buildGEP(builder)(i64)(headBuf)([index])(1u32)("copy_list_build_slot"))
    |> (given (head) -> buildCall(builder)(copyOut.copyOutListHeadType)(copyOut.copyOutListHeadFn)([head, state.listKind, state.listManaged])(3u32)("copy_list_build_head_copy"))
    |> (given (headCopy) ->
        "copy_list_build_cell"
        |> buildCall(builder)(copyOut.copyOutCellType)(copyOut.copyOutCellFn)([state.listManaged])(1u32)
        |> (given (cell) ->
            Unit
            |> (given (_) -> storeWordAt(builder)(i64)(i8)(ptrType)(cell)(0)(headCopy)("copy_list_build_store_head"))
            |> (given (_) ->
                storeWordAt(builder)(i64)(i8)(ptrType)(cell)(8)(arenaConst(i64)(0))("copy_list_build_store_tail"))
            |> (given (_) -> cell)))

// Build phase: the first cell before the loop (so it can be returned), then one cell per remaining
// cached head with `listIndexSlot` running from `1` to the count, each linked through the previous
// cell's tail word. Returns the first cell with the builder at the end of the done block.
let emitCopyOutListBuild context function_ builder i64 i8 ptrType (copyOut: CopyOutRuntime) (state: CopyOutListState) headBuf totalCells =
    match (appendBasicBlock(context)(function_)("copy_list_build_head"), appendBasicBlock(context)(function_)("copy_list_build_body"), appendBasicBlock(context)(function_)("copy_list_build_done")) with
        | (headBlock, bodyBlock, doneBlock) ->
            0
            |> arenaConst(i64)
            |> emitCopyOutListNewCell(builder)(i64)(i8)(ptrType)(copyOut)(state)(headBuf)
            |> (given (firstCell) ->
                Unit
                |> (given (_) -> buildStore(builder)(firstCell)(state.listPrevSlot))
                |> (given (_) ->
                    buildStore(builder)(arenaConst(i64)(1))(state.listIndexSlot))
                |> (given (_) -> buildBr(builder)(headBlock))
                |> (given (_) -> positionBuilderAtEnd(builder)(headBlock))
                |> (given (_) -> buildLoad(builder)(i64)(state.listIndexSlot)("copy_list_build_index"))
                |> (given (index) ->
                    Unit
                    |> (given (_) ->
                        buildCondBr(builder)(buildICmp(builder)(intPredicateUge)(index)(totalCells)("copy_list_build_finished"))(doneBlock)(bodyBlock))
                    |> (given (_) -> positionBuilderAtEnd(builder)(bodyBlock))
                    |> (given (_) -> emitCopyOutListNewCell(builder)(i64)(i8)(ptrType)(copyOut)(state)(headBuf)(index))
                    |> (given (cell) ->
                        Unit
                        |> (given (_) ->
                            storeWordAt(builder)(i64)(i8)(ptrType)(buildLoad(builder)(i64)(state.listPrevSlot)("copy_list_build_prev"))(8)(cell)("copy_list_build_link"))
                        |> (given (_) -> buildStore(builder)(cell)(state.listPrevSlot))
                        |> (given (_) ->
                            buildStore(builder)(buildAdd(builder)(index)(arenaConst(i64)(1))("copy_list_build_next"))(state.listIndexSlot))
                        |> (given (_) -> buildBr(builder)(headBlock))))
                |> (given (_) -> positionBuilderAtEnd(builder)(doneBlock))
                |> (given (_) -> firstCell))

// `i64 __ashes_copy_out_list(i64 src, i64 kind, i64 managed)`: a nil list returns nil; otherwise
// the count, cache, and build phases above run in order and the scratch buffer is freed.
let emitCopyOutListBody context builder i64 i8 ptrType (copyOut: CopyOutRuntime) mallocFn mallocType freeFn freeType memcpyFn memcpyType =
    ((given (function_) ->
        Unit
        |> (given (_) ->
            "entry"
            |> appendBasicBlock(context)(function_)
            |> positionBuilderAtEnd(builder))
        |> (given (_) -> createCopyOutListState(builder)(i64)(function_))
        |> (given (state) ->
            match (appendBasicBlock(context)(function_)("copy_list_nil"), appendBasicBlock(context)(function_)("copy_list_count")) with
                | (nilBlock, countBlock) ->
                    Unit
                    |> (given (_) ->
                        buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(state.listSrc)(arenaConst(i64)(0))("copy_list_src_nil"))(nilBlock)(countBlock))
                    |> (given (_) -> positionBuilderAtEnd(builder)(nilBlock))
                    |> (given (_) ->
                        0
                        |> arenaConst(i64)
                        |> buildRet(builder))
                    |> (given (_) -> positionBuilderAtEnd(builder)(countBlock))
                    |> (given (_) -> emitCopyOutListCount(context)(function_)(builder)(i64)(i8)(ptrType)(state))
                    |> (given (totalCells) ->
                        Unit
                        |> (given (_) -> emitCopyOutListStringBytes(context)(function_)(builder)(i64)(i8)(ptrType)(state))
                        |> (given (_) -> emitCopyOutListCache(context)(function_)(builder)(i64)(i8)(ptrType)(mallocFn)(mallocType)(memcpyFn)(memcpyType)(state)(totalCells))
                        |> (given (headBuf) ->
                            totalCells
                            |> emitCopyOutListBuild(context)(function_)(builder)(i64)(i8)(ptrType)(copyOut)(state)(headBuf)
                            |> (given (firstCell) ->
                                Unit
                                |> (given (_) -> buildCall(builder)(freeType)(freeFn)([headBuf])(1u32)(""))
                                |> (given (_) -> buildRet(builder)(firstCell))))))))(copyOut.copyOutListFn)

// Declares the copy-out helper functions in `module_` and emits their bodies against `arena`;
// they reach libc's `malloc`/`free`/`memcpy` through the handles passed in.
let defineCopyOutRuntime module_ context builder i64 i8 ptrType (arena: ArenaRuntime) mallocFn mallocType freeFn freeType memcpyFn memcpyType =
    match (functionType(voidType(context))([ptrType, ptrType, i64])(3u32)(false), functionType(i64)([i64])(1u32)(false), functionType(i64)([i64, i64])(2u32)(false), functionType(i64)([i64, i64, i64])(3u32)(false)) with
        | (moveType, oneWordType, twoWordType, threeWordType) ->
            ((given (copyOut) ->
                Unit
                |> (given (_) -> emitMoveBytesBody(context)(builder)(i64)(i8)(copyOut))
                |> (given (_) -> emitCopyOutFixedBody(context)(builder)(i64)(i8)(ptrType)(arena)(copyOut)(mallocFn)(mallocType))
                |> (given (_) -> emitCopyOutStringBody(context)(builder)(i64)(i8)(ptrType)(arena)(copyOut)(mallocFn)(mallocType)(memcpyFn)(memcpyType))
                |> (given (_) -> emitCopyOutBigIntBody(context)(builder)(i64)(i8)(ptrType)(arena)(copyOut)(mallocFn)(mallocType))
                |> (given (_) -> emitCopyOutCellBody(context)(builder)(i64)(i8)(arena)(copyOut)(mallocFn)(mallocType))
                |> (given (_) -> emitCopyOutListHeadBody(context)(builder)(i64)(copyOut))
                |> (given (_) -> emitCopyOutListBody(context)(builder)(i64)(i8)(ptrType)(copyOut)(mallocFn)(mallocType)(freeFn)(freeType)(memcpyFn)(memcpyType))
                |> (given (_) -> copyOut)))(CopyOutRuntime(
                moveBytesFn = addInternalFunction(module_)("__ashes_move_bytes")(moveType),
                moveBytesType = moveType,
                copyOutFixedFn = addInternalFunction(module_)("__ashes_copy_out_fixed")(threeWordType),
                copyOutFixedType = threeWordType,
                copyOutStringFn = addInternalFunction(module_)("__ashes_copy_out_string")(twoWordType),
                copyOutStringType = twoWordType,
                copyOutBigIntFn = addInternalFunction(module_)("__ashes_copy_out_bigint")(twoWordType),
                copyOutBigIntType = twoWordType,
                copyOutCellFn = addInternalFunction(module_)("__ashes_copy_out_cell")(oneWordType),
                copyOutCellType = oneWordType,
                copyOutListHeadFn = addInternalFunction(module_)("__ashes_copy_out_list_head")(threeWordType),
                copyOutListHeadType = threeWordType,
                copyOutListFn = addInternalFunction(module_)("__ashes_copy_out_list")(threeWordType),
                copyOutListType = threeWordType
            ))

let withCopyOutRuntime (arena: ArenaRuntime) copyOut = arena with arenaCopyOut = Some(copyOut)

// Creates the two arena globals, the failure message, and the grow/reclaim helpers in `module_`,
// emitting the helper bodies with `builder` (repositioned by the caller afterwards); the copy-out
// helpers join them only when `usesCopyOut` says the program's IR needs them.
let defineArenaRuntime module_ context builder i64 i8 ptrType usesCopyOut mallocFn mallocType freeFn freeType memcpyFn memcpyType =
    match (functionType(voidType(context))([i64])(1u32)(false), functionType(voidType(context))([i64, i64])(2u32)(false)) with
        | (growType, reclaimType) ->
            ((given (arena) ->
                Unit
                |> (given (_) -> emitArenaGrowBody(context)(builder)(i64)(i8)(ptrType)(arena))
                |> (given (_) -> emitArenaReclaimBody(context)(builder)(i64)(i8)(ptrType)(arena))
                |> (given (_) ->
                    if usesCopyOut
                    then
                        memcpyType
                        |> defineCopyOutRuntime(module_)(context)(builder)(i64)(i8)(ptrType)(arena)(mallocFn)(mallocType)(freeFn)(freeType)(memcpyFn)
                        |> withCopyOutRuntime(arena)
                    else arena)))(ArenaRuntime(
                arenaCursorGlobal = addZeroWordGlobal(module_)(i64)("__ashes_heap_cursor"),
                arenaEndGlobal = addZeroWordGlobal(module_)(i64)("__ashes_heap_end"),
                arenaGrowFn = addInternalFunction(module_)("__ashes_heap_grow")(growType),
                arenaGrowType = growType,
                arenaReclaimFn = addInternalFunction(module_)("__ashes_reclaim_arena_chunks")(reclaimType),
                arenaReclaimType = reclaimType,
                arenaFailureMessage = addFailureMessageGlobal(module_)(i8),
                arenaFailureMessageLength = Ashes.Collection.List.length(arenaFailureMessageCodes),
                arenaCopyOut = None
            ))

let managedCode runtimeManaged =
    if runtimeManaged
    then 1
    else 0

// The helpers a copy-out instruction calls; `codegenFunctions` defines them for every module whose
// IR carries one, so a missing set is a codegen bug rather than a program property.
let copyOutRuntimeOf (arena: ArenaRuntime) =
    match arena.arenaCopyOut with
        | Some(copyOut) -> copyOut
        | None -> Ashes.IO.panic("codegen: copy-out helpers were not defined for this module")

// `CopyOutArena`: `StaticSizeBytes > 0` copies that many bytes (`__ashes_copy_out_fixed`), `-1` a
// `Str`/`Bytes` value by its header length (`__ashes_copy_out_string`), `copyOutBigIntSize` a
// `BigInt` by its limb count (`__ashes_copy_out_bigint`). Any other size is not a kind stage 0
// has, and panics rather than copying a guessed byte count.
let emitCopyOutArena builder i64 (arena: ArenaRuntime) srcRef staticSizeBytes runtimeManaged name =
    match (copyOutRuntimeOf(arena), runtimeManaged
    |> managedCode
    |> arenaConst(i64)) with
        | (copyOut, managed) ->
            if staticSizeBytes > 0
            then buildCall(builder)(copyOut.copyOutFixedType)(copyOut.copyOutFixedFn)([srcRef, arenaConst(i64)(staticSizeBytes), managed])(3u32)(name)
            else
                if staticSizeBytes == -1
                then buildCall(builder)(copyOut.copyOutStringType)(copyOut.copyOutStringFn)([srcRef, managed])(2u32)(name)
                else
                    if staticSizeBytes == copyOutBigIntSize
                    then buildCall(builder)(copyOut.copyOutBigIntType)(copyOut.copyOutBigIntFn)([srcRef, managed])(2u32)(name)
                    else Ashes.IO.panic("codegen: CopyOutArena with StaticSizeBytes " + Ashes.Text.fromInt(staticSizeBytes) + " not supported")

// `CopyOutList`: the whole cons chain through `__ashes_copy_out_list` with the head kind and
// managed flag as words.
let emitCopyOutList builder i64 (arena: ArenaRuntime) srcRef headCopy runtimeManaged name =
    match copyOutRuntimeOf(arena) with
        | copyOut ->
            buildCall(builder)(copyOut.copyOutListType)(copyOut.copyOutListFn)([srcRef, headCopy
            |> listHeadKindCode
            |> arenaConst(i64), runtimeManaged
            |> managedCode
            |> arenaConst(i64)])(3u32)(name)

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
