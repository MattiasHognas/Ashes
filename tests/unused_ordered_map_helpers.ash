// expect: kept|true
import Ashes.Collection.Map.empty
import Ashes.Collection.Map.getStr
import Ashes.Collection.Map.setStr
let before left right = left <= right

let unused left right = before(left)(right)

let alias = unused

let next = alias

let effect = Ashes.IO.write("kept|")

(match empty
|> setStr("key")(true)
|> getStr("key") with
    | Some(value) -> value
    | None -> false)
|> Ashes.Trait.Show.show
|> Ashes.IO.print
