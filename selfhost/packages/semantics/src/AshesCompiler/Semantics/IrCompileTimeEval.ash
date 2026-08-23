// Aggressive compile-time evaluation (partial evaluation) of pure IR.
//
// Invariants:
// - Evaluates pure, constant-argument calls at compile time with bounded step and depth budgets.
// - Fails open (best-effort): retains runtime code unchanged on unmodeled instructions or budget overruns.
// - Folds scalar results (Int, Float, Bool) into LoadConst instructions.
// - Tracks straight-line constant state and invalidates across control-flow boundaries.

import Ashes.Collection.List.append
import Ashes.Collection.List.filter
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
export (
    type CtValue(..),
    value evaluateCompileTimeConstants,
    value isInstructionModeledPure,
    value computeEvaluableFunctions,
)

type CtValue =
    | CtUnit
    | CtInt(Int)
    | CtFloat(Float)
    | CtBool(Bool)
    | CtStr(Str)
    | CtClosure(Str, CtValue)
    | CtAdt(Int, List(CtValue))
    deriving {Eq, Show}

let stepBudget = 50000

let depthBudget = 1000

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

let isModeledPureLeaf inst =
    match inst with
        | LoadConstInt(_, _) -> true
        | LoadConstFloat(_, _) -> true
        | LoadConstBool(_, _) -> true
        | LoadConstStr(_, _) -> true
        | LoadLocal(_, _) -> true
        | StoreLocal(_, _) -> true
        | Borrow(_, _) -> true
        | RcDup(_, _, _, _) -> true
        | RcDrop(_, _, _, _, _, _) -> true
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
        | ConcatStr(_, _, _, _) -> true
        | AllocAdt(_, _, _, _) -> true
        | AllocAdtStack(_, _, _) -> true
        | SetAdtField(_, _, _) -> true
        | GetAdtTag(_, _) -> true
        | GetAdtField(_, _, _) -> true
        | Label(_) -> true
        | Jump(_) -> true
        | JumpIfFalse(_, _) -> true
        | SwitchTag(_, _, _) -> true
        | Return(_) -> true
        | SaveArenaState(_, _, _) -> true
        | RestoreArenaState(_, _, _, _) -> true
        | ReclaimArenaChunks(_, _, _) -> true
        | SaveStackPointer(_) -> true
        | RestoreStackPointer(_) -> true
        | CallClosure(_, _, _, _) -> true
        | _ -> false

let isInstructionModeledPure evaluable inst =
    match inst with
        | MakeClosure(_, label, _, _, _, _, _) -> listContains(label)(evaluable)
        | MakeClosureStack(_, label, _, _, _, _) -> listContains(label)(evaluable)
        | LoadFuncAddr(_, label) -> listContains(label)(evaluable)
        | CallKnown(_, label, _, _, _, _) -> listContains(label)(evaluable)
        | _ -> isModeledPureLeaf(inst)

let recursive allInstructionsModeledPure evaluable instructions =
    match instructions with
        | [] -> true
        | IrInstruction { instruction = inst } :: tail ->
            if isInstructionModeledPure(evaluable)(inst)
            then allInstructionsModeledPure(evaluable)(tail)
            else false

let computeEvaluableFunctions program =
    match program with
        | IrProgram { entryFunction = entryFunction, functions = functions } ->
            let allFunctions = entryFunction :: functions
            in
                let initialCandidates =
                    map(given (f) ->
                        match f with
                            | IrFunction { label = l } -> l)(allFunctions)
                in
                    let recursive fixpoint candidates =
                        let filtered =
                            filter(
                                given (label) ->
                                    match lookupFunction(label)(allFunctions) with
                                        | None -> false
                                        | Some(fn) ->
                                            match fn with
                                                | IrFunction { instructions = instructions } -> allInstructionsModeledPure(candidates)(instructions)
                            )(
                                candidates
                            )
                        in
                            if length(filtered) == length(candidates)
                            then candidates
                            else fixpoint(filtered)
                    in fixpoint(initialCandidates)

