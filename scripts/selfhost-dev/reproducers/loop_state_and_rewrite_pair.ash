type Where =
    | line: Int
    | column: Int

type Instr =
    | kind: Int
    | target: Int
    | location: Where

type PassState =
    | valueOf: List((Int, Int))
    | cache: List((Int, Int))
    | fresh: List(Int)
    | seen: Int

let emptyState = PassState(valueOf = [], cache = [], fresh = [], seen = 0)

let step (kind: Int) (target: Int) (location: Where) (state: PassState) =
    match kind with
        | 0 -> ((state with fresh = target :: state.fresh), Instr(kind = kind, target = target, location = location))
        | 1 -> ((state with cache = (target, kind) :: state.cache), Instr(kind = 9, target = target, location = location))
        | _ -> (state, Instr(kind = kind, target = target, location = location))

let recursive pass (instructions: List(Instr)) (state: PassState) (acc: List(Instr)) =
    match instructions with
        | [] -> Ashes.Collection.List.reverse(acc)
        | (Instr { kind = 7 } as label) :: tail -> pass(tail)(emptyState)(label :: acc)
        | Instr { kind = kind, target = target, location = location } :: tail ->
            match step(kind)(target)(location)(state) with
                | (nextState, rewritten) -> pass(tail)(nextState)(rewritten :: acc)

let recursive build (n: Int) (acc: List(Instr)) =
    if n == 0
    then acc
    else build(n - 1)(Instr(kind = n - n / 8 * 8, target = n, location = Where(line = n, column = 1)) :: acc)

let recursive weigh (xs: List(Instr)) (total: Int) =
    match xs with
        | [] -> total
        | Instr { kind = kind, target = target } :: rest -> weigh(rest)(total + kind + target)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + weigh(pass(build(400)([]))(emptyState)([]))(0))

0
|> rounds(ROUNDS)
|> Ashes.IO.print
