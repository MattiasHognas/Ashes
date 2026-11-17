// expect: 785
// A record update stores a let-bound list into a field of a record that also holds a map. The
// update's field keeps a reference of its own (the transfer retain a constructor argument takes),
// so the let's release at the end of its scope leaves the list the returned record holds alive.
// Without it the list was freed under the record, and its cells were handed out again while the
// record still linked them (wrong sums here; a cyclic block list and a hang in the compiler's own
// lifetime placement).
import Ashes.Collection.Map
import Ashes.Collection.Map.MapTree
type Graph =
    | blocks: List(Int)
    | edges: MapTree(Int, List(Int))
    | dominators: List(Int)

let recursive halves (blocks: List(Int)) =
    match blocks with
        | [] -> []
        | block :: rest -> block / 2 :: halves(rest)

let recursive sum (items: List(Int)) (acc: Int) =
    match items with
        | [] -> acc
        | head :: rest -> sum(rest)(acc + head)

let withDominators (graph: Graph) =
    if Ashes.Collection.List.length(graph.dominators) == Ashes.Collection.List.length(graph.blocks)
    then graph
    else
        let dominators = halves(graph.blocks)
        in graph with dominators = dominators

let recursive build (n: Int) (acc: List(Int)) =
    if n == 0
    then acc
    else build(n - 1)(n :: acc)

let region (n: Int) =
    (let graph = withDominators(Graph(blocks = build(n)([]), edges = Ashes.Collection.Map.empty, dominators = []))
    in sum(graph.dominators)(n))

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + region(n % 7 + 3))

0
|> loop(50)
|> Ashes.IO.print
