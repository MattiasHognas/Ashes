// expect: 20
// A loop's accumulator, its type left to inference, grows by a cell whose head is a tuple holding
// a label a pattern binds out of the loop's list parameter. The parameter is placed on the
// reference-counted heap only once the self call's argument types resolve, and its back edge
// copies the successor with references of its own; the reference the tuple took for the label is
// released after that copy, and every label still reads back.
type Kind =
    | Target(Int, List(Str), Str)
    | Other(Int)

let recursive collect instructions acc =
    match instructions with
        | [] -> acc
        | inst :: tail ->
            match inst with
                | Target(_, _, label) -> collect(tail)((label, 1) :: acc)
                | _ -> collect(tail)(acc)

let recursive total (entries: List((Str, Int))) (sum: Int) =
    match entries with
        | [] -> sum
        | (label, count) :: rest -> total(rest)(sum + count + Ashes.Text.byteLength(label))

let program n = [Target(1)(["a", "b" + Ashes.Text.fromInt(n)])("c"), Other(1), Target(2)([])("ab")]

let recursive rounds (n: Int) (sum: Int) =
    if n == 0
    then sum
    else
        rounds(n - 1)(sum + total(collect(program(n))([]))(0))

0
|> rounds(4)
|> Ashes.IO.print
