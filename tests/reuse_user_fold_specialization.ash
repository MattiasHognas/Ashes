// A user-defined nested-recursive-return fold (the Map.set shape, written in the entry file) must be
// reuse-specialized like a stdlib one: constant memory across many iterations, with an in-arm
// computed tuple value materialized correctly (min/max/sum update on hit).
// expect: k17=(17,80017,17,1)
import Ashes.IO
import Ashes.Text
import Ashes.Text
type MapTree(K, V) =
    | Empty
    | Node(Int, MapTree, K, V, MapTree)

let height map =
    match map with
        | Empty -> 0
        | Node(nodeHeight, _left, _key, _value, _right) -> nodeHeight

let max left right =
    if left >= right
    then left
    else right

let makeNode left key value right = Node(max(height(left))(height(right)) + 1)(left)(key)(value)(right)

let rotateLeft map =
    match map with
        | Node(_height, left, key, value, Node(_rightHeight, rightLeft, rightKey, rightValue, rightRight)) -> makeNode(makeNode(left)(key)(value)(rightLeft))(rightKey)(rightValue)(rightRight)
        | _ -> map

let rotateRight map =
    match map with
        | Node(_height, Node(_leftHeight, leftLeft, leftKey, leftValue, leftRight), key, value, right) -> makeNode(leftLeft)(leftKey)(leftValue)(makeNode(leftRight)(key)(value)(right))
        | _ -> map

let balance map =
    match map with
        | Empty -> Empty
        | Node(_height, left, key, value, right) ->
            let normalized = makeNode(left)(key)(value)(right)
            in
                if height(left) >= height(right) + 2
                then
                    match left with
                        | Empty -> normalized
                        | Node(_leftHeight, leftLeft, _leftKey, _leftValue, leftRight) ->
                            if height(leftLeft) >= height(leftRight)
                            then rotateRight(normalized)
                            else rotateRight(makeNode(rotateLeft(left))(key)(value)(right))
                else
                    if height(right) >= height(left) + 2
                    then
                        match right with
                            | Empty -> normalized
                            | Node(_rightHeight, rightLeft, _rightKey, _rightValue, rightRight) ->
                                if height(rightRight) >= height(rightLeft)
                                then rotateLeft(normalized)
                                else rotateLeft(makeNode(left)(key)(value)(rotateRight(right)))
                    else normalized

let getStr wanted map =
    (let recursive go current =
        match current with
            | Empty -> None
            | Node(_height, left, key, value, right) ->
                let ordering = Ashes.Byte.compare(Ashes.Byte.fromText(wanted))(Ashes.Byte.fromText(key))
                in
                    if ordering == 0
                    then Some(value)
                    else
                        if ordering <= -1
                        then go(left)
                        else go(right)
    in go(map))

let upd newKey tenths =
    (let recursive go map =
        match map with
            | Empty -> makeNode(Empty)(newKey)((tenths, tenths, tenths, 1))(Empty)
            | Node(_height, left, key, value, right) ->
                let ordering = Ashes.Byte.compare(Ashes.Byte.fromText(newKey))(Ashes.Byte.fromText(key))
                in
                    if ordering == 0
                    then
                        match value with
                            | (mn, mx, sm, ct) ->
                                let newMin =
                                    if tenths < mn
                                    then tenths
                                    else mn
                                in
                                    let newMax =
                                        if tenths > mx
                                        then tenths
                                        else mx
                                    in makeNode(left)(key)((newMin, newMax, sm + tenths, ct + 1))(right)
                    else
                        if ordering <= -1
                        then balance(makeNode(go(left))(key)(value)(right))
                        else balance(makeNode(left)(key)(value)(go(right)))
    in go)

let recursive loop i map =
    if i >= 80020
    then map
    else
        let key = "k" + Ashes.Text.fromInt(i - i / 20 * 20)
        in loop(i + 1)(upd(key)(i)(map))

let final = loop(17)(Empty)

let shown =
    match getStr("k17")(final) with
        | Some((mn, mx, sm, ct)) -> "k17=(" + Ashes.Text.fromInt(mn) + "," + Ashes.Text.fromInt(mx) + "," + Ashes.Text.fromInt(sm - sm / 1000 * 1000) + "," + Ashes.Text.fromInt(ct - ct / 1000 * 1000) + ")"
        | None -> "missing"

Ashes.IO.writeLine(shown)
