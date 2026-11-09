// expect: 12
type Tree =
    | Leaf(Str)
    | Node(Tree, Tree)

type Module =
    | names: List(Str)
    | tree: Tree

let recursive table (names: List(Str)) (acc: List((Str, Str))) =
    match names with
        | [] -> acc
        | name :: rest -> table(rest)((name, name + "_next") :: acc)

let recursive lookup (key: Str) (entries: List((Str, Str))) =
    match entries with
        | [] -> key
        | (k, v) :: rest ->
            if k == key
            then v
            else lookup(key)(rest)

let renamePair (left: List(Str)) (right: List(Str)) = (left, right)

let rewrite (mod: Module) =
    (let known = table(mod.names)([])
    in
        match mod.names
        |> Ashes.Collection.List.map(given (name: Str) -> lookup(name)(known))
        |> renamePair(mod.names) with
            | (_original, renamed) -> Module(names = renamed, tree = mod.tree))

let recursive size (tree: Tree) (acc: Int) =
    match tree with
        | Leaf(_) -> acc + 1
        | Node(left, right) ->
            acc
            |> size(left)
            |> size(right)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        let built = Module(names = ["a" + Ashes.Text.fromInt(n), "b"], tree = Node(Leaf("x"))(Leaf("y")))
        in
            match rewrite(built) with
                | Module { names = names, tree = tree } -> loop(n - 1)(acc + Ashes.Collection.List.length(names) + size(tree)(0))
in
    0
    |> loop(3)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
