// expect: alpha|beta|7|3
import Ashes.Internal
import Ashes.Text
import Ashes.IO
let original = (("alpha", "beta"), 7, "x" :: "y" :: "z" :: [])

let copy = Ashes.Internal.deepCopy(original)

let rec len xs = 
    match xs with
        | [] -> 0
        | _h :: t -> 1 + len(t)
in 
    match copy with
        | ((a, b), n, items) -> Ashes.IO.print(a + "|" + b + "|" + Ashes.Text.fromInt(n) + "|" + Ashes.Text.fromInt(len(items)))
