// expect: x:4 y:5 3 a,b:6
type Inner =
    | I(Str, Int)

type Inst =
    | Add(Int, Int)
    | Name(Inner, Int)
    | Tags(List(Str), Int)

let recursive bump (insts: List(Inst)) =
    match insts with
        | [] -> []
        | Add(a, b) :: tail -> Add(a + 1)(b) :: bump(tail)
        | Name(inner, k) :: tail -> Name(inner)(k + 1) :: bump(tail)
        | Tags(tags, k) :: tail -> Tags(tags)(k + 1) :: bump(tail)

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
        | Name(I(s, _n), k) :: tail -> render(tail)(acc + s + ":" + Ashes.Text.fromInt(k) + " ")
        | Tags(tags, k) :: tail -> render(tail)(acc + Ashes.Text.join(",")(tags) + ":" + Ashes.Text.fromInt(k) + " ")

let staged =
    (let once =
        bump([Name(I("x")(1))(1), Name(I("y")(2))(2), Add(0)(0), Tags(["a", "b"])(3)])
    in
        let twice = bump(once)
        in passes(1)(twice))

""
|> render(staged)
|> Ashes.Text.trimEnd
|> Ashes.IO.print
