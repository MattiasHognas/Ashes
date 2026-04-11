// expect: orange
type Color =
    | Red(Int)
    | Green(Int)
    | Blue(Int)
    | Yellow(Int)
    | Purple(Int)
    | Orange(Int)

let color = Orange(0)
in 
    Ashes.IO.print(match color with
        | Red(_) -> "red"
        | Green(_) -> "green"
        | Blue(_) -> "blue"
        | Yellow(_) -> "yellow"
        | Purple(_) -> "purple"
        | Orange(_) -> "orange")
