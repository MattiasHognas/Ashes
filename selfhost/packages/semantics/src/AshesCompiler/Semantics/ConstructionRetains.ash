// The retains an arena cell's construction made, read back from the instructions that built it
// (stage 0's `Lowering.ConstructionRetains`). A loop's successor or result cell holds a retain for
// each loop-parameter read it stores. A copy that releases the dying cell's children releases
// exactly the children its construction retained; a copy a normalizer makes instead borrows the
// children, and the construction's retains of loop-parameter reads are withdrawn. Every function
// here takes the instructions of the function being lowered, most recent first.
import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrControlFlowGraph.containsInt
import AshesCompiler.Semantics.StateMachineTransform.getDefinedTemps
import AshesCompiler.Semantics.StateMachineTransform.getUsedTemps
export (
    value arenaCellsCarrying,
    value constructionFieldSource,
    value constructionWordSource,
    value countTempUses,
    value isArenaCell,
    value isBareFieldRead,
    value isBorrowedRead,
    value isUnrealizedConstructionRetain,
    value leaveWithdrawnPartDuplicateToItsCell,
    value retainsHeldPastTheCopy,
    value storedValuesOf,
    value withdrawLoopParameterRetainsOfCopiedCells,
    value withdrawLoopResultRetainsOfCopiedCells,
    value withdrawLetCopiedCellTailDuplicates,
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

// The instructions from the one defining `temp` on.
let recursive definitionFrom (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = kind } :: rest ->
            if kind
            |> getDefinedTemps
            |> containsInt(temp)
            then Some(instructions)
            else definitionFrom(temp)(rest)

// The sources of every store into `slot` among `instructions`, each with the instructions
// before that store.
let recursive slotStores (slot: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | IrInstruction { instruction = StoreLocal(stored, source) } :: rest ->
            if stored == slot
            then (source, rest) :: slotStores(slot)(rest)
            else slotStores(slot)(rest)
        | _instruction :: rest -> slotStores(slot)(rest)

let isFieldReadDefinition (definition: Maybe(List(IrInstruction))) =
    match definition with
        | Some(IrInstruction { instruction = GetAdtField(_target, _pointer, _index, _tagless) } :: _rest) -> true
        | Some(IrInstruction { instruction = LoadMemOffset(_target, _pointer, _offset) } :: _rest) -> true
        | _ -> false

// Stage 0's `IsBareFieldRead`: `temp` reads a field of another value, directly, through a
// borrow, or through a local stored once from such a read; a cell storing it took no reference.
let recursive isBareFieldRead (temp: Int) (instructions: List(IrInstruction)) =
    match definitionFrom(temp)(instructions) with
        | Some(IrInstruction { instruction = Borrow(_target, source) } :: _rest) -> isBareFieldRead(source)(instructions)
        | Some(IrInstruction { instruction = LoadLocal(_target, slot) } :: earlier) ->
            match slotStores(slot)(earlier) with
                | (source, beforeStore) :: [] ->
                    beforeStore
                    |> definitionFrom(source)
                    |> isFieldReadDefinition
                | _ -> false
        | definition -> isFieldReadDefinition(definition)

// Whether `temp` is a borrowed read: the construction storing it took no reference of its own.
let isBorrowedRead (temp: Int) (instructions: List(IrInstruction)) =
    match readDefinitionOf(temp)(instructions) with
        | Some(Borrow(_target, _source)) -> true
        | _ -> false

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
// storing it is its only reader (stage 0's `WithdrawLoopParameterRetain`), as is one of
// `withdrawable`; true beside the instructions when `storedTemp` was a retain at all.
let withdrawLoopParameterRetain (storedTemp: Int) (heldSlots: List(Int)) (withdrawable: List(Int)) (instructions: List(IrInstruction)) =
    match retainDefinitionOf(storedTemp)(instructions) with
        | None -> (instructions, false)
        | Some(IrInstruction { instruction = RcDup(_target, source, runtimeManaged, _mayBeEmpty) } :: _rest) ->
            if runtimeManaged && (readsLoopParameter(source)(heldSlots)(0)(instructions) || containsInt(storedTemp)(withdrawable)) && countTempUses(storedTemp)(instructions)(0) == 1
            then (withdrawRetainDefining(storedTemp)(instructions), true)
            else (instructions, true)
        | Some(definition) ->
            match pendingFieldRetainFlag(definition) with
                | None -> (instructions, false)
                | Some((flag, _value, source)) ->
                    if readsLoopParameter(source)(heldSlots)(0)(instructions) && countTempUses(storedTemp)(instructions)(0) == 1
                    then (zeroFlagDefining(flag)(instructions), true)
                    else (instructions, true)

let recursive loadsSlot (slot: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = LoadLocal(_target, loaded) } :: rest -> loaded == slot || loadsSlot(slot)(rest)
        | _instruction :: rest -> loadsSlot(slot)(rest)

// The value the most recent retain defining `temp` reads.
let recursive retainedSourceOf (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = RcDup(target, source, _runtimeManaged, _mayBeEmpty) } :: rest ->
            if target == temp
            then Some(source)
            else retainedSourceOf(temp)(rest)
        | _instruction :: rest -> retainedSourceOf(temp)(rest)

let recursive storeBorrowedValueInto (slot: Int) (all: List(IrInstruction)) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> []
        | (IrInstruction { instruction = StoreLocal(stored, source) } as instruction) :: rest ->
            if stored == slot
            then
                match retainedSourceOf(source)(all) with
                    | Some(borrowed) -> (instruction with instruction = StoreLocal(stored)(borrowed)) :: storeBorrowedValueInto(slot)(all)(rest)
                    | None -> instruction :: storeBorrowedValueInto(slot)(all)(rest)
            else instruction :: storeBorrowedValueInto(slot)(all)(rest)
        | instruction :: rest -> instruction :: storeBorrowedValueInto(slot)(all)(rest)

// A withdrawn part duplicate's slot is never read, yet its store still reads the duplicate, and
// `withdrawLoopParameterRetain` acts only on a retain the cell alone reads. The store takes the
// borrowed value instead, in place, which leaves the cell the duplicate's only reader (stage 0's
// `LeaveWithdrawnPartDuplicateToItsCell`). A slot some path does read keeps its store.
let leaveWithdrawnPartDuplicateToItsCell (slot: Int) (instructions: List(IrInstruction)) =
    if loadsSlot(slot)(instructions)
    then instructions
    else storeBorrowedValueInto(slot)(instructions)(instructions)

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

// The arena cells reaching a stored value that is neither a retain nor a cell itself, through a
// borrow or a `let` binding's slot (stage 0's `LetCarriedCellRetainsEnabled` branch of
// `WithdrawLoopParameterRetainsOfCell`): the copy walks such a cell as it walks one built in place.
// Without carrier slots (the walk of a cons head) nothing is followed.
let carriedArenaCells (value: Int) (carriers: Maybe(List(Int))) (instructions: List(IrInstruction)) =
    match carriers with
        | Some(slots) -> arenaCellsCarrying([value])([])(slots)(instructions)([])
        | None -> []

let recursive withdrawCellRetains (cellTemp: Int) (slots: List(Int)) (withdrawable: List(Int)) (carriers: Maybe(List(Int))) (visited: List(Int)) (instructions: List(IrInstruction)) =
    if containsInt(cellTemp)(visited)
    then (instructions, visited)
    else
        withdrawStoredRetains(storedValuesOf(cellTemp)(instructions)([]))(slots)(withdrawable)(carriers)(cellTemp :: visited)(instructions)
and withdrawStoredRetains (values: List(Int)) (slots: List(Int)) (withdrawable: List(Int)) (carriers: Maybe(List(Int))) (visited: List(Int)) (instructions: List(IrInstruction)) =
    match values with
        | [] -> (instructions, visited)
        | value :: rest ->
            match withdrawLoopParameterRetain(value)(slots)(withdrawable)(instructions) with
                | (withdrawn, true) -> withdrawStoredRetains(rest)(slots)(withdrawable)(carriers)(visited)(withdrawn)
                | (unchanged, false) ->
                    if isArenaCell(value)(unchanged)
                    then
                        match withdrawCellRetains(value)(slots)(withdrawable)(carriers)(visited)(unchanged) with
                            | (nested, nestedVisited) -> withdrawStoredRetains(rest)(slots)(withdrawable)(carriers)(nestedVisited)(nested)
                    else
                        match withdrawCarriedCells(carriedArenaCells(value)(carriers)(unchanged))(slots)(withdrawable)(carriers)(visited)(unchanged) with
                            | (nested, nestedVisited) -> withdrawStoredRetains(rest)(slots)(withdrawable)(carriers)(nestedVisited)(nested)
and withdrawCarriedCells (cells: List(Int)) (slots: List(Int)) (withdrawable: List(Int)) (carriers: Maybe(List(Int))) (visited: List(Int)) (instructions: List(IrInstruction)) =
    match cells with
        | [] -> (instructions, visited)
        | cell :: rest ->
            match withdrawCellRetains(cell)(slots)(withdrawable)(carriers)(visited)(instructions) with
                | (nested, nestedVisited) -> withdrawCarriedCells(rest)(slots)(withdrawable)(carriers)(nestedVisited)(nested)

// The tag of the arena cell `cellTemp`, allocated as a constructor's.
let recursive arenaCellTag (cellTemp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = AllocAdt(target, tag, _fieldCount, _runtimeManaged, _tagless) } :: rest ->
            if target == cellTemp
            then Some(tag)
            else arenaCellTag(cellTemp)(rest)
        | _instruction :: rest -> arenaCellTag(cellTemp)(rest)

// Whether `storedTemp` is a real retain the cell storing it holds the only reference to.
let isSoleHeldRetain (storedTemp: Int) (instructions: List(IrInstruction)) =
    match retainDefinitionOf(storedTemp)(instructions) with
        | Some(IrInstruction { instruction = RcDup(_target, _source, true, _mayBeEmpty) } :: _rest) -> countTempUses(storedTemp)(instructions)(0) == 1
        | _ -> false

// The tag of `temp` when it is an arena constructor cell not yet visited.
let unvisitedArenaCellTag (temp: Int) (visited: List(Int)) (instructions: List(IrInstruction)) =
    if containsInt(temp)(visited) == false && isArenaCell(temp)(instructions)
    then arenaCellTag(temp)(instructions)
    else None

// The field retains of `cellTemp`, each as its temp beside its field path from the outermost cell
// (each step a cell's tag and a field's index), in field order, with the cells visited. A
// constructor cell nested in the cell as a field (`Node(seen, Item(name))`) is copied by the same
// normalizer, so its retains are collected too.
let recursive heldFieldRetains (cellTemp: Int) (path: List((Int, Int))) (tag: Int) (all: List(IrInstruction)) (instructions: List(IrInstruction)) (found: (List((Int, List((Int, Int)))), List(Int))) =
    match (instructions, found) with
        | ([], _) -> found
        | (IrInstruction { instruction = SetAdtField(pointer, index, source, _tagless) } :: rest, (retains, visited)) ->
            if pointer != cellTemp
            then heldFieldRetains(cellTemp)(path)(tag)(all)(rest)(found)
            else
                if isSoleHeldRetain(source)(all)
                then heldFieldRetains(cellTemp)(path)(tag)(all)(rest)(((source, append(path)([(tag, index)])) :: retains, visited))
                else
                    match unvisitedArenaCellTag(source)(visited)(all) with
                        | Some(nestedTag) ->
                            (retains, source :: visited)
                            |> heldFieldRetains(source)(append(path)([(tag, index)]))(nestedTag)(all)(all)
                            |> heldFieldRetains(cellTemp)(path)(tag)(all)(rest)
                        | None -> heldFieldRetains(cellTemp)(path)(tag)(all)(rest)(found)
        | (_instruction :: rest, _) -> heldFieldRetains(cellTemp)(path)(tag)(all)(rest)(found)

let recursive heldRetainsOfCells (cells: List(Int)) (instructions: List(IrInstruction)) (found: (List((Int, List((Int, Int)))), List(Int))) =
    match (cells, found) with
        | ([], _) -> found
        | (cell :: rest, (retains, visited)) ->
            match arenaCellTag(cell)(instructions) with
                | Some(tag) ->
                    if containsInt(cell)(visited)
                    then heldRetainsOfCells(rest)(instructions)(found)
                    else
                        ([], cell :: visited)
                        |> heldFieldRetains(cell)([])(tag)(instructions)(instructions)
                        |> (given (cellFound: (List((Int, List((Int, Int)))), List(Int))) ->
                            match cellFound with
                                | (cellRetains, cellVisited) -> heldRetainsOfCells(rest)(instructions)((append(retains)(cellRetains), cellVisited)))
                | None -> heldRetainsOfCells(rest)(instructions)(found)

// Stage 0's `RetainsHeldPastTheCopy`: the retains the arena constructor cells reaching `valueTemp`
// still hold once the loop-parameter reads among them are withdrawn, each as its temp beside its
// field path. A head matched out of a parameter the loop releases before the back edge is kept
// alive by the cell until the copy takes its own reference, and is released right after that copy,
// since the arena cell never releases anything.
let retainsHeldPastTheCopy (valueTemp: Int) (slots: List(Int)) (instructions: List(IrInstruction)) =
    match heldRetainsOfCells([]
    |> arenaCellsCarrying([valueTemp])([])(slots)(instructions)
    |> reverse)(instructions)(([], [])) with
        | (retains, _visited) -> retains

let recursive withdrawCells (cells: List(Int)) (slots: List(Int)) (withdrawable: List(Int)) (carriers: Maybe(List(Int))) (visited: List(Int)) (instructions: List(IrInstruction)) =
    match cells with
        | [] -> instructions
        | cell :: rest ->
            match withdrawCellRetains(cell)(slots)(withdrawable)(carriers)(visited)(instructions) with
                | (withdrawn, nextVisited) -> withdrawCells(rest)(slots)(withdrawable)(carriers)(nextVisited)(withdrawn)

let recursive zeroConstantTemps (transientClears: List(Int)) (instructions: List(IrInstruction)) (temps: List(Int)) =
    match instructions with
        | [] -> temps
        | IrInstruction { instruction = LoadConstInt(target, 0) } :: rest ->
            if containsInt(target)(transientClears)
            then zeroConstantTemps(transientClears)(rest)(temps)
            else zeroConstantTemps(transientClears)(rest)(target :: temps)
        | _instruction :: rest -> zeroConstantTemps(transientClears)(rest)(temps)

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
let parametersHeldToTheEnd (transientClears: List(Int)) (parameters: List((Int, Int))) (instructions: List(IrInstruction)) =
    []
    |> zeroConstantTemps(transientClears)(instructions)
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
let withdrawRetainsOfCopiedCells (withdrawable: List(Int)) (transientClears: List(Int)) (valueTemp: Int) (parameters: List((Int, Int))) (instructions: List(IrInstruction)) =
    match parameters with
        | [] -> instructions
        | _parameters ->
            withdrawCells(arenaCellsCarrying([valueTemp])([])(parameterSlotsOf(parameters))(instructions)([]))(parametersHeldToTheEnd(transientClears)(parameters)(instructions))(withdrawable)(parameters
            |> parameterSlotsOf
            |> Some)([])(instructions)

let withdrawLoopParameterRetainsOfCopiedCells (transientClears: List(Int)) (valueTemp: Int) (parameters: List((Int, Int))) (instructions: List(IrInstruction)) = withdrawRetainsOfCopiedCells([])(transientClears)(valueTemp)(parameters)(instructions)

// The slot `temp` reads, through borrows, or -1.
let recursive slotReadThroughBorrows (temp: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> -1
        | IrInstruction { instruction = LoadLocal(target, slot) } :: rest ->
            if target == temp
            then slot
            else slotReadThroughBorrows(temp)(rest)
        | IrInstruction { instruction = Borrow(target, source) } :: rest ->
            if target == temp
            then slotReadThroughBorrows(source)(rest)
            else slotReadThroughBorrows(temp)(rest)
        | _instruction :: rest -> slotReadThroughBorrows(temp)(rest)

// Stage 0's `IsWithdrawablePatternOwnerDuplicate`: the promoted duplicates of a read of a pattern
// owner (a slot of `ownerSlots`) no promoted release was paired with (`released`).
let recursive withdrawablePatternOwnerDuplicates (ownerSlots: List(Int)) (released: List(Int)) (instructions: List(IrInstruction)) (found: List(Int)) =
    match instructions with
        | [] -> found
        | IrInstruction { instruction = RcDup(target, source, true, _mayBeEmpty) } :: rest ->
            if !containsInt(target)(released) && containsInt(slotReadThroughBorrows(source)(rest))(ownerSlots)
            then withdrawablePatternOwnerDuplicates(ownerSlots)(released)(rest)(target :: found)
            else withdrawablePatternOwnerDuplicates(ownerSlots)(released)(rest)(found)
        | _instruction :: rest -> withdrawablePatternOwnerDuplicates(ownerSlots)(released)(rest)(found)

// The same withdrawal for a loop's copied result: a pattern owner's duplicate an arena cell of the
// result stores is promoted to a retain like a loop-parameter read's, and the arena cell releases
// it just as little, so it is withdrawn the same way.
let withdrawLoopResultRetainsOfCopiedCells (ownerSlots: List(Int)) (released: List(Int)) (transientClears: List(Int)) (valueTemp: Int) (parameters: List((Int, Int))) (instructions: List(IrInstruction)) =
    match parameters with
        | [] -> instructions
        | _parameters ->
            withdrawRetainsOfCopiedCells(withdrawablePatternOwnerDuplicates(ownerSlots)(released)(instructions)([]))(transientClears)(valueTemp)(parameters)(instructions)

let recursive tempReaders (temp: Int) (instructions: List(IrInstruction)) (readers: List(IrInstructionKind)) =
    match instructions with
        | [] -> readers
        | IrInstruction { instruction = kind } :: rest ->
            if kind
            |> getUsedTemps
            |> containsInt(temp)
            then tempReaders(temp)(rest)(kind :: readers)
            else tempReaders(temp)(rest)(readers)

// Stage 0's `ReadIsCopiedOntoHeap`: every reader of `temp` copies it onto the reference-counted
// heap, a representation test whose reference-counted branch retains and whose other branch
// copies, or a borrow whose readers do the same. The answer is `(copied, tested)` folded over
// the readers, or `None` for a reader that does anything else.
let recursive readersCopyOntoHeap (readers: List(IrInstructionKind)) (depth: Int) (instructions: List(IrInstruction)) (copied: Bool) (tested: Bool) =
    match readers with
        | [] -> Some((copied, tested))
        | IsReferenceCounted(_target, _source) :: rest -> readersCopyOntoHeap(rest)(depth)(instructions)(copied)(true)
        | RcDup(_target, _source, _runtimeManaged, _mayBeEmpty) :: rest -> readersCopyOntoHeap(rest)(depth)(instructions)(copied)(tested)
        | CopyOutList(_target, _source, _headCopy, true, RcNormalization) :: rest -> readersCopyOntoHeap(rest)(depth)(instructions)(true)(tested)
        | Borrow(target, _source) :: rest ->
            if depth > 0 && readIsCopiedOntoHeap(target)(depth - 1)(instructions)
            then readersCopyOntoHeap(rest)(depth)(instructions)(true)(true)
            else None
        | _reader :: _rest -> None
and readIsCopiedOntoHeap (temp: Int) (depth: Int) (instructions: List(IrInstruction)) =
    match tempReaders(temp)(instructions)([]) with
        | [] -> false
        | readers ->
            match readersCopyOntoHeap(readers)(depth)(instructions)(false)(false) with
                | Some((copied, tested)) -> copied && tested
                | None -> false

// Stage 0's `IsOwnReleaseRead`: a read only the binding's own lexical release takes, which releases
// nothing for an arena cell.
let recursive onlyReleasesOwner (slot: Int) (readers: List(IrInstructionKind)) =
    match readers with
        | [] -> true
        | RcDrop(_source, _typeName, owner, _runtimeManaged, _mayBeEmpty, _dropper) :: rest -> owner == slot && onlyReleasesOwner(slot)(rest)
        | _reader :: _rest -> false

let recursive slotLoads (slot: Int) (instructions: List(IrInstruction)) (loads: List(Int)) =
    match instructions with
        | [] -> loads
        | IrInstruction { instruction = LoadLocal(target, loaded) } :: rest ->
            if loaded == slot
            then slotLoads(slot)(rest)(target :: loads)
            else slotLoads(slot)(rest)(loads)
        | _instruction :: rest -> slotLoads(slot)(rest)(loads)

let recursive loadsCopiedOrOwnRelease (slot: Int) (loads: List(Int)) (instructions: List(IrInstruction)) (anyCopied: Bool) =
    match loads with
        | [] -> anyCopied
        | load :: rest ->
            if readIsCopiedOntoHeap(load)(4)(instructions)
            then loadsCopiedOrOwnRelease(slot)(rest)(instructions)(true)
            else
                if []
                |> tempReaders(load)(instructions)
                |> onlyReleasesOwner(slot)
                then loadsCopiedOrOwnRelease(slot)(rest)(instructions)(anyCopied)
                else false

// Whether `slot` is a `let` slot of an arena cell every read of which copies it onto the
// reference-counted heap: stored once, never a loop parameter's.
let letCellCopiedOntoHeap (slot: Int) (cell: Int) (parameterSlots: List(Int)) (instructions: List(IrInstruction)) =
    if containsInt(slot)(parameterSlots) || isArenaCell(cell)(instructions) == false
    then false
    else
        match storeSourcesOf(slot)(instructions)([]) with
            | _source :: [] ->
                loadsCopiedOrOwnRelease(slot)(slotLoads(slot)(instructions)([]))(instructions)(false)
            | _ -> false

// The tail word a cons cell's construction stored.
let recursive cellTailSource (cell: Int) (instructions: List(IrInstruction)) =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = StoreMemOffset(basePointer, 8, source) } :: rest ->
            if basePointer == cell
            then Some(source)
            else cellTailSource(cell)(rest)
        | _instruction :: rest -> cellTailSource(cell)(rest)

let withdrawLetCellTailDuplicate (cell: Int) (withdrawable: List(Int)) (instructions: List(IrInstruction)) =
    match cellTailSource(cell)(instructions) with
        | Some(tail) ->
            if containsInt(tail)(withdrawable) && countTempUses(tail)(instructions)(0) == 1
            then withdrawRetainDefining(tail)(instructions)
            else instructions
        | None -> instructions

let recursive withdrawLetCellTails (candidates: List(IrInstructionKind)) (withdrawable: List(Int)) (parameterSlots: List(Int)) (instructions: List(IrInstruction)) =
    match candidates with
        | [] -> instructions
        | StoreLocal(slot, cell) :: rest ->
            withdrawLetCellTails(rest)(withdrawable)(parameterSlots)(if letCellCopiedOntoHeap(slot)(cell)(parameterSlots)(instructions)
            then withdrawLetCellTailDuplicate(cell)(withdrawable)(instructions)
            else instructions)
        | _candidate :: rest -> withdrawLetCellTails(rest)(withdrawable)(parameterSlots)(instructions)

let recursive arenaCellStores (instructions: List(IrInstruction)) (all: List(IrInstruction)) (found: List(IrInstructionKind)) =
    match instructions with
        | [] -> found
        | IrInstruction { instruction = StoreLocal(slot, source) } :: rest ->
            if isArenaCell(source)(all)
            then arenaCellStores(rest)(all)(StoreLocal(slot)(source) :: found)
            else arenaCellStores(rest)(all)(found)
        | _instruction :: rest -> arenaCellStores(rest)(all)(found)

// Stage 0's `WithdrawLetCopiedCellTailDuplicates`: a loop body's `let` binding of an arena cons
// cell over a pattern-bound list (`let rest = 0 :: matched in Wrap(rest)`) whose every read is
// copied onto the reference-counted heap. The copy shares the reference-counted tail with a
// reference of its own and the arena cell releases nothing, so the pattern-owner duplicate the
// cell took of its tail is withdrawn, as a copied successor cell's is.
let withdrawLetCopiedCellTailDuplicates (ownerSlots: List(Int)) (released: List(Int)) (parameterSlots: List(Int)) (instructions: List(IrInstruction)) =
    match arenaCellStores(instructions)(instructions)([]) with
        | [] -> instructions
        | candidates ->
            withdrawLetCellTails(candidates)(withdrawablePatternOwnerDuplicates(ownerSlots)(released)(instructions)([]))(parameterSlots)(instructions)
