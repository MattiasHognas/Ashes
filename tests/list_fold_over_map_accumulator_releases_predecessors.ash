// expect: 50
// `List.foldLeft` threads a map accumulator its generic body has no layout for, so it cannot release
// the map each step replaces. A call whose accumulator type is concrete runs a copy of the fold
// lowered at that type, whose loop releases the predecessor as a concrete fold does.
import Ashes.Collection.Map.MapTree
let step (pending: MapTree(Int, Int)) (variable: Int) = Ashes.Collection.Map.setInt(variable)(variable + 1)(pending)

let recursive upTo (k: Int) (acc: List(Int)) =
    if k == 0
    then acc
    else upTo(k - 1)(k :: acc)

let run (n: Int) =
    []
    |> upTo(n)
    |> Ashes.Collection.List.foldLeft(step)(Ashes.Collection.Map.empty)
    |> Ashes.Collection.Map.size

50
|> run
|> Ashes.IO.print
