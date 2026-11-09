// expect: 1 1 1 1
external type Cell
external malloc(Int) -> Cell
external free(Cell) -> void
external compareCells(FfiBuffer(Cell), FfiBuffer(Cell), Int) -> Int = "memcmp"

let first = malloc(1)

let second = malloc(1)

let empty = compareCells([])([])(0) == 0

let single = compareCells([first])([first])(8) == 0

let ordered = compareCells([first, second, first])([first, second, first])(24) == 0

let different = compareCells([first, second])([second, first])(16) != 0

let _ =
    first
    |> free
    |> (given (_) -> free(second))

Ashes.IO.print((if empty
then "1"
else "0") + " " + (if single
then "1"
else "0") + " " + (if ordered
then "1"
else "0") + " " + (if different
then "1"
else "0"))
