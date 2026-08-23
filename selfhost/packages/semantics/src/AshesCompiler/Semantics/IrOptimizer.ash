// Complete pure-Ashes IR optimization pipeline.
//
// Invariants:
// - Optimization is semantics-preserving and invisible to user programs.
// - Pass ordering matches stage 0:
//   1. Compile-time evaluation (IrCompileTimeEval)
//   2. Trivial ownership-copy elision (erased RcDup, copy-type / single-use Borrow)
//   3. Runtime RcDup sinking into branch diamonds
//   4. Adjacent runtime RcDup / RcDrop pair fusion
//   5. Known closure devirtualization (CallClosure -> CallKnown)
//   6. Constant folding (arithmetic, bitwise, comparison with single-predecessor label state)
//   7. Identity elimination and strength reduction (x+0, x-0, x*0, x*1, x*2, x/1)
//   8. Unreachable code elimination (after Jump, Return, SwitchTag)
//   9. Dead code elimination (unused LoadConst, StoreLocal, MakeClosure)
//   10. Erased RcDrop marker elision
//   11. Interprocedural redundant arena bracket elimination (whole-function and straight-line regions)

import Ashes.Collection.List.append
import Ashes.Collection.List.filter
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrCompileTimeEval
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.StateMachineTransform
import AshesCompiler.Semantics.Types
export (
    type IrOptimizerOptions(..),
    value defaultOptimizerOptions,
    value optimizeIrProgram,
    value optimizeIrProgramWithOptions,
    value optimizeIrFunction,
)

type IrOptimizerOptions =
    | enableCompileTimeEval: Bool
    | enableInlining: Bool
    | enableDeadCodeElision: Bool
    | enableIdentityReduction: Bool

let defaultOptimizerOptions =
    IrOptimizerOptions(
        enableCompileTimeEval = true,
        enableInlining = true,
        enableDeadCodeElision = true,
        enableIdentityReduction = true
    )

let recursive lookupAssociation key map =
    match map with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupAssociation(key)(tail)

let recursive setAssociation key value map =
    match map with
        | [] -> [(key, value)]
        | (k, v) :: tail ->
            if k == key
            then (key, value) :: tail
            else (k, v) :: setAssociation(key)(value)(tail)

let recursive removeAssociation key map =
    match map with
        | [] -> []
        | (k, v) :: tail ->
            if k == key
            then tail
            else (k, v) :: removeAssociation(key)(tail)

let recursive listContains item xs =
    match xs with
        | [] -> false
        | head :: tail ->
            if head == item
            then true
            else listContains(item)(tail)

let recursive resolveTemp remap temp =
    match lookupAssociation(temp)(remap) with
        | None -> temp
        | Some(resolved) -> resolveTemp(remap)(resolved)

let remapSourceTemps inst remap =
    (let r temp = resolveTemp(remap)(temp)
    in
        match inst with
            | LoadConstInt(_, _) -> inst
            | LoadConstFloat(_, _) -> inst
            | LoadConstBool(_, _) -> inst
            | LoadConstStr(_, _) -> inst
            | LoadProgramArgs(_) -> inst
            | LoadLocal(_, _) -> inst
            | StoreLocal(slot, src) -> StoreLocal(slot)(r(src))
            | LoadEnv(_, _) -> inst
            | StoreMemOffset(basePtr, offset, src) -> StoreMemOffset(r(basePtr))(offset)(r(src))
            | LoadMemOffset(dest, basePtr, offset) -> LoadMemOffset(dest)(r(basePtr))(offset)
            | AddInt(dest, l, right) -> AddInt(dest)(r(l))(r(right))
            | SubInt(dest, l, right) -> SubInt(dest)(r(l))(r(right))
            | MulInt(dest, l, right) -> MulInt(dest)(r(l))(r(right))
            | DivInt(dest, l, right) -> DivInt(dest)(r(l))(r(right))
            | DivUInt(dest, l, right) -> DivUInt(dest)(r(l))(r(right))
            | AndInt(dest, l, right) -> AndInt(dest)(r(l))(r(right))
            | OrInt(dest, l, right) -> OrInt(dest)(r(l))(r(right))
            | XorInt(dest, l, right) -> XorInt(dest)(r(l))(r(right))
            | ShlInt(dest, l, right) -> ShlInt(dest)(r(l))(r(right))
            | ShrInt(dest, l, right) -> ShrInt(dest)(r(l))(r(right))
            | AddFloat(dest, l, right) -> AddFloat(dest)(r(l))(r(right))
            | SubFloat(dest, l, right) -> SubFloat(dest)(r(l))(r(right))
            | MulFloat(dest, l, right) -> MulFloat(dest)(r(l))(r(right))
            | DivFloat(dest, l, right) -> DivFloat(dest)(r(l))(r(right))
            | IntToFloat(dest, src) -> IntToFloat(dest)(r(src))
            | FloatToInt(dest, src) -> FloatToInt(dest)(r(src))
            | FloatUnaryIntrinsic(dest, src, name) -> FloatUnaryIntrinsic(dest)(r(src))(name)
            | CallLibm(dest, name, args) -> CallLibm(dest)(name)(map(r)(args))
            | CmpIntGt(dest, l, right) -> CmpIntGt(dest)(r(l))(r(right))
            | CmpIntGe(dest, l, right) -> CmpIntGe(dest)(r(l))(r(right))
            | CmpIntLt(dest, l, right) -> CmpIntLt(dest)(r(l))(r(right))
            | CmpIntLe(dest, l, right) -> CmpIntLe(dest)(r(l))(r(right))
            | CmpUIntGt(dest, l, right) -> CmpUIntGt(dest)(r(l))(r(right))
            | CmpUIntGe(dest, l, right) -> CmpUIntGe(dest)(r(l))(r(right))
            | CmpUIntLt(dest, l, right) -> CmpUIntLt(dest)(r(l))(r(right))
            | CmpUIntLe(dest, l, right) -> CmpUIntLe(dest)(r(l))(r(right))
            | CmpIntEq(dest, l, right) -> CmpIntEq(dest)(r(l))(r(right))
            | CmpIntNe(dest, l, right) -> CmpIntNe(dest)(r(l))(r(right))
            | CmpFloatGt(dest, l, right) -> CmpFloatGt(dest)(r(l))(r(right))
            | CmpFloatGe(dest, l, right) -> CmpFloatGe(dest)(r(l))(r(right))
            | CmpFloatLt(dest, l, right) -> CmpFloatLt(dest)(r(l))(r(right))
            | CmpFloatLe(dest, l, right) -> CmpFloatLe(dest)(r(l))(r(right))
            | CmpFloatEq(dest, l, right) -> CmpFloatEq(dest)(r(l))(r(right))
            | CmpFloatNe(dest, l, right) -> CmpFloatNe(dest)(r(l))(r(right))
            | CmpStrEq(dest, l, right) -> CmpStrEq(dest)(r(l))(r(right))
            | CmpStrNe(dest, l, right) -> CmpStrNe(dest)(r(l))(r(right))
            | ConcatStr(dest, l, right, managed) -> ConcatStr(dest)(r(l))(r(right))(managed)
            | ConcatStrTip(dest, l, right, cur, endSlot, managed) -> ConcatStrTip(dest)(r(l))(r(right))(cur)(endSlot)(managed)
            | MakeClosure(dest, fnLabel, envTemp, envSize, hasEnv, isClosure, isAsync) -> MakeClosure(dest)(fnLabel)(r(envTemp))(envSize)(hasEnv)(isClosure)(isAsync)
            | MakeClosureStack(dest, fnLabel, envTemp, envSize, hasEnv, isClosure) -> MakeClosureStack(dest)(fnLabel)(r(envTemp))(envSize)(hasEnv)(isClosure)
            | CallClosure(dest, closureTemp, argTemp, flagTemp) ->
                let remappedFlag =
                    if flagTemp < 0
                    then -1
                    else r(flagTemp)
                in CallClosure(dest)(r(closureTemp))(r(argTemp))(remappedFlag)
            | CallKnown(dest, fnLabel, envTemp, argTemp, flagTemp, envOnStack) ->
                let remappedFlag =
                    if flagTemp < 0
                    then -1
                    else r(flagTemp)
                in CallKnown(dest)(fnLabel)(r(envTemp))(r(argTemp))(remappedFlag)(envOnStack)
            | SetAdtField(ptr, idx, src) -> SetAdtField(r(ptr))(idx)(r(src))
            | GetAdtTag(dest, ptr) -> GetAdtTag(dest)(r(ptr))
            | GetAdtField(dest, ptr, idx) -> GetAdtField(dest)(r(ptr))(idx)
            | PrintInt(src) -> PrintInt(r(src))
            | PrintStr(src) -> PrintStr(r(src))
            | PrintBool(src) -> PrintBool(r(src))
            | WriteStr(src) -> WriteStr(r(src))
            | WriteErrorStr(src, managed) -> WriteErrorStr(r(src))(managed)
            | WriteBufferedStr(src, managed) -> WriteBufferedStr(r(src))(managed)
            | ExitProcess(src) -> ExitProcess(r(src))
            | PanicStr(src) -> PanicStr(r(src))
            | JumpIfFalse(cond, target) -> JumpIfFalse(r(cond))(target)
            | SwitchTag(tagTemp, cases, defaultLabel) -> SwitchTag(r(tagTemp))(cases)(defaultLabel)
            | Return(src) -> Return(r(src))
            | Borrow(dest, src) -> Borrow(dest)(r(src))
            | RcDup(dest, src, managed, borrowFlag) -> RcDup(dest)(r(src))(managed)(borrowFlag)
            | RcDrop(src, name, slot, managed, borrowFlag, origin) -> RcDrop(r(src))(name)(slot)(managed)(borrowFlag)(origin)
            | RcIsUnique(dest, src) -> RcIsUnique(dest)(r(src))
            | DropReuse(dest, src, size, managed) -> DropReuse(dest)(r(src))(size)(managed)
            | AllocReusing(dest, tag, size, tok, isStack, isManaged) -> AllocReusing(dest)(tag)(size)(r(tok))(isStack)(isManaged)
            | StoreCapabilityHandler(idx, src) -> StoreCapabilityHandler(idx)(r(src))
            | _ -> inst)

