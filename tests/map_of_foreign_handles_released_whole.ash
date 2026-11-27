// expect: ok
// A map whose values are foreign handles (an external type with no destructor) is released with
// all of its nodes, as one of integers is: a handle is one word that needs no release, so the
// dropper walking the tree admits it. Each round's map is garbage once its function returns.
import Ashes.Collection.Map
import Ashes.Collection.Map.MapTree
external type Handle
external handleOf(Str) -> Handle = "strlen"

let recursive fill (n: Int) (env: MapTree(Int, Handle)) =
    if n == 0
    then env
    else
        env
        |> Map.setInt(n)(handleOf("v"))
        |> fill(n - 1)

let body (k: Int) =
    (let _ = fill(k)(Map.empty)
    in Unit)

let recursive each (items: List(Int)) =
    match items with
        | [] -> Unit
        | item :: rest ->
            let _ = body(item)
            in each(rest)

let _ = each([1, 2, 3, 4, 5, 6, 7, 8])

Ashes.IO.print("ok")
