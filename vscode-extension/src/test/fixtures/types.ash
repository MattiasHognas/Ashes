// A file with various language features for syntax highlighting tests
type Color =
    | Red
    | Green
    | Blue

let describe =
    fun (c) ->
        match c with
        | Red -> "red"
        | Green -> "green"
        | Blue -> "blue"
in
    Red
    |> describe
    |> Ashes.IO.print
