// The runtime-managed reference-count emitters for `AshesCompiler.Backend.IrCodegen`: the retain
// (`RcDup`), the uniqueness test (`RcIsUnique`), the release (`RcDrop`, including its closure and
// structural-dropper forms), and the in-place reuse pair (`DropReuse`/`AllocReusing`), all over
// the 16-byte `{i64 reference_count, i64 allocation_size}` header every RC-managed cell carries
// immediately before its payload pointer — `LlvmCodegenMemory.cs`'s `EmitRuntimeRcDup`,
// `EmitRuntimeRcIsUnique`, `EmitRuntimeRcDrop`, `EmitRuntimeDropReuse`, `EmitAllocReusing`,
// `LlvmCodegenExpressions.cs`'s `EmitRuntimeRcClosureDrop`, and `LlvmCodegen.cs`'s
// `EmitRuntimeManagedDupValue`/`EmitRuntimeManagedDropInstruction`, emitter for emitter. libc
// `malloc`/`free` is the allocator: a cell whose count reaches zero goes straight back to `free`,
// where stage 0's `EmitRuntimeRcRelease` first tries its size-binned free-list cache.
//
// An immortal value (a string literal's static header, or an arena-resident `Str`/`BigInt` copy)
// carries `rcImmortalSentinel` in place of a count: a retain or release on it is a no-op and
// `DropReuse` never yields it as a token. The empty list is the null pointer, which has no header
// at all, so the `mayBeEmpty` forms of retain and release branch around the header access. No
// `phi` exists in this package's LLVM surface, so an emitter that produces a value merges its
// arms through an entry-block scratch slot and a void one falls through into a shared continue
// block. Depends only on the LLVM bindings and `IrCodegen.Support`.

import AshesCompiler.Backend.Llvm
import AshesCompiler.Backend.IrCodegen.Support
import Ashes.Number.UInt
export (
    value rcImmortalSentinel,
    value emitRuntimeRcDup,
    value emitRuntimeRcIsUnique,
    value emitRuntimeRcDrop,
    value emitRuntimeRcClosureDrop,
    value emitRuntimeDropReuse,
    value emitAllocReusing,
    value emitRuntimeManagedDup,
    value emitRuntimeManagedDrop,
)

// `1 << 62`, the same sentinel `IrCodegen`'s string-literal globals and `IrCodegen.Arena`'s
// arena-resident value copies write into their headers instead of a count of `1`. Far from `0`
// and from any real count, and deliberately not `-1`, which is what an accidental underflow of a
// real count would also produce.
let rcImmortalSentinel = Ashes.Number.UInt.fromInt64(1 << 62)

// `(1 << 62) - 1`: the environment-size bits of a closure's packed size word, below the two
// ownership bits `packClosureEnvironmentSize` sets.
let closureEnvironmentSizeMask = Ashes.Number.UInt.fromInt64((1 << 62) - 1)

let rcHeaderSizeBytes = 16

let rcListCellPayloadBytes = 16

let rcConst i64 value =
    constInt(i64)(Ashes.Number.UInt.fromInt64(value))(false)

// The header pointer of the cell whose payload pointer is `valueRef` (an `i64` word): the same
// `-16` walk back that `emitRcAllocPayloadPtr`'s forward `+16` established.
let rcHeaderPtr builder i64 i8 ptrType valueRef prefix =
    gepBytes(builder)(i64)(i8)(buildIntToPtr(builder)(valueRef)(ptrType)(prefix + "_value_ptr"))(-rcHeaderSizeBytes)(prefix + "_base")

let rcLoadCount builder i64 headerPtr prefix = buildLoad(builder)(i64)(headerPtr)(prefix + "_count")

let rcIsImmortal builder i64 count prefix =
    buildICmp(builder)(intPredicateEq)(count)(constInt(i64)(rcImmortalSentinel)(false))(prefix + "_immortal")

let rcIsCountOne builder i64 count prefix =
    buildICmp(builder)(intPredicateEq)(count)(rcConst(i64)(1))(prefix + "_one")

let rcIsPresent builder i64 valueRef prefix =
    buildICmp(builder)(intPredicateNe)(valueRef)(rcConst(i64)(0))(prefix + "_present")

