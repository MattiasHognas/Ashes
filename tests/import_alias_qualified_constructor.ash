// expect: 42
import Ashes.Text.Json as json
import Ashes.IO as io
let v = json.stringify(json.JsonInt(42))

io.print(v)