let recursive countTempUses instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            let used = getUsedTemps(inst)
            in
                let recursive addUses us map =
                    match us with
                        | [] -> map
                        | u :: uTail ->
                            let count =
                                match lookupAssociation(u)(map) with
                                    | Some(c) -> c + 1
                                    | None -> 1
                            in addUses(uTail)(setAssociation(u)(count)(map))
                in countTempUses(tail)(addUses(used)(acc))

let recursive collectCopyTypeProducers instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            match inst with
                | LoadConstInt(t, _) -> collectCopyTypeProducers(tail)(t :: acc)
                | LoadConstFloat(t, _) -> collectCopyTypeProducers(tail)(t :: acc)
                | LoadConstBool(t, _) -> collectCopyTypeProducers(tail)(t :: acc)
                | _ -> collectCopyTypeProducers(tail)(acc)

let elideTrivialOwnershipCopies instructions =
    (let copyTypes = collectCopyTypeProducers(instructions)([])
    in
        let useCounts = countTempUses(instructions)([])
        in
            let recursive buildRemap insts remap =
                match insts with
                    | [] -> remap
                    | IrInstruction { instruction = inst } :: tail ->
                        match inst with
                            | RcDup(dest, src, false, _) ->
                                let resolvedSrc = resolveTemp(remap)(src)
                                in buildRemap(tail)(setAssociation(dest)(resolvedSrc)(remap))
                            | Borrow(dest, src) ->
                                let resolvedSrc = resolveTemp(remap)(src)
                                in
                                    let isCopy = listContains(resolvedSrc)(copyTypes)
                                    in
                                        let isSingleUse =
                                            match lookupAssociation(dest)(useCounts) with
                                                | Some(c) -> c <= 1
                                                | None -> true
                                        in
                                            let isEligible =
                                                if isCopy
                                                then true
                                                else isSingleUse
                                            in
                                                if isEligible
                                                then buildRemap(tail)(setAssociation(dest)(resolvedSrc)(remap))
                                                else buildRemap(tail)(remap)
                            | _ -> buildRemap(tail)(remap)
            in
                let remap = buildRemap(instructions)([])
                in
                    let recursive applyRemap insts acc =
                        match insts with
                            | [] -> reverse(acc)
                            | (IrInstruction { instruction = inst, location = loc } as irInst) :: tail ->
                                match inst with
                                    | RcDup(dest, _, false, _) ->
                                        if lookupAssociation(dest)(remap) != None
                                        then applyRemap(tail)(acc)
                                        else applyRemap(tail)(irInst :: acc)
                                    | Borrow(dest, _) ->
                                        if lookupAssociation(dest)(remap) != None
                                        then applyRemap(tail)(acc)
                                        else applyRemap(tail)(irInst :: acc)
                                    | _ ->
                                        let remapped =
                                            IrInstruction(
                                                instruction = remapSourceTemps(inst)(remap),
                                                location = loc
                                            )
                                        in applyRemap(tail)(remapped :: acc)
                    in applyRemap(instructions)([]))

let recursive isTempUsedInInstructions instructions temp =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = inst } :: tail ->
            if listContains(temp)(getUsedTemps(inst))
            then true
            else isTempUsedInInstructions(tail)(temp)