let rcStoreIncremented builder i64 headerPtr count name =
    name
    |> buildAdd(builder)(count)(rcConst(i64)(1))
    |> (given (incremented) -> buildStore(builder)(incremented)(headerPtr))

let rcStoreDecremented builder i64 headerPtr count name =
    name
    |> buildSub(builder)(count)(rcConst(i64)(1))
    |> (given (decremented) -> buildStore(builder)(decremented)(headerPtr))

// Emits `body` into `block` and ends it with a branch to `continueBlock`; the builder is left at
// the end of `continueBlock` only by the caller, since several arms usually share one.
let emitRcArm builder block continueBlock body =
    block
    |> positionBuilderAtEnd(builder)
    |> body
    |> (given (_) -> buildBr(builder)(continueBlock))

// `RcDup` on a value that is never the empty list: increments the count unless the value is
// immortal, and yields the same pointer (the duplicate is identity-preserving).
let emitRuntimeRcDup context function_ i64 i8 ptrType builder valueRef =
    (let headerPtr = rcHeaderPtr(builder)(i64)(i8)(ptrType)(valueRef)("rc_dup")
    in
        let count = rcLoadCount(builder)(i64)(headerPtr)("rc_dup")
        in
            let incBlock = appendBasicBlock(context)(function_)("rc_dup_inc")
            in
                let mergeBlock = appendBasicBlock(context)(function_)("rc_dup_merge")
                in
                    "rc_dup"
                    |> rcIsImmortal(builder)(i64)(count)
                    |> (given (isImmortal) -> buildCondBr(builder)(isImmortal)(mergeBlock)(incBlock))
                    |> (given (_) ->
                        emitRcArm(builder)(incBlock)(mergeBlock)(given (_) -> rcStoreIncremented(builder)(i64)(headerPtr)(count)("rc_dup_incremented")))
                    |> (given (_) -> positionBuilderAtEnd(builder)(mergeBlock))
                    |> (given (_) -> valueRef))

// `RcIsUnique`: whether the count is exactly `1`, as this codegen's canonical 0/1 `i64` Bool. An
// immortal value's sentinel never equals `1`, so it is never unique.
let emitRuntimeRcIsUnique builder i64 i8 ptrType resultName valueRef =
    "rc_unique"
    |> rcHeaderPtr(builder)(i64)(i8)(ptrType)(valueRef)
    |> (given (headerPtr) -> rcLoadCount(builder)(i64)(headerPtr)("rc_unique"))
    |> (given (count) -> rcIsCountOne(builder)(i64)(count)(resultName))
    |> (given (isUnique) -> buildZExt(builder)(isUnique)(i64)(resultName))

// `RcDrop` on a value that is never the empty list and needs no structural dropper: an immortal
// value is left alone, a count of `1` frees the header (and with it the payload), anything else
// is decremented.
let emitRuntimeRcDrop context function_ i64 i8 ptrType builder freeFn freeType valueRef =
    (let headerPtr = rcHeaderPtr(builder)(i64)(i8)(ptrType)(valueRef)("rc_drop")
    in
        let count = rcLoadCount(builder)(i64)(headerPtr)("rc_drop")
        in
            let checkLastBlock = appendBasicBlock(context)(function_)("rc_drop_check_last")
            in
                let releaseBlock = appendBasicBlock(context)(function_)("rc_drop_release")
                in
                    let retainBlock = appendBasicBlock(context)(function_)("rc_drop_retain")
                    in
                        let continueBlock = appendBasicBlock(context)(function_)("rc_drop_continue")
                        in
                            "rc_drop"
                            |> rcIsImmortal(builder)(i64)(count)
                            |> (given (isImmortal) -> buildCondBr(builder)(isImmortal)(continueBlock)(checkLastBlock))
                            |> (given (_) -> positionBuilderAtEnd(builder)(checkLastBlock))
                            |> (given (_) -> rcIsCountOne(builder)(i64)(count)("rc_drop_last"))
                            |> (given (isLast) -> buildCondBr(builder)(isLast)(releaseBlock)(retainBlock))
                            |> (given (_) ->
                                emitRcArm(builder)(retainBlock)(continueBlock)(given (_) -> rcStoreDecremented(builder)(i64)(headerPtr)(count)("rc_drop_decremented")))
                            |> (given (_) ->
                                emitRcArm(builder)(releaseBlock)(continueBlock)(given (_) -> buildCall(builder)(freeType)(freeFn)([headerPtr])(1u32)("")))
                            |> (given (_) -> positionBuilderAtEnd(builder)(continueBlock)))

