type ArrayTree(T) =
    | TreeEmpty
    | TreeNode(Int, ArrayTree, Int, T, ArrayTree)

type Array(T) =
    | Array(Int, ArrayTree)

let empty = Array(0)(TreeEmpty)
in 
    let isEmpty = 
        fun (array) -> 
            match array with
                | Array(length, _root) -> length == 0
    in 
        let length = 
            fun (array) -> 
                match array with
                    | Array(length, _root) -> length
        in 
            let height = 
                fun (tree) -> 
                    match tree with
                        | TreeEmpty -> 0
                        | TreeNode(treeHeight, _left, _index, _value, _right) -> treeHeight
            in 
                let max = 
                    fun (left) -> 
                        fun (right) -> 
                            if left >= right
                            then left
                            else right
                in 
                    let makeNode = 
                        fun (left) -> 
                            fun (index) -> 
                                fun (value) -> 
                                    fun (right) -> TreeNode(max(height(left))(height(right)) + 1)(left)(index)(value)(right)
                    in 
                        let rotateLeft = 
                            fun (tree) -> 
                                match tree with
                                    | TreeNode(_height, left, index, value, TreeNode(_rightHeight, rightLeft, rightIndex, rightValue, rightRight)) -> makeNode(makeNode(left)(index)(value)(rightLeft))(rightIndex)(rightValue)(rightRight)
                                    | _ -> tree
                        in 
                            let rotateRight = 
                                fun (tree) -> 
                                    match tree with
                                        | TreeNode(_height, TreeNode(_leftHeight, leftLeft, leftIndex, leftValue, leftRight), index, value, right) -> makeNode(leftLeft)(leftIndex)(leftValue)(makeNode(leftRight)(index)(value)(right))
                                        | _ -> tree
                            in 
                                let balance = 
                                    fun (tree) -> 
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
                                in 
                                    let getNode = 
                                        let rec go = 
                                            fun (searchIndex) -> 
                                                fun (tree) -> 
                                                    match tree with
                                                        | TreeEmpty -> None
                                                        | TreeNode(_height, left, index, value, right) -> 
                                                            if searchIndex == index
                                                            then Some(value)
                                                            else 
                                                                if searchIndex <= index - 1
                                                                then go(searchIndex)(left)
                                                                else go(searchIndex)(right)
                                        in go
                                    in 
                                        let setNode = 
                                            let rec go = 
                                                fun (targetIndex) -> 
                                                    fun (newValue) -> 
                                                        fun (tree) -> 
                                                            match tree with
                                                                | TreeEmpty -> TreeEmpty
                                                                | TreeNode(_height, left, index, value, right) -> 
                                                                    if targetIndex == index
                                                                    then makeNode(left)(index)(newValue)(right)
                                                                    else 
                                                                        if targetIndex <= index - 1
                                                                        then balance(makeNode(go(targetIndex)(newValue)(left))(index)(value)(right))
                                                                        else balance(makeNode(left)(index)(value)(go(targetIndex)(newValue)(right)))
                                            in go
                                        in 
                                            let insertNode = 
                                                let rec go = 
                                                    fun (newIndex) -> 
                                                        fun (newValue) -> 
                                                            fun (tree) -> 
                                                                match tree with
                                                                    | TreeEmpty -> makeNode(TreeEmpty)(newIndex)(newValue)(TreeEmpty)
                                                                    | TreeNode(_height, left, index, value, right) -> 
                                                                        if newIndex <= index - 1
                                                                        then balance(makeNode(go(newIndex)(newValue)(left))(index)(value)(right))
                                                                        else balance(makeNode(left)(index)(value)(go(newIndex)(newValue)(right)))
                                                in go
                                            in 
                                                let get = 
                                                    fun (index) -> 
                                                        fun (array) -> 
                                                            match array with
                                                                | Array(length, root) -> 
                                                                    if index <= -1
                                                                    then None
                                                                    else 
                                                                        if index >= length
                                                                        then None
                                                                        else getNode(index)(root)
                                                in 
                                                    let set = 
                                                        fun (index) -> 
                                                            fun (value) -> 
                                                                fun (array) -> 
                                                                    match array with
                                                                        | Array(length, root) -> 
                                                                            if index <= -1
                                                                            then array
                                                                            else 
                                                                                if index >= length
                                                                                then array
                                                                                else Array(length)(setNode(index)(value)(root))
                                                    in 
                                                        let append = 
                                                            fun (value) -> 
                                                                fun (array) -> 
                                                                    match array with
                                                                        | Array(length, root) -> Array(length + 1)(insertNode(length)(value)(root))
                                                        in 
                                                            let toList = 
                                                                fun (array) -> 
                                                                    let rec go = 
                                                                        fun (tree) -> 
                                                                            fun (acc) -> 
                                                                                match tree with
                                                                                    | TreeEmpty -> acc
                                                                                    | TreeNode(_height, left, _index, value, right) -> 
                                                                                        let afterRight = go(right)(acc)
                                                                                        in 
                                                                                            let withNode = value :: afterRight
                                                                                            in go(left)(withNode)
                                                                    in 
                                                                        match array with
                                                                            | Array(_length, root) -> go(root)([])
                                                            in 
                                                                let fromList = 
                                                                    fun (values) -> 
                                                                        let rec go = 
                                                                            fun (rest) -> 
                                                                                fun (array) -> 
                                                                                    match rest with
                                                                                        | [] -> array
                                                                                        | head :: tail -> go(tail)(append(head)(array))
                                                                        in go(values)(empty)
                                                                in fromList
