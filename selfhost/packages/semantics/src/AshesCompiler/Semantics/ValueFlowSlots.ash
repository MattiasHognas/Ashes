// The local slots a value can have been read from or stored into, stage 0's `LocalSlotsReachingTemp`: followed
// backwards from the value through a slot's every store, a cell's every stored field and every
// operand of any other instruction, stopping at an instruction whose value carries no pointer
// into another value.
import Ashes.Collection.Map
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.StateMachineTransform.getDefinedTemps
import AshesCompiler.Semantics.StateMachineTransform.getUsedTemps
export (
    value localSlotsReachingTemp,
    value localSlotsReachingTemps,
    value localSlotsReachingTempsThrough,
    value localSlotsReachingTempsStopping,
)

type ValueFlowIndex =
    | definitions: MapTree(Int, List(IrInstructionKind))
    | slotStores: MapTree(Int, List(Int))
    | cellStores: MapTree(Int, List(Int))

let addFlow (key: Int) (value: a) (flows: MapTree(Int, List(a))) =
    match Ashes.Collection.Map.getInt(key)(flows) with
        | Some(existing) -> Ashes.Collection.Map.setInt(key)(value :: existing)(flows)
        | None -> Ashes.Collection.Map.setInt(key)([value])(flows)

let recursive addDefinitions (temps: List(Int)) (instruction: IrInstructionKind) (definitions: MapTree(Int, List(IrInstructionKind))) =
    match temps with
        | [] -> definitions
        | temp :: rest ->
            definitions
            |> addFlow(temp)(instruction)
            |> addDefinitions(rest)(instruction)

let indexInstruction (index: ValueFlowIndex) (instruction: IrInstructionKind) =
    (let defined =
        addDefinitions(getDefinedTemps(instruction))(instruction)(index.definitions)
    in
        match instruction with
            | StoreLocal(slot, source) -> index with definitions = defined, slotStores = addFlow(slot)(source)(index.slotStores)
            | StoreMemOffset(basePtr, _offset, source) -> index with definitions = defined, cellStores = addFlow(basePtr)(source)(index.cellStores)
            | SetAdtField(pointer, _field, source, _tagless) -> index with definitions = defined, cellStores = addFlow(pointer)(source)(index.cellStores)
            | _ -> index with definitions = defined)

// An instruction whose value holds no pointer into any value it reads: a constant, a comparison,
// arithmetic, a length, or a string it builds fresh.
let carriesNoPointer (instruction: IrInstructionKind) =
    match instruction with
        | LoadConstInt(_, _) -> true
        | LoadConstFloat(_, _) -> true
        | LoadConstBool(_, _) -> true
        | LoadConstStr(_, _) -> true
        | AddInt(_, _, _) -> true
        | SubInt(_, _, _) -> true
        | MulInt(_, _, _) -> true
        | DivInt(_, _, _) -> true
        | DivUInt(_, _, _) -> true
        | AndInt(_, _, _) -> true
        | OrInt(_, _, _) -> true
        | XorInt(_, _, _) -> true
        | ShlInt(_, _, _) -> true
        | ShrInt(_, _, _) -> true
        | AddFloat(_, _, _) -> true
        | SubFloat(_, _, _) -> true
        | MulFloat(_, _, _) -> true
        | DivFloat(_, _, _) -> true
        | CmpIntGt(_, _, _) -> true
        | CmpIntGe(_, _, _) -> true
        | CmpIntLt(_, _, _) -> true
        | CmpIntLe(_, _, _) -> true
        | CmpUIntGt(_, _, _) -> true
        | CmpUIntGe(_, _, _) -> true
        | CmpUIntLt(_, _, _) -> true
        | CmpUIntLe(_, _, _) -> true
        | CmpIntEq(_, _, _) -> true
        | CmpIntNe(_, _, _) -> true
        | CmpFloatGt(_, _, _) -> true
        | CmpFloatGe(_, _, _) -> true
        | CmpFloatLt(_, _, _) -> true
        | CmpFloatLe(_, _, _) -> true
        | CmpFloatEq(_, _, _) -> true
        | CmpFloatNe(_, _, _) -> true
        | CmpStrEq(_, _, _) -> true
        | CmpStrNe(_, _, _) -> true
        | IntToFloat(_, _) -> true
        | FloatToInt(_, _) -> true
        | ConcatStr(_, _, _, _) -> true
        | ConcatStrN(_, _, _) -> true
        | TextByteLength(_, _) -> true
        | TextFromInt(_, _, _) -> true
        | TextFromFloat(_, _, _) -> true
        | TextFormatFloat(_, _, _, _) -> true
        | TextToHex(_, _, _) -> true
        | IsReferenceCounted(_, _) -> true
        | RcIsUnique(_, _) -> true
        | GetAdtTag(_, _) -> true
        | BytesLength(_, _) -> true
        | LoadArgumentOwnership(_) -> true
        | MonotonicMillis(_) -> true
        | _ -> false

