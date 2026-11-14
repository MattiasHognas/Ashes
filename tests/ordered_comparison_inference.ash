// expect: true,true,true,true,a,m,z
import Ashes.Collection.List.sort
let before left right = left < right

let through left right = left <= right

let after left right = left > right

let from left right = left >= right

[
    "z"
    |> before("a")
    |> Ashes.Trait.Show.show,
    "a"
    |> through("a")
    |> Ashes.Trait.Show.show,
    "a"
    |> after("z")
    |> Ashes.Trait.Show.show,
    "z"
    |> from("z")
    |> Ashes.Trait.Show.show,
    ["z", "a", "m"]
    |> sort
    |> Ashes.Text.join(",")
]
|> Ashes.Text.join(",")
|> Ashes.IO.print
