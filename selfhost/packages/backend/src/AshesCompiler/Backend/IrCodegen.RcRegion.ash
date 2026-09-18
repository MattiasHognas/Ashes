// The reference-counted region of a program this backend compiles, stage 0's
// `LlvmCodegenMemory.ReferenceCountedRegion.cs`: the heap every reference-counted cell comes from
// is a range of addresses the program reserves for it, so `IsReferenceCounted` is a bounds test on
// a value's address. The first allocation reserves the range without committing memory. Blocks
// are bumped out of it behind a 16-byte prefix holding their size: a small released block goes
// onto the free list of its size, which the next allocation of that size takes first; a large one
// starts on a page and spans whole pages, which its release hands back to the kernel. Every block
// of a program whose reservation the kernel refused comes from libc instead and tests as not
// reference-counted, which only costs a copy where a reference would do.
//
// Every allocation of this backend goes through the two functions, a C string handed to a system
// call as much as a reference-counted cell: the region answers only for the cells, since a system
// call's scratch buffer is never a value `IsReferenceCounted` is asked about.
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Syscalls.LinuxX64
import Ashes.Number.UInt
export (
    type RcRegionRuntime,
    value defineRcRegionRuntime,
    value emitIsReferenceCountedIn,
)

type RcRegionRuntime =
    | regionBaseGlobal: LLVMValueRef
    | regionEndGlobal: LLVMValueRef
    | regionCursorGlobal: LLVMValueRef
    | regionInitializedGlobal: LLVMValueRef
    | freeLists: LLVMValueRef
    | allocFn: LLVMValueRef
    | freeFn: LLVMValueRef

let regionConst i64 value =
    constInt(i64)(Ashes.Number.UInt.fromInt64(value))(false)

// The same range stage 0's runtime reserves: 4 TB from 0x100000000000.
let regionBaseAddress = 17592186044416

let regionSizeBytes = 4398046511104

let blockPrefixBytes = 16

let pageBytes = 4096

// Blocks of up to this many bytes, prefix included, are kept on free lists: one list per
// multiple of 16.
let largestListedBlockBytes = 1040

let freeListCount = 66

// `mmap` protection read|write, and `MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE |
// MAP_FIXED_NOREPLACE`: a reservation that commits nothing and never replaces another mapping.
let mmapReadWrite = 3

let mmapReserveFlags = 1064994

// `madvise`'s `MADV_DONTNEED`: the pages go back to the kernel and read as zero if touched again.
let madviseDontNeed = 4

let zeroWordGlobal module_ i64 name =
    name
    |> addGlobal(module_)(i64)
    |> (given (global) ->
        Unit
        |> (given (_) ->
            0
            |> regionConst(i64)
            |> setInitializer(global))
        |> (given (_) -> setLinkage(global)(linkageInternal))
        |> (given (_) -> global))

let freeListsGlobal module_ i64 =
    (let listsType =
        freeListCount
        |> Ashes.Number.UInt.fromInt64
        |> arrayType(i64)
    in
        "__ashes_rc_free_lists"
        |> addGlobal(module_)(listsType)
        |> (given (global) ->
            Unit
            |> (given (_) ->
                listsType
                |> constNull
                |> setInitializer(global))
            |> (given (_) -> setLinkage(global)(linkageInternal))
            |> (given (_) -> global)))

let internalFunction module_ name type_ =
    type_
    |> addFunction(module_)(name)
    |> (given (function_) ->
        Unit
        |> (given (_) -> setLinkage(function_)(linkageInternal))
        |> (given (_) -> function_))

let wordPointer builder ptrType i64 address offset name =
    buildIntToPtr(builder)(buildAdd(builder)(address)(regionConst(i64)(offset))(name + "_address"))(ptrType)(name)

let roundUp builder i64 value multiple name =
    buildAnd(builder)(buildAdd(builder)(value)(regionConst(i64)(multiple - 1))(name + "_up"))(regionConst(i64)(-multiple))(name)

let freeListSlot builder i64 (runtime: RcRegionRuntime) blockBytes name =
    buildGEP(builder)(i64)(runtime.freeLists)([buildLShr(builder)(blockBytes)(regionConst(i64)(4))(name + "_index")])(1u32)(name)

// Whether `address` lies inside the reserved range, as an `i1`. The bounds are zero until the
// reservation succeeds, so nothing is inside before then.
let emitInRegion builder i64 (runtime: RcRegionRuntime) address name =
    buildAnd(builder)(buildICmp(builder)(intPredicateUge)(address)(buildLoad(builder)(i64)(runtime.regionBaseGlobal)(name + "_base"))(name + "_above"))(buildICmp(builder)(intPredicateUlt)(address)(buildLoad(builder)(i64)(runtime.regionEndGlobal)(name + "_end"))(name + "_below"))(name + "_inside")