let fuseAdjacentRuntimeRcPairs instructions =
    (let recursive fusePass insts remap acc =
        match insts with
            | [] -> reverse(acc)
            | (IrInstruction { instruction = RcDup(dest, src, true, borrowFlag), location = loc1 } as dupInst) :: (IrInstruction { instruction = RcDrop(dropSrc, dropName, dropSlot, true, dropBorrow, dropOrigin), location = loc2 } as dropInst) :: tail ->
                let remappedDup = remapSourceTemps(dupInst.instruction)(remap)
                in
                    let remappedDrop = remapSourceTemps(dropInst.instruction)(remap)
                    in
                        match (remappedDup, remappedDrop) with
                            | (RcDup(rDest, rSrc, true, _), RcDrop(rDropSrc, _, _, true, _, _)) ->
                                if rDropSrc == rDest
                                then
                                    if not(isTempUsedInInstructions(tail)(rDest))
                                    then fusePass(tail)(remap)(acc)
                                    else
                                        fusePass(dropInst :: tail)(remap)(
                                            IrInstruction(instruction = remappedDup, location = loc1) :: acc
                                        )
                                else
                                    if rDropSrc == rSrc
                                    then
                                        let nextRemap = setAssociation(rDest)(rSrc)(remap)
                                        in fusePass(tail)(nextRemap)(acc)
                                    else
                                        fusePass(dropInst :: tail)(remap)(
                                            IrInstruction(instruction = remappedDup, location = loc1) :: acc
                                        )
                            | _ ->
                                fusePass(dropInst :: tail)(remap)(
                                    IrInstruction(instruction = remappedDup, location = loc1) :: acc
                                )
            | IrInstruction { instruction = inst, location = loc } :: tail ->
                let remapped =
                    IrInstruction(
                        instruction = remapSourceTemps(inst)(remap),
                        location = loc
                    )
                in fusePass(tail)(remap)(remapped :: acc)
    in fusePass(instructions)([])([]))

let recursive countDefinitions instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            let defs = getDefinedTemps(inst)
            in
                let recursive addDefs ds map =
                    match ds with
                        | [] -> map
                        | d :: dTail ->
                            let count =
                                match lookupAssociation(d)(map) with
                                    | Some(c) -> c + 1
                                    | None -> 1
                            in addDefs(dTail)(setAssociation(d)(count)(map))
                in countDefinitions(tail)(addDefs(defs)(acc))

let recursive collectSingleDefinitions instructions defCounts acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            match inst with
                | MakeClosure(dest, fnLabel, envTemp, envSize, hasEnv, isClosure, isAsync) ->
                    if lookupAssociation(dest)(defCounts) == Some(1)
                    then collectSingleDefinitions(tail)(defCounts)(setAssociation(dest)(inst)(acc))
                    else collectSingleDefinitions(tail)(defCounts)(acc)
                | MakeClosureStack(dest, fnLabel, envTemp, envSize, hasEnv, isClosure) ->
                    if lookupAssociation(dest)(defCounts) == Some(1)
                    then collectSingleDefinitions(tail)(defCounts)(setAssociation(dest)(inst)(acc))
                    else collectSingleDefinitions(tail)(defCounts)(acc)
                | _ -> collectSingleDefinitions(tail)(defCounts)(acc)

let devirtualizeKnownClosureCalls instructions =
    (let defCounts = countDefinitions(instructions)([])
    in
        let singleDefs = collectSingleDefinitions(instructions)(defCounts)([])
        in
            let recursive devirtualize insts acc =
                match insts with
                    | [] -> reverse(acc)
                    | (IrInstruction { instruction = CallClosure(dest, closureTemp, argTemp, flagTemp), location = loc } as irInst) :: tail ->
                        match lookupAssociation(closureTemp)(singleDefs) with
                            | Some(MakeClosure(_, fnLabel, envTemp, _, _, _, _)) ->
                                if lookupAssociation(envTemp)(defCounts) == Some(1)
                                then
                                    let directCall =
                                        IrInstruction(
                                            instruction = CallKnown(dest)(fnLabel)(envTemp)(argTemp)(flagTemp)(false),
                                            location = loc
                                        )
                                    in devirtualize(tail)(directCall :: acc)
                                else devirtualize(tail)(irInst :: acc)
                            | Some(MakeClosureStack(_, fnLabel, envTemp, envSize, _, _)) ->
                                if lookupAssociation(envTemp)(defCounts) == Some(1)
                                then
                                    let isStack = envSize > 0
                                    in
                                        let directCall =
                                            IrInstruction(
                                                instruction = CallKnown(
                                                    dest,
                                                    fnLabel,
                                                    envTemp,
                                                    argTemp,
                                                    flagTemp,
                                                    isStack
                                                ),
                                                location = loc
                                            )
                                        in devirtualize(tail)(directCall :: acc)
                                else devirtualize(tail)(irInst :: acc)
                            | _ -> devirtualize(tail)(irInst :: acc)
                    | head :: tail -> devirtualize(tail)(head :: acc)
            in devirtualize(instructions)([]))

let recursive countBranchRefsToLabels instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            match inst with
                | Jump(target) ->
                    let count =
                        match lookupAssociation(target)(acc) with
                            | Some(c) -> c + 1
                            | None -> 1
                    in countBranchRefsToLabels(tail)(setAssociation(target)(count)(acc))
                | JumpIfFalse(_, target) ->
                    let count =
                        match lookupAssociation(target)(acc) with
                            | Some(c) -> c + 1
                            | None -> 1
                    in countBranchRefsToLabels(tail)(setAssociation(target)(count)(acc))
                | SwitchTag(_, cases, defaultLabel) ->
                    let recursive addCases cs map =
                        match cs with
                            | [] -> map
                            | IrSwitchCase { label = l } :: cTail ->
                                let c =
                                    match lookupAssociation(l)(map) with
                                        | Some(n) -> n + 1
                                        | None -> 1
                                in addCases(cTail)(setAssociation(l)(c)(map))
                    in
                        let mapWithCases = addCases(cases)(acc)
                        in
                            let defCount =
                                match lookupAssociation(defaultLabel)(mapWithCases) with
                                    | Some(n) -> n + 1
                                    | None -> 1
                            in countBranchRefsToLabels(tail)(setAssociation(defaultLabel)(defCount)(mapWithCases))
                | _ -> countBranchRefsToLabels(tail)(acc)

type FoldState =
    | ints: List((IrTemp, Int))
    | floats: List((IrTemp, Float))
    | bools: List((IrTemp, Bool))
    | savedInts: List((Str, List((IrTemp, Int))))
    | savedFloats: List((Str, List((IrTemp, Float))))
    | savedBools: List((Str, List((IrTemp, Bool))))
    | prevIsTerminator: Bool

let emptyFoldState =
    FoldState(
        ints = [],
        floats = [],
        bools = [],
        savedInts = [],
        savedFloats = [],
        savedBools = [],
        prevIsTerminator = false
    )

