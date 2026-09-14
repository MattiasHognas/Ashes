// expect: f:5 g:5 2
type Inst =
    | Add(Int, Int)
    | Name(Str, Int)

type Fn =
    | label: Str
    | insts: List(Inst)

let recursive bump (insts: List(Inst)) =
    match insts with
        | [] -> []
        | Add(a, b) :: tail -> Add(a + 1)(b) :: bump(tail)
        | Name(s, k) :: tail -> Name(s)(k + 1) :: bump(tail)

let optFn (fn: Fn) =
    match fn with
        | Fn { label = label, insts = insts } -> Fn(label = label, insts = bump(insts))

let optFnFields (fn: Fn) =
    (let bumped = bump(fn.insts)
    in Fn(label = fn.label, insts = bumped))

let recursive mapFns (fns: List(Fn)) =
    match fns with
        | [] -> []
        | fn :: rest -> optFn(fn) :: mapFns(rest)

let recursive mapFnsFields (fns: List(Fn)) =
    match fns with
        | [] -> []
        | fn :: rest -> optFnFields(fn) :: mapFnsFields(rest)

let recursive rounds (k: Int) (fns: List(Fn)) =
    if k == 0
    then fns
    else
        fns
        |> mapFns
        |> mapFnsFields
        |> rounds(k - 1)

let describe (fn: Fn) =
    match fn with
        | Fn { label = label, insts = insts } ->
            match insts with
                | Name(_, k) :: _ -> label + ":" + Ashes.Text.fromInt(k)
                | _ -> label + ":?"

let recursive count (fns: List(Fn)) (acc: Int) =
    match fns with
        | [] -> acc
        | _ :: rest -> count(rest)(acc + 1)

let program = rounds(2)([Fn(label = "f", insts = [Name("x")(1), Add(0)(0)]), Fn(label = "g", insts = [Name("y")(1)])])

Ashes.IO.print((match program with
    | first :: second :: _ -> describe(first) + " " + describe(second)
    | _ -> "?") + " " + Ashes.Text.fromInt(count(program)(0)))
