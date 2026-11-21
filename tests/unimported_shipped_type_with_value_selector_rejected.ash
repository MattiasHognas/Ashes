// expect-compile-error: ASH048 Type 'MapTree' is declared in module 'Ashes.Collection.Map', which this module does not import.
import Ashes.Collection.Map.size
let count (map: MapTree(Str, Int)) = size(map)

Ashes.Collection.Map.empty
|> count
|> Ashes.IO.print
