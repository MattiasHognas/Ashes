// The copy and normalization instructions past `IrCodegen.Arena`'s `CopyOutArena`/`CopyOutList`:
// the persistent-region allocations of the in-place reuse specializations (`AllocAdtToSpace`,
// `CopyOutArenaToSpace`, `CopyFixedInto`, `CopyStringIntoOrFresh`, `CopyFixedIntoOrFresh`), the
// closure copy-out with its environment normalizer dispatch (`CopyOutClosure`), and the TCO
// accumulator's single-cell copy (`CopyOutTcoListCell`) — `LlvmCodegenMemory.cs`'s
// `EmitAllocAdtToSpace`, `EmitCopyOutArenaToSpace`, `EmitCopyFixedInto`,
// `EmitCopyStringIntoOrFresh`, `EmitCopyFixedIntoOrFresh`, `EmitCopyOutClosure`,
// `EmitNormalizeOrCopyClosureEnvironment`, and `EmitCopyOutTcoListCell`, emitter for emitter.
//
// Stage 0 keeps two bump regions beside the scoped arena, both with the arena's chunk format and
// grow rule but never reset or reclaimed: the to-space (`__ashes_tospace_cursor`/`_end`), which
// holds the genuinely new nodes an in-place reuse specialization creates, and the blob region
// (`__ashes_blob_cursor`/`_end`), which holds the variable-size leaf values (map keys and values)
// those nodes point at — kept apart so blobs never interleave with fixed-size nodes. Both start
// at `0`/`0` and are grown on their first allocation by `__ashes_region_grow`, the arena grow
// body over caller-supplied cursor/end slots. The in-place-or-fresh forms overwrite an old blob
// only when it lies in the blob region's current chunk (`[footer base, cursor)`), since a blob in
// the reclaimable arena would dangle after the next reset. `CopyRuntime` is defined only for a
// module whose IR carries a copy-family instruction, alongside the arena's `CopyOutRuntime`, whose
// `__ashes_move_bytes`, `__ashes_copy_out_string`, and `__ashes_copy_out_list` helpers the closure
// and TCO cell copies reuse.

import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.TaglessAdtLayout.adtAllocationSizeBytes
import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
import AshesCompiler.Backend.IrCodegen.Arena
import Ashes.Number.UInt
import Ashes.Text
export (
    type PersistentRegion(..),
    type CopyRuntime(..),
    value defineCopyRuntime,
    value copyRuntimeOf,
    value emitAllocAdtToSpace,
    value emitCopyOutArenaToSpace,
    value emitCopyFixedInto,
    value emitCopyStringIntoOrFresh,
    value emitCopyFixedIntoOrFresh,
    value emitCopyOutClosure,
    value emitCopyOutTcoListCell,
)

// A persistent bump region: the two `.bss` globals bounding its current chunk, both `0` until the
// first allocation grows it.
type PersistentRegion =
    | regionCursorGlobal: LLVMValueRef
    | regionEndGlobal: LLVMValueRef

type CopyRuntime =
    | toSpaceRegion: PersistentRegion
    | blobRegion: PersistentRegion
    | regionGrowFn: LLVMValueRef
    | regionGrowType: LLVMTypeRef

let rcHeaderBytes = 16

let listCellBytes = 16

let closureEnvironmentSizeMask = Ashes.Number.UInt.fromInt64((1 << 62) - 1)

let closureNormalizerSuffix = "$env_normalize"

let regionCursorGlobalOf (region: PersistentRegion) = region.regionCursorGlobal

let regionEndGlobalOf (region: PersistentRegion) = region.regionEndGlobal

let definePersistentRegion module_ i64 name =
    PersistentRegion(
        regionCursorGlobal = addZeroWordGlobal(module_)(i64)("__ashes_" + name + "_cursor"),
        regionEndGlobal = addZeroWordGlobal(module_)(i64)("__ashes_" + name + "_end")
    )

// `void __ashes_region_grow(ptr cursor, ptr end, i64 needed)`: the arena grow body over the slots
// passed in, shared by both persistent regions.
let emitRegionGrowBody context builder i64 i8 ptrType (arena: ArenaRuntime) (copy: CopyRuntime) =
    "entry"
    |> appendBasicBlock(context)(copy.regionGrowFn)
    |> positionBuilderAtEnd(builder)
    |> (given (_) ->
        2u32
        |> getParam(copy.regionGrowFn)
        |> emitRegionGrow(context)(copy.regionGrowFn)(builder)(i64)(i8)(ptrType)(arena)(getParam(copy.regionGrowFn)(0u32))(getParam(copy.regionGrowFn)(1u32)))
    |> (given (_) -> buildRetVoid(builder))

