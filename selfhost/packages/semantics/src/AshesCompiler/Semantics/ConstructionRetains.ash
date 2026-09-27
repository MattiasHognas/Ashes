// The retains an arena cell's construction made, read back from the instructions that built it
// (stage 0's `Lowering.ConstructionRetains`). A loop's successor or result cell holds a retain for
// each loop-parameter read it stores. A copy that releases the dying cell's children releases
// exactly the children its construction retained; a copy a normalizer makes instead borrows the
// children, and the construction's retains of loop-parameter reads are withdrawn. Every function
// here takes the instructions of the function being lowered, most recent first.
import Ashes.Collection.List.append
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.StateMachineTransform.getUsedTemps
export (
    value constructionFieldSource,
    value constructionWordSource,
    value isUnrealizedConstructionRetain,
    value withdrawLoopParameterRetainsOfCopiedCells,
)

// The temp the construction of `cellTemp` stored into field `fieldIndex`.
let recursive constructionFieldSource (cellTemp: Int) (fieldIndex: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = SetAdtField(pointer, index, source, _tagless) } :: rest ->
            if pointer == cellTemp && index == fieldIndex
            then Some(source)
            else constructionFieldSource(cellTemp)(fieldIndex)(rest)
        | _instruction :: rest -> constructionFieldSource(cellTemp)(fieldIndex)(rest)

// The temp the construction of the list or tuple cell `cellTemp` stored at `offsetBytes`.
let recursive constructionWordSource (cellTemp: Int) (offsetBytes: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = StoreMemOffset(basePointer, offset, source) } :: rest ->
            if basePointer == cellTemp && offset == offsetBytes
            then Some(source)
            else constructionWordSource(cellTemp)(offsetBytes)(rest)
        | _instruction :: rest -> constructionWordSource(cellTemp)(offsetBytes)(rest)

// A pending constructor field retain whose reload heads `instructions` (stage 0's
// `EmitPendingRuntimeManagedConstructorFieldRetain`): its flag temp, the value the flag was
// resolved to, and the value the retain reads.
let pendingFieldRetainFlag (instructions: List(IrInstruction)) =
    match instructions with
        | IrInstruction { instruction = LoadLocal(_reload, slot) } :: IrInstruction { instruction = Label(doneLabel) } :: IrInstruction { instruction = StoreLocal(retainedSlot, retainedSource) } :: IrInstruction { instruction = RcDup(retained, source, true, _mayBeEmpty) } :: IrInstruction { instruction = JumpIfFalse(condition, skipLabel) } :: IrInstruction { instruction = StoreLocal(borrowedSlot, _borrowed) } :: IrInstruction { instruction = LoadConstInt(flag, value) } :: _rest ->
            if retainedSlot == slot && borrowedSlot == slot && retainedSource == retained && skipLabel == doneLabel && condition == flag
            then Some((flag, value, source))
            else None
        | _ -> None

let definesRetainOrReload (temp: Int) (instruction: IrInstruction) =
    match instruction with
        | IrInstruction { instruction = RcDup(target, _source, _runtimeManaged, _mayBeEmpty) } -> target == temp
        | IrInstruction { instruction = LoadLocal(target, _slot) } -> target == temp
        | _ -> false

