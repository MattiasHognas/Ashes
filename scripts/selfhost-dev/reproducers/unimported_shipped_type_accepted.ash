let f (m: MapTree(Str, Int)) = Ashes.Collection.Map.size(m)

Ashes.Collection.Map.empty
|> f
|> Ashes.IO.print