// `RcDrop` of a runtime-managed closure object: releases the environment block first when the
// closure has one (a non-zero environment size in its packed size word), then the closure
// itself. Both are ordinary RC cells, so each release is `emitRuntimeRcDrop`.
let emitRuntimeRcClosureDrop context function_ i64 i8 ptrType builder freeFn freeType closureRef =
    (let closurePtr = buildIntToPtr(builder)(closureRef)(ptrType)("rc_closure_ptr")
    in
        let dropEnvBlock = appendBasicBlock(context)(function_)("rc_closure_drop_env")
        in
            let dropClosureBlock = appendBasicBlock(context)(function_)("rc_closure_drop_value")
            in
                "rc_closure_env_size_slot"
                |> gepBytes(builder)(i64)(i8)(closurePtr)(16)
                |> (given (sizeSlot) -> buildLoad(builder)(i64)(sizeSlot)("rc_closure_env_size"))
                |> (given (packedSize) ->
                    buildAnd(builder)(packedSize)(constInt(i64)(closureEnvironmentSizeMask)(false))("rc_closure_env_size_masked"))
                |> (given (envSize) -> rcIsPresent(builder)(i64)(envSize)("rc_closure_env"))
                |> (given (hasEnv) -> buildCondBr(builder)(hasEnv)(dropEnvBlock)(dropClosureBlock))
                |> (given (_) ->
                    emitRcArm(builder)(dropEnvBlock)(dropClosureBlock)(given (_) ->
                        "rc_closure_env_slot"
                        |> gepBytes(builder)(i64)(i8)(closurePtr)(8)
                        |> (given (envSlot) -> buildLoad(builder)(i64)(envSlot)("rc_closure_env"))
                        |> emitRuntimeRcDrop(context)(function_)(i64)(i8)(ptrType)(builder)(freeFn)(freeType)))
                |> (given (_) -> positionBuilderAtEnd(builder)(dropClosureBlock))
                |> (given (_) -> emitRuntimeRcDrop(context)(function_)(i64)(i8)(ptrType)(builder)(freeFn)(freeType)(closureRef)))

// `DropReuse` on a runtime-managed value: consumes the source ownership and yields the cell
// itself as the reuse token when its count is `1`, otherwise decrements the count and yields the
// null token. An immortal value yields the null token without its header being touched.
let emitRuntimeDropReuse context function_ i64 i8 ptrType builder resultName valueRef =
    (let resultSlot = buildEntryAlloca(builder)(i64)("drop_reuse_result_slot")
    in
        let headerPtr = rcHeaderPtr(builder)(i64)(i8)(ptrType)(valueRef)("drop_reuse")
        in
            let count = rcLoadCount(builder)(i64)(headerPtr)("drop_reuse")
            in
                let checkUniqueBlock = appendBasicBlock(context)(function_)("drop_reuse_check_unique")
                in
                    let immortalBlock = appendBasicBlock(context)(function_)("drop_reuse_immortal_block")
                    in
                        let takeBlock = appendBasicBlock(context)(function_)("drop_reuse_take")
                        in
                            let sharedBlock = appendBasicBlock(context)(function_)("drop_reuse_shared")
                            in
                                let continueBlock = appendBasicBlock(context)(function_)("drop_reuse_continue")
                                in
                                    "drop_reuse"
                                    |> rcIsImmortal(builder)(i64)(count)
                                    |> (given (isImmortal) -> buildCondBr(builder)(isImmortal)(immortalBlock)(checkUniqueBlock))
                                    |> (given (_) ->
                                        emitRcArm(builder)(immortalBlock)(continueBlock)(given (_) ->
                                            buildStore(builder)(rcConst(i64)(0))(resultSlot)))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(checkUniqueBlock))
                                    |> (given (_) -> rcIsCountOne(builder)(i64)(count)("drop_reuse_unique"))
                                    |> (given (isUnique) -> buildCondBr(builder)(isUnique)(takeBlock)(sharedBlock))
                                    |> (given (_) ->
                                        emitRcArm(builder)(takeBlock)(continueBlock)(given (_) -> buildStore(builder)(valueRef)(resultSlot)))
                                    |> (given (_) ->
                                        emitRcArm(builder)(sharedBlock)(continueBlock)(given (_) ->
                                            "drop_reuse_decremented"
                                            |> rcStoreDecremented(builder)(i64)(headerPtr)(count)
                                            |> (given (_) ->
                                                buildStore(builder)(rcConst(i64)(0))(resultSlot))))
                                    |> (given (_) -> positionBuilderAtEnd(builder)(continueBlock))
                                    |> (given (_) -> buildLoad(builder)(i64)(resultSlot)(resultName)))

