// expect: 6033600 1 2 name1 0
// A curried stage keeps a record and a string argument whose caller releases its own references
// before the returned closures are applied, some of them after unrelated allocation reused freed
// cells. The capture must hold its own references once the closure escapes.
type Where =
    | line: Int
    | column: Int

type Instr =
    | instruction: Int
    | location: Where
    | label: Str

let make (loc: Where) (name: Str) (k: Int) = Instr(instruction = k, location = loc, label = name)

let recursive wheres (n: Int) (acc: List(Where)) =
    if n == 0
    then acc
    else wheres(n - 1)(Where(line = n, column = n + 1) :: acc)

let recursive names (n: Int) (acc: List(Str)) =
    if n == 0
    then acc
    else names(n - 1)("name" + Ashes.Text.fromInt(n) :: acc)

let stage (n: Int) =
    match wheres(n)([]) with
        | w :: _ ->
            match names(n)([]) with
                | s :: _ -> make(w)(s)
                | [] -> make(w)("none")
        | [] -> make(Where(line = 0, column = 0))("empty")

let recursive stages (n: Int) (acc: List(Int -> Instr)) =
    if n == 0
    then acc
    else stages(n - 1)(stage(n) :: acc)

let recursive noise (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match wheres(4)([]) with
            | w :: _ -> noise(n - 1)(total + w.line + Ashes.Text.byteLength(Ashes.Text.fromInt(n) + "zzzzzzz"))
            | [] -> noise(n - 1)(total)

let recursive apply (fs: List(Int -> Instr)) (total: Int) =
    match fs with
        | [] -> total
        | f :: rest ->
            match f(7) with
                | Instr { instruction = k, location = w, label = s } -> apply(rest)(total + k + w.line * 100 + w.column * 10000 + Ashes.Text.byteLength(s))

let fs = stages(300)([])

let spent = noise(2000)(0)

let one = stage(5)

let spentAgain = noise(2000)(0)

match one(1) with
    | Instr { location = w, label = s } ->
        Ashes.IO.print(Ashes.Text.fromInt(apply(fs)(0)) + " " + Ashes.Text.fromInt(w.line) + " " + Ashes.Text.fromInt(w.column) + " " + s + " " + Ashes.Text.fromInt(spent - spentAgain))
