// expect: 5
// Two map loops routed to the in-place specialization of `Map.setStr`, each from two call sites so
// the second call of each takes the cached specialization: one stores `Int` values, the other
// `Bool` values. The first loop's calls must not fix the type of the second's.
import Ashes.Collection.Map
let recursive assign (labels: List(Str)) (current: Int) (m: MapTree(Str, Int)) =
    match labels with
        | [] -> m
        | label :: rest ->
            if current > 3
            then
                m
                |> Ashes.Collection.Map.setStr(label)(current)
                |> assign(rest)(0)
            else
                m
                |> Ashes.Collection.Map.setStr(label)(current)
                |> assign(rest)(current + 1)

let recursive mark (names: List(Str)) (flag: Bool) (m: MapTree(Str, Bool)) =
    match names with
        | [] -> m
        | name :: rest ->
            if flag
            then
                m
                |> Ashes.Collection.Map.setStr(name)(true)
                |> mark(rest)(false)
            else
                m
                |> Ashes.Collection.Map.setStr(name)(false)
                |> mark(rest)(true)

let a = assign(["a", "b", "c"])(0)(Ashes.Collection.Map.empty)

let b = mark(["x", "y"])(true)(Ashes.Collection.Map.empty)

Ashes.Collection.Map.size(a) + Ashes.Collection.Map.size(b)
|> Ashes.Text.fromInt
|> Ashes.IO.print