// The instructions from the retain or reload that defines `temp` on.
let recursive retainDefinitionOf (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | instruction :: rest ->
            if definesRetainOrReload(temp)(instruction)
            then Some(instructions)
            else retainDefinitionOf(temp)(rest)

// Whether `storedTemp` is a retain the construction emitted that finalization left without
// effect: a retain marker never made real, or a pending field retain whose flag was zeroed.
let isUnrealizedConstructionRetain (storedTemp: Int) (instructions: List(IrInstruction)) =
    match retainDefinitionOf(storedTemp)(instructions) with
        | Some(IrInstruction { instruction = RcDup(_target, _source, runtimeManaged, _mayBeEmpty) } :: _rest) -> runtimeManaged == false
        | Some(definition) ->
            match pendingFieldRetainFlag(definition) with
                | Some((_flag, value, _source)) -> value == 0
                | None -> false
        | None -> false

let definesRead (temp: Int) (instruction: IrInstruction) =
    match instruction with
        | IrInstruction { instruction = Borrow(target, _source) } -> target == temp
        | IrInstruction { instruction = LoadLocal(target, _slot) } -> target == temp
        | IrInstruction { instruction = GetAdtField(target, _pointer, _index, _tagless) } -> target == temp
        | IrInstruction { instruction = CopyOutArena(target, _source, _sizeBytes, _runtimeManaged, _purpose, Some(_deferred)) } -> target == temp
        | _ -> false

let recursive readDefinitionOf (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | (IrInstruction { instruction = kind } as instruction) :: rest ->
            if definesRead(temp)(instruction)
            then Some(kind)
            else readDefinitionOf(temp)(rest)

// Whether `temp` reads one of `slots` or a field, at any depth, of one: a value that loop
// parameter keeps alive. A provisional copy of a tuple element whose type was unresolved when
// the tuple was built (stage 0's deferred materialization, which this lowering does not emit)
// is a plain read of its source.
let recursive readsLoopParameter (temp: Int) (slots: List(Int)) (hops: Int) (instructions: List(IrInstruction)) =
    if hops >= 16
    then false
    else
        match readDefinitionOf(temp)(instructions) with
            | Some(LoadLocal(_target, slot)) -> containsInt(slot)(slots)
            | Some(Borrow(_target, source)) -> readsLoopParameter(source)(slots)(hops + 1)(instructions)
            | Some(CopyOutArena(_target, source, _sizeBytes, _runtimeManaged, _purpose, Some(_deferred))) -> readsLoopParameter(source)(slots)(hops + 1)(instructions)
            | Some(GetAdtField(_target, pointer, _index, _tagless)) -> readsLoopParameter(pointer)(slots)(hops + 1)(instructions)
            | _ -> false

let recursive isArenaCell (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = AllocAdt(target, _tag, _fieldCount, false, _tagless) } :: rest -> target == temp || isArenaCell(temp)(rest)
        | IrInstruction { instruction = Alloc(target, _sizeBytes, false) } :: rest -> target == temp || isArenaCell(temp)(rest)
        | IrInstruction { instruction = AllocAdtStack(target, _tag, _fieldCount, _tagless) } :: rest -> target == temp || isArenaCell(temp)(rest)
        | _instruction :: rest -> isArenaCell(temp)(rest)

// `instructions` with the retain defining `temp` made an identity.
let recursive withdrawRetainDefining (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = RcDup(target, source, _runtimeManaged, _mayBeEmpty) } as instruction) :: rest ->
            if target == temp
            then (instruction with instruction = RcDup(target)(source)(false)(false)) :: rest
            else instruction :: withdrawRetainDefining(temp)(rest)
        | instruction :: rest -> instruction :: withdrawRetainDefining(temp)(rest)

// `instructions` with the flag `flag` defined as zero.
let recursive zeroFlagDefining (flag: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = LoadConstInt(target, _value) } as instruction) :: rest ->
            if target == flag
            then (instruction with instruction = LoadConstInt(target)(0)) :: rest
            else instruction :: zeroFlagDefining(flag)(rest)
        | instruction :: rest -> instruction :: zeroFlagDefining(flag)(rest)

// How many instructions read the retain or reload `temp`: every reader follows the definition, so
// the count stops there.
let recursive countTempUses (temp: Int) (instructions: List(IrInstruction)) (count: Int) =
    match instructions with
        | [] -> count
        | (IrInstruction { instruction = kind } as instruction) :: rest ->
            if definesRetainOrReload(temp)(instruction)
            then count
            else
                if kind
                |> getUsedTemps
                |> containsInt(temp)
                then countTempUses(temp)(rest)(count + 1)
                else countTempUses(temp)(rest)(count)

