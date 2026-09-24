type Where =
    | line: Int
    | column: Int

type Kind =
    | Label(Str)
    | Load(Int, Int)
    | Borrow(Int, Int)
    | Call(Int, Str, Int)

type Instr =
    | instruction: Kind
    | location: Where

type PassState =
    | valueOf: List((Int, Int))
    | cache: List((Int, Int))
    | fresh: List(Int)
    | seen: Int

let emptyState = PassState(valueOf = [], cache = [], fresh = [], seen = 0)

let recursive lookup (key: Int) (entries: List((Int, Int))) =
    match entries with
        | [] -> None
        | (k, v) :: rest ->
            if k == key
            then Some(v)
            else lookup(key)(rest)

let step (inst: Kind) (loc: Where) (state: PassState) =
    match inst with
        | Load(target, slot) ->
            match lookup(slot)(state.cache) with
                | Some(cached) -> ((state with valueOf = (target, cached) :: state.valueOf), Instr(instruction = Borrow(target)(cached), location = loc))
                | None -> ((state with cache = (slot, target) :: state.cache), Instr(instruction = inst, location = loc))
        | Call(target, _, _) -> ((state with fresh = target :: state.fresh), Instr(instruction = inst, location = loc))
        | _ -> (state, Instr(instruction = inst, location = loc))

let recursive pass (instructions: List(Instr)) (state: PassState) (acc: List(Instr)) =
    match instructions with
        | [] -> Ashes.Collection.List.reverse(acc)
        | (Instr { instruction = Label(_) } as label) :: tail -> pass(tail)(emptyState)(label :: acc)
        | Instr { instruction = inst, location = loc } :: tail ->
            match step(inst)(loc)(state) with
                | (nextState, rewritten) -> pass(tail)(state)(Instr(instruction = inst, location = loc) :: acc)

let kindOf (n: Int) =
    match n - n / 8 * 8 with
        | 0 -> Label("l" + Ashes.Text.fromInt(n))
        | 1 -> Load(n)(n / 16)
        | 2 -> Load(n)(n / 16)
        | 3 -> Call(n)("f" + Ashes.Text.fromInt(n))(0)
        | _ -> Borrow(n)(n - 1)

let recursive build (n: Int) (acc: List(Instr)) =
    if n == 0
    then acc
    else build(n - 1)(Instr(instruction = kindOf(n), location = Where(line = n, column = 1)) :: acc)

let weighKind (kind: Kind) =
    match kind with
        | Label(name) -> Ashes.Text.byteLength(name)
        | Load(target, slot) -> target + slot
        | Borrow(target, source) -> target + source
        | Call(target, name, _) -> target + Ashes.Text.byteLength(name)

let recursive weigh (xs: List(Instr)) (total: Int) =
    match xs with
        | [] -> total
        | Instr { instruction = kind, location = loc } :: rest -> weigh(rest)(total + weighKind(kind) + loc.line)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + weigh(pass(build(400)([]))(emptyState)([]))(0))

0
|> rounds(ROUNDS)
|> Ashes.IO.print