// The first allocation's reservation: the range is recorded only when the kernel placed it at the
// requested address, so a refused one leaves the bounds at zero and every allocation on libc.
let emitReservation context builder i64 (runtime: RcRegionRuntime) readyBlock =
    (let reserveBlock = appendBasicBlock(context)(runtime.allocFn)("rc_region_reserve")
    in
        let recordBlock = appendBasicBlock(context)(runtime.allocFn)("rc_region_record")
        in
            reserveBlock
            |> positionBuilderAtEnd(builder)
            |> (given (_) ->
                buildStore(builder)(regionConst(i64)(1))(runtime.regionInitializedGlobal))
            |> (given (_) ->
                emitLinuxSyscallCall6(builder)(i64)(regionConst(i64)(9))(regionConst(i64)(regionBaseAddress))(regionConst(i64)(regionSizeBytes))(regionConst(i64)(mmapReadWrite))(regionConst(i64)(mmapReserveFlags))(regionConst(i64)(-1))(regionConst(i64)(0))("rc_region_mmap"))
            |> (given (mapped) ->
                buildICmp(builder)(intPredicateEq)(mapped)(regionConst(i64)(regionBaseAddress))("rc_region_placed"))
            |> (given (placed) -> buildCondBr(builder)(placed)(recordBlock)(readyBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(recordBlock))
            |> (given (_) ->
                buildStore(builder)(regionConst(i64)(regionBaseAddress))(runtime.regionBaseGlobal))
            |> (given (_) ->
                buildStore(builder)(regionConst(i64)(regionBaseAddress))(runtime.regionCursorGlobal))
            |> (given (_) ->
                buildStore(builder)(regionConst(i64)(regionBaseAddress + regionSizeBytes))(runtime.regionEndGlobal))
            |> (given (_) -> buildBr(builder)(readyBlock))
            |> (given (_) -> reserveBlock))

// Bumps a block of `blockBytes` off the range at `start` (the cursor, or the cursor rounded up to
// a page): its size goes into the prefix and the payload after it is returned. A block the range
// has no room left for comes from libc instead.
let emitBump context builder i64 ptrType (runtime: RcRegionRuntime) libcBlock start blockBytes name =
    (let takeBlock = appendBasicBlock(context)(runtime.allocFn)(name + "_take")
    in
        let advanced = buildAdd(builder)(start)(blockBytes)(name + "_advanced")
        in
            name + "_fits"
            |> buildICmp(builder)(intPredicateUle)(advanced)(buildLoad(builder)(i64)(runtime.regionEndGlobal)(name + "_end"))
            |> (given (fits) -> buildCondBr(builder)(fits)(takeBlock)(libcBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(takeBlock))
            |> (given (_) -> buildStore(builder)(advanced)(runtime.regionCursorGlobal))
            |> (given (_) ->
                name + "_prefix"
                |> buildIntToPtr(builder)(start)(ptrType)
                |> buildStore(builder)(blockBytes))
            |> (given (_) ->
                name + "_payload"
                |> wordPointer(builder)(ptrType)(i64)(start)(blockPrefixBytes)
                |> buildRet(builder)))

// A small block: the head of its free list when there is one, else the next block of the range.
let emitListedAllocation context builder i64 ptrType (runtime: RcRegionRuntime) libcBlock blockBytes =
    (let popBlock = appendBasicBlock(context)(runtime.allocFn)("rc_alloc_pop")
    in
        let bumpBlock = appendBasicBlock(context)(runtime.allocFn)("rc_alloc_bump")
        in
            let listSlot = freeListSlot(builder)(i64)(runtime)(blockBytes)("rc_alloc_list_slot")
            in
                let head = buildLoad(builder)(i64)(listSlot)("rc_alloc_list_head")
                in
                    "rc_alloc_has_free"
                    |> buildICmp(builder)(intPredicateNe)(head)(regionConst(i64)(0))
                    |> (given (hasFree) -> buildCondBr(builder)(hasFree)(popBlock)(bumpBlock))
                    |> (given (_) -> positionBuilderAtEnd(builder)(popBlock))
                    |> (given (_) ->
                        "rc_alloc_next"
                        |> buildLoad(builder)(i64)(wordPointer(builder)(ptrType)(i64)(head)(8)("rc_alloc_next_slot"))
                        |> (given (next) -> buildStore(builder)(next)(listSlot)))
                    |> (given (_) ->
                        "rc_alloc_reused"
                        |> wordPointer(builder)(ptrType)(i64)(head)(blockPrefixBytes)
                        |> buildRet(builder))
                    |> (given (_) -> positionBuilderAtEnd(builder)(bumpBlock))
                    |> (given (_) ->
                        "rc_alloc_cursor"
                        |> buildLoad(builder)(i64)(runtime.regionCursorGlobal)
                        |> (given (cursor) -> emitBump(context)(builder)(i64)(ptrType)(runtime)(libcBlock)(cursor)(blockBytes)("rc_alloc_small"))))

// A large block starts on a page and spans whole pages, so its release can return them.
let emitPagedAllocation context builder i64 ptrType (runtime: RcRegionRuntime) libcBlock blockBytes =
    "rc_alloc_cursor"
    |> buildLoad(builder)(i64)(runtime.regionCursorGlobal)
    |> (given (cursor) ->
        emitBump(context)(builder)(i64)(ptrType)(runtime)(libcBlock)(roundUp(builder)(i64)(cursor)(pageBytes)("rc_alloc_page_start"))(roundUp(builder)(i64)(blockBytes)(pageBytes)("rc_alloc_pages"))("rc_alloc_large"))

// `ptr __ashes_rc_alloc(i64 size)`: the range once reserved, libc otherwise.
let emitAllocBody context builder i64 ptrType (runtime: RcRegionRuntime) libcMalloc mallocType =
    (let function_ = runtime.allocFn
    in
        let entryBlock = appendBasicBlock(context)(function_)("entry")
        in
            let readyBlock = appendBasicBlock(context)(function_)("rc_alloc_ready")
            in
                let sizedBlock = appendBasicBlock(context)(function_)("rc_alloc_sized")
                in
                    let listedBlock = appendBasicBlock(context)(function_)("rc_alloc_listed")
                    in
                        let pagedBlock = appendBasicBlock(context)(function_)("rc_alloc_paged")
                        in
                            let libcBlock = appendBasicBlock(context)(function_)("rc_alloc_libc")
                            in
                                let reserveBlock = emitReservation(context)(builder)(i64)(runtime)(readyBlock)
                                in
                                    let _ = positionBuilderAtEnd(builder)(entryBlock)
                                    in
                                        let size = getParam(function_)(0u32)
                                        in
                                            let blockBytes =
                                                buildAdd(builder)(roundUp(builder)(i64)(size)(16)("rc_alloc_rounded"))(regionConst(i64)(blockPrefixBytes))("rc_alloc_block")
                                            in
                                                "rc_alloc_is_initialized"
                                                |> buildICmp(builder)(intPredicateNe)(buildLoad(builder)(i64)(runtime.regionInitializedGlobal)("rc_alloc_initialized"))(regionConst(i64)(0))
                                                |> (given (initialized) -> buildCondBr(builder)(initialized)(readyBlock)(reserveBlock))
                                                |> (given (_) -> positionBuilderAtEnd(builder)(readyBlock))
                                                |> (given (_) ->
                                                    "rc_alloc_has_region" |> buildICmp(builder)(intPredicateNe)(buildLoad(builder)(i64)(runtime.regionBaseGlobal)("rc_alloc_base"))(regionConst(i64)(0)))
                                                |> (given (hasRegion) -> buildCondBr(builder)(hasRegion)(sizedBlock)(libcBlock))
                                                |> (given (_) -> positionBuilderAtEnd(builder)(sizedBlock))
                                                |> (given (_) ->
                                                    buildICmp(builder)(intPredicateUle)(blockBytes)(regionConst(i64)(largestListedBlockBytes))("rc_alloc_is_listed"))
                                                |> (given (listed) -> buildCondBr(builder)(listed)(listedBlock)(pagedBlock))
                                                |> (given (_) -> positionBuilderAtEnd(builder)(listedBlock))
                                                |> (given (_) -> emitListedAllocation(context)(builder)(i64)(ptrType)(runtime)(libcBlock)(blockBytes))
                                                |> (given (_) -> positionBuilderAtEnd(builder)(pagedBlock))
                                                |> (given (_) -> emitPagedAllocation(context)(builder)(i64)(ptrType)(runtime)(libcBlock)(blockBytes))
                                                |> (given (_) -> positionBuilderAtEnd(builder)(libcBlock))
                                                |> (given (_) ->
                                                    "rc_alloc_libc_block"
                                                    |> buildCall(builder)(mallocType)(libcMalloc)([size])(1u32)
                                                    |> buildRet(builder)))

// `void __ashes_rc_free(ptr block)`: a small block of the range goes onto the free list of its
// size, a large one's pages back to the kernel, and any other block back to libc.
let emitFreeBody context builder i64 ptrType (runtime: RcRegionRuntime) libcFree freeType =
    (let function_ = runtime.freeFn
    in
        let entryBlock = appendBasicBlock(context)(function_)("entry")
        in
            let regionBlock = appendBasicBlock(context)(function_)("rc_free_region")
            in
                let listedBlock = appendBasicBlock(context)(function_)("rc_free_listed")
                in
                    let pagedBlock = appendBasicBlock(context)(function_)("rc_free_paged")
                    in
                        let libcBlock = appendBasicBlock(context)(function_)("rc_free_libc")
                        in
                            let _ = positionBuilderAtEnd(builder)(entryBlock)
                            in
                                let block = getParam(function_)(0u32)
                                in
                                    let address = buildPtrToInt(builder)(block)(i64)("rc_free_address")
                                    in
                                        let _ =
                                            buildCondBr(builder)(emitInRegion(builder)(i64)(runtime)(address)("rc_free"))(regionBlock)(libcBlock)
                                        in
                                            let _ = positionBuilderAtEnd(builder)(regionBlock)
                                            in
                                                let prefix =
                                                    buildSub(builder)(address)(regionConst(i64)(blockPrefixBytes))("rc_free_prefix")
                                                in
                                                    let blockBytes =
                                                        buildLoad(builder)(i64)(buildIntToPtr(builder)(prefix)(ptrType)("rc_free_prefix_ptr"))("rc_free_block")
                                                    in
                                                        "rc_free_is_listed"
                                                        |> buildICmp(builder)(intPredicateUle)(blockBytes)(regionConst(i64)(largestListedBlockBytes))
                                                        |> (given (listed) -> buildCondBr(builder)(listed)(listedBlock)(pagedBlock))
                                                        |> (given (_) -> positionBuilderAtEnd(builder)(listedBlock))
                                                        |> (given (_) -> freeListSlot(builder)(i64)(runtime)(blockBytes)("rc_free_list_slot"))
                                                        |> (given (listSlot) ->
                                                            Unit
                                                            |> (given (_) ->
                                                                "rc_free_next_slot"
                                                                |> wordPointer(builder)(ptrType)(i64)(prefix)(8)
                                                                |> buildStore(builder)(buildLoad(builder)(i64)(listSlot)("rc_free_head")))
                                                            |> (given (_) -> buildStore(builder)(prefix)(listSlot)))
                                                        |> (given (_) -> buildRetVoid(builder))
                                                        |> (given (_) -> positionBuilderAtEnd(builder)(pagedBlock))
                                                        |> (given (_) ->
                                                            emitLinuxSyscallCall(builder)(i64)(regionConst(i64)(28))(prefix)(blockBytes)(regionConst(i64)(madviseDontNeed))("rc_free_madvise"))
                                                        |> (given (_) -> buildRetVoid(builder))
                                                        |> (given (_) -> positionBuilderAtEnd(builder)(libcBlock))
                                                        |> (given (_) -> buildCall(builder)(freeType)(libcFree)([block])(1u32)(""))
                                                        |> (given (_) -> buildRetVoid(builder)))

// `IsReferenceCounted`: whether a value's address lies inside the reserved range, as this
// codegen's canonical 0/1 `i64` Bool.
let emitIsReferenceCountedIn builder i64 (runtime: RcRegionRuntime) name valueRef =
    buildZExt(builder)(emitInRegion(builder)(i64)(runtime)(valueRef)(name))(i64)(name)

let defineRcRegionRuntime module_ context builder i64 ptrType libcMalloc mallocType libcFree freeType =
    (let runtime =
        RcRegionRuntime(
            regionBaseGlobal = zeroWordGlobal(module_)(i64)("__ashes_rc_region_base"),
            regionEndGlobal = zeroWordGlobal(module_)(i64)("__ashes_rc_region_end"),
            regionCursorGlobal = zeroWordGlobal(module_)(i64)("__ashes_rc_region_cursor"),
            regionInitializedGlobal = zeroWordGlobal(module_)(i64)("__ashes_rc_region_initialized"),
            freeLists = freeListsGlobal(module_)(i64),
            allocFn = internalFunction(module_)("__ashes_rc_alloc")(mallocType),
            freeFn = internalFunction(module_)("__ashes_rc_free")(freeType)
        )
    in
        mallocType
        |> emitAllocBody(context)(builder)(i64)(ptrType)(runtime)(libcMalloc)
        |> (given (_) -> emitFreeBody(context)(builder)(i64)(ptrType)(runtime)(libcFree)(freeType))
        |> (given (_) -> runtime))
