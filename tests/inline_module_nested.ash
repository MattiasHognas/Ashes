// expect: 50
module Json =
    module Parse =
        let value = given (s) -> s + 1
    module Render =
        let value = given (v) -> v * 10

Ashes.IO.print(Json.Render.value(Json.Parse.value(4)))