// Creates the two regions' globals and the shared grow helper in `module_`, emitting the helper's
// body with `builder` (repositioned by the caller afterwards).
let defineCopyRuntime module_ context builder i64 i8 ptrType (arena: ArenaRuntime) =
    false
    |> functionType(voidType(context))([ptrType, ptrType, i64])(3u32)
    |> (given (growType) ->
        CopyRuntime(
            toSpaceRegion = definePersistentRegion(module_)(i64)("tospace"),
            blobRegion = definePersistentRegion(module_)(i64)("blob"),
            regionGrowFn = addInternalFunction(module_)("__ashes_region_grow")(growType),
            regionGrowType = growType
        ))
    |> (given (copy) ->
        Unit
        |> (given (_) -> emitRegionGrowBody(context)(builder)(i64)(i8)(ptrType)(arena)(copy))
        |> (given (_) -> copy))

// The runtime a copy-family instruction needs; `codegenFunctions` defines it for every module
// whose IR carries one, so a missing runtime is a codegen bug rather than a program property.
let copyRuntimeOf (copy: Maybe(CopyRuntime)) =
    match copy with
        | Some(runtime) -> runtime
        | None -> Ashes.IO.panic("codegen: persistent-region helpers were not defined for this module")

// Bumps the region's cursor by `sizeRef` (an already-aligned `i64` byte count), growing first
// when the request does not fit in its current chunk, and returns the allocation's address word.
let emitRegionAllocDynamic context function_ builder i64 (copy: CopyRuntime) (region: PersistentRegion) sizeRef name =
    match (appendBasicBlock(context)(function_)(name + "_region_grow"), appendBasicBlock(context)(function_)(name + "_region_ok")) with
        | (growBlock, okBlock) ->
            Unit
            |> (given (_) -> buildLoad(builder)(i64)(region.regionCursorGlobal)(name + "_check_cursor"))
            |> (given (cursor) ->
                buildICmp(builder)(intPredicateUgt)(buildAdd(builder)(cursor)(sizeRef)(name + "_check_needed"))(buildLoad(builder)(i64)(region.regionEndGlobal)(name + "_check_end"))(name + "_overflow"))
            |> (given (overflow) -> buildCondBr(builder)(overflow)(growBlock)(okBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(growBlock))
            |> (given (_) -> buildCall(builder)(copy.regionGrowType)(copy.regionGrowFn)([region.regionCursorGlobal, region.regionEndGlobal, sizeRef])(3u32)(""))
            |> (given (_) -> buildBr(builder)(okBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(okBlock))
            |> (given (_) -> buildLoad(builder)(i64)(region.regionCursorGlobal)(name))
            |> (given (address) ->
                Unit
                |> (given (_) ->
                    buildStore(builder)(buildAdd(builder)(address)(sizeRef)(name + "_next_cursor"))(region.regionCursorGlobal))
                |> (given (_) -> address))

let emitRegionAlloc context function_ builder i64 (copy: CopyRuntime) (region: PersistentRegion) sizeBytes name =
    emitRegionAllocDynamic(context)(function_)(builder)(i64)(copy)(region)(arenaConst(i64)((sizeBytes + 7) / 8 * 8))(name)

// A region block of `16 + valueSize` bytes behind the immortal RC header, returned as the value
// address — `emitArenaValueAllocDynamic` for a persistent region.
let emitRegionValueAllocDynamic context function_ builder i64 i8 ptrType (copy: CopyRuntime) (region: PersistentRegion) valueSize name =
    name + "_total"
    |> buildAdd(builder)(valueSize)(arenaConst(i64)(rcHeaderBytes))
    |> (given (total) -> alignArenaSizeDynamic(builder)(i64)(total)(name + "_aligned"))
    |> (given (aligned) -> emitRegionAllocDynamic(context)(function_)(builder)(i64)(copy)(region)(aligned)(name + "_base"))
    |> (given (base) ->
        Unit
        |> (given (_) ->
            storeWordAt(builder)(i64)(i8)(ptrType)(base)(0)(arenaConst(i64)(arenaValueImmortalCount))(name + "_count"))
        |> (given (_) ->
            storeWordAt(builder)(i64)(i8)(ptrType)(base)(8)(arenaConst(i64)(0))(name + "_size"))
        |> (given (_) ->
            buildAdd(builder)(base)(arenaConst(i64)(rcHeaderBytes))(name)))

// A fresh owned `{len, bytes}` copy of `srcRef` (a view collapses into an owned value) in the
// blob region, behind the immortal header.
let emitBlobStringCopy context function_ builder i64 i8 ptrType (copy: CopyRuntime) (copyOut: CopyOutRuntime) srcRef name =
    match emitStringParts(builder)(i64)(ptrType)(srcRef)(name + "_src") with
        | (len, bytesAddr) ->
            name
            |> emitRegionValueAllocDynamic(context)(function_)(builder)(i64)(i8)(ptrType)(copy)(copy.blobRegion)(buildAdd(builder)(len)(arenaConst(i64)(8))(name + "_size"))
            |> (given (dest) ->
                Unit
                |> (given (_) -> storeWordAt(builder)(i64)(i8)(ptrType)(dest)(0)(len)(name + "_len"))
                |> (given (_) ->
                    emitMoveBytes(builder)(i64)(ptrType)(copyOut)(buildAdd(builder)(dest)(arenaConst(i64)(8))(name + "_bytes"))(bytesAddr)(len)(name))
                |> (given (_) -> dest))

// `sizeBytes` bytes from address word `srcRef` to `destRef` through `memcpy`: the two ranges
// never overlap, since one of them lies in a persistent region.
let emitCopyBytes builder i64 ptrType memcpyFn memcpyType destRef srcRef sizeBytes name = buildCall(builder)(memcpyType)(memcpyFn)([buildIntToPtr(builder)(destRef)(ptrType)(name + "_dest"), buildIntToPtr(builder)(srcRef)(ptrType)(name + "_src"), arenaConst(i64)(sizeBytes)])(3u32)(name + "_memcpy")

// A fixed-size cell in the blob region holding `sizeBytes` bytes of `srcRef`: a flat copy fully
// materializes a tuple of copy-type elements, which has no nested heap fields to follow.
let emitBlobFixedCopy context function_ builder i64 ptrType (copy: CopyRuntime) memcpyFn memcpyType srcRef sizeBytes name =
    name
    |> emitRegionAlloc(context)(function_)(builder)(i64)(copy)(copy.blobRegion)(sizeBytes)
    |> (given (dest) ->
        Unit
        |> (given (_) -> emitCopyBytes(builder)(i64)(ptrType)(memcpyFn)(memcpyType)(dest)(srcRef)(sizeBytes)(name))
        |> (given (_) -> dest))

// `AllocAdtToSpace`: `emitArenaAllocAdt`'s cell layout bumped from the to-space, which the TCO
// back-edge reset never reclaims, so the cell outlives the iteration that made it.
let emitAllocAdtToSpace context function_ builder i64 i8 ptrType (copy: CopyRuntime) tag fieldCount tagless name =
    name
    |> emitRegionAlloc(context)(function_)(builder)(i64)(copy)(copy.toSpaceRegion)(adtAllocationSizeBytes(tagless)(fieldCount))
    |> (given (address) ->
        if tagless
        then address
        else
            Unit
            |> (given (_) ->
                storeWordAt(builder)(i64)(i8)(ptrType)(address)(0)(arenaConst(i64)(tag))(name + "_tag"))
            |> (given (_) -> address))

// `CopyOutArenaToSpace`: `StaticSizeBytes > 0` copies that many bytes into a blob-region cell, `-1`
// a `Str`/`Bytes` value by its header length; no other size is a kind stage 0 has.
let emitCopyOutArenaToSpace context function_ builder i64 i8 ptrType (copy: CopyRuntime) (copyOut: CopyOutRuntime) memcpyFn memcpyType srcRef staticSizeBytes name =
    if staticSizeBytes > 0
    then emitBlobFixedCopy(context)(function_)(builder)(i64)(ptrType)(copy)(memcpyFn)(memcpyType)(srcRef)(staticSizeBytes)(name)
    else
        if staticSizeBytes == -1
        then emitBlobStringCopy(context)(function_)(builder)(i64)(i8)(ptrType)(copy)(copyOut)(srcRef)(name)
        else Ashes.IO.panic("codegen: CopyOutArenaToSpace with StaticSizeBytes " + Ashes.Text.fromInt(staticSizeBytes) + " not supported")

// `CopyFixedInto`: `sizeBytes` bytes of `srcRef` over the already-persistent, same-size cell at
// `destRef`.
let emitCopyFixedInto builder i64 ptrType memcpyFn memcpyType destRef srcRef sizeBytes =
    Unit
    |> (given (_) -> emitCopyBytes(builder)(i64)(ptrType)(memcpyFn)(memcpyType)(destRef)(srcRef)(sizeBytes)("copy_into"))
    |> (given (_) -> Unit)

// The four blocks of an in-place-or-fresh copy and the entry slot its arms merge through.
type InPlaceOrFreshBlocks =
    | checkBlock: LLVMBasicBlockRef
    | inPlaceBlock: LLVMBasicBlockRef
    | freshBlock: LLVMBasicBlockRef
    | continueBlock: LLVMBasicBlockRef
    | resultSlot: LLVMValueRef

let createInPlaceOrFreshBlocks context function_ builder i64 prefix =
    InPlaceOrFreshBlocks(
        checkBlock = appendBasicBlock(context)(function_)(prefix + "_check"),
        inPlaceBlock = appendBasicBlock(context)(function_)(prefix + "_in_place"),
        freshBlock = appendBasicBlock(context)(function_)(prefix + "_fresh"),
        continueBlock = appendBasicBlock(context)(function_)(prefix + "_continue"),
        resultSlot = buildEntryAlloca(builder)(i64)(prefix + "_result")
    )

// Whether `oldRef` lies in the blob region's current chunk, `[base, cursor)` with the base read
// from the footer at the region's end word. Emitted in the check block, which is only reached once
// the region is live (a non-zero end), so the footer read is always valid.
let emitOldInBlobChunk builder i64 i8 ptrType (copy: CopyRuntime) blobEnd oldRef prefix =
    match (buildLoad(builder)(i64)(regionCursorGlobalOf(copy.blobRegion))(prefix + "_blob_cursor"), loadWordAt(builder)(i64)(i8)(ptrType)(blobEnd)(0)(prefix + "_chunk_base")) with
        | (blobCursor, chunkBase) ->
            buildAnd(builder)(buildICmp(builder)(intPredicateUge)(oldRef)(chunkBase)(prefix + "_ge_base"))(buildICmp(builder)(intPredicateUlt)(oldRef)(blobCursor)(prefix + "_lt_cursor"))(prefix + "_in_chunk")

// The shared skeleton: a dead blob region (end `0`) goes straight to the fresh path; otherwise
// `emitSafe` decides between the in-place write (`emitInPlace`, yielding `oldRef`) and the fresh
// materialization (`emitFresh`). Both arms store into the result slot and merge.
let emitInPlaceOrFresh context function_ builder i64 (copy: CopyRuntime) prefix oldRef emitSafe emitInPlace emitFresh =
    prefix
    |> createInPlaceOrFreshBlocks(context)(function_)(builder)(i64)
    |> (given (blocks: InPlaceOrFreshBlocks) ->
        prefix + "_blob_end"
        |> buildLoad(builder)(i64)(regionEndGlobalOf(copy.blobRegion))
        |> (given (blobEnd) ->
            Unit
            |> (given (_) ->
                buildCondBr(builder)(buildICmp(builder)(intPredicateNe)(blobEnd)(arenaConst(i64)(0))(prefix + "_blob_live"))(blocks.checkBlock)(blocks.freshBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(blocks.checkBlock))
            |> (given (_) -> emitSafe(blobEnd))
            |> (given (safe) -> buildCondBr(builder)(safe)(blocks.inPlaceBlock)(blocks.freshBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(blocks.inPlaceBlock))
            |> (given (_) -> emitInPlace(Unit))
            |> (given (_) -> buildStore(builder)(oldRef)(blocks.resultSlot))
            |> (given (_) -> buildBr(builder)(blocks.continueBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(blocks.freshBlock))
            |> (given (_) -> emitFresh(Unit))
            |> (given (fresh) -> buildStore(builder)(fresh)(blocks.resultSlot))
            |> (given (_) -> buildBr(builder)(blocks.continueBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(blocks.continueBlock))
            |> (given (_) -> buildLoad(builder)(i64)(blocks.resultSlot)(prefix + "_result_value"))))

// `CopyStringIntoOrFresh`: the new string's bytes over the old owned blob when they fit its
// capacity (its length, for an owned blob) and the blob lies in the blob region's current chunk;
// otherwise a fresh blob-region copy. The result is whichever blob now holds the value.
let emitCopyStringIntoOrFresh context function_ builder i64 i8 ptrType (copy: CopyRuntime) (copyOut: CopyOutRuntime) oldRef srcRef name =
    match emitStringParts(builder)(i64)(ptrType)(srcRef)("cstr_new") with
        | (newLen, srcBytesAddr) ->
            emitInPlaceOrFresh(context)(function_)(builder)(i64)(copy)("cstr")(oldRef)(given (blobEnd) ->
                buildAnd(builder)(buildICmp(builder)(intPredicateUle)(newLen)(emitStringLengthValue(builder)(i64)(ptrType)(oldRef)("cstr_old_cap"))("cstr_fits"))(emitOldInBlobChunk(builder)(i64)(i8)(ptrType)(copy)(blobEnd)(oldRef)("cstr"))("cstr_safe"))(given (_) ->
                Unit
                |> (given (_) -> storeWordAt(builder)(i64)(i8)(ptrType)(oldRef)(0)(newLen)("cstr_in_place_len"))
                |> (given (_) ->
                    emitMoveBytes(builder)(i64)(ptrType)(copyOut)(buildAdd(builder)(oldRef)(arenaConst(i64)(8))("cstr_in_place_dest"))(srcBytesAddr)(newLen)("cstr_in_place")))(given (_) -> emitBlobStringCopy(context)(function_)(builder)(i64)(i8)(ptrType)(copy)(copyOut)(srcRef)(name + "_fresh"))

// `CopyFixedIntoOrFresh`: `sizeBytes` bytes of `srcRef` over the old cell when it lies in the
// blob region's current chunk; otherwise a fresh blob-region cell.
let emitCopyFixedIntoOrFresh context function_ builder i64 i8 ptrType (copy: CopyRuntime) memcpyFn memcpyType oldRef srcRef sizeBytes name =
    emitInPlaceOrFresh(context)(function_)(builder)(i64)(copy)("cfif")(oldRef)(given (blobEnd) -> emitOldInBlobChunk(builder)(i64)(i8)(ptrType)(copy)(blobEnd)(oldRef)("cfif"))(given (_) -> emitCopyBytes(builder)(i64)(ptrType)(memcpyFn)(memcpyType)(oldRef)(srcRef)(sizeBytes)("cfif_in_place"))(given (_) -> emitBlobFixedCopy(context)(function_)(builder)(i64)(ptrType)(copy)(memcpyFn)(memcpyType)(srcRef)(sizeBytes)(name + "_fresh"))

let endsWith (text: Str) (suffix: Str) =
    Ashes.Text.length(text) >= Ashes.Text.length(suffix) && Ashes.Text.substring(text)(Ashes.Text.length(text) - Ashes.Text.length(suffix))(Ashes.Text.length(suffix)) == suffix

let recursive findLiftedFunction (label: Str) (liftedFunctions: List((Str, LLVMValueRef))) =
    match liftedFunctions with
        | [] -> None
        | (candidate, function_) :: rest ->
            if candidate == label
            then Some(function_)
            else findLiftedFunction(label)(rest)

// Every `(closure, normalizer)` function pair in the program: a lifted `L$env_normalize` whose
// closure `L` is itself lifted. The normalizer takes `(sourceEnv, destinationEnv, 0)` under the
// closure calling convention and returns the copied environment's dropper address.
let recursive collectClosureNormalizers (liftedFunctions: List((Str, LLVMValueRef))) (candidates: List((Str, LLVMValueRef))) =
    match candidates with
        | [] -> []
        | (label, normalizer) :: rest ->
            if endsWith(label)(closureNormalizerSuffix)
            then
                match findLiftedFunction(Ashes.Text.substring(label)(0)(Ashes.Text.length(label) - Ashes.Text.length(closureNormalizerSuffix)))(liftedFunctions) with
                    | Some(closure) -> (closure, normalizer) :: collectClosureNormalizers(liftedFunctions)(rest)
                    | None -> collectClosureNormalizers(liftedFunctions)(rest)
            else collectClosureNormalizers(liftedFunctions)(rest)

// The runtime-managed environment copy's normalizer dispatch: one code-address compare per
// known normalizer, calling the matching one (its returned dropper replaces the source's) and
// branching to `copiedBlock`; the builder is left in the block that falls through to the raw
// copy when no closure matched.
let recursive emitNormalizerDispatch context function_ builder i64 closureFnType code sourceEnv destinationEnv dropperSlot copiedBlock normalizers =
    match normalizers with
        | [] -> Unit
        | (closure, normalizer) :: rest ->
            match (appendBasicBlock(context)(function_)("copy_closure_normalize_env"), appendBasicBlock(context)(function_)("copy_closure_check_normalizer")) with
                | (normalizeBlock, nextBlock) ->
                    Unit
                    |> (given (_) ->
                        buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(code)(buildPtrToInt(builder)(closure)(i64)("copy_closure_known_code"))("copy_closure_has_normalizer"))(normalizeBlock)(nextBlock))
                    |> (given (_) -> positionBuilderAtEnd(builder)(normalizeBlock))
                    |> (given (_) -> buildCall(builder)(closureFnType)(normalizer)([sourceEnv, destinationEnv, arenaConst(i64)(0)])(3u32)("copy_closure_normalizer_call"))
                    |> (given (normalizedDropper) -> buildStore(builder)(normalizedDropper)(dropperSlot))
                    |> (given (_) -> buildBr(builder)(copiedBlock))
                    |> (given (_) -> positionBuilderAtEnd(builder)(nextBlock))
                    |> (given (_) -> emitNormalizerDispatch(context)(function_)(builder)(i64)(closureFnType)(code)(sourceEnv)(destinationEnv)(dropperSlot)(copiedBlock)(rest))

// Fills the fresh environment block: an arena copy is the raw bytes; a runtime-managed copy runs
// the closure's normalizer when the program has one for its code address, else the raw bytes.
let emitNormalizeOrCopyClosureEnvironment context function_ builder i64 ptrType (copyOut: CopyOutRuntime) closureFnType liftedFunctions runtimeManaged code sourceEnv destinationEnv envSize dropperSlot =
    if runtimeManaged == false
    then
        Unit
        |> (given (_) -> emitMoveBytes(builder)(i64)(ptrType)(copyOut)(destinationEnv)(sourceEnv)(envSize)("copy_closure_env"))
        |> (given (_) -> Unit)
    else
        match (appendBasicBlock(context)(function_)("copy_closure_raw_env"), appendBasicBlock(context)(function_)("copy_closure_env_copied")) with
            | (rawCopyBlock, copiedBlock) ->
                Unit
                |> (given (_) ->
                    liftedFunctions
                    |> collectClosureNormalizers(liftedFunctions)
                    |> emitNormalizerDispatch(context)(function_)(builder)(i64)(closureFnType)(code)(sourceEnv)(destinationEnv)(dropperSlot)(copiedBlock))
                |> (given (_) -> buildBr(builder)(rawCopyBlock))
                |> (given (_) -> positionBuilderAtEnd(builder)(rawCopyBlock))
                |> (given (_) -> emitMoveBytes(builder)(i64)(ptrType)(copyOut)(destinationEnv)(sourceEnv)(envSize)("copy_closure_env"))
                |> (given (_) -> buildBr(builder)(copiedBlock))
                |> (given (_) -> positionBuilderAtEnd(builder)(copiedBlock))

// A fresh block of `sizeRef` bytes for a closure copy: an RC cell, or a word-aligned arena bump.
let emitClosureCopyBlock context function_ builder i64 i8 (arena: ArenaRuntime) mallocFn mallocType runtimeManaged sizeRef name =
    if runtimeManaged
    then emitRcAllocWord(builder)(i64)(i8)(mallocFn)(mallocType)(sizeRef)(name)
    else
        emitArenaAllocDynamic(context)(function_)(builder)(i64)(arena)(alignArenaSizeDynamic(builder)(i64)(sizeRef)(name + "_aligned"))(name)

// The source closure's four words and the two entry slots the environment copy merges through.
type ClosureCopyState =
    | closureCode: LLVMValueRef
    | closureEnv: LLVMValueRef
    | closurePackedSize: LLVMValueRef
    | closureEnvSize: LLVMValueRef
    | newEnvSlot: LLVMValueRef
    | newDropperSlot: LLVMValueRef

let loadClosureCopyState builder i64 i8 ptrType srcRef =
    match (buildEntryAlloca(builder)(i64)("copy_closure_new_env_slot"), buildEntryAlloca(builder)(i64)("copy_closure_new_dropper_slot"), loadWordAt(builder)(i64)(i8)(ptrType)(srcRef)(16)("copy_closure_env_size")) with
        | (envSlot, dropperSlot, packedSize) ->
            Unit
            |> (given (_) ->
                buildStore(builder)(arenaConst(i64)(0))(envSlot))
            |> (given (_) ->
                buildStore(builder)(loadWordAt(builder)(i64)(i8)(ptrType)(srcRef)(24)("copy_closure_dropper"))(dropperSlot))
            |> (given (_) ->
                ClosureCopyState(
                    closureCode = loadWordAt(builder)(i64)(i8)(ptrType)(srcRef)(0)("copy_closure_code"),
                    closureEnv = loadWordAt(builder)(i64)(i8)(ptrType)(srcRef)(8)("copy_closure_env"),
                    closurePackedSize = packedSize,
                    closureEnvSize = buildAnd(builder)(packedSize)(constInt(i64)(closureEnvironmentSizeMask)(false))("copy_closure_env_size_masked"),
                    newEnvSlot = envSlot,
                    newDropperSlot = dropperSlot
                ))

// A nil environment (no captures) is kept as nil; otherwise a fresh block of the environment's
// size is filled by `emitNormalizeOrCopyClosureEnvironment` and its address stored in the slot.
let emitCopyOutClosureEnvironment context function_ builder i64 i8 ptrType (arena: ArenaRuntime) (copyOut: CopyOutRuntime) mallocFn mallocType closureFnType liftedFunctions runtimeManaged (state: ClosureCopyState) =
    match (appendBasicBlock(context)(function_)("copy_closure_env_copy"), appendBasicBlock(context)(function_)("copy_closure_env_merge")) with
        | (copyBlock, mergeBlock) ->
            Unit
            |> (given (_) ->
                buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(state.closureEnv)(arenaConst(i64)(0))("copy_closure_env_nil"))(mergeBlock)(copyBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(copyBlock))
            |> (given (_) -> emitClosureCopyBlock(context)(function_)(builder)(i64)(i8)(arena)(mallocFn)(mallocType)(runtimeManaged)(state.closureEnvSize)("copy_closure_new_env"))
            |> (given (newEnv) ->
                Unit
                |> (given (_) -> emitNormalizeOrCopyClosureEnvironment(context)(function_)(builder)(i64)(ptrType)(copyOut)(closureFnType)(liftedFunctions)(runtimeManaged)(state.closureCode)(state.closureEnv)(newEnv)(state.closureEnvSize)(state.newDropperSlot))
                |> (given (_) -> buildStore(builder)(newEnv)(state.newEnvSlot)))
            |> (given (_) -> buildBr(builder)(mergeBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(mergeBlock))

// `CopyOutClosure`: the environment copied first (see above), then a fresh 32-byte closure holding
// the same code word and packed size, the new environment, and the possibly normalized dropper.
let emitCopyOutClosure context function_ builder i64 i8 ptrType (arena: ArenaRuntime) (copyOut: CopyOutRuntime) mallocFn mallocType closureFnType liftedFunctions runtimeManaged srcRef name =
    srcRef
    |> loadClosureCopyState(builder)(i64)(i8)(ptrType)
    |> (given (state) ->
        Unit
        |> (given (_) -> emitCopyOutClosureEnvironment(context)(function_)(builder)(i64)(i8)(ptrType)(arena)(copyOut)(mallocFn)(mallocType)(closureFnType)(liftedFunctions)(runtimeManaged)(state))
        |> (given (_) ->
            emitClosureCopyBlock(context)(function_)(builder)(i64)(i8)(arena)(mallocFn)(mallocType)(runtimeManaged)(arenaConst(i64)(closureSizeBytes))(name))
        |> (given (newClosure) ->
            Unit
            |> (given (_) -> storeWordAt(builder)(i64)(i8)(ptrType)(newClosure)(0)(state.closureCode)("copy_closure_store_code"))
            |> (given (_) ->
                storeWordAt(builder)(i64)(i8)(ptrType)(newClosure)(8)(buildLoad(builder)(i64)(state.newEnvSlot)("copy_closure_new_env"))("copy_closure_store_env"))
            |> (given (_) -> storeWordAt(builder)(i64)(i8)(ptrType)(newClosure)(16)(state.closurePackedSize)("copy_closure_store_env_size"))
            |> (given (_) ->
                storeWordAt(builder)(i64)(i8)(ptrType)(newClosure)(24)(buildLoad(builder)(i64)(state.newDropperSlot)("copy_closure_new_dropper"))("copy_closure_store_dropper"))
            |> (given (_) -> newClosure)))

// The head copy of a TCO cell: a string through `__ashes_copy_out_string`, an inner list of
// inline elements through `__ashes_copy_out_list`, both into the arena. An inline head never
// reaches this instruction (lowering uses `CopyOutArena(16)` for it), so it is refused.
let emitTcoCellHeadCopy builder i64 (copyOut: CopyOutRuntime) headCopy head =
    match headCopy with
        | StringListHead -> buildCall(builder)(copyOut.copyOutStringType)(copyOut.copyOutStringFn)([head, arenaConst(i64)(0)])(2u32)("tco_cell_new_head")
        | InnerListHead ->
            buildCall(builder)(copyOut.copyOutListType)(copyOut.copyOutListFn)([head, InlineListHead
            |> listHeadKindCode
            |> arenaConst(i64), arenaConst(i64)(0)])(3u32)("tco_cell_new_head")
        | InlineListHead -> Ashes.IO.panic("codegen: CopyOutTcoListCell with an inline head is not a form lowering emits")

// `CopyOutTcoListCell`: a nil source stays nil; otherwise the head is copied per its kind and a
// fresh arena cell holds it with the source's tail word unchanged (the tail is a pre-watermark
// cell of an earlier iteration).
let emitCopyOutTcoListCell context function_ builder i64 i8 ptrType (arena: ArenaRuntime) (copyOut: CopyOutRuntime) headCopy srcRef name =
    match (buildEntryAlloca(builder)(i64)("tco_cell_result_slot"), appendBasicBlock(context)(function_)("tco_cell_copy"), appendBasicBlock(context)(function_)("tco_cell_merge")) with
        | (resultSlot, copyBlock, mergeBlock) ->
            Unit
            |> (given (_) ->
                buildStore(builder)(arenaConst(i64)(0))(resultSlot))
            |> (given (_) ->
                buildCondBr(builder)(buildICmp(builder)(intPredicateEq)(srcRef)(arenaConst(i64)(0))("tco_cell_nil_check"))(mergeBlock)(copyBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(copyBlock))
            |> (given (_) ->
                match (loadWordAt(builder)(i64)(i8)(ptrType)(srcRef)(0)("tco_cell_old_head"), loadWordAt(builder)(i64)(i8)(ptrType)(srcRef)(8)("tco_cell_old_tail")) with
                    | (oldHead, oldTail) ->
                        oldHead
                        |> emitTcoCellHeadCopy(builder)(i64)(copyOut)(headCopy)
                        |> (given (newHead) ->
                            "tco_cell_new"
                            |> emitArenaAlloc(context)(function_)(builder)(i64)(arena)(listCellBytes)
                            |> (given (newCell) ->
                                Unit
                                |> (given (_) -> storeWordAt(builder)(i64)(i8)(ptrType)(newCell)(0)(newHead)("tco_cell_store_head"))
                                |> (given (_) -> storeWordAt(builder)(i64)(i8)(ptrType)(newCell)(8)(oldTail)("tco_cell_store_tail"))
                                |> (given (_) -> buildStore(builder)(newCell)(resultSlot)))))
            |> (given (_) -> buildBr(builder)(mergeBlock))
            |> (given (_) -> positionBuilderAtEnd(builder)(mergeBlock))
            |> (given (_) -> buildLoad(builder)(i64)(resultSlot)(name))
