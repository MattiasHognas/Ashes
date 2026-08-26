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
//   6. Constant propagation and folding (arithmetic, bitwise, comparison; temp and local-slot facts
//      with a true meet over every predecessor edge at a label once all edges are observed, and a
//      conservative clear at a label with a not-yet-observed backward edge; a JumpIfFalse whose
//      condition is known folds to a fall-through or an unconditional Jump, and a SwitchTag whose
//      tag is known folds to a Jump to the taken case)
//   7. Identity elimination and strength reduction (x+0, x-0, x*0, x*1, x*2, x/1), followed by a
//      second ownership-copy elision: an identity rewrites into a Borrow copy rather than
//      retargeting its uses, and the elision that already ran never revisits that new copy
//   8. Local common-subexpression elimination within one straight-line block (reset at every
//      label): a duplicate GetAdtField read, or a duplicate CallKnown of a function the
//      compile-time-evaluation purity oracle proves pure, forwards to the first occurrence through
//      a Borrow copy; operands are canonicalized through a LoadLocal/StoreLocal/Borrow/RcDup alias
//      map (with the function's own env/arg slots seeded to a stable identity) before keying the
//      caches, which any instruction that could write through an existing pointer invalidates,
//      while arena and stack bookkeeping never does; followed by a third ownership-copy elision
//      that forwards the copies it introduced
//   9. Unreachable code elimination (after Jump, Return, SwitchTag; a label with no remaining branch
//      reference inside an unreachable region is dropped with its body)
//   10. Dead code elimination (unused LoadConst, StoreLocal, MakeClosure)
//   11. Erased RcDrop marker elision
//   12. Program-level returned-closure devirtualization: a whole-program least fixpoint of the
//       functions whose every Return is one heap MakeClosure label (directly, or transitively
//       through a CallKnown to a function already proven), then a per-function local fixpoint
//       rewriting each CallClosure on such a call's result into an environment-word read plus
//       a direct CallKnown, so a curry of any depth resolves fully
//   13. Interprocedural redundant arena bracket elimination (whole-function and straight-line regions)

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
    value optimizeIrFunctionWithEvaluable,
)

type IrOptimizerOptions =
    | enableCompileTimeEval: Bool
    | enableInlining: Bool
    | enableDeadCodeElision: Bool
    | enableIdentityReduction: Bool

// Constant facts carried by the folding pass. Temps are single-assignment, so a temp fact never
// changes once recorded; a local slot is ordinary mutable storage, so a store of an unknown or
// non-scalar value kills its fact and a slot holds at most one of Int/Float/Bool at a time.
type FoldFacts =
    | ints: List((IrTemp, Int))
    | floats: List((IrTemp, Float))
    | bools: List((IrTemp, Bool))
    | localInts: List((IrLocal, Int))
    | localFloats: List((IrLocal, Float))
    | localBools: List((IrLocal, Bool))

// Local common-subexpression state for one straight-line block. Cache values are always real,
// already-emitted temps; cache keys are canonical identities, which may be the negative sentinel
// of a function's own env/arg slot (no visible defining instruction) and so must never be emitted.
type LocalCseState =
    | fieldCache: List(((Int, Int), IrTemp))
    | callCache: List(((Str, Int, Int, Int, Bool), IrTemp))
    | valueOf: List((IrTemp, Int))
    | slotValue: List((IrLocal, Int))

let defaultOptimizerOptions =
    IrOptimizerOptions(
        enableCompileTimeEval = true,
        enableInlining = true,
        enableDeadCodeElision = true,
        enableIdentityReduction = true
    )

let recursive lookupAssociation key entries =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupAssociation(key)(tail)

let recursive setAssociation key value entries =
    match entries with
        | [] -> [(key, value)]
        | (k, v) :: tail ->
            if k == key
            then (key, value) :: tail
            else (k, v) :: setAssociation(key)(value)(tail)

let recursive removeAssociation key entries =
    match entries with
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

let remapSourceTemps inst (remap: List((IrTemp, IrTemp))) =
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
                let recursive addUses us entries =
                    match us with
                        | [] -> entries
                        | u :: uTail ->
                            let count =
                                match lookupAssociation(u)(entries) with
                                    | Some(c) -> c + 1
                                    | None -> 1
                            in addUses(uTail)(setAssociation(u)(count)(entries))
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
                                    if !isTempUsedInInstructions(tail)(rDest)
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
                let recursive addDefs ds entries =
                    match ds with
                        | [] -> entries
                        | d :: dTail ->
                            let count =
                                match lookupAssociation(d)(entries) with
                                    | Some(c) -> c + 1
                                    | None -> 1
                            in addDefs(dTail)(setAssociation(d)(count)(entries))
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

let switchCaseTag (switchCase: IrSwitchCase) = switchCase.tag