let recursive lookupFunction label functions =
    match functions with
        | [] -> None
        | (IrFunction { label = l } as fn) :: tail ->
            if l == label
            then Some(fn)
            else lookupFunction(label)(tail)

let recursive lookupStringLiteral label literals =
    match literals with
        | [] -> None
        | IrStringLiteral { label = l, value = v } :: tail ->
            if l == label
            then Some(v)
            else lookupStringLiteral(label)(tail)

let recursive buildLabelIndex instructions index =
    match instructions with
        | [] -> []
        | IrInstruction { instruction = Label(name) } :: tail -> (name, index) :: buildLabelIndex(tail)(index + 1)
        | _ :: tail -> buildLabelIndex(tail)(index + 1)

let recursive nthInstruction instructions targetIndex currentIndex =
    match instructions with
        | [] -> None
        | head :: tail ->
            if currentIndex == targetIndex
            then Some(head)
            else nthInstruction(tail)(targetIndex)(currentIndex + 1)

let recursive makeInitialAdtFields count =
    if count <= 0
    then []
    else CtUnit :: makeInitialAdtFields(count - 1)

let recursive updateAdtField index value fields currentIndex =
    match fields with
        | [] -> []
        | head :: tail ->
            if currentIndex == index
            then value :: tail
            else head :: updateAdtField(index)(value)(tail)(currentIndex + 1)

let recursive getAdtFieldAt index fields currentIndex =
    match fields with
        | [] -> None
        | head :: tail ->
            if currentIndex == index
            then Some(head)
            else getAdtFieldAt(index)(tail)(currentIndex + 1)

let resolveSwitchTag tag cases defaultLabel =
    (let recursive findCase cs =
        match cs with
            | [] -> defaultLabel
            | IrSwitchCase { tag = t, label = l } :: tail ->
                if t == tag
                then l
                else findCase(tail)
    in findCase(cases))

type InterpreterState =
    | stepsLeft: Int
    | temps: List((IrTemp, CtValue))
    | locals: List((IrLocal, CtValue))