let foldConstants instructions =
    (let branchRefs = countBranchRefsToLabels(instructions)([])
    in
        let recursive foldLoop insts state acc =
            match insts with
                | [] -> reverse(acc)
                | (IrInstruction { instruction = inst, location = loc } as irInst) :: tail ->
                    (match inst with
                        | LoadConstInt(dest, v) ->
                            let nextInts = setAssociation(dest)(v)(state.ints)
                            in
                                foldLoop(tail)(
                                    FoldState(
                                        ints = nextInts,
                                        floats = state.floats,
                                        bools = state.bools,
                                        savedInts = state.savedInts,
                                        savedFloats = state.savedFloats,
                                        savedBools = state.savedBools,
                                        prevIsTerminator = false
                                    )
                                )(
                                    irInst :: acc
                                )
                        | LoadConstFloat(dest, v) ->
                            let nextFloats = setAssociation(dest)(v)(state.floats)
                            in
                                foldLoop(tail)(
                                    FoldState(
                                        ints = state.ints,
                                        floats = nextFloats,
                                        bools = state.bools,
                                        savedInts = state.savedInts,
                                        savedFloats = state.savedFloats,
                                        savedBools = state.savedBools,
                                        prevIsTerminator = false
                                    )
                                )(
                                    irInst :: acc
                                )
                        | LoadConstBool(dest, v) ->
                            let nextBools = setAssociation(dest)(v)(state.bools)
                            in
                                foldLoop(tail)(
                                    FoldState(
                                        ints = state.ints,
                                        floats = state.floats,
                                        bools = nextBools,
                                        savedInts = state.savedInts,
                                        savedFloats = state.savedFloats,
                                        savedBools = state.savedBools,
                                        prevIsTerminator = false
                                    )
                                )(
                                    irInst :: acc
                                )
                        | AddInt(dest, l, r) ->
                            match (lookupAssociation(l)(state.ints), lookupAssociation(r)(state.ints)) with
                                | (Some(lv), Some(rv)) ->
                                    let v = lv + rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = setAssociation(dest)(v)(state.ints),
                                                floats = state.floats,
                                                bools = state.bools,
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(instruction = LoadConstInt(dest)(v), location = loc) :: acc
                                        )
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | SubInt(dest, l, r) ->
                            match (lookupAssociation(l)(state.ints), lookupAssociation(r)(state.ints)) with
                                | (Some(lv), Some(rv)) ->
                                    let v = lv - rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = setAssociation(dest)(v)(state.ints),
                                                floats = state.floats,
                                                bools = state.bools,
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(instruction = LoadConstInt(dest)(v), location = loc) :: acc
                                        )
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | MulInt(dest, l, r) ->
                            match (lookupAssociation(l)(state.ints), lookupAssociation(r)(state.ints)) with
                                | (Some(lv), Some(rv)) ->
                                    let v = lv * rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = setAssociation(dest)(v)(state.ints),
                                                floats = state.floats,
                                                bools = state.bools,
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(instruction = LoadConstInt(dest)(v), location = loc) :: acc
                                        )
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | DivInt(dest, l, r) ->
                            match (lookupAssociation(l)(state.ints), lookupAssociation(r)(state.ints)) with
                                | (Some(lv), Some(rv)) ->
                                    if rv != 0
                                    then
                                        let v = lv / rv
                                        in
                                            foldLoop(tail)(
                                                FoldState(
                                                    ints = setAssociation(dest)(v)(state.ints),
                                                    floats = state.floats,
                                                    bools = state.bools,
                                                    savedInts = state.savedInts,
                                                    savedFloats = state.savedFloats,
                                                    savedBools = state.savedBools,
                                                    prevIsTerminator = false
                                                )
                                            )(
                                                IrInstruction(
                                                    instruction = LoadConstInt(dest)(v),
                                                    location = loc
                                                ) :: acc
                                            )
                                    else foldLoop(tail)(state)(irInst :: acc)
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | DivUInt(dest, l, r) ->
                            match (lookupAssociation(l)(state.ints), lookupAssociation(r)(state.ints)) with
                                | (Some(lv), Some(rv)) ->
                                    if rv != 0
                                    then
                                        let v = lv / rv
                                        in
                                            foldLoop(tail)(
                                                FoldState(
                                                    ints = setAssociation(dest)(v)(state.ints),
                                                    floats = state.floats,
                                                    bools = state.bools,
                                                    savedInts = state.savedInts,
                                                    savedFloats = state.savedFloats,
                                                    savedBools = state.savedBools,
                                                    prevIsTerminator = false
                                                )
                                            )(
                                                IrInstruction(
                                                    instruction = LoadConstInt(dest)(v),
                                                    location = loc
                                                ) :: acc
                                            )
                                    else foldLoop(tail)(state)(irInst :: acc)
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | AndInt(dest, l, r) ->
                            match (lookupAssociation(l)(state.ints), lookupAssociation(r)(state.ints)) with
                                | (Some(lv), Some(rv)) ->
                                    let v = lv & rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = setAssociation(dest)(v)(state.ints),
                                                floats = state.floats,
                                                bools = state.bools,
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(instruction = LoadConstInt(dest)(v), location = loc) :: acc
                                        )
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | OrInt(dest, l, r) ->
                            (match (lookupAssociation(l)(state.ints), lookupAssociation(r)(state.ints)) with
                                | (Some(lv), Some(rv)) ->
                                    (let v = lv | rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = setAssociation(dest)(v)(state.ints),
                                                floats = state.floats,
                                                bools = state.bools,
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(instruction = LoadConstInt(dest)(v), location = loc) :: acc
                                        ))
                                | _ -> foldLoop(tail)(state)(irInst :: acc))
                        | XorInt(dest, l, r) ->
                            match (lookupAssociation(l)(state.ints), lookupAssociation(r)(state.ints)) with
                                | (Some(lv), Some(rv)) ->
                                    let v = lv ^ rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = setAssociation(dest)(v)(state.ints),
                                                floats = state.floats,
                                                bools = state.bools,
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(instruction = LoadConstInt(dest)(v), location = loc) :: acc
                                        )
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | ShlInt(dest, l, r) ->
                            match (lookupAssociation(l)(state.ints), lookupAssociation(r)(state.ints)) with
                                | (Some(lv), Some(rv)) ->
                                    let v = lv << rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = setAssociation(dest)(v)(state.ints),
                                                floats = state.floats,
                                                bools = state.bools,
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(instruction = LoadConstInt(dest)(v), location = loc) :: acc
                                        )
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | ShrInt(dest, l, r) ->
                            match (lookupAssociation(l)(state.ints), lookupAssociation(r)(state.ints)) with
                                | (Some(lv), Some(rv)) ->
                                    let v = lv >> rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = setAssociation(dest)(v)(state.ints),
                                                floats = state.floats,
                                                bools = state.bools,
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(instruction = LoadConstInt(dest)(v), location = loc) :: acc
                                        )
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | AddFloat(dest, l, r) ->
                            match (lookupAssociation(l)(state.floats), lookupAssociation(r)(state.floats)) with
                                | (Some(lv), Some(rv)) ->
                                    let v = lv + rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = state.ints,
                                                floats = setAssociation(dest)(v)(state.floats),
                                                bools = state.bools,
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(
                                                instruction = LoadConstFloat(dest)(v),
                                                location = loc
                                            ) :: acc
                                        )
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | SubFloat(dest, l, r) ->
                            match (lookupAssociation(l)(state.floats), lookupAssociation(r)(state.floats)) with
                                | (Some(lv), Some(rv)) ->
                                    let v = lv - rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = state.ints,
                                                floats = setAssociation(dest)(v)(state.floats),
                                                bools = state.bools,
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(
                                                instruction = LoadConstFloat(dest)(v),
                                                location = loc
                                            ) :: acc
                                        )
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | MulFloat(dest, l, r) ->
                            match (lookupAssociation(l)(state.floats), lookupAssociation(r)(state.floats)) with
                                | (Some(lv), Some(rv)) ->
                                    let v = lv * rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = state.ints,
                                                floats = setAssociation(dest)(v)(state.floats),
                                                bools = state.bools,
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(
                                                instruction = LoadConstFloat(dest)(v),
                                                location = loc
                                            ) :: acc
                                        )
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | DivFloat(dest, l, r) ->
                            match (lookupAssociation(l)(state.floats), lookupAssociation(r)(state.floats)) with
                                | (Some(lv), Some(rv)) ->
                                    let v = lv / rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = state.ints,
                                                floats = setAssociation(dest)(v)(state.floats),
                                                bools = state.bools,
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(
                                                instruction = LoadConstFloat(dest)(v),
                                                location = loc
                                            ) :: acc
                                        )
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | CmpIntEq(dest, l, r) ->
                            match (lookupAssociation(l)(state.ints), lookupAssociation(r)(state.ints)) with
                                | (Some(lv), Some(rv)) ->
                                    let v = lv == rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = state.ints,
                                                floats = state.floats,
                                                bools = setAssociation(dest)(v)(state.bools),
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(instruction = LoadConstBool(dest)(v), location = loc) :: acc
                                        )
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | CmpIntNe(dest, l, r) ->
                            match (lookupAssociation(l)(state.ints), lookupAssociation(r)(state.ints)) with
                                | (Some(lv), Some(rv)) ->
                                    let v = lv != rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = state.ints,
                                                floats = state.floats,
                                                bools = setAssociation(dest)(v)(state.bools),
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(instruction = LoadConstBool(dest)(v), location = loc) :: acc
                                        )
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | CmpIntGt(dest, l, r) ->
                            match (lookupAssociation(l)(state.ints), lookupAssociation(r)(state.ints)) with
                                | (Some(lv), Some(rv)) ->
                                    let v = lv > rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = state.ints,
                                                floats = state.floats,
                                                bools = setAssociation(dest)(v)(state.bools),
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(instruction = LoadConstBool(dest)(v), location = loc) :: acc
                                        )
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | CmpIntGe(dest, l, r) ->
                            match (lookupAssociation(l)(state.ints), lookupAssociation(r)(state.ints)) with
                                | (Some(lv), Some(rv)) ->
                                    let v = lv >= rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = state.ints,
                                                floats = state.floats,
                                                bools = setAssociation(dest)(v)(state.bools),
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(instruction = LoadConstBool(dest)(v), location = loc) :: acc
                                        )
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | CmpIntLt(dest, l, r) ->
                            match (lookupAssociation(l)(state.ints), lookupAssociation(r)(state.ints)) with
                                | (Some(lv), Some(rv)) ->
                                    let v = lv < rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = state.ints,
                                                floats = state.floats,
                                                bools = setAssociation(dest)(v)(state.bools),
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(instruction = LoadConstBool(dest)(v), location = loc) :: acc
                                        )
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | CmpIntLe(dest, l, r) ->
                            match (lookupAssociation(l)(state.ints), lookupAssociation(r)(state.ints)) with
                                | (Some(lv), Some(rv)) ->
                                    let v = lv <= rv
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = state.ints,
                                                floats = state.floats,
                                                bools = setAssociation(dest)(v)(state.bools),
                                                savedInts = state.savedInts,
                                                savedFloats = state.savedFloats,
                                                savedBools = state.savedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            IrInstruction(instruction = LoadConstBool(dest)(v), location = loc) :: acc
                                        )
                                | _ -> foldLoop(tail)(state)(irInst :: acc)
                        | Label(name) ->
                            let branchCount =
                                match lookupAssociation(name)(branchRefs) with
                                    | Some(c) -> c
                                    | None -> 0
                            in
                                let totalPreds =
                                    branchCount + (if state.prevIsTerminator
                                    then 0
                                    else 1)
                                in
                                    let restoredState =
                                        if totalPreds <= 1
                                        then
                                            if state.prevIsTerminator
                                            then
                                                match lookupAssociation(name)(state.savedInts) with
                                                    | Some(si) ->
                                                        let sFloats =
                                                            match lookupAssociation(name)(state.savedFloats) with
                                                                | Some(sf) -> sf
                                                                | None -> []
                                                        in
                                                            let sBools =
                                                                match lookupAssociation(name)(state.savedBools) with
                                                                    | Some(sb) -> sb
                                                                    | None -> []
                                                            in
                                                                FoldState(
                                                                    ints = si,
                                                                    floats = sFloats,
                                                                    bools = sBools,
                                                                    savedInts = removeAssociation(name)(state.savedInts),
                                                                    savedFloats = removeAssociation(name)(state.savedFloats),
                                                                    savedBools = removeAssociation(name)(state.savedBools),
                                                                    prevIsTerminator = false
                                                                )
                                                    | None ->
                                                        FoldState(
                                                            ints = [],
                                                            floats = [],
                                                            bools = [],
                                                            savedInts = removeAssociation(name)(state.savedInts),
                                                            savedFloats = removeAssociation(name)(state.savedFloats),
                                                            savedBools = removeAssociation(name)(state.savedBools),
                                                            prevIsTerminator = false
                                                        )
                                            else
                                                if branchCount == 0
                                                then
                                                    FoldState(
                                                        ints = state.ints,
                                                        floats = state.floats,
                                                        bools = state.bools,
                                                        savedInts = removeAssociation(name)(state.savedInts),
                                                        savedFloats = removeAssociation(name)(state.savedFloats),
                                                        savedBools = removeAssociation(name)(state.savedBools),
                                                        prevIsTerminator = false
                                                    )
                                                else
                                                    FoldState(
                                                        ints = [],
                                                        floats = [],
                                                        bools = [],
                                                        savedInts = removeAssociation(name)(state.savedInts),
                                                        savedFloats = removeAssociation(name)(state.savedFloats),
                                                        savedBools = removeAssociation(name)(state.savedBools),
                                                        prevIsTerminator = false
                                                    )
                                        else
                                            FoldState(
                                                ints = [],
                                                floats = [],
                                                bools = [],
                                                savedInts = removeAssociation(name)(state.savedInts),
                                                savedFloats = removeAssociation(name)(state.savedFloats),
                                                savedBools = removeAssociation(name)(state.savedBools),
                                                prevIsTerminator = false
                                            )
                                    in foldLoop(tail)(restoredState)(irInst :: acc)
                        | Jump(target) ->
                            let nextSavedInts = setAssociation(target)(state.ints)(state.savedInts)
                            in
                                let nextSavedFloats = setAssociation(target)(state.floats)(state.savedFloats)
                                in
                                    let nextSavedBools = setAssociation(target)(state.bools)(state.savedBools)
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = state.ints,
                                                floats = state.floats,
                                                bools = state.bools,
                                                savedInts = nextSavedInts,
                                                savedFloats = nextSavedFloats,
                                                savedBools = nextSavedBools,
                                                prevIsTerminator = true
                                            )
                                        )(
                                            irInst :: acc
                                        )
                        | JumpIfFalse(_, target) ->
                            let nextSavedInts = setAssociation(target)(state.ints)(state.savedInts)
                            in
                                let nextSavedFloats = setAssociation(target)(state.floats)(state.savedFloats)
                                in
                                    let nextSavedBools = setAssociation(target)(state.bools)(state.savedBools)
                                    in
                                        foldLoop(tail)(
                                            FoldState(
                                                ints = state.ints,
                                                floats = state.floats,
                                                bools = state.bools,
                                                savedInts = nextSavedInts,
                                                savedFloats = nextSavedFloats,
                                                savedBools = nextSavedBools,
                                                prevIsTerminator = false
                                            )
                                        )(
                                            irInst :: acc
                                        )
                        | SwitchTag(_, _, _) ->
                            foldLoop(tail)(
                                FoldState(
                                    ints = state.ints,
                                    floats = state.floats,
                                    bools = state.bools,
                                    savedInts = state.savedInts,
                                    savedFloats = state.savedFloats,
                                    savedBools = state.savedBools,
                                    prevIsTerminator = true
                                )
                            )(
                                irInst :: acc
                            )
                        | Return(_) ->
                            foldLoop(tail)(
                                FoldState(
                                    ints = state.ints,
                                    floats = state.floats,
                                    bools = state.bools,
                                    savedInts = state.savedInts,
                                    savedFloats = state.savedFloats,
                                    savedBools = state.savedBools,
                                    prevIsTerminator = true
                                )
                            )(
                                irInst :: acc
                            )
                        | _ -> foldLoop(tail)(state)(irInst :: acc))
        in foldLoop(instructions)(emptyFoldState)([]))