let switchCaseLabel (switchCase: IrSwitchCase) = switchCase.label

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
                    let recursive addCases cs entries =
                        match cs with
                            | [] -> entries
                            | switchCase :: cTail ->
                                let l = switchCaseLabel(switchCase)
                                in
                                    let c =
                                        match lookupAssociation(l)(entries) with
                                            | Some(n) -> n + 1
                                            | None -> 1
                                    in addCases(cTail)(setAssociation(l)(c)(entries))
                    in
                        let mapWithCases = addCases(cases)(acc)
                        in
                            let defCount =
                                match lookupAssociation(defaultLabel)(mapWithCases) with
                                    | Some(n) -> n + 1
                                    | None -> 1
                            in countBranchRefsToLabels(tail)(setAssociation(defaultLabel)(defCount)(mapWithCases))
                | _ -> countBranchRefsToLabels(tail)(acc)

let emptyFoldFacts = FoldFacts(ints = [], floats = [], bools = [], localInts = [], localFloats = [], localBools = [])

let factsWithInt dest value (facts: FoldFacts) = facts with ints = setAssociation(dest)(value)(facts.ints)

let factsWithFloat dest value (facts: FoldFacts) = facts with floats = setAssociation(dest)(value)(facts.floats)

let factsWithBool dest value (facts: FoldFacts) = facts with bools = setAssociation(dest)(value)(facts.bools)

let factsWithStore slot source (facts: FoldFacts) =
    (let cleared =
        FoldFacts(
            ints = facts.ints,
            floats = facts.floats,
            bools = facts.bools,
            localInts = removeAssociation(slot)(facts.localInts),
            localFloats = removeAssociation(slot)(facts.localFloats),
            localBools = removeAssociation(slot)(facts.localBools)
        )
    in
        match lookupAssociation(source)(facts.ints) with
            | Some(value) -> cleared with localInts = setAssociation(slot)(value)(cleared.localInts)
            | None ->
                match lookupAssociation(source)(facts.floats) with
                    | Some(value) -> cleared with localFloats = setAssociation(slot)(value)(cleared.localFloats)
                    | None ->
                        match lookupAssociation(source)(facts.bools) with
                            | Some(value) -> cleared with localBools = setAssociation(slot)(value)(cleared.localBools)
                            | None -> cleared)

let intEquals (left: Int) (right: Int) = left == right

let floatEquals (left: Float) (right: Float) = left == right

let boolEquals (left: Bool) (right: Bool) = left == right

let recursive associationAgreesEverywhere equalValues key value snapshots =
    match snapshots with
        | [] -> true
        | snapshot :: tail ->
            match lookupAssociation(key)(snapshot) with
                | Some(other) ->
                    if equalValues(other)(value)
                    then associationAgreesEverywhere(equalValues)(key)(value)(tail)
                    else false
                | None -> false

// The meet over predecessor edges: an entry survives only when every other edge carries the same
// key with the same value. Value equality arrives as a function so the helper carries a single
// abstract requirement (the key's equality) like every other association helper here.
let recursive meetAssociation equalValues entries others =
    match entries with
        | [] -> []
        | (key, value) :: tail ->
            if associationAgreesEverywhere(equalValues)(key)(value)(others)
            then (key, value) :: meetAssociation(equalValues)(tail)(others)
            else meetAssociation(equalValues)(tail)(others)

let snapshotInts (snapshot: FoldFacts) = snapshot.ints

let snapshotFloats (snapshot: FoldFacts) = snapshot.floats

let snapshotBools (snapshot: FoldFacts) = snapshot.bools

let snapshotLocalInts (snapshot: FoldFacts) = snapshot.localInts

let snapshotLocalFloats (snapshot: FoldFacts) = snapshot.localFloats

let snapshotLocalBools (snapshot: FoldFacts) = snapshot.localBools

let meetFacts (snapshots: List(FoldFacts)) =
    match snapshots with
        | [] -> emptyFoldFacts
        | first :: rest ->
            FoldFacts(
                ints = meetAssociation(intEquals)(first.ints)(map(snapshotInts)(rest)),
                floats = meetAssociation(floatEquals)(first.floats)(map(snapshotFloats)(rest)),
                bools = meetAssociation(boolEquals)(first.bools)(map(snapshotBools)(rest)),
                localInts = meetAssociation(intEquals)(first.localInts)(map(snapshotLocalInts)(rest)),
                localFloats = meetAssociation(floatEquals)(first.localFloats)(map(snapshotLocalFloats)(rest)),
                localBools = meetAssociation(boolEquals)(first.localBools)(map(snapshotLocalBools)(rest))
            )

// One snapshot per predecessor edge observed so far, keyed by target label.
let recordEdgeSnapshot target facts saved =
    match lookupAssociation(target)(saved) with
        | Some(snapshots) -> setAssociation(target)(append(snapshots)([facts]))(saved)
        | None -> setAssociation(target)([facts])(saved)