let lookupFlows (key: Int) (flows: MapTree(Int, List(a))) =
    match Ashes.Collection.Map.getInt(key)(flows) with
        | Some(values) -> values
        | None -> []

let recursive slotListed (slot: Int) (slots: List(Int)) =
    match slots with
        | [] -> false
        | head :: rest -> head == slot || slotListed(slot)(rest)

// The values that flow into the one a definition gives `temp`, noting a slot it reads. A slot of
// `independent` holds a value with references of its own: what reaches it ends there.
let valueFlowSources (definition: IrInstructionKind) (temp: Int) (index: ValueFlowIndex) (independent: List(Int)) (slots: List(Int)) =
    match definition with
        | LoadLocal(_target, slot) ->
            if slotListed(slot)(independent)
            then (slot :: slots, [])
            else (slot :: slots, lookupFlows(slot)(index.slotStores))
        | Alloc(_, _, _) -> (slots, lookupFlows(temp)(index.cellStores))
        | AllocStack(_, _) -> (slots, lookupFlows(temp)(index.cellStores))
        | AllocAdt(_, _, _, _, _) -> (slots, lookupFlows(temp)(index.cellStores))
        | AllocAdtStack(_, _, _, _) -> (slots, lookupFlows(temp)(index.cellStores))
        | other ->
            if carriesNoPointer(other)
            then (slots, [])
            else (slots, getUsedTemps(other))

let recursive followDefinitions (definitions: List(IrInstructionKind)) (temp: Int) (index: ValueFlowIndex) (independent: List(Int)) (slots: List(Int)) (pending: List(Int)) =
    match definitions with
        | [] -> (slots, pending)
        | definition :: rest ->
            match valueFlowSources(definition)(temp)(index)(independent)(slots) with
                | (nextSlots, sources) ->
                    pending
                    |> Ashes.Collection.List.append(sources)
                    |> followDefinitions(rest)(temp)(index)(independent)(nextSlots)

let recursive walkValueFlow (pending: List(Int)) (visited: MapTree(Int, Bool)) (index: ValueFlowIndex) (independent: List(Int)) (slots: List(Int)) =
    match pending with
        | [] -> (slots, visited)
        | temp :: rest ->
            match Ashes.Collection.Map.getInt(temp)(visited) with
                | Some(_seen) -> walkValueFlow(rest)(visited)(index)(independent)(slots)
                | None ->
                    match followDefinitions(lookupFlows(temp)(index.definitions))(temp)(index)(independent)(slots)(rest) with
                        | (nextSlots, nextPending) ->
                            walkValueFlow(nextPending)(Ashes.Collection.Map.setInt(temp)(true)(visited))(index)(independent)(nextSlots)

let recursive anyVisited (sources: List(Int)) (visited: MapTree(Int, Bool)) =
    match sources with
        | [] -> false
        | source :: rest ->
            match Ashes.Collection.Map.getInt(source)(visited) with
                | Some(_seen) -> true
                | None -> anyVisited(rest)(visited)