let reduceIdentitiesAndStrength instructions =
    (let branchRefs = countBranchRefsToLabels(instructions)([])
    in
        let recursive reduceLoop insts knownInts savedInts prevTerm acc =
            match insts with
                | [] -> reverse(acc)
                | (IrInstruction { instruction = inst, location = loc } as irInst) :: tail ->
                    match inst with
                        | LoadConstInt(dest, v) ->
                            let nextInts = setAssociation(dest)(v)(knownInts)
                            in reduceLoop(tail)(nextInts)(savedInts)(false)(irInst :: acc)
                        | AddInt(dest, l, r) ->
                            let leftZero = lookupAssociation(l)(knownInts) == Some(0)
                            in
                                let rightZero = lookupAssociation(r)(knownInts) == Some(0)
                                in
                                    if leftZero
                                    then
                                        reduceLoop(tail)(knownInts)(savedInts)(false)(
                                            IrInstruction(instruction = Borrow(dest)(r), location = loc) :: acc
                                        )
                                    else
                                        if rightZero
                                        then
                                            reduceLoop(tail)(knownInts)(savedInts)(false)(
                                                IrInstruction(instruction = Borrow(dest)(l), location = loc) :: acc
                                            )
                                        else reduceLoop(tail)(knownInts)(savedInts)(false)(irInst :: acc)
                        | SubInt(dest, l, r) ->
                            let rightZero = lookupAssociation(r)(knownInts) == Some(0)
                            in
                                if rightZero
                                then
                                    reduceLoop(tail)(knownInts)(savedInts)(false)(
                                        IrInstruction(instruction = Borrow(dest)(l), location = loc) :: acc
                                    )
                                else reduceLoop(tail)(knownInts)(savedInts)(false)(irInst :: acc)
                        | MulInt(dest, l, r) ->
                            let lOpt = lookupAssociation(l)(knownInts)
                            in
                                let rOpt = lookupAssociation(r)(knownInts)
                                in
                                    let isZero =
                                        if lOpt == Some(0)
                                        then true
                                        else rOpt == Some(0)
                                    in
                                        if isZero
                                        then
                                            reduceLoop(tail)(knownInts)(savedInts)(false)(
                                                IrInstruction(instruction = LoadConstInt(dest)(0), location = loc) :: acc
                                            )
                                        else
                                            if lOpt == Some(1)
                                            then
                                                reduceLoop(tail)(knownInts)(savedInts)(false)(
                                                    IrInstruction(instruction = Borrow(dest)(r), location = loc) :: acc
                                                )
                                            else
                                                if rOpt == Some(1)
                                                then
                                                    reduceLoop(tail)(knownInts)(savedInts)(false)(
                                                        IrInstruction(instruction = Borrow(dest)(l), location = loc) :: acc
                                                    )
                                                else
                                                    if rOpt == Some(2)
                                                    then
                                                        reduceLoop(tail)(knownInts)(savedInts)(false)(
                                                            IrInstruction(
                                                                instruction = AddInt(dest)(l)(l),
                                                                location = loc
                                                            ) :: acc
                                                        )
                                                    else
                                                        if lOpt == Some(2)
                                                        then
                                                            reduceLoop(tail)(knownInts)(savedInts)(false)(
                                                                IrInstruction(
                                                                    instruction = AddInt(dest)(r)(r),
                                                                    location = loc
                                                                ) :: acc
                                                            )
                                                        else reduceLoop(tail)(knownInts)(savedInts)(false)(irInst :: acc)
                        | DivInt(dest, l, r) ->
                            let rightOne = lookupAssociation(r)(knownInts) == Some(1)
                            in
                                if rightOne
                                then
                                    reduceLoop(tail)(knownInts)(savedInts)(false)(
                                        IrInstruction(instruction = Borrow(dest)(l), location = loc) :: acc
                                    )
                                else reduceLoop(tail)(knownInts)(savedInts)(false)(irInst :: acc)
                        | DivUInt(dest, l, r) ->
                            let rightOne = lookupAssociation(r)(knownInts) == Some(1)
                            in
                                if rightOne
                                then
                                    reduceLoop(tail)(knownInts)(savedInts)(false)(
                                        IrInstruction(instruction = Borrow(dest)(l), location = loc) :: acc
                                    )
                                else reduceLoop(tail)(knownInts)(savedInts)(false)(irInst :: acc)
                        | Label(name) ->
                            let branchCount =
                                match lookupAssociation(name)(branchRefs) with
                                    | Some(c) -> c
                                    | None -> 0
                            in
                                let totalPreds =
                                    branchCount + (if prevTerm
                                    then 0
                                    else 1)
                                in
                                    let nextInts =
                                        if totalPreds <= 1
                                        then
                                            if prevTerm
                                            then
                                                match lookupAssociation(name)(savedInts) with
                                                    | Some(si) -> si
                                                    | None -> []
                                            else
                                                if branchCount == 0
                                                then knownInts
                                                else []
                                        else []
                                    in
                                        reduceLoop(tail)(nextInts)(removeAssociation(name)(savedInts))(false)(
                                            irInst :: acc
                                        )
                        | Jump(target) ->
                            let nextSaved = setAssociation(target)(knownInts)(savedInts)
                            in reduceLoop(tail)(knownInts)(nextSaved)(true)(irInst :: acc)
                        | JumpIfFalse(_, target) ->
                            let nextSaved = setAssociation(target)(knownInts)(savedInts)
                            in reduceLoop(tail)(knownInts)(nextSaved)(false)(irInst :: acc)
                        | SwitchTag(_, _, _) -> reduceLoop(tail)(knownInts)(savedInts)(true)(irInst :: acc)
                        | Return(_) -> reduceLoop(tail)(knownInts)(savedInts)(true)(irInst :: acc)
                        | _ -> reduceLoop(tail)(knownInts)(savedInts)(false)(irInst :: acc)
        in reduceLoop(instructions)([])([])(false)([]))

