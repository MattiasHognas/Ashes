type MapTree(K, V) =
    | Empty
    | Node(Int, MapTree, K, V, MapTree)

let empty = Empty

let isEmpty map =
    match map with
        | Empty -> true
        | _ -> false

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

let get compare searchKey =
    (let recursive go map =
        match map with
            | Empty -> None
            | Node(_height, left, key, value, right) ->
                let ordering = compare(searchKey)(key)
                in
                    if ordering == 0
                    then Some(value)
                    else
                        if ordering <= -1
                        then go(left)
                        else go(right)
    in go)

let getStr searchKey =
    (let recursive go map =
        match map with
            | Empty -> None
            | Node(_height, left, key, value, right) ->
                let ordering = Ashes.Bytes.compare(Ashes.Bytes.fromText(searchKey))(Ashes.Bytes.fromText(key))
                in
                    if ordering == 0
                    then Some(value)
                    else
                        if ordering <= -1
                        then go(left)
                        else go(right)
    in go)

let contains compare searchKey map =
    match get(compare)(searchKey)(map) with
        | None -> false
        | Some(_) -> true

let set compare newKey newValue =
    (let recursive go map =
        match map with
            | Empty -> makeNode(Empty)(newKey)(newValue)(Empty)
            | Node(_height, left, key, value, right) ->
                let ordering = compare(newKey)(key)
                in
                    if ordering == 0
                    then makeNode(left)(key)(newValue)(right)
                    else
                        if ordering <= -1
                        then balance(makeNode(go(left))(key)(value)(right))
                        else balance(makeNode(left)(key)(value)(go(right)))
    in go)

let setStr newKey newValue =
    (let recursive go map =
        match map with
            | Empty -> makeNode(Empty)(newKey)(newValue)(Empty)
            | Node(_height, left, key, value, right) ->
                let ordering = Ashes.Bytes.compare(Ashes.Bytes.fromText(newKey))(Ashes.Bytes.fromText(key))
                in
                    if ordering == 0
                    then makeNode(left)(key)(newValue)(right)
                    else
                        if ordering <= -1
                        then balance(makeNode(go(left))(key)(value)(right))
                        else balance(makeNode(left)(key)(value)(go(right)))
    in go)

let upsertStr newKey missValue onHit =
    (let recursive go map =
        match map with
            | Empty -> makeNode(Empty)(newKey)(missValue)(Empty)
            | Node(_height, left, key, value, right) ->
                let ordering = Ashes.Bytes.compare(Ashes.Bytes.fromText(newKey))(Ashes.Bytes.fromText(key))
                in
                    if ordering == 0
                    then makeNode(left)(key)(onHit(value))(right)
                    else
                        if ordering <= -1
                        then balance(makeNode(go(left))(key)(value)(right))
                        else balance(makeNode(left)(key)(value)(go(right)))
    in go)

let insert = set

let recursive size map =
    match map with
        | Empty -> 0
        | Node(_height, left, _key, _value, right) -> 1 + size(left) + size(right)

let foldLeft folder state =
    (let recursive go acc map =
        match map with
            | Empty -> acc
            | Node(_height, left, key, value, right) ->
                let afterLeft = go(acc)(left)
                in
                    let afterNode = folder(afterLeft)(key)(value)
                    in go(afterNode)(right)
    in go(state))

let toList map =
    (let prepend rest key value = (key, value) :: rest
    in foldLeft(prepend)([])(map))

let fromList compare =
    (let recursive go entries map =
        match entries with
            | [] -> map
            | (key, value) :: tail -> go(tail)(set(compare)(key)(value)(map))
    in
        given (entries) -> go(entries)(empty))
