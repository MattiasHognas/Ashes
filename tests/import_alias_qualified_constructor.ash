// expect: 42
import Ashes.Text.Json as json
import Ashes.IO as io
let v =
    42
    |> json.JsonInt
    |> json.stringify

io.print(v)