let elideUnreachableCode instructions =
    (let recursive elideLoop insts unreachable acc =
        match insts with
            | [] -> reverse(acc)
            | (IrInstruction { instruction = inst } as irInst) :: tail ->
                match inst with
                    | Label(_) -> elideLoop(tail)(false)(irInst :: acc)
                    | _ ->
                        if unreachable
                        then elideLoop(tail)(true)(acc)
                        else
                            let isTerm =
                                match inst with
                                    | Jump(_) -> true
                                    | Return(_) -> true
                                    | SwitchTag(_, _, _) -> true
                                    | _ -> false
                            in elideLoop(tail)(isTerm)(irInst :: acc)
    in elideLoop(instructions)(false)([]))

let recursive collectAllUsedTemps instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            let used = getUsedTemps(inst)
            in
                let recursive addUnique us xs =
                    match us with
                        | [] -> xs
                        | u :: uTail ->
                            if listContains(u)(xs)
                            then addUnique(uTail)(xs)
                            else addUnique(uTail)(u :: xs)
                in collectAllUsedTemps(tail)(addUnique(used)(acc))

let recursive collectAllReadSlots instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            let read = getReadLocalSlots(inst)
            in
                let recursive addUnique rs xs =
                    match rs with
                        | [] -> xs
                        | r :: rTail ->
                            if listContains(r)(xs)
                            then addUnique(rTail)(xs)
                            else addUnique(rTail)(r :: xs)
                in collectAllReadSlots(tail)(addUnique(read)(acc))

