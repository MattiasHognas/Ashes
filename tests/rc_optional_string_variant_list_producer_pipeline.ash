// expect: x:4 -:5 3
type Inst =
    | Add(Int, Int)
    | Loc(Maybe(Str), Int)

let recursive bump (insts: List(Inst)) =
    match insts with
        | [] -> []
        | Add(a, b) :: tail -> Add(a + 1)(b) :: bump(tail)
        | Loc(loc, k) :: tail -> Loc(loc)(k + 1) :: bump(tail)

let recursive passes (n: Int) (insts: List(Inst)) =
    if n == 0
    then insts
    else
        insts
        |> bump
        |> passes(n - 1)

let recursive count (insts: List(Inst)) (acc: Int) =
    match insts with
        | [] -> acc
        | _ :: tail -> count(tail)(acc + 1)

let describe (inst: Inst) =
    match inst with
        | Loc(Some(name), k) -> name + ":" + Ashes.Text.fromInt(k)
        | Loc(None, k) -> "-:" + Ashes.Text.fromInt(k)
        | Add(a, _) -> Ashes.Text.fromInt(a)

let program = passes(3)([Loc(Some("x"))(1), Loc(None)(2), Add(0)(0)])

Ashes.IO.print((match program with
    | first :: second :: _ -> describe(first) + " " + describe(second)
    | _ -> "?") + " " + Ashes.Text.fromInt(count(program)(0)))
