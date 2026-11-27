// expect: 80
// A fresh list of integers consed for a generic parameter (`setInt`'s value) goes to the call as the
// value itself: only a list of strings is handed over as a separate owned reference, so the cell
// carries one reference, the map's, and leaves with the map each round.
import Ashes.Collection.Map
import Ashes.Collection.Map.MapTree
let edgesAt (block: Int) (children: MapTree(Int, List(Int))) =
    match Map.getInt(block)(children) with
        | Some(edges) -> edges
        | None -> []

let recursive childrenInto (idoms: List(Int)) (block: Int) (children: MapTree(Int, List(Int))) (roots: List(Int)) =
    match idoms with
        | [] -> (children, roots)
        | parent :: rest ->
            if parent < 0 || parent == block
            then childrenInto(rest)(block + 1)(children)(block :: roots)
            else
                childrenInto(rest)(block + 1)(Map.setInt(parent)(block :: edgesAt(parent)(children))(children))(roots)

let recursive sizes (n: Int) (children: MapTree(Int, List(Int))) (total: Int) =
    if n < 0
    then total
    else
        sizes(n - 1)(children)(total + Ashes.Collection.List.length(edgesAt(n)(children)))

let recursive rounds (k: Int) (total: Int) =
    if k == 0
    then total
    else
        match childrenInto([-1, 0, 0, 1, 1, 2, 0, k % 3])(0)(Map.empty)([]) with
            | (children, roots) -> rounds(k - 1)(total + sizes(8)(children)(0) + Ashes.Collection.List.length(roots))

0
|> rounds(10)
|> Ashes.IO.print
