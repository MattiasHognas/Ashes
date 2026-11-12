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

let recursive count paths =
    match paths with
        | [] -> 0
        | path :: rest -> Ashes.Collection.List.length(path.names) + count(rest)

let recursive consume work visited =
    match work with
        | [] ->
            match Ashes.Collection.Map.getInt(0)(visited) with
                | None -> 0
                | Some(paths) -> count(paths)
        | Visit(shape, path) :: rest ->
            let bucket = match Ashes.Collection.Map.getInt(0)(visited) with
                | None -> []
                | Some(paths) -> paths
            in
                match shape with
                    | Named(_, _, _) -> consume(prepend([Leaf(1)])(path)(rest))(Ashes.Collection.Map.setInt(0)(path :: bucket)(visited))
                    | Leaf(_) -> consume(rest)(visited)

let fresh round =
    (let a = Named(round)("A")([])
    in
        let byName = Ashes.Collection.Map.setStr("A")(round)(Ashes.Collection.Map.empty)
        in
            let path = Path(byId = Ashes.Collection.Map.setInt(round)(byName)(Ashes.Collection.Map.empty), names = [a])
            in consume(prepend([a, a])(path)([]))(Ashes.Collection.Map.empty))

let recursive repeat remaining total =
    if remaining == 0
    then total |> Ashes.Text.fromInt |> Ashes.IO.print
    else repeat(remaining - 1)(total + fresh(remaining))
match Ashes.IO.args with
    | "1" :: [] -> repeat(1)(0)
    | "2" :: [] -> repeat(2)(0)
    | "1000" :: [] -> repeat(1000)(0)
    | _ -> Ashes.IO.panic("rounds")