// A slot holding one of the values found is reached too, through whichever copy of the pointer
// the result was built from.
let recursive slotsStoringVisited (stores: List((Int, List(Int)))) (visited: MapTree(Int, Bool)) (slots: List(Int)) =
    match stores with
        | [] -> slots
        | (slot, sources) :: rest ->
            if anyVisited(sources)(visited)
            then slotsStoringVisited(rest)(visited)(slot :: slots)
            else slotsStoringVisited(rest)(visited)(slots)

// The slots an element of an arena aggregate is demoted through, stage 0's `ArenaDemotedSlots`:
// the value is stored, and replaced by an independent arena copy when it is reference-counted, so
// what the slot holds is never a part of an owned value. The instructions are most recent first.
let recursive arenaDemotedSlots (instructions: List(IrInstruction)) (slots: List(Int)) =
    match instructions with
        | IrInstruction { instruction = JumpIfFalse(condition, target) } :: afterJump ->
            match afterJump with
                | IrInstruction { instruction = IsReferenceCounted(tested, source) } :: IrInstruction { instruction = StoreLocal(slot, stored) } :: rest ->
                    if condition == tested && source == stored && Ashes.Text.startsWith(target)("rc_arena_demote_done")
                    then arenaDemotedSlots(rest)(slot :: slots)
                    else arenaDemotedSlots(afterJump)(slots)
                | _ -> arenaDemotedSlots(afterJump)(slots)
        | _ :: rest -> arenaDemotedSlots(rest)(slots)
        | [] -> slots

let recursive forgetSlotStores (slots: List(Int)) (stores: MapTree(Int, List(Int))) =
    match slots with
        | [] -> stores
        | slot :: rest ->
            stores
            |> Ashes.Collection.Map.setInt(slot)([])
            |> forgetSlotStores(rest)

let recursive indexReversedInstructions (instructions: List(IrInstruction)) (index: ValueFlowIndex) =
    match instructions with
        | [] -> index
        | IrInstruction { instruction = instruction } :: rest ->
            instruction
            |> indexInstruction(index)
            |> indexReversedInstructions(rest)

let recursive markVisited (temps: List(Int)) (visited: MapTree(Int, Bool)) =
    match temps with
        | [] -> visited
        | temp :: rest ->
            visited
            |> Ashes.Collection.Map.setInt(temp)(true)
            |> markVisited(rest)

// The instructions are the function's own, most recent first; the index keeps no order. The slots of
// `independent` hold values with references of their own (stage 0's `IndependentOwnedSlots`): the walk
// does not follow their stores. The values of `stops` are reached but not followed: a call result
// that holds nothing of its arguments.
let localSlotsReachingTempsStopping (temps: List(Int)) (independent: List(Int)) (stops: List(Int)) (reversedInstructions: List(IrInstruction)) =
    (let indexed = indexReversedInstructions(reversedInstructions)(ValueFlowIndex(definitions = Ashes.Collection.Map.empty, slotStores = Ashes.Collection.Map.empty, cellStores = Ashes.Collection.Map.empty))
    in
        let index =
            indexed with slotStores = forgetSlotStores(arenaDemotedSlots(reversedInstructions)([]))(indexed.slotStores)
        in
            match walkValueFlow(temps)(markVisited(stops)(Ashes.Collection.Map.empty))(index)(independent)([]) with
                | (slots, visited) ->
                    slotsStoringVisited(Ashes.Collection.Map.toList(index.slotStores))(visited)(slots))

let localSlotsReachingTempsThrough (temps: List(Int)) (independent: List(Int)) (reversedInstructions: List(IrInstruction)) = localSlotsReachingTempsStopping(temps)(independent)([])(reversedInstructions)

let localSlotsReachingTemps (temps: List(Int)) (reversedInstructions: List(IrInstruction)) = localSlotsReachingTempsThrough(temps)([])(reversedInstructions)

let localSlotsReachingTemp (temp: Int) (reversedInstructions: List(IrInstruction)) = localSlotsReachingTemps([temp])(reversedInstructions)