let recursive recordSwitchCaseSnapshots cases facts saved =
    match cases with
        | [] -> saved
        | switchCase :: tail -> recordSwitchCaseSnapshots(tail)(facts)(recordEdgeSnapshot(switchCaseLabel(switchCase))(facts)(saved))

// The case a switch on an already-known tag takes: the first case carrying that tag, else the default.
let recursive switchTakenLabel tag cases defaultLabel =
    match cases with
        | [] -> defaultLabel
        | switchCase :: rest ->
            if switchCaseTag(switchCase) == tag
            then switchCaseLabel(switchCase)
            else switchTakenLabel(tag)(rest)(defaultLabel)

// The facts entering a label. When every predecessor edge (each explicit branch plus fall-through,
// if any) has been observed by this forward scan, the entering facts are the meet over those edges;
// a single-predecessor label and a fall-through-only label are the one-snapshot special case. A
// predecessor not yet observed (a backward branch, whose source appears later) is unknowable here,
// so a loop header conservatively clears every fact.
let factsAtLabel name branchRefs (facts: FoldFacts) saved prevIsTerminator =
    (let branchCount =
        match lookupAssociation(name)(branchRefs) with
            | Some(count) -> count
            | None -> 0
    in
        let fallthrough =
            if prevIsTerminator
            then 0
            else 1
        in
            let snapshots =
                match lookupAssociation(name)(saved) with
                    | Some(list) -> list
                    | None -> []
            in
                let total = branchCount + fallthrough
                in
                    let observed = length(snapshots) + fallthrough
                    in
                        if total > 0
                        then
                            if observed == total
                            then
                                meetFacts(
                                    if fallthrough == 1
                                    then facts :: snapshots
                                    else snapshots
                                )
                            else emptyFoldFacts
                        else emptyFoldFacts)

let foldIntPair (facts: FoldFacts) left right =
    match (lookupAssociation(left)(facts.ints), lookupAssociation(right)(facts.ints)) with
        | (Some(leftValue), Some(rightValue)) -> Some((leftValue, rightValue))
        | _ -> None

let foldFloatPair (facts: FoldFacts) left right =
    match (lookupAssociation(left)(facts.floats), lookupAssociation(right)(facts.floats)) with
        | (Some(leftValue), Some(rightValue)) -> Some((leftValue, rightValue))
        | _ -> None

let intResult dest value location = IrInstruction(instruction = LoadConstInt(dest)(value), location = location)

let floatResult dest value location = IrInstruction(instruction = LoadConstFloat(dest)(value), location = location)

let boolResult dest value location = IrInstruction(instruction = LoadConstBool(dest)(value), location = location)

