// Describes task frame slots and coroutine representation records for lowered state machines.
//
// Invariants:
// - Capture slots start at taskStructLayout.headerSize and occupy consecutive eight-byte words.
// - Saved temps follow captures, and saved locals follow saved temps in deterministic offset order.
// - Saved locals that alias a saved temp word are demoted to avoid duplicate drops at teardown.
// - State struct sizes and frame slots provide the metadata required for runtime teardown.

import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.StateMachineTransform
export (
    type CoroutineFrameSlotKind(..),
    type CoroutineFrameSlotOwnership(..),
    type CoroutineFrameSlotReason(..),
    type CoroutineFrameSlot(..),
    type CoroutineRepresentationRecord(..),
    value buildCoroutineFrameSlots,
    value savedLocalAliasesSavedTemp,
    value buildCoroutineRepresentationRecord,
)

type CoroutineFrameSlotKind =
    | CaptureFrameSlot
    | SavedTempFrameSlot
    | SavedLocalFrameSlot
    deriving {Eq, Show}

type CoroutineFrameSlotOwnership =
    | FrameOwnedRuntimeRc
    | FrameOwnedResource
    | BorrowedFrameSlot
    | NotOwnedFrameSlot
    | ConservativeUnknownFrameSlot
    deriving {Eq, Show}

type CoroutineFrameSlotReason =
    | CaptureRetainedReason
    | SavedTempProducedReason
    | SavedLocalStoredReason
    | AliasesOwnedFrameWordReason
    | UnownedTemporaryReason
    | NoOwnershipFactReason
    deriving {Eq, Show}

type CoroutineFrameSlot =
    | offsetBytes: Int
    | kind: CoroutineFrameSlotKind
    | sourceIndex: Int
    | ownership: CoroutineFrameSlotOwnership
    | typeName: Maybe(Str)
    | reason: CoroutineFrameSlotReason
    deriving {Eq, Show}

type CoroutineRepresentationRecord =
    | coroutineLabel: Str
    | stateCount: Int
    | stateStructSize: Int
    | captureCount: Int
    | frameSlots: List(CoroutineFrameSlot)
    deriving {Eq, Show}

let recursive listLength list =
    match list with
        | [] -> 0
        | _ :: tail -> 1 + listLength(tail)

let recursive lookupOffset key map =
    match map with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupOffset(key)(tail)

let recursive checkStoreAliasesSavedTemp instructions localSlot savedTempOffsets =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = inst } :: tail ->
            match inst with
                | StoreLocal(slot, source) ->
                    if slot == localSlot
                    then
                        match lookupOffset(source)(savedTempOffsets) with
                            | Some(_) -> true
                            | None -> checkStoreAliasesSavedTemp(tail)(localSlot)(savedTempOffsets)
                    else checkStoreAliasesSavedTemp(tail)(localSlot)(savedTempOffsets)
                | _ -> checkStoreAliasesSavedTemp(tail)(localSlot)(savedTempOffsets)

let savedLocalAliasesSavedTemp localSlot bodyInstructions savedTempOffsets = checkStoreAliasesSavedTemp(bodyInstructions)(localSlot)(savedTempOffsets)

let recursive buildCaptureSlots captures index =
    match captures with
        | [] -> []
        | _ :: tail ->
            let offset = 160 + index * 8
            in
                let slot =
                    CoroutineFrameSlot(
                        offsetBytes = offset,
                        kind = CaptureFrameSlot,
                        sourceIndex = index,
                        ownership = FrameOwnedRuntimeRc,
                        typeName = None,
                        reason = CaptureRetainedReason
                    )
                in slot :: buildCaptureSlots(tail)(index + 1)

let recursive buildSavedTempSlots savedTempOffsets =
    match savedTempOffsets with
        | [] -> []
        | (temp, offset) :: tail ->
            let slot =
                CoroutineFrameSlot(
                    offsetBytes = offset,
                    kind = SavedTempFrameSlot,
                    sourceIndex = temp,
                    ownership = FrameOwnedRuntimeRc,
                    typeName = None,
                    reason = SavedTempProducedReason
                )
            in slot :: buildSavedTempSlots(tail)

let recursive buildSavedLocalSlots savedLocalOffsets bodyInstructions savedTempOffsets =
    match savedLocalOffsets with
        | [] -> []
        | (local, offset) :: tail ->
            let isAlias = savedLocalAliasesSavedTemp(local)(bodyInstructions)(savedTempOffsets)
            in
                match if isAlias
                then (NotOwnedFrameSlot, AliasesOwnedFrameWordReason)
                else (FrameOwnedRuntimeRc, SavedLocalStoredReason) with
                    | (ownership, reason) ->
                        let slot =
                            CoroutineFrameSlot(
                                offsetBytes = offset,
                                kind = SavedLocalFrameSlot,
                                sourceIndex = local,
                                ownership = ownership,
                                typeName = None,
                                reason = reason
                            )
                        in slot :: buildSavedLocalSlots(tail)(bodyInstructions)(savedTempOffsets)

let buildCoroutineFrameSlots transformResult captureTemps bodyInstructions =
    match transformResult with
        | StateMachineResult { savedTempOffsets = savedTempOffsets, savedLocalOffsets = savedLocalOffsets } ->
            let captureSlots = buildCaptureSlots(captureTemps)(0)
            in
                let tempSlots = buildSavedTempSlots(savedTempOffsets)
                in
                    let localSlots = buildSavedLocalSlots(savedLocalOffsets)(bodyInstructions)(savedTempOffsets)
                    in append(captureSlots)(append(tempSlots)(localSlots))

let buildCoroutineRepresentationRecord coroutineLabel transformResult captureTemps bodyInstructions =
    match transformResult with
        | StateMachineResult { stateCount = stateCount, stateStructSize = stateStructSize } ->
            let frameSlots = buildCoroutineFrameSlots(transformResult)(captureTemps)(bodyInstructions)
            in
                CoroutineRepresentationRecord(
                    coroutineLabel = coroutineLabel,
                    stateCount = stateCount,
                    stateStructSize = stateStructSize,
                    captureCount = listLength(captureTemps),
                    frameSlots = frameSlots
                )