// Writes the constructor tag into word `0` of a reused ADT cell; a list cell has no tag word.
let emitReuseTagStore builder i64 ptrType tokenRef tag listCell =
    if listCell
    then Unit
    else
        let _ =
            "adt_reuse_tag_base"
            |> buildIntToPtr(builder)(tokenRef)(ptrType)
            |> buildStore(builder)(rcConst(i64)(tag))
        in Unit

// The fresh cell `AllocReusing` allocates when its runtime-managed token is null: a two-word
// list cell, or a tagged `[tag][fields...]` ADT cell of the requested layout.
let emitReuseFreshCell builder i64 i8 mallocFn mallocType tag fieldCount listCell =
    if listCell
    then
        buildPtrToInt(builder)(emitRcAllocPayloadPtr(builder)(i64)(i8)(mallocFn)(mallocType)(rcListCellPayloadBytes)("rc_list_reuse"))(i64)("rc_list_reuse_word")
    else emitAllocAdtRuntimeManaged(builder)(i64)(i8)(mallocFn)(mallocType)(tag)(fieldCount)("rc_adt_reuse")

let emitRuntimeAllocReusing context function_ i64 i8 ptrType builder mallocFn mallocType tag fieldCount listCell resultName tokenRef =
    (let resultSlot = buildEntryAlloca(builder)(i64)("alloc_reuse_result_slot")
    in
        let takeBlock = appendBasicBlock(context)(function_)("alloc_reuse_take")
        in
            let freshBlock = appendBasicBlock(context)(function_)("alloc_reuse_fresh")
            in
                let continueBlock = appendBasicBlock(context)(function_)("alloc_reuse_continue")
                in
                    "alloc_reuse_token"
                    |> rcIsPresent(builder)(i64)(tokenRef)
                    |> (given (hasToken) -> buildCondBr(builder)(hasToken)(takeBlock)(freshBlock))
                    |> (given (_) ->
                        emitRcArm(builder)(takeBlock)(continueBlock)(given (_) ->
                            listCell
                            |> emitReuseTagStore(builder)(i64)(ptrType)(tokenRef)(tag)
                            |> (given (_) -> buildStore(builder)(tokenRef)(resultSlot))))
                    |> (given (_) ->
                        emitRcArm(builder)(freshBlock)(continueBlock)(given (_) ->
                            listCell
                            |> emitReuseFreshCell(builder)(i64)(i8)(mallocFn)(mallocType)(tag)(fieldCount)
                            |> (given (fresh) -> buildStore(builder)(fresh)(resultSlot))))
                    |> (given (_) -> positionBuilderAtEnd(builder)(continueBlock))
                    |> (given (_) -> buildLoad(builder)(i64)(resultSlot)(resultName)))

