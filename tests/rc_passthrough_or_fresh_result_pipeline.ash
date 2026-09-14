// expect: 15 x:8
type Inst =
    | Add(Int, Int)
    | Name(Str, Int)

let recursive build (n: Int) (acc: List(Inst)) =
    if n == 0
    then acc
    else build(n - 1)(Name("x")(n) :: acc)

let recursive buildAll (m: Int) (n: Int) (acc: List(List(Inst))) =
    if m == 0
    then acc
    else buildAll(m - 1)(n)(build(n)([]) :: acc)

let recursive bump (insts: List(Inst)) =
    match insts with
        | [] -> []
        | Add(a, b) :: tail -> Add(a + 1)(b) :: bump(tail)
        | Name(s, k) :: tail -> Name(s)(k + 1) :: bump(tail)

let bumpIf (flag: Int) (insts: List(Inst)) =
    if flag == 0
    then insts
    else bump(insts)

let keepOrBump (insts: List(Inst)) =
    match insts with
        | [] -> insts
        | Add(_, _) :: _ -> insts
        | _ -> bump(insts)

let recursive bumpTimes (p: Int) (insts: List(Inst)) =
    if p == 0
    then insts
    else
        insts
        |> bumpTimes(p - 1)
        |> bump

let recursive passes (p: Int) (insts: List(Inst)) =
    if p == 0
    then insts
    else
        insts
        |> bumpIf(p - p / 2 * 2)
        |> passes(p - 1)

let recursive mapBump (p: Int) (lists: List(List(Inst))) =
    match lists with
        | [] -> []
        | insts :: rest ->
            keepOrBump(insts
            |> bumpTimes(p)
            |> passes(p)) :: mapBump(p)(rest)

let recursive count (insts: List(Inst)) (acc: Int) =
    match insts with
        | [] -> acc
        | _ :: tail -> count(tail)(acc + 1)

let recursive countAll (lists: List(List(Inst))) (acc: Int) =
    match lists with
        | [] -> acc
        | insts :: rest ->
            acc
            |> count(insts)
            |> countAll(rest)

let describe (insts: List(Inst)) =
    match insts with
        | Name(s, k) :: _ -> s + ":" + Ashes.Text.fromInt(k)
        | _ -> "none"

let lists =
    []
    |> buildAll(3)(5)
    |> mapBump(4)

Ashes.IO.print(
    Ashes.Text.fromInt(countAll(lists)(0)) + " " + (match lists with
        | first :: _ -> describe(first)
        | [] -> "none")
)
