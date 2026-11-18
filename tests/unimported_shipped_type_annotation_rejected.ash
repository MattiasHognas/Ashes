// expect-compile-error: ASH048 Type 'MapTree' is declared in module 'Ashes.Collection.Map', which this module does not import.
let size (map: MapTree(Str, Int)) = Ashes.Collection.Map.size(map)

Ashes.Collection.Map.empty
|> size
|> Ashes.IO.print
