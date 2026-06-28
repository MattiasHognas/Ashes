type ArrayTree(T) =
    | TreeEmpty
    | TreeNode(Int, ArrayTree, Int, T, ArrayTree)

let empty = (0, TreeEmpty)

let isEmpty array = 
    match array with
        | (arrayLength, _root) -> arrayLength == 0

let length array = 
    match array with
        | (arrayLength, _root) -> arrayLength

let height tree = 
    match tree with
        | TreeEmpty -> 0
        | TreeNode(treeHeight, _left, _index, _value, _right) -> treeHeight

let max left right = 
    if left >= right
    then left
    else right

let makeNode left index value right = TreeNode(max(height(left))(height(right)) + 1)(left)(index)(value)(right)

let rotateLeft tree = 
    match tree with
        | TreeNode(_height, left, index, value, TreeNode(_rightHeight, rightLeft, rightIndex, rightValue, rightRight)) -> makeNode(makeNode(left)(index)(value)(rightLeft))(rightIndex)(rightValue)(rightRight)
        | _ -> tree

let rotateRight tree = 
    match tree with
        | TreeNode(_height, TreeNode(_leftHeight, leftLeft, leftIndex, leftValue, leftRight), index, value, right) -> makeNode(leftLeft)(leftIndex)(leftValue)(makeNode(leftRight)(index)(value)(right))
        | _ -> tree

let balance tree = 
    match tree with
        | TreeEmpty -> TreeEmpty
        | TreeNode(_height, left, index, value, right) -> 
            let normalized = makeNode(left)(index)(value)(right)
            in 
                if height(left) >= height(right) + 2
                then 
                    match left with
                        | TreeEmpty -> normalized
                        | TreeNode(_leftHeight, leftLeft, _leftIndex, _leftValue, leftRight) -> 
                            if height(leftLeft) >= height(leftRight)
                            then rotateRight(normalized)
                            else rotateRight(makeNode(rotateLeft(left))(index)(value)(right))
                else 
                    if height(right) >= height(left) + 2
                    then 
                        match right with
                            | TreeEmpty -> normalized
                            | TreeNode(_rightHeight, rightLeft, _rightIndex, _rightValue, rightRight) -> 
                                if height(rightRight) >= height(rightLeft)
                                then rotateLeft(normalized)
                                else rotateLeft(makeNode(left)(index)(value)(rotateRight(right)))
                    else normalized

let rec getNode searchIndex tree = 
    match tree with
        | TreeEmpty -> None
        | TreeNode(_height, left, index, value, right) -> 
            if searchIndex == index
            then Some(value)
            else 
                if searchIndex <= index - 1
                then getNode(searchIndex)(left)
                else getNode(searchIndex)(right)

let rec setNode targetIndex newValue tree = 
    match tree with
        | TreeEmpty -> TreeEmpty
        | TreeNode(_height, left, index, value, right) -> 
            if targetIndex == index
            then makeNode(left)(index)(newValue)(right)
            else 
                if targetIndex <= index - 1
                then balance(makeNode(setNode(targetIndex)(newValue)(left))(index)(value)(right))
                else balance(makeNode(left)(index)(value)(setNode(targetIndex)(newValue)(right)))

let rec insertNode newIndex newValue tree = 
    match tree with
        | TreeEmpty -> makeNode(TreeEmpty)(newIndex)(newValue)(TreeEmpty)
        | TreeNode(_height, left, index, value, right) -> 
            if newIndex <= index - 1
            then balance(makeNode(insertNode(newIndex)(newValue)(left))(index)(value)(right))
            else balance(makeNode(left)(index)(value)(insertNode(newIndex)(newValue)(right)))

let get index array = 
    match array with
        | (arrayLength, root) -> 
            if index <= -1
            then None
            else 
                if index >= arrayLength
                then None
                else getNode(index)(root)

let set index value array = 
    match array with
        | (arrayLength, root) -> 
            if index <= -1
            then array
            else 
                if index >= arrayLength
                then array
                else (arrayLength, setNode(index)(value)(root))

let append value array = 
    match array with
        | (arrayLength, root) -> (arrayLength + 1, insertNode(arrayLength)(value)(root))

let toList = 
    fun (array) -> 
        let rec go tree acc = 
            match tree with
                | TreeEmpty -> acc
                | TreeNode(_height, left, _index, value, right) -> 
                    let afterRight = go(right)(acc)
                    in 
                        let withNode = value :: afterRight
                        in go(left)(withNode)
        in 
            match array with
                | (_arrayLength, root) -> go(root)([])

let fromList = 
    fun (values) -> 
        let rec go remaining array = 
            match remaining with
                | [] -> array
                | head :: tail -> go(tail)(append(head)(array))
        in go(values)(empty)
