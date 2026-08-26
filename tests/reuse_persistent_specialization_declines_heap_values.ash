// expect: 1 -> item 1, 2 -> item 2, 3 -> item 3 | 1 -> item 1, 2 -> item 2, 3 -> item 3 | 1 -> item 1, 2 -> item 2, 3 -> item 3 | 5000
import Ashes.Collection.HashMap
import Ashes.Collection.List
import Ashes.IO
import Ashes.Text

type Item =
    | text: Str
    | position: Int

type Tree(v) =
    | Leaf
    | Node(Tree(v), Int, v, Tree(v))

let recursive setTree key value tree =
    match tree with
        | Leaf -> Node(Leaf)(key)(value)(Leaf)
        | Node(left, nodeKey, nodeValue, right) ->
            if key == nodeKey
            then Node(left)(nodeKey)(value)(right)
            else
                if key <= nodeKey
                then Node(setTree(key)(value)(left))(nodeKey)(nodeValue)(right)
                else Node(left)(nodeKey)(nodeValue)(setTree(key)(value)(right))

let recursive getTree key tree =
    match tree with
        | Leaf -> None
        | Node(left, nodeKey, nodeValue, right) ->
            if key == nodeKey
            then Some(nodeValue)
            else
                if key <= nodeKey
                then getTree(key)(left)
                else getTree(key)(right)

let recursive build count acc =
    if count == 0
    then acc
    else build(count - 1)(Item(text = "item " + Ashes.Text.fromInt(count), position = count) :: acc)

let itemText item =
    match item with
        | Item { text = text } -> text

let itemPosition item =
    match item with
        | Item { position = position } -> position

let recursive groupListsIntoMap items grouped =
    match items with
        | [] -> grouped
        | item :: rest ->
            groupListsIntoMap(rest)(
                Ashes.Collection.HashMap.set(Ashes.Text.fromInt(itemPosition(item)))([itemText(item)])(grouped)
            )

let recursive groupRecordsIntoMap items grouped =
    match items with
        | [] -> grouped
        | item :: rest ->
            groupRecordsIntoMap(rest)(Ashes.Collection.HashMap.set(Ashes.Text.fromInt(itemPosition(item)))(item)(grouped))

let recursive groupListsIntoTree items grouped =
    match items with
        | [] -> grouped
        | item :: rest -> groupListsIntoTree(rest)(setTree(itemPosition(item))([itemText(item)])(grouped))

let recursive churn count acc =
    if count == 0
    then acc
    else churn(count - 1)(("churn " + Ashes.Text.fromInt(count)) :: acc)

let recursive renderMapLists keys grouped =
    match keys with
        | [] -> []
        | key :: rest ->
            match Ashes.Collection.HashMap.get(key)(grouped) with
                | Some(text :: _) -> (key + " -> " + text) :: renderMapLists(rest)(grouped)
                | _ -> (key + " missing") :: renderMapLists(rest)(grouped)

let recursive renderMapRecords keys grouped =
    match keys with
        | [] -> []
        | key :: rest ->
            match Ashes.Collection.HashMap.get(key)(grouped) with
                | Some(item) -> (key + " -> " + itemText(item)) :: renderMapRecords(rest)(grouped)
                | None -> (key + " missing") :: renderMapRecords(rest)(grouped)

let recursive renderTreeLists keys grouped =
    match keys with
        | [] -> []
        | key :: rest ->
            match getTree(key)(grouped) with
                | Some(text :: _) -> (Ashes.Text.fromInt(key) + " -> " + text) :: renderTreeLists(rest)(grouped)
                | _ -> (Ashes.Text.fromInt(key) + " missing") :: renderTreeLists(rest)(grouped)

let mapOfLists = groupListsIntoMap(build(3)([]))(Ashes.Collection.HashMap.empty)

let mapOfRecords = groupRecordsIntoMap(build(3)([]))(Ashes.Collection.HashMap.empty)

let treeOfLists = groupListsIntoTree(build(3)([]))(Leaf)

let noise = churn(5000)([])

Ashes.IO.print(
    Ashes.Text.join(", ")(renderMapLists(["1", "2", "3"])(mapOfLists)) + " | " + Ashes.Text.join(", ")(
        renderMapRecords(["1", "2", "3"])(mapOfRecords)
    ) + " | " + Ashes.Text.join(", ")(renderTreeLists([1, 2, 3])(treeOfLists)) + " | " + Ashes.Text.fromInt(
        Ashes.Collection.List.length(noise)
    )
)