let foldConstants instructions =
    (let branchRefs = countBranchRefsToLabels(instructions)([])
    in
        let recursive foldLoop insts (facts: FoldFacts) saved prevIsTerminator acc =
            match insts with
                | [] -> reverse(acc)
                | (IrInstruction { instruction = inst, location = loc } as irInst) :: tail ->
                    (match inst with
                        | LoadConstInt(dest, value) -> foldLoop(tail)(factsWithInt(dest)(value)(facts))(saved)(false)(irInst :: acc)
                        | LoadConstFloat(dest, value) -> foldLoop(tail)(factsWithFloat(dest)(value)(facts))(saved)(false)(irInst :: acc)
                        | LoadConstBool(dest, value) -> foldLoop(tail)(factsWithBool(dest)(value)(facts))(saved)(false)(irInst :: acc)
                        | AddInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) -> foldLoop(tail)(factsWithInt(dest)(lv + rv)(facts))(saved)(false)(intResult(dest)(lv + rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | SubInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) -> foldLoop(tail)(factsWithInt(dest)(lv - rv)(facts))(saved)(false)(intResult(dest)(lv - rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | MulInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) -> foldLoop(tail)(factsWithInt(dest)(lv * rv)(facts))(saved)(false)(intResult(dest)(lv * rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | DivInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    if rv != 0
                                    then foldLoop(tail)(factsWithInt(dest)(lv / rv)(facts))(saved)(false)(intResult(dest)(lv / rv)(loc) :: acc)
                                    else foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | DivUInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) ->
                                    if rv != 0
                                    then foldLoop(tail)(factsWithInt(dest)(lv / rv)(facts))(saved)(false)(intResult(dest)(lv / rv)(loc) :: acc)
                                    else foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | AndInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) -> foldLoop(tail)(factsWithInt(dest)(lv & rv)(facts))(saved)(false)(intResult(dest)(lv & rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | OrInt(dest, l, r) ->
                            (match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) -> (foldLoop(tail)(factsWithInt(dest)(lv | rv)(facts))(saved)(false)(intResult(dest)(lv | rv)(loc) :: acc))
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc))
                        | XorInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) -> foldLoop(tail)(factsWithInt(dest)(lv ^ rv)(facts))(saved)(false)(intResult(dest)(lv ^ rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | ShlInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) -> foldLoop(tail)(factsWithInt(dest)(lv << rv)(facts))(saved)(false)(intResult(dest)(lv << rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | ShrInt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) -> foldLoop(tail)(factsWithInt(dest)(lv >> rv)(facts))(saved)(false)(intResult(dest)(lv >> rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | AddFloat(dest, l, r) ->
                            match foldFloatPair(facts)(l)(r) with
                                | Some((lv, rv)) -> foldLoop(tail)(factsWithFloat(dest)(lv + rv)(facts))(saved)(false)(floatResult(dest)(lv + rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | SubFloat(dest, l, r) ->
                            match foldFloatPair(facts)(l)(r) with
                                | Some((lv, rv)) -> foldLoop(tail)(factsWithFloat(dest)(lv - rv)(facts))(saved)(false)(floatResult(dest)(lv - rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | MulFloat(dest, l, r) ->
                            match foldFloatPair(facts)(l)(r) with
                                | Some((lv, rv)) -> foldLoop(tail)(factsWithFloat(dest)(lv * rv)(facts))(saved)(false)(floatResult(dest)(lv * rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | DivFloat(dest, l, r) ->
                            match foldFloatPair(facts)(l)(r) with
                                | Some((lv, rv)) -> foldLoop(tail)(factsWithFloat(dest)(lv / rv)(facts))(saved)(false)(floatResult(dest)(lv / rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | CmpIntEq(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) -> foldLoop(tail)(factsWithBool(dest)(lv == rv)(facts))(saved)(false)(boolResult(dest)(lv == rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | CmpIntNe(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) -> foldLoop(tail)(factsWithBool(dest)(lv != rv)(facts))(saved)(false)(boolResult(dest)(lv != rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | CmpIntGt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) -> foldLoop(tail)(factsWithBool(dest)(lv > rv)(facts))(saved)(false)(boolResult(dest)(lv > rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | CmpIntGe(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) -> foldLoop(tail)(factsWithBool(dest)(lv >= rv)(facts))(saved)(false)(boolResult(dest)(lv >= rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | CmpIntLt(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) -> foldLoop(tail)(factsWithBool(dest)(lv < rv)(facts))(saved)(false)(boolResult(dest)(lv < rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | CmpIntLe(dest, l, r) ->
                            match foldIntPair(facts)(l)(r) with
                                | Some((lv, rv)) -> foldLoop(tail)(factsWithBool(dest)(lv <= rv)(facts))(saved)(false)(boolResult(dest)(lv <= rv)(loc) :: acc)
                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | StoreLocal(slot, source) -> foldLoop(tail)(factsWithStore(slot)(source)(facts))(saved)(false)(irInst :: acc)
                        | LoadLocal(dest, slot) ->
                            match lookupAssociation(slot)(facts.localInts) with
                                | Some(value) -> foldLoop(tail)(factsWithInt(dest)(value)(facts))(saved)(false)(intResult(dest)(value)(loc) :: acc)
                                | None ->
                                    match lookupAssociation(slot)(facts.localFloats) with
                                        | Some(value) -> foldLoop(tail)(factsWithFloat(dest)(value)(facts))(saved)(false)(floatResult(dest)(value)(loc) :: acc)
                                        | None ->
                                            match lookupAssociation(slot)(facts.localBools) with
                                                | Some(value) -> foldLoop(tail)(factsWithBool(dest)(value)(facts))(saved)(false)(boolResult(dest)(value)(loc) :: acc)
                                                | None -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc)
                        | Label(name) -> foldLoop(tail)(factsAtLabel(name)(branchRefs)(facts)(saved)(prevIsTerminator))(removeAssociation(name)(saved))(false)(irInst :: acc)
                        | Jump(target) -> foldLoop(tail)(facts)(recordEdgeSnapshot(target)(facts)(saved))(true)(irInst :: acc)
                        | JumpIfFalse(condition, target) ->
                            match lookupAssociation(condition)(facts.bools) with
                                | Some(true) -> foldLoop(tail)(facts)(saved)(false)(acc)
                                | Some(false) -> foldLoop(tail)(facts)(recordEdgeSnapshot(target)(facts)(saved))(true)(IrInstruction(instruction = Jump(target), location = loc) :: acc)
                                | None -> foldLoop(tail)(facts)(recordEdgeSnapshot(target)(facts)(saved))(false)(irInst :: acc)
                        | SwitchTag(tagTemp, cases, defaultLabel) ->
                            match lookupAssociation(tagTemp)(facts.ints) with
                                | Some(tag) -> foldLoop(tail)(facts)(recordEdgeSnapshot(switchTakenLabel(tag)(cases)(defaultLabel))(facts)(saved))(true)(IrInstruction(instruction = Jump(switchTakenLabel(tag)(cases)(defaultLabel)), location = loc) :: acc)
                                | None -> foldLoop(tail)(facts)(recordEdgeSnapshot(defaultLabel)(facts)(recordSwitchCaseSnapshots(cases)(facts)(saved)))(true)(irInst :: acc)
                        | Return(_) -> foldLoop(tail)(facts)(saved)(true)(irInst :: acc)
                        | _ -> foldLoop(tail)(facts)(saved)(false)(irInst :: acc))
        in foldLoop(instructions)(emptyFoldFacts)([])(false)([]))

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

// Predecessor counts are rebuilt from this pass's own input rather than shared with the folding
// pass: branch folding above can remove the only edge that used to target a label, so a label with
// zero remaining branch references that sits inside an unreachable region cannot be entered at all
// and is dropped together with its body, instead of surviving as emitted-but-dead code.
let elideUnreachableCode instructions =
    (let branchRefs = countBranchRefsToLabels(instructions)([])
    in
        let recursive elideLoop insts unreachable acc =
            match insts with
                | [] -> reverse(acc)
                | (IrInstruction { instruction = inst } as irInst) :: tail ->
                    match inst with
                        | Label(name) ->
                            let referenced =
                                match lookupAssociation(name)(branchRefs) with
                                    | Some(count) -> count > 0
                                    | None -> false
                            in
                                if unreachable
                                then
                                    if referenced
                                    then elideLoop(tail)(false)(irInst :: acc)
                                    else elideLoop(tail)(true)(acc)
                                else elideLoop(tail)(false)(irInst :: acc)
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
        | LoadConstInt(t, _) -> !listContains(t)(usedTemps)
        | LoadConstFloat(t, _) -> !listContains(t)(usedTemps)
        | LoadConstBool(t, _) -> !listContains(t)(usedTemps)
        | StoreLocal(slot, _) -> !listContains(slot)(readSlots)
        | MakeClosure(t, _, _, _, _, _, _) -> !listContains(t)(usedTemps)
        | MakeClosureStack(t, _, _, _, _, _) -> !listContains(t)(usedTemps)
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
                                | IrInstruction { instruction = inst } -> !isDeadInstruction(inst)(usedTemps)(readSlots)
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

let recursive lookupFunction label (functions: List(IrFunction)) =
    match functions with
        | [] -> None
        | (IrFunction { label = l } as fn) :: tail ->
            if l == label
            then Some(fn)
            else lookupFunction(label)(tail)

let functionLabel (fn: IrFunction) = fn.label

let functionInstructions (fn: IrFunction) = fn.instructions

// Every single-defined temp mapped to its defining instruction.
let recursive collectSingleDefiningInstructions instructions defCounts acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            let recursive addDefs ds entries =
                match ds with
                    | [] -> entries
                    | d :: dTail ->
                        if lookupAssociation(d)(defCounts) == Some(1)
                        then addDefs(dTail)(setAssociation(d)(inst)(entries))
                        else addDefs(dTail)(entries)
            in collectSingleDefiningInstructions(tail)(defCounts)(addDefs(getDefinedTemps(inst))(acc))

// The heap closure label a temp provably holds: directly from a MakeClosure (never a
// MakeClosureStack, whose environment lives in its defining function's own frame and is gone once
// that function returns), or transitively through a CallKnown to a function already proven to
// return one label.
let knownClosureLabelOf sourceTemp singleDefs knownLabels =
    match lookupAssociation(sourceTemp)(singleDefs) with
        | Some(MakeClosure(_, fnLabel, _, _, _, _, _)) -> Some(fnLabel)
        | Some(CallKnown(_, fnLabel, _, _, _, _)) -> lookupAssociation(fnLabel)(knownLabels)
        | _ -> None

let recursive returnedClosureLabel instructions singleDefs knownLabels current sawReturn =
    match instructions with
        | [] ->
            if sawReturn
            then current
            else None
        | IrInstruction { instruction = Return(source) } :: tail ->
            match knownClosureLabelOf(source)(singleDefs)(knownLabels) with
                | None -> None
                | Some(label) ->
                    match current with
                        | None -> returnedClosureLabel(tail)(singleDefs)(knownLabels)(Some(label))(true)
                        | Some(previous) ->
                            if previous == label
                            then returnedClosureLabel(tail)(singleDefs)(knownLabels)(current)(true)
                            else None
        | _ :: tail -> returnedClosureLabel(tail)(singleDefs)(knownLabels)(current)(sawReturn)

// The one heap closure label every Return of the function provably yields, if any.
let tryDetermineKnownReturnedClosureLabel (fn: IrFunction) knownLabels =
    (let defCounts = countDefinitions(fn.instructions)([])
    in
        let singleDefs = collectSingleDefiningInstructions(fn.instructions)(defCounts)([])
        in returnedClosureLabel(fn.instructions)(singleDefs)(knownLabels)(None)(false))

let recursive addKnownReturnedClosureLabels functions knownLabels changed =
    match functions with
        | [] -> (knownLabels, changed)
        | fn :: rest ->
            match lookupAssociation(functionLabel(fn))(knownLabels) with
                | Some(_) -> addKnownReturnedClosureLabels(rest)(knownLabels)(changed)
                | None ->
                    match tryDetermineKnownReturnedClosureLabel(fn)(knownLabels) with
                        | Some(label) -> addKnownReturnedClosureLabels(rest)(setAssociation(functionLabel(fn))(label)(knownLabels))(true)
                        | None -> addKnownReturnedClosureLabels(rest)(knownLabels)(changed)

// Whole-program least fixpoint: each pass admits the functions whose returns resolve against the
// labels known so far, so a chain of curried helpers converges over several passes while a
// genuine cycle never does.
let recursive computeKnownReturnedClosureLabels (functions: List(IrFunction)) knownLabels =
    match addKnownReturnedClosureLabels(functions)(knownLabels)(false) with
        | (nextLabels, true) -> computeKnownReturnedClosureLabels(functions)(nextLabels)
        | (nextLabels, false) -> nextLabels

// A CallClosure whose closure temp is the result of a CallKnown to a function with a known
// returned label becomes a plain read of the closure object's environment word (offset 8, the
// layout the ordinary closure call reads) followed by a direct CallKnown of that label. The read
// neither consumes nor extends the closure object's lifetime, so drop placement is undisturbed.
let recursive devirtualizeReturnedClosureCallsOnce insts singleDefs knownLabels nextTemp acc changed =
    match insts with
        | [] -> (reverse(acc), nextTemp, changed)
        | (IrInstruction { instruction = CallClosure(dest, closureTemp, argTemp, flagTemp), location = loc } as irInst) :: tail ->
            match lookupAssociation(closureTemp)(singleDefs) with
                | Some(CallKnown(_, calleeLabel, _, _, _, _)) ->
                    match lookupAssociation(calleeLabel)(knownLabels) with
                        | Some(label) ->
                            devirtualizeReturnedClosureCallsOnce(tail)(singleDefs)(knownLabels)(nextTemp + 1)(
                                IrInstruction(instruction = CallKnown(dest)(label)(nextTemp)(argTemp)(flagTemp)(false), location = loc) :: IrInstruction(instruction = LoadMemOffset(nextTemp)(closureTemp)(8), location = loc) :: acc
                            )(
                                true
                            )
                        | None -> devirtualizeReturnedClosureCallsOnce(tail)(singleDefs)(knownLabels)(nextTemp)(irInst :: acc)(changed)
                | _ -> devirtualizeReturnedClosureCallsOnce(tail)(singleDefs)(knownLabels)(nextTemp)(irInst :: acc)(changed)
        | head :: tail -> devirtualizeReturnedClosureCallsOnce(tail)(singleDefs)(knownLabels)(nextTemp)(head :: acc)(changed)

// Iterates to the function's own local fixed point so a curry deeper than two arguments resolves
// fully in one optimization: each rewrite turns a CallClosure into a CallKnown that the next
// CallClosure in the chain can resolve through.
let recursive devirtualizeReturnedClosureCallsInFunction knownLabels (fn: IrFunction) =
    (let defCounts = countDefinitions(fn.instructions)([])
    in
        let singleDefs = collectSingleDefiningInstructions(fn.instructions)(defCounts)([])
        in
            match devirtualizeReturnedClosureCallsOnce(fn.instructions)(singleDefs)(knownLabels)(fn.tempCount)([])(false) with
                | (_, _, false) -> fn
                | (rewritten, nextTemp, true) -> devirtualizeReturnedClosureCallsInFunction(knownLabels)((fn with instructions = rewritten, tempCount = nextTemp)))

let computeNonAllocatingFunctions (functions: List(IrFunction)) =
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

let stripRedundantArenaBrackets nonAllocatingFns (fn: IrFunction) =
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

// Local common-subexpression elimination, scoped to a single straight-line block (reset at every
// label, never across control flow). Operands are canonicalized through a
// LoadLocal/StoreLocal/Borrow/RcDup alias map before keying the caches: real Ashes IR round-trips
// almost every value through a local slot, so raw-temp-identity keying would fold nothing (the
// ubiquitous `let x = p.x in let y = p.x` shape never matches without it).
// Deny by default: only these instruction kinds are known never to write through an existing
// pointer or call into unmodeled effects. Arena and stack bookkeeping qualifies because it moves
// an allocator cursor rather than writing through a pointer, and every `let` binding gets its own
// such bracket in practice, so treating it as aliasing would silence the pass almost everywhere.
let isLocalCseSafeInstruction inst =
    match inst with
        | LoadConstInt(_, _) -> true
        | LoadConstFloat(_, _) -> true
        | LoadConstBool(_, _) -> true
        | LoadConstStr(_, _) -> true
        | LoadLocal(_, _) -> true
        | StoreLocal(_, _) -> true
        | RcDup(_, _, _, _) -> true
        | Borrow(_, _) -> true
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
        | GetAdtTag(_, _) -> true
        | LoadArgumentOwnership(_) -> true
        | LoadFuncAddr(_, _) -> true
        | SaveArenaState(_, _, _) -> true
        | RestoreArenaState(_, _, _, _) -> true
        | ReclaimArenaChunks(_, _, _) -> true
        | SaveStackPointer(_) -> true
        | RestoreStackPointer(_) -> true
        | _ -> false
// Negative, so it can never collide with a real temp: the identity of the value the backend's
// entry prologue stores into env/arg slot 0/1 before any instruction this pass can see.

let entrySlotIdentity (slot: IrLocal) = -1 - slot

let emptyLocalCseState hasEnvAndArgParams =
    LocalCseState(
        fieldCache = [],
        callCache = [],
        valueOf = [],
        slotValue = if hasEnvAndArgParams
        then [(0, entrySlotIdentity(0)), (1, entrySlotIdentity(1))]
        else []
    )

let recursive resolveCseValue (valueOf: List((IrTemp, Int))) (temp: Int) =
    match lookupAssociation(temp)(valueOf) with
        | Some(alias) -> resolveCseValue(valueOf)(alias)
        | None -> temp

let recursive lookupFieldCache (ptr: Int) (field: Int) (entries: List(((Int, Int), IrTemp))) =
    match entries with
        | [] -> None
        | ((cachedPtr, cachedField), cached) :: tail ->
            if cachedPtr == ptr
            then
                if cachedField == field
                then Some(cached)
                else lookupFieldCache(ptr)(field)(tail)
            else lookupFieldCache(ptr)(field)(tail)

let callKeyMatches (label: Str) (env: Int) (arg: Int) (flag: Int) (stackAllocated: Bool) (key: (Str, Int, Int, Int, Bool)) =
    match key with
        | (cachedLabel, cachedEnv, cachedArg, cachedFlag, cachedStack) ->
            if cachedLabel == label
            then
                if cachedEnv == env
                then
                    if cachedArg == arg
                    then
                        if cachedFlag == flag
                        then cachedStack == stackAllocated
                        else false
                    else false
                else false
            else false

let recursive lookupCallCache label env arg flag stackAllocated (entries: List(((Str, Int, Int, Int, Bool), IrTemp))) =
    match entries with
        | [] -> None
        | (key, cached) :: tail ->
            if callKeyMatches(label)(env)(arg)(flag)(stackAllocated)(key)
            then Some(cached)
            else lookupCallCache(label)(env)(arg)(flag)(stackAllocated)(tail)

let cseInvalidateCaches (state: LocalCseState) = state with fieldCache = [], callCache = []

// Pure value-identity aliases: the alias map records the canonical value so the caches can be
// keyed by value rather than by raw temp.
let trackLocalCseAlias inst (state: LocalCseState) =
    match inst with
        | Borrow(target, source) -> Some((state with valueOf = setAssociation(target)(resolveCseValue(state.valueOf)(source))(state.valueOf)))
        | RcDup(target, source, _, _) -> Some((state with valueOf = setAssociation(target)(resolveCseValue(state.valueOf)(source))(state.valueOf)))
        | StoreLocal(slot, source) -> Some((state with slotValue = setAssociation(slot)(resolveCseValue(state.valueOf)(source))(state.slotValue)))
        | LoadLocal(target, slot) ->
            match lookupAssociation(slot)(state.slotValue) with
                | Some(known) -> Some((state with valueOf = setAssociation(target)(known)(state.valueOf)))
                | None -> Some((state with valueOf = removeAssociation(target)(state.valueOf)))
        | _ -> None

// A cache hit becomes a Borrow copy of the first occurrence's result, exactly the idiom the
// identity reduction uses, so the ownership-copy elision that follows forwards and erases it.
let eliminateLocalCseInstruction evaluable inst loc (state: LocalCseState) =
    match inst with
        | GetAdtField(target, ptr, field) ->
            let key = resolveCseValue(state.valueOf)(ptr)
            in
                match lookupFieldCache(key)(field)(state.fieldCache) with
                    | Some(cached) -> ((state with valueOf = setAssociation(target)(cached)(state.valueOf)), IrInstruction(instruction = Borrow(target)(cached), location = loc))
                    | None -> ((state with fieldCache = ((key, field), target) :: state.fieldCache), IrInstruction(instruction = inst, location = loc))
        | CallKnown(dest, label, envTemp, argTemp, flagTemp, stackAllocated) ->
            if listContains(label)(evaluable)
            then
                let env = resolveCseValue(state.valueOf)(envTemp)
                in
                    let arg = resolveCseValue(state.valueOf)(argTemp)
                    in
                        let flag = resolveCseValue(state.valueOf)(flagTemp)
                        in
                            match lookupCallCache(label)(env)(arg)(flag)(stackAllocated)(state.callCache) with
                                | Some(cached) -> ((state with valueOf = setAssociation(dest)(cached)(state.valueOf)), IrInstruction(instruction = Borrow(dest)(cached), location = loc))
                                | None -> ((state with callCache = ((label, env, arg, flag, stackAllocated), dest) :: state.callCache), IrInstruction(instruction = inst, location = loc))
            else (cseInvalidateCaches(state), IrInstruction(instruction = inst, location = loc))
        | _ ->
            if isLocalCseSafeInstruction(inst)
            then (state, IrInstruction(instruction = inst, location = loc))
            else (cseInvalidateCaches(state), IrInstruction(instruction = inst, location = loc))

let recursive eliminateLocalRedundantComputationPass evaluable hasEnvAndArgParams instructions (state: LocalCseState) acc =
    match instructions with
        | [] -> reverse(acc)
        | (IrInstruction { instruction = Label(_) } as labelInst) :: tail -> eliminateLocalRedundantComputationPass(evaluable)(hasEnvAndArgParams)(tail)(emptyLocalCseState(hasEnvAndArgParams))(labelInst :: acc)
        | (IrInstruction { instruction = inst, location = loc } as irInst) :: tail ->
            match trackLocalCseAlias(inst)(state) with
                | Some(nextState) -> eliminateLocalRedundantComputationPass(evaluable)(hasEnvAndArgParams)(tail)(nextState)(irInst :: acc)
                | None ->
                    match eliminateLocalCseInstruction(evaluable)(inst)(loc)(state) with
                        | (nextState, rewritten) -> eliminateLocalRedundantComputationPass(evaluable)(hasEnvAndArgParams)(tail)(nextState)(rewritten :: acc)
// Slots 0 (env) and 1 (arg) are populated by the backend's entry prologue, a native store never
// visible as a StoreLocal, so they are seeded with a stable identity at function entry and again
// at every label: without it every read of a function's own argument looks like an unknown value.

let eliminateLocalRedundantComputation evaluable hasEnvAndArgParams instructions = eliminateLocalRedundantComputationPass(evaluable)(hasEnvAndArgParams)(instructions)(emptyLocalCseState(hasEnvAndArgParams))([])

let optimizeIrFunctionWithEvaluable evaluable (fn: IrFunction) =
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
                        let insts5 = elideTrivialOwnershipCopies(reduceIdentitiesAndStrength(insts4))
                        in
                            let insts6 = elideTrivialOwnershipCopies(eliminateLocalRedundantComputation(evaluable)(fn.hasEnvAndArgParams)(insts5))
                            in
                                let insts7 = elideUnreachableCode(insts6)
                                in
                                    let insts8 = elideDeadCode(insts7)
                                    in
                                        let insts9 = elideErasedRcDrops(insts8)
                                        in
                                            IrFunction(
                                                label = fn.label,
                                                instructions = insts9,
                                                localCount = fn.localCount,
                                                tempCount = fn.tempCount,
                                                hasEnvAndArgParams = fn.hasEnvAndArgParams,
                                                coroutine = fn.coroutine,
                                                localNames = fn.localNames,
                                                localTypes = fn.localTypes,
                                                origin = fn.origin,
                                                lifetimesPlaced = fn.lifetimesPlaced
                                            ))

// The per-function pipeline without a purity oracle: no known call is ever merged.
let optimizeIrFunction (fn: IrFunction) = optimizeIrFunctionWithEvaluable([])(fn)

let optimizeIrProgramWithOptions (options: IrOptimizerOptions) (program: IrProgram) =
    (let programAfterCtEval =
        if options.enableCompileTimeEval
        then evaluateCompileTimeConstants(program)
        else program
    in
        let evaluable = computeEvaluableFunctions(programAfterCtEval)
        in
            let optEntry = optimizeIrFunctionWithEvaluable(evaluable)(programAfterCtEval.entryFunction)
            in
                let optFuncs = map(optimizeIrFunctionWithEvaluable(evaluable))(programAfterCtEval.functions)
                in
                    let knownLabels = computeKnownReturnedClosureLabels(optFuncs)([])
                    in
                        let devirtEntry = devirtualizeReturnedClosureCallsInFunction(knownLabels)(optEntry)
                        in
                            let devirtFuncs = map(devirtualizeReturnedClosureCallsInFunction(knownLabels))(optFuncs)
                            in
                                let nonAllocating = computeNonAllocatingFunctions(devirtFuncs)
                                in
                                    let finalEntry = stripRedundantArenaBrackets(nonAllocating)(devirtEntry)
                                    in
                                        let finalFuncs = map(stripRedundantArenaBrackets(nonAllocating))(devirtFuncs)
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