let isDeadInstruction inst usedTemps readSlots =
    match inst with
        | LoadConstInt(t, _) -> not(listContains(t)(usedTemps))
        | LoadConstFloat(t, _) -> not(listContains(t)(usedTemps))
        | LoadConstBool(t, _) -> not(listContains(t)(usedTemps))
        | StoreLocal(slot, _) -> not(listContains(slot)(readSlots))
        | MakeClosure(t, _, _, _, _, _, _) -> not(listContains(t)(usedTemps))
        | MakeClosureStack(t, _, _, _, _, _) -> not(listContains(t)(usedTemps))
        | _ -> false

let elideDeadCode instructions =
    (let recursive fixpoint insts =
        let usedTemps = collectAllUsedTemps(insts)([])
        in
            let readSlots = collectAllReadSlots(insts)([])
            in
                let filtered =
                    filter(
                        given (irInst) ->
                            match irInst with
                                | IrInstruction { instruction = inst } -> not(isDeadInstruction(inst)(usedTemps)(readSlots))
                    )(
                        insts
                    )
                in
                    if length(filtered) == length(insts)
                    then insts
                    else fixpoint(filtered)
    in fixpoint(instructions))

let elideErasedRcDrops instructions =
    (let recursive collectLoadLocals insts idx acc =
        match insts with
            | [] -> acc
            | IrInstruction { instruction = LoadLocal(t, slot) } :: tail -> collectLoadLocals(tail)(idx + 1)(setAssociation(t)(idx)(acc))
            | _ :: tail -> collectLoadLocals(tail)(idx + 1)(acc)
    in
        let useCounts = countTempUses(instructions)([])
        in
            let loadLocalDefs = collectLoadLocals(instructions)(0)([])
            in
                let recursive findElidableDrops insts idx acc =
                    match insts with
                        | [] -> acc
                        | IrInstruction { instruction = RcDrop(src, _, _, false, _, _) } :: tail ->
                            let accWithDrop = idx :: acc
                            in
                                let accWithLoad =
                                    match lookupAssociation(src)(loadLocalDefs) with
                                        | Some(defIdx) ->
                                            if lookupAssociation(src)(useCounts) == Some(1)
                                            then defIdx :: accWithDrop
                                            else accWithDrop
                                        | None -> accWithDrop
                                in findElidableDrops(tail)(idx + 1)(accWithLoad)
                        | _ :: tail -> findElidableDrops(tail)(idx + 1)(acc)
                in
                    let toRemove = findElidableDrops(instructions)(0)([])
                    in
                        if length(toRemove) == 0
                        then instructions
                        else
                            let recursive filterInsts insts idx acc =
                                match insts with
                                    | [] -> reverse(acc)
                                    | head :: tail ->
                                        if listContains(idx)(toRemove)
                                        then filterInsts(tail)(idx + 1)(acc)
                                        else filterInsts(tail)(idx + 1)(head :: acc)
                            in filterInsts(instructions)(0)([]))

let isNonAllocatingInst nonAllocatingFns inst =
    match inst with
        | LoadConstInt(_, _) -> true
        | LoadConstFloat(_, _) -> true
        | LoadConstBool(_, _) -> true
        | LoadConstStr(_, _) -> true
        | LoadLocal(_, _) -> true
        | StoreLocal(_, _) -> true
        | LoadEnv(_, _) -> true
        | LoadMemOffset(_, _, _) -> true
        | StoreMemOffset(_, _, _) -> true
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
        | IntToFloat(_, _) -> true
        | FloatToInt(_, _) -> true
        | FloatUnaryIntrinsic(_, _, _) -> true
        | CallLibm(_, _, _) -> true
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
        | LoadFuncAddr(_, _) -> true
        | GetAdtTag(_, _) -> true
        | GetAdtField(_, _, _) -> true
        | SetAdtField(_, _, _) -> true
        | Borrow(_, _) -> true
        | DropReuse(_, _, _, _) -> true
        | RcDup(_, _, _, _) -> true
        | RcDrop(_, _, _, _, _, _) -> true
        | RcIsUnique(_, _) -> true
        | BytesLength(_, _) -> true
        | BytesGet(_, _, _) -> true
        | BytesCompare(_, _, _) -> true
        | BytesIndexOf(_, _, _, _) -> true
        | BytesHash(_, _) -> true
        | BytesGetU16Le(_, _, _) -> true
        | BytesGetU32Le(_, _, _) -> true
        | BytesGetU64Le(_, _, _) -> true
        | TextByteLength(_, _) -> true
        | SaveArenaState(_, _, _) -> true
        | RestoreArenaState(_, _, _, _) -> true
        | ReclaimArenaChunks(_, _, _) -> true
        | SaveStackPointer(_) -> true
        | RestoreStackPointer(_) -> true
        | Label(_) -> true
        | Jump(_) -> true
        | JumpIfFalse(_, _) -> true
        | SwitchTag(_, _, _) -> true
        | Return(_) -> true
        | CallKnown(_, label, _, _, _, _) -> listContains(label)(nonAllocatingFns)
        | _ -> false

let recursive allInstructionsNonAllocating nonAllocatingFns instructions =
    match instructions with
        | [] -> true
        | IrInstruction { instruction = inst } :: tail ->
            if isNonAllocatingInst(nonAllocatingFns)(inst)
            then allInstructionsNonAllocating(nonAllocatingFns)(tail)
            else false

let computeNonAllocatingFunctions functions =
    (let initialCandidates =
        map(given (f) ->
            match f with
                | IrFunction { label = l } -> l)(functions)
    in
        let recursive fixpoint candidates =
            let filtered =
                filter(
                    given (label) ->
                        match lookupFunction(label)(functions) with
                            | None -> false
                            | Some(fn) -> allInstructionsNonAllocating(candidates)(fn.instructions)
                )(
                    candidates
                )
            in
                if length(filtered) == length(candidates)
                then candidates
                else fixpoint(filtered)
        in fixpoint(initialCandidates))