// Withdraws the retain `storedTemp` names when it reads a parameter of `heldSlots` and the cell
// storing it is its only reader (stage 0's `WithdrawLoopParameterRetain`); true beside the
// instructions when `storedTemp` was a retain at all.
let withdrawLoopParameterRetain (storedTemp: Int) (heldSlots: List(Int)) (instructions: List(IrInstruction)) =
    match retainDefinitionOf(storedTemp)(instructions) with
        | None -> (instructions, false)
        | Some(IrInstruction { instruction = RcDup(_target, source, runtimeManaged, _mayBeEmpty) } :: _rest) ->
            if runtimeManaged && readsLoopParameter(source)(heldSlots)(0)(instructions) && countTempUses(storedTemp)(instructions)(0) == 1
            then (withdrawRetainDefining(storedTemp)(instructions), true)
            else (instructions, true)
        | Some(definition) ->
            match pendingFieldRetainFlag(definition) with
                | None -> (instructions, false)
                | Some((flag, _value, source)) ->
                    if readsLoopParameter(source)(heldSlots)(0)(instructions) && countTempUses(storedTemp)(instructions)(0) == 1
                    then (zeroFlagDefining(flag)(instructions), true)
                    else (instructions, true)

// The values the construction of `cellTemp` stored.
let recursive storedValuesOf (cellTemp: Int) (instructions: List(IrInstruction)) (values: List(Int)) =
    match instructions with
        | [] -> values
        | IrInstruction { instruction = SetAdtField(pointer, _index, source, _tagless) } :: rest ->
            if pointer == cellTemp
            then storedValuesOf(cellTemp)(rest)(source :: values)
            else storedValuesOf(cellTemp)(rest)(values)
        | IrInstruction { instruction = StoreMemOffset(basePointer, _offset, source) } :: rest ->
            if basePointer == cellTemp
            then storedValuesOf(cellTemp)(rest)(source :: values)
            else storedValuesOf(cellTemp)(rest)(values)
        | _instruction :: rest -> storedValuesOf(cellTemp)(rest)(values)

let recursive withdrawCellRetains (cellTemp: Int) (slots: List(Int)) (visited: List(Int)) (instructions: List(IrInstruction)) =
    if containsInt(cellTemp)(visited)
    then (instructions, visited)
    else
        withdrawStoredRetains(storedValuesOf(cellTemp)(instructions)([]))(slots)(cellTemp :: visited)(instructions)
and withdrawStoredRetains (values: List(Int)) (slots: List(Int)) (visited: List(Int)) (instructions: List(IrInstruction)) =
    match values with
        | [] -> (instructions, visited)
        | value :: rest ->
            match withdrawLoopParameterRetain(value)(slots)(instructions) with
                | (withdrawn, true) -> withdrawStoredRetains(rest)(slots)(visited)(withdrawn)
                | (unchanged, false) ->
                    if isArenaCell(value)(unchanged)
                    then
                        match withdrawCellRetains(value)(slots)(visited)(unchanged) with
                            | (nested, nestedVisited) -> withdrawStoredRetains(rest)(slots)(nestedVisited)(nested)
                    else withdrawStoredRetains(rest)(slots)(visited)(unchanged)

let definesCarrier (temp: Int) (instruction: IrInstruction) =
    match instruction with
        | IrInstruction { instruction = Borrow(target, _source) } -> target == temp
        | IrInstruction { instruction = LoadLocal(target, _slot) } -> target == temp
        | _ -> false

