// expect: 600
import Ashes.Collection.Map.MapTree
type Shape =
    | Named(Int, Str, List(Shape))
    | Leaf(Int)

type Path =
    | byId: MapTree(Int, MapTree(Str, Int))
    | names: List(Shape)

type Work =
    | Visit(Shape, Path)

let recursive prepend types path pending =
    match types with
        | [] -> pending
        | head :: rest -> Visit(head)(path) :: prepend(rest)(path)(pending)

let recursive consume work =
    match work with
        | [] -> 0
        | Visit(_, path) :: rest -> Ashes.Collection.List.length(path.names) + consume(rest)

let fresh round =
    (let shape = Named(round)("A")([])
    in
        let byName = Ashes.Collection.Map.setStr("A")(round)(Ashes.Collection.Map.empty)
        in
            let path = Path(byId = Ashes.Collection.Map.setInt(round)(byName)(Ashes.Collection.Map.empty), names = [shape])
            in
                consume(prepend([shape, shape])(path)([])) + Ashes.Collection.List.length(path.names))

let recursive repeat remaining total =
    if remaining == 0
    then
        total
        |> Ashes.Text.fromInt
        |> Ashes.IO.print
    else repeat(remaining - 1)(total + fresh(remaining))

repeat(200)(0)
