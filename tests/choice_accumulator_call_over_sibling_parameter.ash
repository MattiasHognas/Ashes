// expect: 48
// A loop's accumulator is a choice between handing it on and consing onto it a call's result over a
// sibling loop parameter (`same(cur)`, which returns its argument). A call is a value of its own, so
// the head does not count as reading the parameter and the accumulator is placed on the
// reference-counted heap: the back edge copies the consed cell with a reference of its own to the
// head before the sibling's predecessor is released, and every pair survives to be summed.
let same x = x

let recursive sumAll (values: List((Str, Int))) (total: Int) =
    match values with
        | [] -> total
        | (name, count) :: rest -> sumAll(rest)(total + count + Ashes.Text.byteLength(name))

let recursive walk (pending: List(Str)) (cur: (Str, Int)) (acc: List((Str, Int))) (index: Int) =
    match pending with
        | [] -> sumAll(acc)(0)
        | key :: rest ->
            walk(rest)((key + "y", index))(if index == 1
            then acc
            else same(cur) :: acc)(index + 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let keys = ["a" + Ashes.Text.fromInt(n), "bb", "ccc", "dddd"]
        in rounds(n - 1)(total + walk(keys)(("s" + Ashes.Text.fromInt(n), 0))([])(0))

0
|> rounds(4)
|> Ashes.IO.print
