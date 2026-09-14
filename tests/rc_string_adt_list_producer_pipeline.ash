// expect: x:4 y:5 3
type Inst =
    | Add(Int, Int)
    | Name(Str, Int)

let recursive bump (insts: List(Inst)) =
    match insts with
        | [] -> []
        | Add(a, b) :: tail -> Add(a + 1)(b) :: bump(tail)
        | Name(s, k) :: tail -> Name(s)(k + 1) :: bump(tail)

let recursive passes (k: Int) (insts: List(Inst)) =
    if k == 0
    then insts
    else
        insts
        |> bump
        |> passes(k - 1)

let recursive render (insts: List(Inst)) (acc: Str) =
    match insts with
        | [] -> acc
        | Add(a, _b) :: tail -> render(tail)(acc + Ashes.Text.fromInt(a) + " ")
        | Name(s, k) :: tail -> render(tail)(acc + s + ":" + Ashes.Text.fromInt(k) + " ")

let staged =
    (let once = bump([Name("x")(1), Name("y")(2), Add(0)(0)])
    in
        let twice = bump(once)
        in passes(1)(twice))

""
|> render(staged)
|> Ashes.Text.trimEnd
|> Ashes.IO.print
