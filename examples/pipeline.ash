// Flat top-level `let` declarations feeding a final pipeline expression.

let inc = 
    fun (x) -> x + 1

let double = 
    fun (x) -> x + x

let result = 
    1
    |> inc
    |> double
in Ashes.IO.print(result)
