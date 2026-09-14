// expect: x:4 y:5 3
type Inst =
    | Add(Int, Int)
    | Name(Str, Int)

let recursive bumpInto (insts: List(Inst)) (acc: List(Inst)) =
    match insts with
        | [] -> Ashes.Collection.List.reverse(acc)
        | Add(a, b) :: tail -> bumpInto(tail)(Add(a + 1)(b) :: acc)
        | Name(s, k) :: tail -> bumpInto(tail)(Name(s)(k + 1) :: acc)

let bump (insts: List(Inst)) = bumpInto(insts)([])

let recursive passes (k: Int) (insts: List(Inst)) =
    if k == 0
    then insts
    else
        insts
        |> bump
        |> passes(k - 1)

let recursive count (insts: List(Inst)) (acc: Int) =
    match insts with
        | [] -> acc
        | _ :: tail -> count(tail)(acc + 1)

let describe (inst: Inst) =
    match inst with
        | Name(name, k) -> name + ":" + Ashes.Text.fromInt(k)
        | Add(a, _) -> Ashes.Text.fromInt(a)

let program = passes(3)([Name("x")(1), Name("y")(2), Add(0)(0)])

Ashes.IO.print((match program with
    | first :: second :: _ -> describe(first) + " " + describe(second)
    | _ -> "?") + " " + Ashes.Text.fromInt(count(program)(0)))