let recursive evalFunction program evaluable fn env arg depth steps =
    if depth > depthBudget
    then (None, steps)
    else
        if steps <= 0
        then (None, steps)
        else
            if not(fn.hasEnvAndArgParams)
            then (None, steps)
            else
                let labelIndex = buildLabelIndex(fn.instructions)(0)
                in
                    let initialLocals = [(0, env), (1, arg)]
                    in
                        let recursive runLoop pc state =
                            if state.stepsLeft <= 0
                            then (None, state.stepsLeft)
                            else
                                match nthInstruction(fn.instructions)(pc)(0) with
                                    | None -> (None, state.stepsLeft)
                                    | Some(IrInstruction { instruction = inst, location = loc }) ->
                                        let nextSteps = state.stepsLeft - 1
                                        in
                                            let stepState =
                                                InterpreterState(
                                                    stepsLeft = nextSteps,
                                                    temps = state.temps,
                                                    locals = state.locals
                                                )
                                            in
                                                match inst with
                                                    | Return(src) ->
                                                        match lookupAssociation(src)(stepState.temps) with
                                                            | None -> (None, nextSteps)
                                                            | Some(val) -> (Some(val), nextSteps)
                                                    | Jump(target) ->
                                                        match lookupAssociation(target)(labelIndex) with
                                                            | None -> (None, nextSteps)
                                                            | Some(nextPc) -> runLoop(nextPc)(stepState)
                                                    | JumpIfFalse(cond, target) ->
                                                        match lookupAssociation(cond)(stepState.temps) with
                                                            | Some(CtBool(false)) ->
                                                                match lookupAssociation(target)(labelIndex) with
                                                                    | None -> (None, nextSteps)
                                                                    | Some(nextPc) -> runLoop(nextPc)(stepState)
                                                            | Some(CtBool(true)) -> runLoop(pc + 1)(stepState)
                                                            | _ -> (None, nextSteps)
                                                    | SwitchTag(tagTemp, cases, defaultLabel) ->
                                                        match lookupAssociation(tagTemp)(stepState.temps) with
                                                            | Some(CtInt(tagVal)) ->
                                                                let target = resolveSwitchTag(tagVal)(cases)(defaultLabel)
                                                                in
                                                                    match lookupAssociation(target)(labelIndex) with
                                                                        | None -> (None, nextSteps)
                                                                        | Some(nextPc) -> runLoop(nextPc)(stepState)
                                                            | _ -> (None, nextSteps)
                                                    | CallKnown(target, calleeLabel, envTemp, argTemp, _, _) ->
                                                        if not(listContains(calleeLabel)(evaluable))
                                                        then (None, nextSteps)
                                                        else
                                                            let calleeOpt =
                                                                lookupFunction(calleeLabel)(
                                                                    program.entryFunction :: program.functions
                                                                )
                                                            in
                                                                let calleeEnv =
                                                                    match lookupAssociation(envTemp)(stepState.temps) with
                                                                        | Some(e) -> e
                                                                        | None -> CtUnit
                                                                in
                                                                    match lookupAssociation(argTemp)(stepState.temps) with
                                                                        | None -> (None, nextSteps)
                                                                        | Some(calleeArg) ->
                                                                            match calleeOpt with
                                                                                | None -> (None, nextSteps)
                                                                                | Some(callee) ->
                                                                                    match evalFunction(program)(evaluable)(callee)(
                                                                                        calleeEnv
                                                                                    )(
                                                                                        calleeArg
                                                                                    )(
                                                                                        depth + 1
                                                                                    )(
                                                                                        nextSteps
                                                                                    ) with
                                                                                        | (callResult, remSteps) ->
                                                                                            match callResult with
                                                                                                | None -> (None, remSteps)
                                                                                                | Some(res) ->
                                                                                                    let updatedTemps =
                                                                                                        setAssociation(target)(res)(
                                                                                                            stepState.temps
                                                                                                        )
                                                                                                    in
                                                                                                        runLoop(pc + 1)(
                                                                                                            InterpreterState(
                                                                                                                stepsLeft = remSteps,
                                                                                                                temps = updatedTemps,
                                                                                                                locals = stepState.locals
                                                                                                            )
                                                                                                        )
                                                    | CallClosure(target, closureTemp, argTemp, _) ->
                                                        match lookupAssociation(closureTemp)(stepState.temps) with
                                                            | Some(CtClosure(calleeLabel, closureEnv)) ->
                                                                if not(listContains(calleeLabel)(evaluable))
                                                                then (None, nextSteps)
                                                                else
                                                                    match lookupAssociation(argTemp)(stepState.temps) with
                                                                        | None -> (None, nextSteps)
                                                                        | Some(calleeArg) ->
                                                                            let calleeOpt =
                                                                                lookupFunction(calleeLabel)(
                                                                                    program.entryFunction :: program.functions
                                                                                )
                                                                            in
                                                                                match calleeOpt with
                                                                                    | None -> (None, nextSteps)
                                                                                    | Some(callee) ->
                                                                                        match evalFunction(program)(evaluable)(callee)(
                                                                                            closureEnv
                                                                                        )(
                                                                                            calleeArg
                                                                                        )(
                                                                                            depth + 1
                                                                                        )(
                                                                                            nextSteps
                                                                                        ) with
                                                                                            | (callResult, remSteps) ->
                                                                                                match callResult with
                                                                                                    | None -> (None, remSteps)
                                                                                                    | Some(res) ->
                                                                                                        let updatedTemps =
                                                                                                            setAssociation(target)(
                                                                                                                res
                                                                                                            )(
                                                                                                                stepState.temps
                                                                                                            )
                                                                                                        in
                                                                                                            runLoop(pc + 1)(
                                                                                                                InterpreterState(
                                                                                                                    stepsLeft = remSteps,
                                                                                                                    temps = updatedTemps,
                                                                                                                    locals = stepState.locals
                                                                                                                )
                                                                                                            )
                                                            | _ -> (None, nextSteps)
                                                    | _ ->
                                                        match execPureInst(program)(inst)(stepState) with
                                                            | None -> (None, nextSteps)
                                                            | Some(nextState) -> runLoop(pc + 1)(nextState)
                        in
                            runLoop(0)(
                                InterpreterState(
                                    stepsLeft = steps,
                                    temps = [],
                                    locals = initialLocals
                                )
                            )

