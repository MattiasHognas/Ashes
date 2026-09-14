// expect: 3:owner_7:lbl_7 2:owner_7:lbl_7 1:owner_7:lbl_7
type Tag =
    | A(Str)
    | B

type Anchor =
    | name: Str
    | tag: Tag

type Item =
    | Item(Int, Str, Tag)

let placed (n: Int) (anchor: Anchor) =
    match anchor with
        | Anchor { name = name, tag = tag } -> Item(n)(name)(tag)

let recursive collect (anchor: Anchor) (remaining: List(Int)) (acc: List((Int, Item))) =
    match remaining with
        | [] -> acc
        | n :: rest ->
            let drop = (n, placed(n)(anchor))
            in collect(anchor)(rest)(drop :: acc)

let describe (entry: (Int, Item)) =
    match entry with
        | (n, Item(_, name, A(label))) -> Ashes.Text.fromInt(n) + ":" + name + ":" + label
        | (n, Item(_, name, B)) -> Ashes.Text.fromInt(n) + ":" + name

let build (n: Int) = Anchor(name = "owner_" + Ashes.Text.fromInt(n), tag = A("lbl_" + Ashes.Text.fromInt(n)))

let recursive joinAll (entries: List((Int, Item))) (acc: Str) =
    match entries with
        | [] -> acc
        | entry :: rest -> joinAll(rest)(acc + describe(entry) + " ")

""
|> joinAll(collect(build(7))([1, 2, 3])([]))
|> Ashes.IO.print