// `AllocReusing`: yields the cell at the token's address as the new value instead of allocating,
// with the tag written for an ADT cell. An arena token is statically a dead, uniquely-owned cell
// of the compatible layout, so that form is the tag store alone; a runtime-managed token is null
// when `DropReuse` found the cell shared, in which case a fresh RC cell of the same layout is
// allocated instead.
let emitAllocReusing context function_ i64 i8 ptrType builder mallocFn mallocType tag fieldCount runtimeManaged listCell resultName tokenRef =
    if runtimeManaged
    then emitRuntimeAllocReusing(context)(function_)(i64)(i8)(ptrType)(builder)(mallocFn)(mallocType)(tag)(fieldCount)(listCell)(resultName)(tokenRef)
    else
        let _ = emitReuseTagStore(builder)(i64)(ptrType)(tokenRef)(tag)(listCell)
        in tokenRef

// The runtime-managed `RcDup` instruction: a value that may be the empty list (the null pointer,
// with no header) is retained only when present. The result is the same pointer either way.
let emitRuntimeManagedDup context function_ i64 i8 ptrType builder mayBeEmpty valueRef =
    if mayBeEmpty == false
    then emitRuntimeRcDup(context)(function_)(i64)(i8)(ptrType)(builder)(valueRef)
    else
        let dupBlock = appendBasicBlock(context)(function_)("rc_dup_present")
        in
            let afterBlock = appendBasicBlock(context)(function_)("rc_dup_after_empty")
            in
                "rc_dup"
                |> rcIsPresent(builder)(i64)(valueRef)
                |> (given (isPresent) -> buildCondBr(builder)(isPresent)(dupBlock)(afterBlock))
                |> (given (_) ->
                    emitRcArm(builder)(dupBlock)(afterBlock)(given (_) -> emitRuntimeRcDup(context)(function_)(i64)(i8)(ptrType)(builder)(valueRef)))
                |> (given (_) -> positionBuilderAtEnd(builder)(afterBlock))
                |> (given (_) -> valueRef)

// One present (non-null) value's release. A structural release reaches past this allocation (a
// list spine and its elements, an aggregate and its managed children), so it is one call of the
// dropper function `structuralDropper` names, with the closure calling convention's `(env = 0,
// value, flag = 0)` — the dropper owns the whole release, including this cell's own count. A
// closure (`isClosure`, type name `Function`) releases its environment along with itself.
let emitDropCountedValue context function_ i64 i8 ptrType builder freeFn freeType closureFnType structuralDropper isClosure valueRef =
    match structuralDropper with
        | Some(dropperFn) ->
            let _ = buildCall(builder)(closureFnType)(dropperFn)([rcConst(i64)(0), valueRef, rcConst(i64)(0)])(3u32)("rc_structural_drop")
            in Unit
        | None ->
            if isClosure
            then emitRuntimeRcClosureDrop(context)(function_)(i64)(i8)(ptrType)(builder)(freeFn)(freeType)(valueRef)
            else emitRuntimeRcDrop(context)(function_)(i64)(i8)(ptrType)(builder)(freeFn)(freeType)(valueRef)

// The runtime-managed `RcDrop` instruction: a value that may be the empty list is released only
// when present; the release itself is `emitDropCountedValue`.
let emitRuntimeManagedDrop context function_ i64 i8 ptrType builder freeFn freeType closureFnType structuralDropper isClosure mayBeEmpty valueRef =
    if mayBeEmpty == false
    then emitDropCountedValue(context)(function_)(i64)(i8)(ptrType)(builder)(freeFn)(freeType)(closureFnType)(structuralDropper)(isClosure)(valueRef)
    else
        let dropBlock = appendBasicBlock(context)(function_)("rc_drop_present")
        in
            let afterBlock = appendBasicBlock(context)(function_)("rc_drop_after_empty")
            in
                "rc_drop"
                |> rcIsPresent(builder)(i64)(valueRef)
                |> (given (isPresent) -> buildCondBr(builder)(isPresent)(dropBlock)(afterBlock))
                |> (given (_) ->
                    emitRcArm(builder)(dropBlock)(afterBlock)(given (_) -> emitDropCountedValue(context)(function_)(i64)(i8)(ptrType)(builder)(freeFn)(freeType)(closureFnType)(structuralDropper)(isClosure)(valueRef)))
                |> (given (_) -> positionBuilderAtEnd(builder)(afterBlock))
