// A loop over boxes binds each box's list of items under a cons pattern and hands the list to a
// curried helper while the binding stays live past the call. The list may be empty, a null
// reference with no header, so the duplicate taken for the call skips it as the binding's own
// duplicate does.
// expect: 24
type Item =
    | itemName: Str
    | weight: Int

type Box =
    | label: Str
    | tag: Int
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
        | Box { label = label, items = items } :: rest ->
            if Ashes.Text.startsWith(label)("A")
            then
                match find(name)(items) with
                    | Some(found) ->
                        if Ashes.Text.byteLength(found) > 0
                        then Some(found)
                        else scan(name)(rest)
                    | None -> scan(name)(rest)
            else scan(name)(rest)

let recursive boxesFrom (labels: List(Str)) (built: List(Box)) =
    match labels with
        | [] -> built
        | label :: rest ->
            let items =
                if label == "A-empty"
                then []
                else [Item(itemName = label + "x", weight = 1), Item(itemName = label + "y", weight = 2)]
            in boxesFrom(rest)(Box(label = label + "", tag = 1, items = items) :: built)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        let boxes = boxesFrom(["A-full", "A-empty", "Main"])([])
        in
            match scan("A-fullx")(boxes) with
                | Some(found) -> rounds(n - 1)(total + Ashes.Text.byteLength(found))
                | None -> rounds(n - 1)(total)

0
|> rounds(3)
|> Ashes.IO.print
