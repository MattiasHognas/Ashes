// expect: 5000
type Kind =
    | LoadLocal(Int, Int)
    | StoreLocal(Int, Int)
    | RcDup(Int, Int)
    | RcDrop(Int, Int)

type Inst =
    | instruction: Kind
    | location: Int

let recursive lookup (temp: Int) (remap: List((Int, Int))) =
    match remap with
        | [] -> None
        | (key, value) :: rest ->
            if key == temp
            then Some(value)
            else lookup(temp)(rest)

let recursive resolve (remap: List((Int, Int))) (temp: Int) =
    match lookup(temp)(remap) with
        | None -> temp
        | Some(next) -> resolve(remap)(next)

let remapSources (kind: Kind) (remap: List((Int, Int))) =
    match kind with
        | LoadLocal(target, slot) -> LoadLocal(target)(slot)
        | StoreLocal(slot, source) ->
            source
            |> resolve(remap)
            |> StoreLocal(slot)
        | RcDup(target, source) ->
            source
            |> resolve(remap)
            |> RcDup(target)
        | RcDrop(source, slot) -> RcDrop(source + 1)(slot)

let recursive fusePass (insts: List(Inst)) (remap: List((Int, Int))) (acc: List(Inst)) =
    match insts with
        | [] -> Ashes.Collection.List.reverse(acc)
        | Inst { instruction = RcDup(dest, src) } :: Inst { instruction = RcDrop(dropSrc, _) } :: tail ->
            if dropSrc == src
            then fusePass(tail)(remap)(acc)
            else fusePass(tail)(remap)(acc)
        | Inst { instruction = inst, location = loc } :: tail -> fusePass(tail)(remap)(Inst(instruction = remapSources(inst)(remap), location = loc) :: acc)

let recursive weight (instructions: List(Inst)) (total: Int) =
    match instructions with
        | [] -> total
        | Inst { instruction = StoreLocal(_, source) } :: rest -> weight(rest)(total + source)
        | _ :: rest -> weight(rest)(total + 1)

let recursive rounds (code: List(Inst)) (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(code)(n - 1)(total + weight(fusePass(code)([])([]))(0))

0
|> rounds([Inst(instruction = LoadLocal(1)(2), location = 7), Inst(instruction = RcDup(3)(1), location = 0), Inst(instruction = RcDrop(1)(2), location = 0), Inst(instruction = StoreLocal(4)(3), location = 2), Inst(instruction = LoadLocal(5)(4), location = 0)])(1000)
|> Ashes.IO.print