let execPureInst program inst state =
    match inst with
        | LoadConstInt(target, v) ->
            Some(
                InterpreterState(
                    stepsLeft = state.stepsLeft,
                    temps = setAssociation(target)(CtInt(v))(state.temps),
                    locals = state.locals
                )
            )
        | LoadConstFloat(target, v) ->
            Some(
                InterpreterState(
                    stepsLeft = state.stepsLeft,
                    temps = setAssociation(target)(CtFloat(v))(state.temps),
                    locals = state.locals
                )
            )
        | LoadConstBool(target, v) ->
            Some(
                InterpreterState(
                    stepsLeft = state.stepsLeft,
                    temps = setAssociation(target)(CtBool(v))(state.temps),
                    locals = state.locals
                )
            )
        | LoadConstStr(target, strLabel) ->
            match lookupStringLiteral(strLabel)(program.stringLiterals) with
                | None -> None
                | Some(v) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtStr(v))(state.temps),
                            locals = state.locals
                        )
                    )
        | LoadLocal(target, slot) ->
            match lookupAssociation(slot)(state.locals) with
                | None -> None
                | Some(v) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(v)(state.temps),
                            locals = state.locals
                        )
                    )
        | StoreLocal(slot, src) ->
            match lookupAssociation(src)(state.temps) with
                | None -> None
                | Some(v) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = state.temps,
                            locals = setAssociation(slot)(v)(state.locals)
                        )
                    )
        | Borrow(dest, src) ->
            match lookupAssociation(src)(state.temps) with
                | None -> None
                | Some(v) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(dest)(v)(state.temps),
                            locals = state.locals
                        )
                    )
        | RcDup(dest, src, _, _) ->
            match lookupAssociation(src)(state.temps) with
                | None -> None
                | Some(v) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(dest)(v)(state.temps),
                            locals = state.locals
                        )
                    )
        | RcDrop(_, _, _, _, _, _) -> Some(state)
        | MakeClosure(target, fnLabel, envTemp, _, _, _, _) ->
            let envVal =
                match lookupAssociation(envTemp)(state.temps) with
                    | Some(v) -> v
                    | None -> CtUnit
            in
                Some(
                    InterpreterState(
                        stepsLeft = state.stepsLeft,
                        temps = setAssociation(target)(CtClosure(fnLabel)(envVal))(state.temps),
                        locals = state.locals
                    )
                )
        | MakeClosureStack(target, fnLabel, envTemp, _, _, _) ->
            let envVal =
                match lookupAssociation(envTemp)(state.temps) with
                    | Some(v) -> v
                    | None -> CtUnit
            in
                Some(
                    InterpreterState(
                        stepsLeft = state.stepsLeft,
                        temps = setAssociation(target)(CtClosure(fnLabel)(envVal))(state.temps),
                        locals = state.locals
                    )
                )
        | LoadFuncAddr(target, fnLabel) ->
            Some(
                InterpreterState(
                    stepsLeft = state.stepsLeft,
                    temps = setAssociation(target)(CtClosure(fnLabel)(CtUnit))(state.temps),
                    locals = state.locals
                )
            )
        | AddInt(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtInt(lv + rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | SubInt(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtInt(lv - rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | MulInt(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtInt(lv * rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | DivInt(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    if rv == 0
                    then None
                    else
                        Some(
                            InterpreterState(
                                stepsLeft = state.stepsLeft,
                                temps = setAssociation(target)(CtInt(lv / rv))(state.temps),
                                locals = state.locals
                            )
                        )
                | _ -> None
        | DivUInt(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    if rv == 0
                    then None
                    else
                        Some(
                            InterpreterState(
                                stepsLeft = state.stepsLeft,
                                temps = setAssociation(target)(CtInt(lv / rv))(state.temps),
                                locals = state.locals
                            )
                        )
                | _ -> None
        | AndInt(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtInt(lv & rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | OrInt(target, l, r) ->
            (match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    (Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtInt(lv | rv))(state.temps),
                            locals = state.locals
                        )
                    ))
                | _ -> None)
        | XorInt(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtInt(lv ^ rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | ShlInt(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtInt(lv << rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | ShrInt(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtInt(lv >> rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | AddFloat(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtFloat(lv)), Some(CtFloat(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtFloat(lv + rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | SubFloat(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtFloat(lv)), Some(CtFloat(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtFloat(lv - rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | MulFloat(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtFloat(lv)), Some(CtFloat(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtFloat(lv * rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | DivFloat(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtFloat(lv)), Some(CtFloat(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtFloat(lv / rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | IntToFloat(target, src) ->
            match lookupAssociation(src)(state.temps) with
                | Some(CtInt(v)) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtFloat(intToFloat(v)))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | FloatToInt(target, src) ->
            match lookupAssociation(src)(state.temps) with
                | Some(CtFloat(v)) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtInt(floatToInt(v)))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpIntGt(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv > rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpIntGe(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv >= rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpIntLt(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv < rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpIntLe(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv <= rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpUIntGt(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv > rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpUIntGe(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv >= rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpUIntLt(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv < rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpUIntLe(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv <= rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpIntEq(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv == rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpIntNe(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtInt(lv)), Some(CtInt(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv != rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpFloatGt(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtFloat(lv)), Some(CtFloat(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv > rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpFloatGe(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtFloat(lv)), Some(CtFloat(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv >= rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpFloatLt(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtFloat(lv)), Some(CtFloat(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv < rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpFloatLe(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtFloat(lv)), Some(CtFloat(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv <= rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpFloatEq(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtFloat(lv)), Some(CtFloat(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv == rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpFloatNe(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtFloat(lv)), Some(CtFloat(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv != rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpStrEq(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtStr(lv)), Some(CtStr(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv == rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | CmpStrNe(target, l, r) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtStr(lv)), Some(CtStr(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtBool(lv != rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | ConcatStr(target, l, r, _) ->
            match (lookupAssociation(l)(state.temps), lookupAssociation(r)(state.temps)) with
                | (Some(CtStr(lv)), Some(CtStr(rv))) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtStr(lv + rv))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | AllocAdt(target, tag, size, _) ->
            Some(
                InterpreterState(
                    stepsLeft = state.stepsLeft,
                    temps = setAssociation(target)(CtAdt(tag)(makeInitialAdtFields(size)))(state.temps),
                    locals = state.locals
                )
            )
        | AllocAdtStack(target, tag, size) ->
            Some(
                InterpreterState(
                    stepsLeft = state.stepsLeft,
                    temps = setAssociation(target)(CtAdt(tag)(makeInitialAdtFields(size)))(state.temps),
                    locals = state.locals
                )
            )
        | SetAdtField(ptr, idx, src) ->
            match (lookupAssociation(ptr)(state.temps), lookupAssociation(src)(state.temps)) with
                | (Some(CtAdt(tag, fields)), Some(srcVal)) ->
                    let updatedFields = updateAdtField(idx)(srcVal)(fields)(0)
                    in
                        Some(
                            InterpreterState(
                                stepsLeft = state.stepsLeft,
                                temps = setAssociation(ptr)(CtAdt(tag)(updatedFields))(state.temps),
                                locals = state.locals
                            )
                        )
                | _ -> None
        | GetAdtTag(target, ptr) ->
            match lookupAssociation(ptr)(state.temps) with
                | Some(CtAdt(tag, _)) ->
                    Some(
                        InterpreterState(
                            stepsLeft = state.stepsLeft,
                            temps = setAssociation(target)(CtInt(tag))(state.temps),
                            locals = state.locals
                        )
                    )
                | _ -> None
        | GetAdtField(target, ptr, idx) ->
            match lookupAssociation(ptr)(state.temps) with
                | Some(CtAdt(_, fields)) ->
                    match getAdtFieldAt(idx)(fields)(0) with
                        | None -> None
                        | Some(val) ->
                            Some(
                                InterpreterState(
                                    stepsLeft = state.stepsLeft,
                                    temps = setAssociation(target)(val)(state.temps),
                                    locals = state.locals
                                )
                            )
                | _ -> None
        | SaveArenaState(_, _, _) -> Some(state)
        | RestoreArenaState(_, _, _, _) -> Some(state)
        | ReclaimArenaChunks(_, _, _) -> Some(state)
        | SaveStackPointer(_) -> Some(state)
        | RestoreStackPointer(_) -> Some(state)
        | _ -> None

type CallerScanState =
    | temps: List((IrTemp, CtValue))
    | slots: List((IrLocal, CtValue))

let rewriteFunctionCalls program evaluable fn =
    (let recursive scanInstructions instructions state acc =
        match instructions with
            | [] -> reverse(acc)
            | (IrInstruction { instruction = inst, location = loc } as irInst) :: tail ->
                match inst with
                    | LoadConstInt(target, v) ->
                        let nextTemps = setAssociation(target)(CtInt(v))(state.temps)
                        in
                            scanInstructions(tail)(
                                CallerScanState(
                                    temps = nextTemps,
                                    slots = state.slots
                                )
                            )(
                                irInst :: acc
                            )
                    | LoadConstFloat(target, v) ->
                        let nextTemps = setAssociation(target)(CtFloat(v))(state.temps)
                        in
                            scanInstructions(tail)(
                                CallerScanState(
                                    temps = nextTemps,
                                    slots = state.slots
                                )
                            )(
                                irInst :: acc
                            )
                    | LoadConstBool(target, v) ->
                        let nextTemps = setAssociation(target)(CtBool(v))(state.temps)
                        in
                            scanInstructions(tail)(
                                CallerScanState(
                                    temps = nextTemps,
                                    slots = state.slots
                                )
                            )(
                                irInst :: acc
                            )
                    | Borrow(dest, src) ->
                        let nextTemps =
                            match lookupAssociation(src)(state.temps) with
                                | Some(v) -> setAssociation(dest)(v)(state.temps)
                                | None -> removeAssociation(dest)(state.temps)
                        in
                            scanInstructions(tail)(
                                CallerScanState(
                                    temps = nextTemps,
                                    slots = state.slots
                                )
                            )(
                                irInst :: acc
                            )
                    | RcDup(dest, src, _, _) ->
                        let nextTemps =
                            match lookupAssociation(src)(state.temps) with
                                | Some(v) -> setAssociation(dest)(v)(state.temps)
                                | None -> removeAssociation(dest)(state.temps)
                        in
                            scanInstructions(tail)(
                                CallerScanState(
                                    temps = nextTemps,
                                    slots = state.slots
                                )
                            )(
                                irInst :: acc
                            )
                    | MakeClosure(dest, fnLabel, envTemp, envSize, _, _, _) ->
                        let nextTemps =
                            if envSize == 0
                            then setAssociation(dest)(CtClosure(fnLabel)(CtUnit))(state.temps)
                            else removeAssociation(dest)(state.temps)
                        in
                            scanInstructions(tail)(
                                CallerScanState(
                                    temps = nextTemps,
                                    slots = state.slots
                                )
                            )(
                                irInst :: acc
                            )
                    | MakeClosureStack(dest, fnLabel, envTemp, envSize, _, _) ->
                        let nextTemps =
                            if envSize == 0
                            then setAssociation(dest)(CtClosure(fnLabel)(CtUnit))(state.temps)
                            else removeAssociation(dest)(state.temps)
                        in
                            scanInstructions(tail)(
                                CallerScanState(
                                    temps = nextTemps,
                                    slots = state.slots
                                )
                            )(
                                irInst :: acc
                            )
                    | StoreLocal(slot, src) ->
                        let nextSlots =
                            match lookupAssociation(src)(state.temps) with
                                | Some(v) -> setAssociation(slot)(v)(state.slots)
                                | None -> removeAssociation(slot)(state.slots)
                        in
                            scanInstructions(tail)(
                                CallerScanState(
                                    temps = state.temps,
                                    slots = nextSlots
                                )
                            )(
                                irInst :: acc
                            )
                    | LoadLocal(dest, slot) ->
                        let nextTemps =
                            match lookupAssociation(slot)(state.slots) with
                                | Some(v) -> setAssociation(dest)(v)(state.temps)
                                | None -> removeAssociation(dest)(state.temps)
                        in
                            scanInstructions(tail)(
                                CallerScanState(
                                    temps = nextTemps,
                                    slots = state.slots
                                )
                            )(
                                irInst :: acc
                            )
                    | CallKnown(target, calleeLabel, envTemp, argTemp, _, _) ->
                        let foldOpt =
                            if listContains(calleeLabel)(evaluable)
                            then
                                match lookupAssociation(argTemp)(state.temps) with
                                    | None -> None
                                    | Some(argVal) ->
                                        let envVal =
                                            match lookupAssociation(envTemp)(state.temps) with
                                                | Some(e) -> e
                                                | None -> CtUnit
                                        in
                                            let calleeOpt =
                                                lookupFunction(calleeLabel)(
                                                    program.entryFunction :: program.functions
                                                )
                                            in
                                                match calleeOpt with
                                                    | None -> None
                                                    | Some(callee) ->
                                                        match evalFunction(program)(evaluable)(callee)(envVal)(argVal)(0)(
                                                            stepBudget
                                                        ) with
                                                            | (res, _) -> res
                            else None
                        in
                            match foldOpt with
                                | Some(CtInt(v)) ->
                                    let foldedInst =
                                        IrInstruction(
                                            instruction = LoadConstInt(target)(v),
                                            location = loc
                                        )
                                    in
                                        let nextTemps = setAssociation(target)(CtInt(v))(state.temps)
                                        in
                                            scanInstructions(tail)(
                                                CallerScanState(
                                                    temps = nextTemps,
                                                    slots = state.slots
                                                )
                                            )(
                                                foldedInst :: acc
                                            )
                                | Some(CtFloat(v)) ->
                                    let foldedInst =
                                        IrInstruction(
                                            instruction = LoadConstFloat(target)(v),
                                            location = loc
                                        )
                                    in
                                        let nextTemps = setAssociation(target)(CtFloat(v))(state.temps)
                                        in
                                            scanInstructions(tail)(
                                                CallerScanState(
                                                    temps = nextTemps,
                                                    slots = state.slots
                                                )
                                            )(
                                                foldedInst :: acc
                                            )
                                | Some(CtBool(v)) ->
                                    let foldedInst =
                                        IrInstruction(
                                            instruction = LoadConstBool(target)(v),
                                            location = loc
                                        )
                                    in
                                        let nextTemps = setAssociation(target)(CtBool(v))(state.temps)
                                        in
                                            scanInstructions(tail)(
                                                CallerScanState(
                                                    temps = nextTemps,
                                                    slots = state.slots
                                                )
                                            )(
                                                foldedInst :: acc
                                            )
                                | _ ->
                                    let nextTemps = removeAssociation(target)(state.temps)
                                    in
                                        scanInstructions(tail)(
                                            CallerScanState(
                                                temps = nextTemps,
                                                slots = state.slots
                                            )
                                        )(
                                            irInst :: acc
                                        )
                    | CallClosure(target, closureTemp, argTemp, _) ->
                        let foldOpt =
                            match lookupAssociation(closureTemp)(state.temps) with
                                | Some(CtClosure(calleeLabel, closureEnv)) ->
                                    if listContains(calleeLabel)(evaluable)
                                    then
                                        match lookupAssociation(argTemp)(state.temps) with
                                            | None -> None
                                            | Some(argVal) ->
                                                let calleeOpt =
                                                    lookupFunction(calleeLabel)(
                                                        program.entryFunction :: program.functions
                                                    )
                                                in
                                                    match calleeOpt with
                                                        | None -> None
                                                        | Some(callee) ->
                                                            match evalFunction(program)(evaluable)(callee)(closureEnv)(
                                                                argVal
                                                            )(
                                                                0
                                                            )(
                                                                stepBudget
                                                            ) with
                                                                | (res, _) -> res
                                    else None
                                | _ -> None
                        in
                            match foldOpt with
                                | Some(CtInt(v)) ->
                                    let foldedInst =
                                        IrInstruction(
                                            instruction = LoadConstInt(target)(v),
                                            location = loc
                                        )
                                    in
                                        let nextTemps = setAssociation(target)(CtInt(v))(state.temps)
                                        in
                                            scanInstructions(tail)(
                                                CallerScanState(
                                                    temps = nextTemps,
                                                    slots = state.slots
                                                )
                                            )(
                                                foldedInst :: acc
                                            )
                                | Some(CtFloat(v)) ->
                                    let foldedInst =
                                        IrInstruction(
                                            instruction = LoadConstFloat(target)(v),
                                            location = loc
                                        )
                                    in
                                        let nextTemps = setAssociation(target)(CtFloat(v))(state.temps)
                                        in
                                            scanInstructions(tail)(
                                                CallerScanState(
                                                    temps = nextTemps,
                                                    slots = state.slots
                                                )
                                            )(
                                                foldedInst :: acc
                                            )
                                | Some(CtBool(v)) ->
                                    let foldedInst =
                                        IrInstruction(
                                            instruction = LoadConstBool(target)(v),
                                            location = loc
                                        )
                                    in
                                        let nextTemps = setAssociation(target)(CtBool(v))(state.temps)
                                        in
                                            scanInstructions(tail)(
                                                CallerScanState(
                                                    temps = nextTemps,
                                                    slots = state.slots
                                                )
                                            )(
                                                foldedInst :: acc
                                            )
                                | _ ->
                                    let nextTemps = removeAssociation(target)(state.temps)
                                    in
                                        scanInstructions(tail)(
                                            CallerScanState(
                                                temps = nextTemps,
                                                slots = state.slots
                                            )
                                        )(
                                            irInst :: acc
                                        )
                    | Label(_) -> scanInstructions(tail)(CallerScanState(temps = [], slots = []))(irInst :: acc)
                    | Jump(_) -> scanInstructions(tail)(CallerScanState(temps = [], slots = []))(irInst :: acc)
                    | JumpIfFalse(_, _) -> scanInstructions(tail)(CallerScanState(temps = [], slots = []))(irInst :: acc)
                    | SwitchTag(_, _, _) -> scanInstructions(tail)(CallerScanState(temps = [], slots = []))(irInst :: acc)
                    | _ -> scanInstructions(tail)(state)(irInst :: acc)
    in
        let newInstructions = scanInstructions(fn.instructions)(CallerScanState(temps = [], slots = []))([])
        in
            IrFunction(
                label = fn.label,
                instructions = newInstructions,
                localCount = fn.localCount,
                tempCount = fn.tempCount,
                hasEnvAndArgParams = fn.hasEnvAndArgParams,
                coroutine = fn.coroutine,
                localNames = fn.localNames,
                localTypes = fn.localTypes,
                origin = fn.origin,
                lifetimesPlaced = fn.lifetimesPlaced
            ))

let evaluateCompileTimeConstants program =
    (let evaluable = computeEvaluableFunctions(program)
    in
        if length(evaluable) == 0
        then program
        else
            let newEntry = rewriteFunctionCalls(program)(evaluable)(program.entryFunction)
            in
                let newFuncs = map(rewriteFunctionCalls(program)(evaluable))(program.functions)
                in
                    IrProgram(
                        entryFunction = newEntry,
                        functions = newFuncs,
                        stringLiterals = program.stringLiterals,
                        externalFunctions = program.externalFunctions,
                        externalOpaqueTypes = program.externalOpaqueTypes,
                        usesPrintInt = program.usesPrintInt,
                        usesPrintStr = program.usesPrintStr,
                        usesPrintBool = program.usesPrintBool,
                        usesConcatStr = program.usesConcatStr,
                        usesClosures = program.usesClosures,
                        usesAsync = program.usesAsync,
                        capabilityHandlerGlobals = program.capabilityHandlerGlobals,
                        traitEvidence = program.traitEvidence
                    ))