let recursive lookupFunction label functions =
    match functions with
        | [] -> None
        | (IrFunction { label = l } as fn) :: tail ->
            if l == label
            then Some(fn)
            else lookupFunction(label)(tail)

let stripRedundantArenaBrackets nonAllocatingFns fn =
    (let isWholeNonAllocating =
        if listContains(fn.label)(nonAllocatingFns)
        then true
        else allInstructionsNonAllocating(nonAllocatingFns)(fn.instructions)
    in
        if isWholeNonAllocating
        then
            let stripped =
                filter(
                    given (irInst) ->
                        match irInst with
                            | IrInstruction { instruction = inst } ->
                                match inst with
                                    | SaveArenaState(_, _, _) -> false
                                    | RestoreArenaState(_, _, _, _) -> false
                                    | ReclaimArenaChunks(_, _, _) -> false
                                    | _ -> true
                )(
                    fn.instructions
                )
            in
                IrFunction(
                    label = fn.label,
                    instructions = stripped,
                    localCount = fn.localCount,
                    tempCount = fn.tempCount,
                    hasEnvAndArgParams = fn.hasEnvAndArgParams,
                    coroutine = fn.coroutine,
                    localNames = fn.localNames,
                    localTypes = fn.localTypes,
                    origin = fn.origin,
                    lifetimesPlaced = fn.lifetimesPlaced
                )
        else
            let recursive scanStraightLine insts idx acc toRemove =
                match insts with
                    | [] -> toRemove
                    | IrInstruction { instruction = SaveArenaState(curSlot, endSlot, _) } :: tail ->
                        let recursive findMatchingRestore rInsts j =
                            match rInsts with
                                | [] -> None
                                | IrInstruction { instruction = RestoreArenaState(rCur, rEnd, preSlot, _) } :: rTail ->
                                    if rCur == curSlot
                                    then
                                        if rEnd == endSlot
                                        then
                                            let reclaimIndices =
                                                match rTail with
                                                    | IrInstruction { instruction = ReclaimArenaChunks(recEnd, recPre, _) } :: _ ->
                                                        if recEnd == endSlot
                                                        then
                                                            if recPre == preSlot
                                                            then [j + 1]
                                                            else []
                                                        else []
                                                    | _ -> []
                                            in Some(j :: reclaimIndices)
                                        else None
                                    else None
                                | IrInstruction { instruction = inst } :: rTail ->
                                    match inst with
                                        | Label(_) -> None
                                        | Jump(_) -> None
                                        | JumpIfFalse(_, _) -> None
                                        | SwitchTag(_, _, _) -> None
                                        | _ ->
                                            if isNonAllocatingInst(nonAllocatingFns)(inst)
                                            then findMatchingRestore(rTail)(j + 1)
                                            else None
                        in
                            match findMatchingRestore(tail)(idx + 1) with
                                | Some(foundIndices) -> scanStraightLine(tail)(idx + 1)(acc)(idx :: append(foundIndices)(toRemove))
                                | None -> scanStraightLine(tail)(idx + 1)(acc)(toRemove)
                    | _ :: tail -> scanStraightLine(tail)(idx + 1)(acc)(toRemove)
            in
                let removals = scanStraightLine(fn.instructions)(0)([])([])
                in
                    if length(removals) == 0
                    then fn
                    else
                        let recursive filterRemovals insts idx acc =
                            match insts with
                                | [] -> reverse(acc)
                                | head :: tail ->
                                    if listContains(idx)(removals)
                                    then filterRemovals(tail)(idx + 1)(acc)
                                    else filterRemovals(tail)(idx + 1)(head :: acc)
                        in
                            let filtered = filterRemovals(fn.instructions)(0)([])
                            in
                                IrFunction(
                                    label = fn.label,
                                    instructions = filtered,
                                    localCount = fn.localCount,
                                    tempCount = fn.tempCount,
                                    hasEnvAndArgParams = fn.hasEnvAndArgParams,
                                    coroutine = fn.coroutine,
                                    localNames = fn.localNames,
                                    localTypes = fn.localTypes,
                                    origin = fn.origin,
                                    lifetimesPlaced = fn.lifetimesPlaced
                                ))

let optimizeIrFunction fn =
    (let insts0 = fn.instructions
    in
        let insts1 = elideTrivialOwnershipCopies(insts0)
        in
            let insts2 = fuseAdjacentRuntimeRcPairs(insts1)
            in
                let insts3 = devirtualizeKnownClosureCalls(insts2)
                in
                    let insts4 = foldConstants(insts3)
                    in
                        let insts5 = reduceIdentitiesAndStrength(insts4)
                        in
                            let insts6 = elideUnreachableCode(insts5)
                            in
                                let insts7 = elideDeadCode(insts6)
                                in
                                    let insts8 = elideErasedRcDrops(insts7)
                                    in
                                        IrFunction(
                                            label = fn.label,
                                            instructions = insts8,
                                            localCount = fn.localCount,
                                            tempCount = fn.tempCount,
                                            hasEnvAndArgParams = fn.hasEnvAndArgParams,
                                            coroutine = fn.coroutine,
                                            localNames = fn.localNames,
                                            localTypes = fn.localTypes,
                                            origin = fn.origin,
                                            lifetimesPlaced = fn.lifetimesPlaced
                                        ))

let optimizeIrProgramWithOptions options program =
    (let programAfterCtEval =
        if options.enableCompileTimeEval
        then evaluateCompileTimeConstants(program)
        else program
    in
        let optEntry = optimizeIrFunction(programAfterCtEval.entryFunction)
        in
            let optFuncs = map(optimizeIrFunction)(programAfterCtEval.functions)
            in
                let nonAllocating = computeNonAllocatingFunctions(optFuncs)
                in
                    let finalEntry = stripRedundantArenaBrackets(nonAllocating)(optEntry)
                    in
                        let finalFuncs = map(stripRedundantArenaBrackets(nonAllocating))(optFuncs)
                        in
                            IrProgram(
                                entryFunction = finalEntry,
                                functions = finalFuncs,
                                stringLiterals = programAfterCtEval.stringLiterals,
                                externalFunctions = programAfterCtEval.externalFunctions,
                                externalOpaqueTypes = programAfterCtEval.externalOpaqueTypes,
                                usesPrintInt = programAfterCtEval.usesPrintInt,
                                usesPrintStr = programAfterCtEval.usesPrintStr,
                                usesPrintBool = programAfterCtEval.usesPrintBool,
                                usesConcatStr = programAfterCtEval.usesConcatStr,
                                usesClosures = programAfterCtEval.usesClosures,
                                usesAsync = programAfterCtEval.usesAsync,
                                capabilityHandlerGlobals = programAfterCtEval.capabilityHandlerGlobals,
                                traitEvidence = programAfterCtEval.traitEvidence
                            ))

let optimizeIrProgram program = optimizeIrProgramWithOptions(defaultOptimizerOptions)(program)