let recursive carrierDefinitionOf (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | (IrInstruction { instruction = kind } as instruction) :: rest ->
            if definesCarrier(temp)(instruction)
            then Some(kind)
            else carrierDefinitionOf(temp)(rest)

let recursive storeSourcesOf (slot: Int) (instructions: List(IrInstruction)) (sources: List(Int)) =
    match instructions with
        | [] -> sources
        | IrInstruction { instruction = StoreLocal(storedSlot, source) } :: rest ->
            if storedSlot == slot
            then storeSourcesOf(slot)(rest)(source :: sources)
            else storeSourcesOf(slot)(rest)(sources)
        | _instruction :: rest -> storeSourcesOf(slot)(rest)(sources)

// The arena cells built by this function whose value reaches the pending temps: a cell itself, or
// one a borrow or a local slot (a `let` binding, the join of a `match`) carries there. A loop
// parameter's own slot is not followed, since its stores are other iterations'.
let recursive arenaCellsCarrying (pending: List(Int)) (visited: List(Int)) (slots: List(Int)) (instructions: List(IrInstruction)) (cells: List(Int)) =
    match pending with
        | [] -> cells
        | temp :: rest ->
            if containsInt(temp)(visited)
            then arenaCellsCarrying(rest)(visited)(slots)(instructions)(cells)
            else
                if isArenaCell(temp)(instructions)
                then arenaCellsCarrying(rest)(temp :: visited)(slots)(instructions)(temp :: cells)
                else
                    match carrierDefinitionOf(temp)(instructions) with
                        | Some(Borrow(_target, source)) -> arenaCellsCarrying(source :: rest)(temp :: visited)(slots)(instructions)(cells)
                        | Some(LoadLocal(_target, slot)) ->
                            if containsInt(slot)(slots)
                            then arenaCellsCarrying(rest)(temp :: visited)(slots)(instructions)(cells)
                            else
                                arenaCellsCarrying(append(storeSourcesOf(slot)(instructions)([]))(rest))(temp :: visited)(slots)(instructions)(cells)
                        | _ -> arenaCellsCarrying(rest)(temp :: visited)(slots)(instructions)(cells)

let recursive withdrawCells (cells: List(Int)) (slots: List(Int)) (visited: List(Int)) (instructions: List(IrInstruction)) =
    match cells with
        | [] -> instructions
        | cell :: rest ->
            match withdrawCellRetains(cell)(slots)(visited)(instructions) with
                | (withdrawn, nextVisited) -> withdrawCells(rest)(slots)(nextVisited)(withdrawn)

let recursive zeroConstantTemps (instructions: List(IrInstruction)) (temps: List(Int)) =
    match instructions with
        | [] -> temps
        | IrInstruction { instruction = LoadConstInt(target, 0) } :: rest -> zeroConstantTemps(rest)(target :: temps)
        | _instruction :: rest -> zeroConstantTemps(rest)(temps)

let recursive clearedSlotsOf (instructions: List(IrInstruction)) (zeroTemps: List(Int)) (slots: List(Int)) =
    match instructions with
        | [] -> slots
        | IrInstruction { instruction = StoreLocal(slot, source) } :: rest ->
            if containsInt(source)(zeroTemps)
            then clearedSlotsOf(rest)(zeroTemps)(slot :: slots)
            else clearedSlotsOf(rest)(zeroTemps)(slots)
        | _instruction :: rest -> clearedSlotsOf(rest)(zeroTemps)(slots)

let recursive heldParameterSlots (parameters: List((Int, Int))) (cleared: List(Int)) =
    match parameters with
        | [] -> []
        | (slot, activeSlot) :: rest ->
            if activeSlot < 0 || containsInt(activeSlot)(cleared) == false
            then slot :: heldParameterSlots(rest)(cleared)
            else heldParameterSlots(rest)(cleared)

// Stage 0's `ParametersHeldToTheEnd`: the parameter slots no path releases ahead of the loop's end.
// A parameter a self call hands on as the root of a pattern-bound successor is released there and
// its active flag cleared.
let parametersHeldToTheEnd (parameters: List((Int, Int))) (instructions: List(IrInstruction)) =
    []
    |> zeroConstantTemps(instructions)
    |> (given (zeroTemps: List(Int)) -> clearedSlotsOf(instructions)(zeroTemps)([]))
    |> heldParameterSlots(parameters)

let recursive parameterSlotsOf (parameters: List((Int, Int))) =
    match parameters with
        | [] -> []
        | (slot, _activeSlot) :: rest -> slot :: parameterSlotsOf(rest)

// Stage 0's `WithdrawLoopParameterRetainsOfCopiedCells`: the retains of loop-parameter reads that
// the arena cells reaching `valueTemp` were built with are withdrawn, for a copy that borrows
// those cells' children, since the copy takes its own reference to every reference-counted child
// before the loop releases its parameters. Each parameter is its slot beside its active flag, or
// -1; a retain is withdrawn only when the cell holds its sole reference and the parameter it reads
// is not released ahead of the loop's end.
let withdrawLoopParameterRetainsOfCopiedCells (valueTemp: Int) (parameters: List((Int, Int))) (instructions: List(IrInstruction)) =
    match parameters with
        | [] -> instructions
        | _parameters ->
            withdrawCells(arenaCellsCarrying([valueTemp])([])(parameterSlotsOf(parameters))(instructions)([]))(parametersHeldToTheEnd(parameters)(instructions))([])(instructions)
