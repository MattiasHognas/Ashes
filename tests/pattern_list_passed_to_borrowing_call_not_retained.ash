// A loop over boxes searches each box's item list, bound under a cons pattern, with a helper that
// only reads it, and returns early with what it found. The list stays live past the call, which
// borrows it: the call takes no duplicate of the list, which nothing would release.
// expect: 27
type Item =
    | itemName: Str
    | weight: Int

type Box =
    | label: Str
    | items: List(Item)

let recursive find (name: Str) (items: List(Item)) =
    match items with
        | [] -> None
        | item :: rest ->
            if item.itemName == name
            then Some(item.itemName + "!")
            else find(name)(rest)

let recursive scan name (boxes: List(Box)) =
    match boxes with
        | [] -> None
        | Box { items = items } :: rest ->
            match find(name)(items) with
                | Some(found) -> Some(found)
                | None -> scan(name)(rest)

let recursive boxesFrom (labels: List(Str)) (built: List(Box)) =
    match labels with
        | [] -> built
        | label :: rest -> boxesFrom(rest)(Box(label = label + "", items = [Item(itemName = label + "x", weight = 1)]) :: built)

let found (result: Maybe(Str)) =
    match result with
        | Some(text) -> Ashes.Text.byteLength(text)
        | None -> 1

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let boxes = boxesFrom(["A-full", "Main"])([])
        in
            rounds(n - 1)(total + found(scan("A-full" + "x")(boxes)) + found(scan("absent")(boxes)))

0
|> rounds(3)
|> Ashes.IO.print
